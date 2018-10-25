﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Akka.Actor;
using ServiceStack.Text;
using AElf.Kernel;
using AElf.Configuration;
using AElf.ChainController.Execution;
using AElf.SmartContract;

namespace AElf.Execution
{
    public class ParallelTransactionExecutingService : IParallelTransactionExecutingService
    {
        private readonly IGrouper _grouper;
        private readonly IActorRef _requestor;

        // TODO: Move it to config
        public int TimeoutMilliSeconds { get; set; } = int.MaxValue;

        public ParallelTransactionExecutingService(IActorRef requestor, IGrouper grouper)
        {
            _requestor = requestor;
            _grouper = grouper;
        }

        public async Task<List<TransactionTrace>> ExecuteAsync(
            Dictionary<Hash, List<ITransaction>> transactionsWithChainId)
        {
            using (var cts = new CancellationTokenSource())
            using (new Timer(
                CancelExecutions, cts, TimeSpan.FromMilliseconds(TimeoutMilliSeconds),
                TimeSpan.FromMilliseconds(-1)
            ))
            {
                cts.CancelAfter(TimeoutMilliSeconds);
                var token = cts.Token;

                var tasks = transactionsWithChainId.Select(
                    item => Task.Run(() =>
                    {
                        
                        var result = AttemptToSendExecutionRequest(item.Key, item.Value, token);
                        return result;
                    }, token)
                ).ToArray();
                var results = (await Task.WhenAll(tasks)).SelectMany(x => x).ToList();

                return results;
            }
        }

        public async Task<List<TransactionTrace>> ExecuteAsync(List<ITransaction> transactions, Hash chainId)
        {
            using (var cts = new CancellationTokenSource())
            using (new Timer(
                CancelExecutions, cts, TimeSpan.FromMilliseconds(TimeoutMilliSeconds),
                TimeSpan.FromMilliseconds(-1)
            ))
            {
                cts.CancelAfter(TimeoutMilliSeconds);
                var token = cts.Token;

                List<List<ITransaction>> groups;
                Dictionary<ITransaction, Exception> failedTxs;

                //disable parallel module by default because it doesn't finish yet (don't support contract call)
                if (ParallelConfig.Instance.IsParallelEnable)
                {
                    var groupRes = await _grouper.ProcessWithCoreCount(GroupStrategy.Limited_MaxAddMins,
                        ActorConfig.Instance.ConcurrencyLevel, chainId, transactions);
                    groups = groupRes.Item1;
                    failedTxs = groupRes.Item2;
                }
                else
                {
                    groups = new List<List<ITransaction>>() {transactions};
                    failedTxs = new Dictionary<ITransaction, Exception>();
                }


                var tasks = groups.Select(
                    txs => Task.Run(() => AttemptToSendExecutionRequest(chainId, txs, token), token)
                ).ToArray();
                var results = (await Task.WhenAll(tasks)).SelectMany(x => x).ToList();

                foreach (var failed in failedTxs)
                {
                    var failedTrace = new TransactionTrace
                    {
                        StdErr = "Transaction with ID/hash " + failed.Key.GetHash().ToHex() +
                                 " failed, detail message: \n" + failed.Value.Dump(),
                        TransactionId = failed.Key.GetHash()
                    };
                    results.Add(failedTrace);
                }

                return results;
            }
        }

        private async Task<List<TransactionTrace>> AttemptToSendExecutionRequest(Hash chainId,
            List<ITransaction> transactions, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                Stopwatch swExec = new Stopwatch();
                swExec.Start();
                var tcs = new TaskCompletionSource<List<TransactionTrace>>();
                _requestor.Tell(new LocalExecuteTransactionsMessage(chainId, transactions, tcs));
                var traces = await tcs.Task;

                
                swExec.Stop();
                var timeUsed = swExec.ElapsedMilliseconds;
                Console.WriteLine("-----------------" + timeUsed);
                if (traces.Count > 0)
                {
                    return traces;
                }

                Thread.Sleep(1);
            }

            // Cancelled
            return transactions.Select(tx => new TransactionTrace()
            {
                TransactionId = tx.GetHash(),
                StdErr = "Execution Cancelled"
            }).ToList();
        }

        private void CancelExecutions(object stateInfo)
        {
            _requestor.Tell(JobExecutionCancelMessage.Instance);
        }
    }
}