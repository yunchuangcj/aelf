using System.Collections.Generic;
using AElf.Kernel;

namespace AElf.Benchmark
{
    public class SideChain
    {
        public Hash ChainId { get; set; }
        public List<Hash> ContractAddresses { get; set; }
        public int ContractCount { get; set; }
    }
}