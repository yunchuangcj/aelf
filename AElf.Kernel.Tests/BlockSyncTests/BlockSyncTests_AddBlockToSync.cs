﻿using System;
using System.Threading.Tasks;
using AElf.Common.ByteArrayHelpers;
using AElf.Common.Collections;
using AElf.Kernel.Node.Protocol;
using Xunit;

namespace AElf.Kernel.Tests.BlockSyncTests
{
    public class BlockSyncTests_AddBlockToSync
    {

//        [Fact(Skip = "todo")]
//        public void Test()
//        {
//            byte[] byte01 = ByteArrayHelpers.RandomFill(10);
//            byte[] byte02 = new byte[10];
//            
//            byte[] byte03 = ByteArrayHelpers.RandomFill(10);
//            
//            Array.Copy(byte01, byte02, 10);
//            
//            SyncPeer s = new SyncPeer();
//            s.AlreadyRequested.Enqueue(byte01);
//            
//            Assert.True(s.AlreadyRequested.Contains(byte03));
//        }
        
        [Fact(Skip = "todo")]
        public void Test2()
        {
            BoundedByteArrayQueue bq = new BoundedByteArrayQueue(2);
            
            byte[] byte01 = ByteArrayHelpers.RandomFill(10);
            bq.Enqueue(byte01);
            
            byte[] byte02 = ByteArrayHelpers.RandomFill(10);
            bq.Enqueue(byte02);
            
            Assert.True(bq.Contains(byte01));
        }
        
        [Fact(Skip = "todo")]
        public async Task AddBlockToSync_NullBlock_ShouldThrow()
        {
            /*BlockSynchronizer s = new BlockSynchronizer(null, null);
            
            Exception ex = await Assert.ThrowsAsync<InvalidBlockException>(() => s.AddBlockToSync(null));
            Assert.Equal("The block, blockheader or body is null", ex.Message);
            
            Exception ex2 = await Assert.ThrowsAsync<InvalidBlockException>(() => s.AddBlockToSync(new Block()));
            Assert.Equal("The block, blockheader or body is null", ex2.Message);
            
            Exception ex3 = await Assert.ThrowsAsync<InvalidBlockException>(() => s.AddBlockToSync(new Block()));
            Assert.Equal("The block, blockheader or body is null", ex3.Message);*/
        }

        [Fact(Skip = "todo")]
        public async Task AddBlockToSync_NoTransactions_ShouldThrow()
        {
            /*
            BlockSynchronizer s = new BlockSynchronizer(null, null);
            
            Block b = new Block();
            b.Body = new BlockBody();
            b.Header = new BlockHeader();
            
            Exception ex = await Assert.ThrowsAsync<InvalidBlockException>(() => s.AddBlockToSync(b));
            Assert.Equal("The block contains no transactions", ex.Message);
            */
        }

        [Fact(Skip = "todo")]
        public async Task AddBlockToSync_NoHash_ShouldThrow()
        {
            /*BlockSynchronizer s = new BlockSynchronizer(null, null);
            
            Block b = new Block();
            b.Body = new BlockBody();
            b.Header = new BlockHeader();
            b.AddTransaction(new Hash());
            
            Exception ex = await Assert.ThrowsAsync<InvalidBlockException>(() => s.AddBlockToSync(b));
            Assert.Equal("Invalid block hash", ex.Message);*/
        }
        
        [Fact(Skip = "todo")]
        public async Task AddBlockToSync_BlockHeightLowerThanCurrent_ReturnsFalse()
        {
            /*BlockSynchronizer s = new BlockSynchronizer(null, null);
            s.SetNodeHeight(2);

            Block b = BlockSyncHelpers.GenerateValidBlockToSync(1);
            b.AddTransaction(new Hash());

            bool res = await s.AddBlockToSync(b);
            
            Assert.False(res);*/
        }

        [Fact(Skip = "todo")]
        public async Task AddBlockToSync_TxMissing_ShouldPutBlockToSync()
        {
            /*var missingTxHash = ByteArrayHelpers.RandomFill(256);
            var returnTxHashes = new List<Hash> { new Hash(missingTxHash) };
            
            Mock<IAElfNode> mock = new Mock<IAElfNode>();
            mock.Setup(n => n.GetMissingTransactions(It.IsAny<IBlock>())).Returns(returnTxHashes);
            
            IAElfNode m = mock.Object;
            
            BlockSynchronizer s = new BlockSynchronizer(m, null);

            Block b = BlockSyncHelpers.GenerateValidBlockToSync();
            b.AddTransaction(Hash.Generate());
            
            await s.AddBlockToSync(b);

            byte[] array = b.GetHash().GetHashBytes();
            PendingBlock p = s.GetBlock(array);
            
            Assert.Equal(p.BlockHash, array);

            byte[] missingTx = p.MissingTxs.FirstOrDefault();
            Assert.True(missingTx.BytesEqual(missingTxHash));*/
        }

        [Fact(Skip = "todo")]
        public async Task AddBlockToSync_AlreadyInPool_ShouldPutBlockToSyncIfOrphan()
        {
            /*Mock<IAElfNode> mock = new Mock<IAElfNode>();
            
            // Setup no missing transactions
            mock.Setup(n => n.GetMissingTransactions(It.IsAny<IBlock>())).Returns(new List<Hash>());
            
            // Setup return oprhan block validation error
            BlockExecutionResult res = new BlockExecutionResult(false, TxInsertionAndBroadcastingError.OrphanBlock);
            mock.Setup(n => n.ExecuteAndAddBlock(It.IsAny<IBlock>())).Returns(Task.FromResult(res));
            
            IAElfNode m = mock.Object;
            
            Block b = BlockSyncHelpers.GenerateValidBlockToSync();
            b.AddTransaction(Hash.Generate());
            
            BlockSynchronizer s = new BlockSynchronizer(m, null);
            await s.AddBlockToSync(b);
            
            byte[] array = b.GetHash().GetHashBytes();
            PendingBlock p = s.GetBlock(array);
            
            Assert.Equal(p.BlockHash, array);
            Assert.Equal(p.IsWaitingForPrevious, true);*/
        }
    }
}