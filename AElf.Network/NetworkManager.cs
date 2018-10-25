﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AElf.Common.ByteArrayHelpers;
using AElf.Common.Collections;
using AElf.Kernel;
using AElf.Network.Config;
using AElf.Network.Connection;
using AElf.Network.Data;
using AElf.Network.Peers.Exceptions;
using Google.Protobuf;
using NLog;

[assembly:InternalsVisibleTo("AElf.Network.Tests")]
namespace AElf.Network.Peers
{
    public class PeerAddedEventArgs : EventArgs
    {
        public IPeer Peer { get; set; }
    }
    
    public class PeerRemovedEventArgs : EventArgs
    {
        public IPeer Peer { get; set; }
    }

    public class NetMessageReceivedArgs : EventArgs
    {
        public TimeoutRequest Request { get; set; }
        public Message Message { get; set; }
        public PeerMessageReceivedArgs PeerMessage { get; set; }
    }

    public class RequestFailedArgs : EventArgs
    {
        public Message RequestMessage { get; set; }
        
        public byte[] ItemHash { get; set; }
        public int BlockIndex { get; set; }
        
        public List<IPeer> TriedPeers = new List<IPeer>();
    }
    
    public class NetworkManager : INetworkManager, IPeerManager, IDisposable
    {
        public const int DefaultMaxBlockHistory = 15;
        
        public const int TargetPeerCount = 8;
        public const int DefaultRequestTimeout = 1000;
        public const int DefaultRequestMaxRetry = TimeoutRequest.DefaultMaxRetry;
        
        public event EventHandler MessageReceived;
        public event EventHandler PeerListEmpty;

        public event EventHandler PeerAdded;
        public event EventHandler PeerRemoved;

        public event EventHandler RequestFailed;
        
        private readonly IAElfNetworkConfig _networkConfig;
        private readonly IPeerDatabase _peerDatabase;
        private readonly ILogger _logger;

        // List of bootnodes that the manager was started with
        private readonly List<NodeData> _bootnodes = new List<NodeData>();
        
        // List of connected bootnodes
        private readonly List<IPeer> _bootnodePeers = new List<IPeer>();
        
        // List of non bootnode peers
        private readonly List<IPeer> _peers = new List<IPeer>();
        private readonly List<IPeer> _peersNoAuth = new List<IPeer>();
        
        private readonly int _port;

        public bool UndergoingPm { get; private set; } = false;
        public bool ReceivingPeers { get; private set; } = false;

        private System.Threading.Timer _maintenanceTimer = null;
        private readonly TimeSpan _initialMaintenanceDelay = TimeSpan.FromSeconds(2);
        private readonly TimeSpan _maintenancePeriod = TimeSpan.FromMinutes(1);
        
        public bool NoPeers { get; set; } = true;
        
        private readonly IConnectionListener _connectionListener;

        public int RequestTimeout { get; set; } = DefaultRequestTimeout;
        public int RequestMaxRetry { get; set; } = DefaultRequestMaxRetry;

        private Object _pendingRequestsLock = new Object();  
        public List<TimeoutRequest> _pendingRequests;

        private BoundedByteArrayQueue _lastBlocksReceived;
        public int MaxBlockHistory { get; set; } = DefaultMaxBlockHistory;

        private BlockingPriorityQueue<PeerMessageReceivedArgs> _incomingJobs;

        public NetworkManager(IAElfNetworkConfig config, IConnectionListener connectionListener, ILogger logger)
        {
            _incomingJobs = new BlockingPriorityQueue<PeerMessageReceivedArgs>();
            _pendingRequests = new List<TimeoutRequest>();
            
            _connectionListener = connectionListener;
            
            _networkConfig = config;
            _logger = logger;

            if (_networkConfig != null)
            {
                _port = config.Port;
                
                // Add the provided bootnodes
                if (_networkConfig.Bootnodes != null && _networkConfig.Bootnodes.Any())
                {
                    foreach (var node in _networkConfig.Bootnodes)
                    {
                        _bootnodes.Add(node);
                    }
                }
                else
                {
                    _logger?.Trace("Warning : bootnode list is empty.");
                }
            }
        }

        internal TimeoutRequest HasRequestWithHash(byte[] hash)
        {
            lock (_pendingRequestsLock)
            {
                return _pendingRequests.FirstOrDefault(r => r.ItemHash.BytesEqual(hash));
            }
        }
        
        internal TimeoutRequest HasRequestWithIndex(int index)
        {
            lock (_pendingRequestsLock)
            {
                return _pendingRequests.FirstOrDefault(r => r.BlockIndex == index);
            }
        }

        /// <summary>
        /// This method start the server that listens for incoming
        /// connections and sets up the manager.
        /// </summary>
        public void Start()
        {
            _lastBlocksReceived = new BoundedByteArrayQueue(MaxBlockHistory);
            
            Task.Run(() => _connectionListener.StartListening(_port));
            
            _connectionListener.IncomingConnection += ConnectionListenerOnIncomingConnection;
            _connectionListener.ListeningStopped += ConnectionListenerOnListeningStopped;
            
            Setup().GetAwaiter().GetResult();
            
            Task.Run(() => StartProcessingIncoming()).ConfigureAwait(false);
        }
        
        private void ConnectionListenerOnListeningStopped(object sender, EventArgs eventArgs)
        {
            _logger.Trace("Listening stopped"); // todo
        }

        /// <summary>
        /// Sets up the server according to the configuration that was
        /// provided.
        /// </summary>
        private async Task Setup()
        {
            if (_networkConfig == null)
                return;
            
            if (_networkConfig.Peers.Any())
            {
                foreach (var peer in _networkConfig.Peers)
                {
                    NodeData nodeData = NodeData.FromString(peer);
                    await CreateAndAddPeer(nodeData);
                }
            }

            if (_peerDatabase != null)
            {
                var dbNodeData = _peerDatabase.ReadPeers();

                foreach (var p in dbNodeData)
                {
                    await CreateAndAddPeer(p);
                }
            }
            
            await AddBootnodes();

            if (_peers.Count < 1)
            {
                //throw new NoPeersConnectedException("Could not connect to any of the bootnodes");
                
                // Either a network problem or this node is the first to come online.
                NoPeers = true;
                PeerListEmpty?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                NoPeers = false;
            }

            _maintenanceTimer = new System.Threading.Timer(e => DoPeerMaintenance(), null, _initialMaintenanceDelay, _maintenancePeriod);
        }

        #region Message processing

        private void StartProcessingIncoming()
        {
            while (true)
            {
                try
                {
                    PeerMessageReceivedArgs msg = _incomingJobs.Take();
                    ProcessPeerMessage(msg);
                }
                catch (Exception e)
                {
                    _logger?.Trace(e, "Error while processing incoming messages");
                }
            }
        }
        
        private void ProcessPeerMessage(PeerMessageReceivedArgs args)
        {
            if (args?.Message == null) 
                return;
            
            if (args.Message.Type == (int)MessageType.RequestPeers)
            {
                HandlePeerRequestMessage(args);
            }
            else if (args.Message.Type == (int)MessageType.Peers)
            {
                Task.Run(() => ReceivePeers(args.Peer, ByteString.CopyFrom(args.Message.Payload)));
            }
            else
            {
                TimeoutRequest originalRequest = null;
                    
                if (args.Message.Type == (int) MessageType.Tx)
                {
                    originalRequest = HandleTransactionMessage(args.Peer, args.Message);
                }
                else if (args.Message.Type == (int) MessageType.Block)
                {
                    originalRequest = HandleBlockMessage(args.Peer, args.Message);
                }

                if (args.Message.Type == (int) MessageType.BroadcastBlock)
                {
                    Block b = Block.Parser.ParseFrom(args.Message.Payload); // todo later deserializations will be redundant
                    byte[] blockHash = b.GetHash().Value.ToByteArray();
                        
                    if (_lastBlocksReceived.Contains(blockHash))
                        return;
                        
                    _lastBlocksReceived.Enqueue(blockHash);
                        
                    foreach (var peer in _peers.Where(p => !p.Equals(args.Peer)))
                    {
                        try 
                        {
                            peer.EnqueueOutgoing(args.Message); 
                        }
                        catch (Exception ex) { } // todo think about removing this try/catch, enqueue should be fire and forget
                    }
                }
                    
                var evt = new NetMessageReceivedArgs {
                    Message = args.Message,
                    PeerMessage = args,
                    Request = originalRequest
                };

                // raise the event so the higher levels can process it.
                MessageReceived?.Invoke(this, evt);
            }
        }
        
        private void HandlePeerRequestMessage(PeerMessageReceivedArgs args)
        {
            try
            {
                ReqPeerListData req = ReqPeerListData.Parser.ParseFrom(args.Message.Payload);
                ushort numPeers = (ushort) req.NumPeers;
                    
                PeerListData pListData = new PeerListData();

                foreach (var peer in _peers.Where(p => p.DistantNodeData != null && !p.DistantNodeData.Equals(args.Peer.DistantNodeData)))
                {
                    pListData.NodeData.Add(peer.DistantNodeData);
                            
                    if (pListData.NodeData.Count == numPeers)
                        break;
                }

                byte[] payload = pListData.ToByteArray();
                var resp = new Message
                {
                    Type = (int)MessageType.Peers,
                    Length = payload.Length,
                    Payload = payload,
                    OutboundTrace = Guid.NewGuid().ToString()
                };
                        
                _logger?.Trace($"Sending peers : {pListData} to {args.Peer}");

                Task.Run(() => args.Peer.EnqueueOutgoing(resp));
            }
            catch (Exception exception)
            {
                _logger?.Trace(exception, "Error while answering a peer request.");
            }
        }
        
        private void HandleNewMessage(object sender, EventArgs e)
        {
            if (e is PeerMessageReceivedArgs args)
            {
                _incomingJobs.Enqueue(args, 0);
            }
        }

        #endregion
        
        private void ConnectionListenerOnIncomingConnection(object sender, EventArgs eventArgs)
        {
            if (sender != null && eventArgs is IncomingConnectionArgs args)
            {
                CreatePeerFromConnection(args.Client, null);
            }
        }
        
        public void QueueTransactionRequest(byte[] transactionHash, IPeer hint)
        {
            try
            {
                IPeer selectedPeer = hint ?? _peers.FirstOrDefault();
            
                if(selectedPeer == null)
                    return;
            
                TxRequest br = new TxRequest { TxHash = ByteString.CopyFrom(transactionHash) };
                var msg = NetRequestFactory.CreateMessage(MessageType.TxRequest, br.ToByteArray());
            
                // Select peer for request
                TimeoutRequest request = new TimeoutRequest(transactionHash, msg, RequestTimeout);
                request.MaxRetryCount = RequestMaxRetry;
            
                lock (_pendingRequestsLock)
                {
                    _pendingRequests.Add(request);
                }
            
                request.RequestTimedOut += RequestOnRequestTimedOut;
                request.TryPeer(selectedPeer);
                
                _logger?.Trace($"Request for transaction {transactionHash?.ToHex()} send to {selectedPeer}");
            }
            catch (Exception e)
            {
                _logger?.Trace(e, $"Error while requesting transaction {transactionHash?.ToHex()}.");
            }
        }

        /// <summary>
        /// Callback called when the requests internal timer has executed.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="eventArgs"></param>
        private void RequestOnRequestTimedOut(object sender, EventArgs eventArgs)
        {
            if (sender == null)
            {
                _logger?.Trace("Request timeout - sender null.");
                return;
            }

            if (sender is TimeoutRequest req)
            {
                string id = req.ItemHash != null ? $"for transaction with hash {req.ItemHash.ToHex()}" : $"for block with index {req.BlockIndex}";
                _logger?.Trace("Request timedout " + id + $", with {req.Peer} and timeout : {TimeSpan.FromMilliseconds(req.Timeout)}.");
                
                if (req.HasReachedMaxRetry)
                {
                    lock (_pendingRequestsLock)
                    {
                        _pendingRequests.Remove(req);
                    }
                    
                    req.RequestTimedOut -= RequestOnRequestTimedOut;
                    FireRequestFailed(req);
                    return;
                }
                
                IPeer nextPeer = _peers.FirstOrDefault(p => !p.Equals(req.Peer));
                if (nextPeer != null)
                {
                    _logger?.Trace("Trying another peer " + id + $", next : {nextPeer}.");
                    req.TryPeer(nextPeer);
                }
            }
            else
            {
                _logger?.Trace("Request timeout - sender wrong type.");
            }
        }

        private void FireRequestFailed(TimeoutRequest req)
        {
            RequestFailedArgs reqFailedArgs = new RequestFailedArgs
            {
                RequestMessage = req.RequestMessage,
                BlockIndex = req.BlockIndex,
                ItemHash = req.ItemHash,
                TriedPeers = req.TriedPeers.ToList()
            };

            string id = req.ItemHash != null ? $"for transaction with hash {req.ItemHash.ToHex()}" : $"for block with index {req.BlockIndex}";
            _logger?.Trace("Request failed " + id + $" after {req.TriedPeers.Count} tries. Max tries : {req.MaxRetryCount}.");
                    
            RequestFailed?.Invoke(this, reqFailedArgs);
        }

        public void QueueBlockRequestByIndex(int index)
        {
            try
            {
                Peer selectedPeer = (Peer)_peers.FirstOrDefault();
            
                if(selectedPeer == null)
                    return;
            
                BlockRequest br = new BlockRequest { Height = index };
                Message message = NetRequestFactory.CreateMessage(MessageType.RequestBlock, br.ToByteArray()); 
            
                // Select peer for request
                TimeoutRequest request = new TimeoutRequest(index, message, RequestTimeout);
                request.MaxRetryCount = RequestMaxRetry;
                
                lock (_pendingRequestsLock)
                {
                    _pendingRequests.Add(request);
                }

                request.TryPeer(selectedPeer);
                _logger?.Trace($"Request for block at index {index}");
            }
            catch (Exception e)
            {
                _logger?.Trace(e, $"Error while requesting block for index {index}.");
            }
        }

        #region Peer maintenance

        internal void DoPeerMaintenance()
        {
            List<IPeer> peersSnapshot = _peers.ToList();
            
            if (_peers == null)
                return;
            
            // If we're in the process of receiving peers (potentially modifiying _peers)
            // we return directly, we'll try again in the next cycle.
            if (ReceivingPeers)
                return;
            
            // If we're already in a maintenance cycle: do nothing
            if (UndergoingPm)
                return;
            
            UndergoingPm = true;

            // After either the initial maintenance operation or the removal operation
            // (mutually exclusive) adjust the peers to get to TargetPeerCount.
            try
            {
                int missingPeers = TargetPeerCount - _peers.Count;
                
                if (missingPeers > 0)
                {
                    // We set UndergoingPm here because at this point it will be ok for 
                    // us to receive peers 
                    UndergoingPm = false;
                    
                    var req = NetRequestFactory.CreateMissingPeersReq(missingPeers);
                    var taskAwaiter = BroadcastMessage(req);
                }
                else if (missingPeers < 0)
                {
                    // Here we will be modifying the _peers collection and we don't want
                    // anybody else modifying it.
                    
                    // Calculate peers to remove
                    //List<IPeer> peersToRemove = GetPeersToRemove(Math.Abs(missingPeers));
                    
                    // Remove them
                    //foreach (var peer in peersToRemove)
                    //RemovePeer(peer);
                }
                else
                {
                    // Healthy peer list - nothing to do
                }
            }
            catch (Exception e)
            {
                ;
            }

            if (_peerDatabase != null)
            {
                if (_peers.Count >= peersSnapshot.Count)
                {
                    WritePeersToDb(_peers);
                }
            }

            UndergoingPm = false;
            
            if (_peers.Count < 1)
            {
                // Connection to all peers have been lost
                NoPeers = true;
                PeerListEmpty?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                NoPeers = false;
            }
        }

        /// <summary>
        /// Gets the peers to remove from the manager according to certain
        /// rules.
        /// todo : for now the rule is the first <see cref="count"/> peers
        /// </summary>
        /// <param name="count"></param>
        internal List<IPeer> GetPeersToRemove(int count)
        {
            // Calculate peers to remove
            List<IPeer> peersToRemove = _peers.Take(count).ToList();
            return peersToRemove;
        }
        
        private List<NodeData> AlreadyReceived = new List<NodeData>();
        
        /// <summary>
        /// This method processes the peers received from one of
        /// the connected peers.
        /// </summary>
        /// <param name="messagePayload"></param>
        /// <returns></returns>
        internal async Task ReceivePeers(IPeer pr, ByteString messagePayload)
        {
            // If we're in a maintenance cycle - do nothing
            // todo : maybe later we can queue this work...
            if (UndergoingPm)
            {
                _logger?.Trace("Doing peer maintenance...");
            }
                
            ReceivingPeers = true;
            
            try
            {
                var str = $"Receiving peers from {pr} - current node list: \n";
                var peerStr = _peers.Select(c => c.ToString()).Aggregate((a, b) => a.ToString() + ", " + b);
                _logger?.Trace(str + peerStr);
                
                PeerListData peerList = PeerListData.Parser.ParseFrom(messagePayload);
                _logger?.Trace($"Receiving peers - node list count {peerList.NodeData.Count}.");
                
                if (peerList.NodeData.Count > 0)
                    _logger?.Trace("Peers received : " + peerList.GetLoggerString());

                foreach (var peer in peerList.NodeData)
                {
                    NodeData p = new NodeData { IpAddress = peer.IpAddress, Port = peer.Port };

                    if (AlreadyReceived.Any(n => n.Equals(p)))
                    {
                        _logger?.Trace($"Already received {p}.");
                        continue;
                    }

                    AlreadyReceived.Add(p);
                    
                    IPeer newPeer = await CreateAndAddPeer(p);
                }
            }
            catch (Exception e)
            {
                ReceivingPeers = false;
                _logger?.Error(e, "Invalid peer(s) - Could not receive peer(s) from the network", null);
            }

            ReceivingPeers = false;
        }
        
        #endregion Peer maintenance

        internal async Task AddBootnodes()
        {
            foreach (var bootNode in _bootnodes)
            {
                await CreateAndAddPeer(bootNode);
            }
        }
        
        /// <summary>
        /// Returns the first occurence of the peer. IPeer
        /// implementations may override the equality logic.
        /// </summary>
        /// <param name="peer"></param>
        /// <returns></returns>
        public IPeer GetPeer(IPeer peer)
        {
            return _peers?.FirstOrDefault(p => p.Equals(peer));
        }

        /// <summary>
        /// Adds a peer to the manager and hooks up the callback for
        /// receiving messages from it. It also starts the peers
        /// listening process.
        /// </summary>
        /// <param name="peer">the peer to add</param>
        public IPeer CreatePeerFromConnection(TcpClient client, NodeData nd)
        {
            if (client == null)
                return null;

            Peer peer = new Peer(_port);
            peer.Initialize(client);

            if (nd != null)
            {
                AlreadyReceived.Add(nd);
                peer.DistantNodeData = nd;
            }

            // Don't add a peer already in the list
            if (GetPeer(peer) != null)
            {
                _logger?.Trace($"[AddPeer] Peer already included - {peer.IpAddress} : {peer.Port}");
                return null;
            }
            
            AddPeer(peer);

            return peer;
        }

        internal void AddPeer(IPeer peer)
        {
            _peersNoAuth.Add(peer);
            
            peer.PeerAuthentified += PeerOnPeerAuthentified;
            peer.PeerDisconnected += ProcessClientDisconnection;
        }

        // todo temp fix for unit testing
        internal void AddPeerNoAuth(IPeer peer)
        {
            _peers.Add(peer);
            
            peer.PeerAuthentified += PeerOnPeerAuthentified;
            peer.PeerDisconnected += ProcessClientDisconnection;
        }

        private void PeerOnPeerAuthentified(object sender, EventArgs eventArgs)
        {
            if (sender is Peer peer)
            {
                peer.MessageReceived += HandleNewMessage;
                
                _peersNoAuth.Remove(peer);

                if (_peers.Any(p => p.DistantNodeData != null && p.DistantNodeData.Equals(peer.DistantNodeData)))
                    return;
                
                _peers.Add(peer);
                
                _logger?.Trace("Peer authentified and added : " + peer);
                
                PeerAdded?.Invoke(this, new PeerAddedEventArgs { Peer = peer });
            }
        }

        /// <summary>
        /// Creates a Peer.
        /// </summary>
        /// <param name="nodeData"></param>
        /// <returns></returns>
        private async Task<IPeer> CreateAndAddPeer(NodeData nodeData)
        {
            if (nodeData == null)
                return null;
            
            try
            {
                var nodeDialer = new NodeDialer(nodeData.IpAddress.ToString(), nodeData.Port);
                TcpClient peer = await nodeDialer.DialAsync();
                
                // If we successfully connected to the other peer
                // add it to be managed
                if (peer != null)
                {
                    return CreatePeerFromConnection(peer, nodeData);
                }
            }
            catch (ResponseTimeOutException rex)
            {
                _logger?.Error(rex, rex?.Message + " - "  + nodeData);
            }

            return null;
        }
        
        /// <summary>
        /// Removes a peer from the list of peers.
        /// </summary>
        /// <param name="peer">the peer to remove</param>
        public void RemovePeer(IPeer peer)
        {
            if (peer == null)
                return;
            
            _peers.Remove(peer);
            
            _logger?.Trace("Peer removed : " + peer);
            
            PeerRemoved?.Invoke(this, new PeerRemovedEventArgs { Peer = peer });
        }

        public List<IPeer> GetPeers()
        {
            return _peers.Union(_bootnodePeers).ToList();
        }

        /// <summary>
        /// Returns a specified number of random peers from the peer
        /// list.
        /// </summary>
        /// <param name="numPeers">number of peers requested</param>
        public List<NodeData> GetPeers(ushort? numPeers, bool includeBootnodes = true)
        {
            IQueryable<IPeer> peers = _peers.AsQueryable();
                
            peers = peers.OrderBy(p => p.Port);

            if (numPeers.HasValue)
                peers = peers.Take(numPeers.Value);

            List<NodeData> peersToReturn = peers
                .Select(peer => new NodeData
                    {
                        IpAddress = peer.IpAddress,
                        Port = peer.Port
                    })
                .ToList();

            return peersToReturn;
            
            /*Random rand = new Random();
            List<IPeer> peers = _peers.OrderBy(c => rand.Next()).Select(c => c).ToList();
            List<NodeData> returnPeers = new List<NodeData>();
            
            foreach (var peer in peers)
            {
                NodeData p = new NodeData
                {
                    IpAddress = peer.IpAddress,
                    Port = peer.Port,
                    IsBootnode = peer.IsBootnode
                };
                
                if (!p.IsBootnode)
                    returnPeers.Add(p);

                if (returnPeers.Count == numPeers)
                    break;
            }*/

            //return returnPeers;
        }

        private void WritePeersToDb(List<IPeer> peerList)
        {   
            List<NodeData> peers = new List<NodeData>();

            foreach (var p in peerList)
            {
                NodeData peer = new NodeData
                {
                    IpAddress = p.IpAddress,
                    Port = p.Port
                };
                peers.Add(peer);
            }
            
            _peerDatabase.WritePeers(peers);
        }
        
        /// <summary>
        /// Callback for when a Peer fires a <see cref="PeerDisconnected"/> event. It unsubscribes
        /// the manager from the events and removes it from the list.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ProcessClientDisconnection(object sender, EventArgs e)
        {
            if (sender != null && e is PeerDisconnectedArgs args && args.Peer != null)
            {
                IPeer peer = args.Peer;
                
                peer.MessageReceived -= HandleNewMessage;
                peer.PeerDisconnected -= ProcessClientDisconnection;
                
                _bootnodePeers.Remove(peer);
                
                RemovePeer(args.Peer);
            }
        }

        internal TimeoutRequest HandleBlockMessage(Peer peer, Message msg)
        {
            if (peer == null || msg == null)
            {
                _logger?.Trace("HandleBlockMessage : peer or message null.");
                return null;
            }
            
            try
            {
                Block block = Block.Parser.ParseFrom(msg.Payload);

                if (block?.Header == null)
                    return null;

                TimeoutRequest request;
                lock (_pendingRequestsLock)
                {
                    request = _pendingRequests.FirstOrDefault(r => r.BlockIndex == (int)block.Header.Index);
                }

                if (request != null)
                {
                    request.RequestTimedOut -= RequestOnRequestTimedOut;
                    request.Stop();
                    
                    lock (_pendingRequestsLock)
                    {
                        _pendingRequests.Remove(request);
                    }
                    
                    _logger?.Trace($"Block request matched and removed. Index : { block.Header.Index }");
                }
                else
                {
                    _logger?.Trace($"Block request not found. Index : { block.Header.Index }");
                }

                return request;
            }
            catch (Exception e)
            {
                _logger?.Trace(e, "HandleBlockMessage : exception while handling block.");
                return null;
            }
        }

        internal TimeoutRequest HandleTransactionMessage(Peer peer, Message msg)
        {
            if (peer == null || msg == null)
            {
                _logger?.Trace("HandleTransactionMessage : peer or message null.");
                return null;
            }
            
            try
            {
                Transaction tx = Transaction.Parser.ParseFrom(msg.Payload);
                byte[] txHash = tx.GetHash().Value.ToByteArray();

                TimeoutRequest request;
                lock (_pendingRequestsLock)
                {
                    request = _pendingRequests.FirstOrDefault(r => r.ItemHash.BytesEqual(txHash));
                }

                if (request != null)
                {
                    request.RequestTimedOut -= RequestOnRequestTimedOut;
                    request.Stop();

                    lock (_pendingRequestsLock)
                    {
                        _pendingRequests.Remove(request);
                    }
                    
                    _logger?.Trace($"Transaction request matched and removed. Hash : {txHash.ToHex()}");
                }
                else
                {
                    _logger?.Trace($"Transaction request not found. Hash : {txHash.ToHex()}");
                }

                return request;
            }
            catch (Exception e)
            {
                _logger?.Trace(e, "HandleTransactionMessage : exception while handling transaction.");
                return null;
            }
        }

        public async Task<int> BroadcastBock(byte[] hash, byte[] payload)
        {
            _lastBlocksReceived.Enqueue(hash);
            return await BroadcastMessage(MessageType.BroadcastBlock, payload);
        }
        
        /// <summary>
        /// This message broadcasts data to all of its peers. This creates and
        /// sends a <see cref="AElfPacketData"/> object with the provided pay-
        /// load and message type.
        /// </summary>
        /// <param name="messageType"></param>
        /// <param name="payload"></param>
        /// <param name="messageId"></param>
        /// <returns></returns>
        public async Task<int> BroadcastMessage(MessageType messageType, byte[] payload)
        {
            try
            {
                
                Message packet = NetRequestFactory.CreateMessage(messageType, payload);
                return BroadcastMessage(packet);
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while sending a message to the peers.");
                return 0;
            }
        }

        public int BroadcastMessage(Message message)
        {
            if (_peers == null || !_peers.Any())
                return 0;

            int count = 0;
            
            try
            {
                //byte[] data = packet.ToByteArray();

                foreach (var peer in _peers)
                {
                    try
                    {
                        peer.EnqueueOutgoing(message); //todo
                        count++;
                    }
                    catch (Exception e) { }
                }
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while sending a message to the peers.");
            }

            return count;
        }

        public void Dispose()
        {
            _maintenanceTimer?.Dispose();
        }
    }
}