using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AElf.Common.ByteArrayHelpers;
using AElf.Kernel;
using AElf.Types.CSharp;
using Akka.Util.Internal.Collections;
using Google.Protobuf;
using Org.BouncyCastle.Security;
using ServiceStack;

namespace AElf.Benchmark
{
    public class TransactionDataGenerator
    {
        public List<Hash> KeyList;
        private int _accountCount;
        private int _maxTxNumber;

        public TransactionDataGenerator(BenchmarkOptions opts)
        {
            _maxTxNumber = 4000;
            _accountCount = 5000;
            GenerateHashes(opts);
        }

        private void GenerateHashes(BenchmarkOptions opts)
        {
            Console.WriteLine($"Generate {_accountCount} Account");
            KeyList = new List<Hash>();
            for (int i = 0; i < _accountCount; i++)
            {
                KeyList.Add(Hash.Generate().ToAccount());
            }
        }

        private IEnumerable<KeyValuePair<Hash, Hash>> GenerateTransferAddressPair(int txCount)
        {
            if (txCount > _maxTxNumber) throw new InvalidParameterException();
            var txAccountList = new List<KeyValuePair<Hash, Hash>>();
            Random numRandom = new Random();

            for (int i = 0; i < txCount; i++)
            {
                var senderKp = KeyList[i];
                var receiverKp = KeyList[i+1];
                txAccountList.Add(new KeyValuePair<Hash, Hash>(senderKp, receiverKp));
            }

            return txAccountList;
        }


        public List<ITransaction> GetTransactions(int txNumber, Hash contractAddress)
        {
            var addressPairs = GenerateTransferAddressPair(txNumber);
            return GenerateTransferTransactions(contractAddress, addressPairs);
        }


        public List<ITransaction> GenerateTransferTransactions(Hash tokenContractAddr,
            IEnumerable<KeyValuePair<Hash, Hash>> transferAddressPairs)
        {
            var resList = new List<ITransaction>();
            foreach (var addressPair in transferAddressPairs)
            {
                Transaction tx = new Transaction()
                {
                    From = addressPair.Key,
                    To = tokenContractAddr,
                    IncrementId = 0,
                    MethodName = "Transfer",
                    Params = ByteString.CopyFrom(ParamsPacker.Pack(addressPair.Key, addressPair.Value, (ulong) 1)),
                };

                resList.Add(tx);
            }

            return resList;
        }
    }
}