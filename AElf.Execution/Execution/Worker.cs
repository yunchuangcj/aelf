﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using AElf.Kernel;
using AElf.SmartContract;
using Akka.Dispatch;

/*
    Todo: #338
    There are stability issues about TrackedRouter, so we use the Akka default router temporarily.
    Some of the code is annotated, marked with "todo" and optimized later.
 */

namespace AElf.Execution
{
    /// <summary>
    /// A worker that runs a list of transactions sequentially.
    /// </summary>
    public class Worker : UntypedActor
    {
        public enum State
        {
            PendingSetSericePack,
            Idle,
            Running,
            Suspended // TODO: Support suspend
        }

        private State _state = State.PendingSetSericePack;
        private long _servingRequestId = -1;
        private ServicePack _servicePack;

        // TODO: Add cancellation
        private CancellationTokenSource _cancellationTokenSource;

        protected override void OnReceive(object message)
        {
            switch (message)
            {
                case LocalSerivcePack res:
                    if (_state == State.PendingSetSericePack)
                    {
                        _servicePack = res.ServicePack;
                        _state = State.Idle;
                    }

                    break;
                case JobExecutionRequest req:
                    if (_state == State.Idle)
                    {
                        _cancellationTokenSource?.Dispose();
                        _cancellationTokenSource = new CancellationTokenSource();

                        RunJob(req).ContinueWith(
                            task => task.Result,
                            TaskContinuationOptions.AttachedToParent & TaskContinuationOptions.ExecuteSynchronously
                        );
/*
 Temporarily disabled.
 TODO: https://github.com/AElfProject/AElf/issues/338
                        Sender.Tell(new JobExecutionStatus(req.RequestId, JobExecutionStatus.RequestStatus.Running));
*/
                    }
/*
 Temporarily disabled.
 TODO: https://github.com/AElfProject/AElf/issues/338
                    else if (_state == State.PendingSetSericePack)
                    {
                        Sender.Tell(new JobExecutionStatus(req.RequestId,
                            JobExecutionStatus.RequestStatus.FailedDueToWorkerNotReady));
                    }
                    else
                    {
                        Sender.Tell(new JobExecutionStatus(req.RequestId, JobExecutionStatus.RequestStatus.Rejected));
                    }
*/
                    break;
/*
 Temporarily disabled.
 TODO: https://github.com/AElfProject/AElf/issues/338
                case JobExecutionCancelMessage c:
                    _cancellationTokenSource?.Cancel();
                    Sender.Tell(JobExecutionCancelAckMessage.Instance);
                    break;

                case JobExecutionStatusQuery query:
                    if (query.RequestId != _servingRequestId)
                    {
                        Sender.Tell(new JobExecutionStatus(query.RequestId,
                            JobExecutionStatus.RequestStatus.InvalidRequestId));
                    }
                    else
                    {
                        Sender.Tell(new JobExecutionStatus(query.RequestId, JobExecutionStatus.RequestStatus.Running));
                    }
                    break;
*/
            }
        }

        private async Task<JobExecutionStatus> RunJob(JobExecutionRequest request)
        {
/*
 Temporarily disabled.
 TODO: https://github.com/AElfProject/AElf/issues/338
            _state = State.Running;
*/

            IChainContext chainContext = null;

            Exception chainContextException = null;
            
            //path <-> value
            Dictionary<Hash, StateCache> stateCache = new Dictionary<Hash, StateCache>();
            
            try
            {
                chainContext = await _servicePack.ChainContextService.GetChainContextAsync(request.ChainId);
            }
            catch (Exception e)
            {
                chainContextException = e;
            }

            var result = new List<TransactionTrace>(request.Transactions.Count);
            foreach (var tx in request.Transactions)
            {
                TransactionTrace trace;

                if (chainContextException != null)
                {
                    trace = new TransactionTrace()
                    {
                        TransactionId = tx.GetHash(),
                        StdErr = chainContextException + "\n"
                    };
                }
                else if (_cancellationTokenSource.IsCancellationRequested)
                {
                    trace = new TransactionTrace()
                    {
                        TransactionId = tx.GetHash(),
                        StdErr = "Execution Cancelled"
                    };
                }
                else
                {
                    if (chainContext == null)
                    {
                        trace = new TransactionTrace()
                        {
                            TransactionId = tx.GetHash(),
                            StdErr = "Invalid chain"
                        };
                    }
                    else
                    {
                        // TODO: The job is still running but we will leave it, we need a way to abort the job if it runs for too long
                        var task = Task.Run(async () => await ExecuteTransaction(chainContext, tx, stateCache),
                            _cancellationTokenSource.Token);
                        try
                        {
                            task.Wait(_cancellationTokenSource.Token);
                            trace = await task;
                            if (trace.IsSuccessful())
                            {
                                //commit update results to state cache
                                var bufferedStateUpdates = await trace.CommitChangesAsync(_servicePack.WorldStateDictator, chainContext.ChainId);
                                foreach (var kv in bufferedStateUpdates)
                                {
                                    stateCache[kv.Key] = kv.Value;
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            trace = new TransactionTrace()
                            {
                                TransactionId = tx.GetHash(),
                                StdErr = "Execution Cancelled"
                            };
                        }
                        catch (Exception e)
                        {
                            trace = new TransactionTrace()
                            {
                                TransactionId = tx.GetHash(),
                                StdErr = e + "\n"
                            };
                        }
                    }
                }
                result.Add(trace);
            }
            request.ResultCollector?.Tell(new TransactionTraceMessage(request.RequestId, result));


            if (chainContext != null)
            {
                await _servicePack.WorldStateDictator.ApplyCachedDataAction(stateCache, chainContext.ChainId);
            }
            stateCache.Clear();

            // TODO: What if actor died in the middle

            var retMsg = new JobExecutionStatus(request.RequestId, JobExecutionStatus.RequestStatus.Completed);
            // TODO: tell requestor and router about the worker complete job,and set to idle state.
/*
 Temporarily disabled.
 TODO: https://github.com/AElfProject/AElf/issues/338
            request.ResultCollector?.Tell(retMsg);
            request.Router?.Tell(retMsg);
*/
            _servingRequestId = -1;
/*
 Temporarily disabled.
 TODO: https://github.com/AElfProject/AElf/issues/338
            _state = State.Idle;
*/
            return retMsg;
        }

        private async Task<TransactionTrace> ExecuteTransaction(IChainContext chainContext, ITransaction transaction, Dictionary<Hash, StateCache> stateCache)
        {
            
            var trace = new TransactionTrace()
            {
                TransactionId = transaction.GetHash()
            };


            var txCtxt = new TransactionContext()
            {
                PreviousBlockHash = chainContext.BlockHash,
                Transaction = transaction,
                Trace = trace
            };

            IExecutive executive = null;

            try
            {
                executive = await _servicePack.SmartContractService
                    .GetExecutiveAsync(transaction.To, chainContext.ChainId);

                executive.SetDataCache(stateCache);

                await executive.SetTransactionContext(txCtxt).Apply(false);
                trace.Logs.AddRange(txCtxt.Trace.FlattenedLogs);
                // TODO: Check run results / logs etc.
            }
            catch (Exception ex)
            {
                // TODO: Improve log
                txCtxt.Trace.StdErr += ex + "\n";
            }
            finally
            {
                if (executive != null)
                {
                    await _servicePack.SmartContractService.PutExecutiveAsync(transaction.To, executive);
                }
            }

            return trace;
        }
    }
}