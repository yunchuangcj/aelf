using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AElf.Kernel;
using AElf.Kernel.Managers;
using AElf.ChainController;
using AElf.SmartContract;
using AElf.Execution;
using AElf.Execution.Scheduling;
using AElf.Sdk.CSharp.Types;
using AElf.Types.CSharp;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NLog;
using NLog.Fluent;
using ServiceStack;
using ServiceStack.Text;

namespace AElf.Benchmark
{
    public class MultiSideChainTest
    {
        private readonly IChainCreationService _chainCreationService;
        private readonly IBlockManager _blockManager;
        private readonly ISmartContractService _smartContractService;
        private readonly ILogger _logger;
        private readonly BenchmarkOptions _options;
        private readonly IConcurrencyExecutingService _concurrencyExecutingService;
        private readonly ServicePack _servicePack;

        private readonly TransactionDataGenerator _dataGenerater;

        private List<SideChain> Chains;
        private int _incrementId;
        private Hash _ChainId;

        public byte[] SmartContractZeroCode
        {
            get
            {
                byte[] code;
                using (FileStream file =
                    File.OpenRead(Path.GetFullPath(Path.Combine(_options.DllDir, _options.ZeroContractDll))))
                {
                    code = file.ReadFully();
                }

                return code;
            }
        }

        public MultiSideChainTest(IChainCreationService chainCreationService, IBlockManager blockManager,
            IChainContextService chainContextService, ISmartContractService smartContractService,
            ILogger logger, IFunctionMetadataService functionMetadataService,
            IAccountContextService accountContextService, IWorldStateDictator worldStateDictator,
            BenchmarkOptions options, IConcurrencyExecutingService concurrencyExecutingService)
        {
            _ChainId = Hash.Generate();

            var worldStateDictator1 = worldStateDictator;
            worldStateDictator1.SetChainId(_ChainId).DeleteChangeBeforesImmidiately = true;

            _chainCreationService = chainCreationService;
            _blockManager = blockManager;
            _smartContractService = smartContractService;
            _logger = logger;
            _options = options;
            _concurrencyExecutingService = concurrencyExecutingService;


            _servicePack = new ServicePack
            {
                ChainContextService = chainContextService,
                SmartContractService = _smartContractService,
                ResourceDetectionService = new ResourceUsageDetectionService(functionMetadataService),
                WorldStateDictator = worldStateDictator1,
                AccountContextService = accountContextService,
            };
            _dataGenerater = new TransactionDataGenerator(options);
        }

        public async Task MultiChainWithSingleContract()
        {
            int repeatTime = 1;
            _logger.Info("Runing  MultiChain with SingleContract!\n");
            byte[] contractCode = GetContractCode();
            Chains = new List<SideChain>()
            {
                new SideChain() {ChainId = Hash.Generate(), ContractCount = 1, ContractAddresses = new List<Hash>()},
                new SideChain() {ChainId = Hash.Generate(), ContractCount = 1, ContractAddresses = new List<Hash>()},
                new SideChain() {ChainId = Hash.Generate(), ContractCount = 1, ContractAddresses = new List<Hash>()},
                new SideChain() {ChainId = Hash.Generate(), ContractCount = 1, ContractAddresses = new List<Hash>()},
                new SideChain() {ChainId = Hash.Generate(), ContractCount = 1, ContractAddresses = new List<Hash>()}
            };

            _logger.Info("Prepare Chain and init contract");
            foreach (var sideChain in Chains)
            {
                await Prepare(contractCode, sideChain);
                await InitContract(sideChain, _dataGenerater.KeyList);
            }

            Dictionary<Hash, List<ITransaction>> jobs = new Dictionary<Hash, List<ITransaction>>();

            _logger.Info("Generate Transaction");
            List<int> txCounts = new List<int>() {1000,1000,1000,1000,1000};
            for (int i = 0; i <Chains.Count ; i++)
            {
                _logger.Info($"\tTransaction to Chain {Chains[i].ChainId} with {txCounts[i]}");
                var txs = _dataGenerater.GetTransactions(txCounts[i], Chains[i].ContractAddresses[0]);
                foreach (var tx in txs)
                {
                    tx.IncrementId = NewIncrementId();
                }
                jobs.Add(Chains[i].ChainId,txs);
            }

            _logger.Info($"Start Test with {repeatTime} times");
            long timeUsed = 0;
            for (int i = 0; i < repeatTime; i++)
            {
                _logger.Info($"\tround {i + 1} / {repeatTime} start");
                

                Stopwatch swExec = new Stopwatch();
                swExec.Start();
                var txResult = await Task.Factory.StartNew(async () =>
                {
                    return await _concurrencyExecutingService
                        .ExecuteAsync(jobs,
                            new Grouper(_servicePack.ResourceDetectionService, _logger));
                }).Unwrap();

                swExec.Stop();
                timeUsed += swExec.ElapsedMilliseconds;
                txResult.ForEach(trace =>
                {
                    if (!trace.StdErr.IsNullOrEmpty())
                    {
                        _logger.Error("Execution error: " + trace.StdErr);
                    }
                });

                Thread.Sleep(50); //sleep 50 ms to let async logger finish printing contents of previous round
                _logger.Info($"\tround {i + 1} / {repeatTime} ended, used time {swExec.ElapsedMilliseconds} ms");
            }

            var time = txCounts.Sum() / (timeUsed / 1000.0 / repeatTime);
            _logger.Info($"Result of All Chain:{time}");
        }

        public async Task SingleChainWithMultiContract()
        {
            int repeatTime = 10;
            _logger.Info("Runing  SingleChain with MultiContract!\n");
            byte[] contractCode = GetContractCode();
            Chains = new List<SideChain>()
            {
                new SideChain() {ChainId = Hash.Generate(), ContractCount = 5, ContractAddresses = new List<Hash>()}
            };

            _logger.Info("Prepare Chain and init contract");
            foreach (var sideChain in Chains)
            {
                await Prepare(contractCode, sideChain);
                await InitContract(sideChain, _dataGenerater.KeyList);
            }

            var chain = Chains[0];

            _logger.Info("Generate Transaction");
            List<int> txCounts = new List<int>() {1000,1000,1000,1000,1000};
            List<ITransaction> txList = new List<ITransaction>();
            for (int i = 0; i < chain.ContractCount; i++)
            {
                _logger.Info($"\tTransaction to Contract {i + 1} with {txCounts[i]}");
                var txs = _dataGenerater.GetTransactions(txCounts[i], chain.ContractAddresses[i]);
                txList.AddRange(txs);
            }

            _logger.Info($"Start Test with {repeatTime} times");
            long timeUsed = 0;
            for (int i = 0; i < repeatTime; i++)
            {
                _logger.Info($"\tround {i + 1} / {repeatTime} start");
                foreach (var tx in txList)
                {
                    tx.IncrementId = NewIncrementId();
                }

                Dictionary<Hash, List<ITransaction>> jobs = new Dictionary<Hash, List<ITransaction>>();
                jobs.Add(chain.ChainId, txList);

                Stopwatch swExec = new Stopwatch();
                swExec.Start();
                var txResult = await Task.Factory.StartNew(async () =>
                {
                    return await _concurrencyExecutingService
                        .ExecuteAsync(jobs,
                            new Grouper(_servicePack.ResourceDetectionService, _logger));
                }).Unwrap();

                swExec.Stop();
                timeUsed += swExec.ElapsedMilliseconds;
                txResult.ForEach(trace =>
                {
                    if (!trace.StdErr.IsNullOrEmpty())
                    {
                        _logger.Error("Execution error: " + trace.StdErr);
                    }
                });

                Thread.Sleep(50); //sleep 50 ms to let async logger finish printing contents of previous round
                _logger.Info($"\tround {i + 1} / {repeatTime} ended, used time {swExec.ElapsedMilliseconds} ms");
            }

            var time = txCounts.Sum() / (timeUsed / 1000.0 / repeatTime);
            _logger.Info($"Result of chain({chain.ChainId}):{time}");
        }

        private byte[] GetContractCode()
        {
            byte[] code;
            using (FileStream file = File.OpenRead(Path.GetFullPath(_options.DllDir + "/" + _options.ContractDll)))
            {
                code = file.ReadFully();
            }

            return code;
        }

        private List<Hash> GetTargetHashesForTransfer(List<ITransaction> transactions)
        {
            if (transactions.Count(a => a.MethodName != "Transfer") != 0)
            {
                throw new Exception("Missuse for function GetTargetHashesForTransfer with non transfer transactions");
            }

            HashSet<Hash> res = new HashSet<Hash>();
            foreach (var tx in transactions)
            {
                var parameters = ParamsPacker.Unpack(tx.Params.ToByteArray(),
                    new[] {typeof(Hash), typeof(Hash), typeof(ulong)});
                res.Add(parameters[1] as Hash);
                res.Add(tx.From);
            }

            return res.ToList();
        }

        private async Task<Dictionary<Hash, ulong>> ReadBalancesForAddrs(List<Hash> targets, Hash tokenContractAddr,
            Hash chainId)
        {
            Dictionary<Hash, ulong> res = new Dictionary<Hash, ulong>();
            foreach (var target in targets)
            {
                Transaction tx = new Transaction()
                {
                    From = target,
                    To = tokenContractAddr,
                    IncrementId = NewIncrementId(),
                    MethodName = "GetBalance",
                    Params = ByteString.CopyFrom(ParamsPacker.Pack(target)),
                };

                var txnCtxt = new TransactionContext()
                {
                    Transaction = tx
                };

                var executive = await _smartContractService.GetExecutiveAsync(tokenContractAddr, chainId);
                try
                {
                    await executive.SetTransactionContext(txnCtxt).Apply(true);
                }
                finally
                {
                    await _smartContractService.PutExecutiveAsync(tokenContractAddr, executive);
                }

                res.Add(target, txnCtxt.Trace.RetVal.Data.DeserializeToUInt64());
            }

            return res;
        }

        private ulong NewIncrementId()
        {
            var n = Interlocked.Increment(ref _incrementId);
            return (ulong) n;
        }

        private async Task<bool> Prepare(byte[] contractCode, SideChain sideChain)
        {
            _logger.Info($"Prepare in chain:{sideChain.ChainId}");
            //create smart contact zero
            var reg = new SmartContractRegistration
            {
                Category = 0,
                ContractBytes = ByteString.CopyFrom(SmartContractZeroCode),
                ContractHash = Hash.Zero
            };

            var chain = await _chainCreationService.CreateNewChainAsync(sideChain.ChainId, reg);
            await _blockManager.GetBlockAsync(chain.GenesisBlockHash);
            var contractAddressZero = _chainCreationService.GenesisContractHash(sideChain.ChainId);

            //deploy token contract
            for (int i = 0; i < sideChain.ContractCount; i++)
            {
                _logger.Info($"deploy {i + 1} contract in chain:{sideChain.ChainId}");
                var code = contractCode;
                var txnDep = new Transaction()
                {
                    From = Hash.Zero.ToAccount(),
                    To = contractAddressZero,
                    IncrementId = (ulong) i,
                    MethodName = "DeploySmartContract",
                    Params = ByteString.CopyFrom(ParamsPacker.Pack(0, code))
                };

                var transactionContext = new TransactionContext()
                {
                    Transaction = txnDep
                };

                var executive = await _smartContractService.GetExecutiveAsync(contractAddressZero, sideChain.ChainId);
                try
                {
                    await executive.SetTransactionContext(transactionContext).Apply(true);
                }
                finally
                {
                    await _smartContractService.PutExecutiveAsync(contractAddressZero, executive);
                }

                var contractAddress = transactionContext.Trace.RetVal.Data.DeserializeToPbMessage<Hash>();
                sideChain.ContractAddresses.Add(contractAddress);
            }

            return true;
        }

        private async Task<bool> InitContract(SideChain sideChain, IEnumerable<Hash> addressBook)
        {
            _logger.Info($"Init in chain:{sideChain.ChainId}");
            for (int i = 0; i < sideChain.ContractCount; i++)
            {
                _logger.Info($"init {i + 1} contract in chain:{sideChain.ChainId}");
                string name = "TestToken" + i;
                var contractAddress = sideChain.ContractAddresses[i];
                var txnInit = new Transaction
                {
                    From = Hash.Zero.ToAccount(),
                    To = contractAddress,
                    IncrementId = NewIncrementId(),
                    MethodName = "Initialize",
                    Params = ByteString.CopyFrom(ParamsPacker.Pack(name, Hash.Zero.ToAccount()))
                };

                var transactionContext = new TransactionContext()
                {
                    Transaction = txnInit
                };
                var executiveUser = await _smartContractService.GetExecutiveAsync(contractAddress, sideChain.ChainId);
                try
                {
                    await executiveUser.SetTransactionContext(transactionContext).Apply(true);
                }
                finally
                {
                    await _smartContractService.PutExecutiveAsync(contractAddress, executiveUser);
                }

                //init contract
                var initTxList = new List<ITransaction>();
                foreach (var addr in addressBook)
                {
                    var txnBalInit = new Transaction
                    {
                        From = addr,
                        To = contractAddress,
                        IncrementId = NewIncrementId(),
                        MethodName = "InitBalance",
                        Params = ByteString.CopyFrom(ParamsPacker.Pack(addr))
                    };

                    initTxList.Add(txnBalInit);
                }

                Dictionary<Hash, List<ITransaction>> jobs = new Dictionary<Hash, List<ITransaction>>();
                jobs.Add(sideChain.ChainId, initTxList);

                var txTrace = await _concurrencyExecutingService.ExecuteAsync(jobs,
                    new Grouper(_servicePack.ResourceDetectionService, _logger));
                foreach (var trace in txTrace)
                {
                    if (!trace.StdErr.IsNullOrEmpty())
                    {
                        _logger.Error("Execution Error: " + trace.StdErr);
                    }
                }

                _logger.Info($"init {i + 1} contract over!");
            }

            return true;
        }
    }
}