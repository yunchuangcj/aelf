﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using AElf.Common.ByteArrayHelpers;
using NLog;

namespace AElf.Network.Connection
{
    /// <summary>
    /// This class performs writes to the underlying tcp stream.
    /// </summary>
    public class MessageWriter : IMessageWriter
    {
        private const int DefaultMaxOutboundPacketSize = 1024;
        
        private readonly ILogger _logger;
        private readonly NetworkStream _stream;
        private readonly BlockingCollection<Message> _outboundMessages;
        
        /// <summary>
        /// This configuration property determines the maximum size an
        /// outgoing messages payload. If the payloads size is larger
        /// than this value, this message will be send in multiple sub
        /// packets.
        /// </summary>
        public int MaxOutboundPacketSize { get; set; } = DefaultMaxOutboundPacketSize;
        
        public MessageWriter(NetworkStream stream)
        {
            _outboundMessages = new BlockingCollection<Message>();
            _stream = stream;
            
            _logger = LogManager.GetLogger(nameof(MessageWriter));
        }
        
        /// <summary>
        /// Starts the dequing of outgoing messages.
        /// </summary>
        public void Start()
        {
            Task.Run(() => DequeueOutgoingLoop()).ConfigureAwait(false);
        }
        
        public void EnqueueMessage(Message p)
        {
            try
            {
                _outboundMessages.Add(p);
            }
            catch (Exception e)
            {
                _logger.Trace(e);
            }
        }
        
        /// <summary>
        /// The main loop that sends queud up messages from the message queue.
        /// </summary>
        internal void DequeueOutgoingLoop()
        {
            while (true)
            {
                try
                {
                    Message p = _outboundMessages.Take();

                    if (p.Payload.Length > MaxOutboundPacketSize)
                    {
                        // Split
                        int packetCount = (p.Payload.Length / MaxOutboundPacketSize);
                        int lastPacketSize = p.Payload.Length % MaxOutboundPacketSize;
                        
                        if (lastPacketSize != 0)
                            packetCount++;

                        List<PartialPacket> partials = new List<PartialPacket>();

                        int currentIndex = 0;
                        for (int i = 0; i < packetCount - 1; i++)
                        {
                            byte[] slice = new byte[MaxOutboundPacketSize];
                            
                            Array.Copy(p.Payload, currentIndex, slice, 0, MaxOutboundPacketSize);
                            
                            var partial = new PartialPacket 
                            {
                                Type = p.Type, Position = i, IsEnd = false, TotalDataSize = p.Payload.Length, Data = slice
                            };
                            
                            partials.Add(partial);

                            currentIndex += MaxOutboundPacketSize;
                        }
                        
                        byte[] endSlice = new byte[lastPacketSize];
                        Array.Copy(p.Payload, currentIndex, endSlice, 0, lastPacketSize);
                        
                        var endPartial = new PartialPacket 
                        {
                            Type = p.Type, Position = packetCount-1, IsEnd = true, TotalDataSize = p.Payload.Length, Data = endSlice
                        };
                        
                        partials.Add(endPartial);

                        _logger.Trace($"Message split into {partials.Count} packets.");

                        foreach (var msg in partials)
                        {
                            SendPartialPacket(msg);
                        }
                    }
                    else
                    {
                        // Send without splitting
                        SendPacketFromMessage(p);
                    }
                }
                catch (Exception e)
                {
                    _logger.Trace(e, "Exception while dequeing message");
                }
            }
        }

        internal void SendPacketFromMessage(Message p)
        {
            byte[] type = { (byte)p.Type };
            byte[] isbuffered = { 0 };
            byte[] isConsensus = { (byte)(p.IsConsensus ? 0 : 1) };
            byte[] length = BitConverter.GetBytes(p.Length);
            byte[] arrData = p.Payload;
            byte[] b = ByteArrayHelpers.Combine(type, isbuffered, isConsensus, length, arrData);
            
            if (!string.IsNullOrWhiteSpace(p.OutboundTrace))
                _logger?.Trace($"About to dequeued message with trace : {p.OutboundTrace}");
            
            _stream.Write(b, 0, b.Length);
            
            if (!string.IsNullOrWhiteSpace(p.OutboundTrace))
                _logger?.Trace($"Dequeued message with trace : {p.OutboundTrace}");
        }

        internal void SendPartialPacket(PartialPacket p)
        {
            byte[] type = { (byte)p.Type };
            byte[] isbuffered = { 1 };
            byte[] isConsensus = { 0 };
            byte[] length = BitConverter.GetBytes(p.Data.Length);

            byte[] posBytes = BitConverter.GetBytes(p.Position);
            byte[] isEndBytes = p.IsEnd ? new byte[] { 1 } : new byte[] { 0 };
            byte[] totalLengthBytes = BitConverter.GetBytes(p.TotalDataSize);
            
            byte[] arrData = p.Data;
            
            byte[] b = ByteArrayHelpers.Combine(type, isbuffered, length, posBytes, isEndBytes, totalLengthBytes, arrData);
            _stream.Write(b, 0, b.Length);
        }
        
        #region Closing and disposing

        public void Close()
        {
            Dispose();
        }
        
        public void Dispose()
        {
            // This will cause an IOException in the read loop
            // but since IsConnected is switched to false, it 
            // will not fire the disconnection exception.
            _stream?.Close();
        }

        #endregion
    }
}