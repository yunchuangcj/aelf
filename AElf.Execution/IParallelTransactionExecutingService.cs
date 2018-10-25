﻿using System.Collections.Generic;
using System.Threading.Tasks;
using AElf.Kernel;
using AElf.SmartContract;

namespace AElf.Execution
{
    public interface IParallelTransactionExecutingService
    {
        int TimeoutMilliSeconds { get; set; }
        Task<List<TransactionTrace>> ExecuteAsync(Dictionary<Hash, List<ITransaction>> transactionsWithChainId);
        Task<List<TransactionTrace>> ExecuteAsync(List<ITransaction> transactions, Hash chainId);
    }
}