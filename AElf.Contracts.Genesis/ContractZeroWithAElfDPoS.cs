﻿using System.Collections.Generic;
using System.Threading.Tasks;
using AElf.Kernel.Consensus;
using AElf.Contracts.Genesis.ConsensusContract;
using AElf.Kernel;
using AElf.Sdk.CSharp.Types;
using Google.Protobuf.WellKnownTypes;

// ReSharper disable UnusedMember.Global
// ReSharper disable InconsistentNaming
namespace AElf.Contracts.Genesis
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class ContractZeroWithAElfDPoS : BasicContractZero
    {
        private readonly IConsensus _consensus = new AElfDPoS(new AElfDPoSFiledMapCollection
        {
            CurrentRoundNumberField = new UInt64Field(Globals.AElfDPoSCurrentRoundNumber),
            BlockProducerField = new PbField<BlockProducer>(Globals.AElfDPoSBlockProducerString),
            DPoSInfoMap = new Map<UInt64Value, RoundInfo>(Globals.AElfDPoSInformationString),
            EBPMap = new Map<UInt64Value, StringValue>(Globals.AElfDPoSExtraBlockProducerString),
            TimeForProducingExtraBlockField = new PbField<Timestamp>(Globals.AElfDPoSExtraBlockTimeslotString),
            FirstPlaceMap = new Map<UInt64Value, StringValue>(Globals.AElfDPoSFirstPlaceOfEachRoundString),
            MiningIntervalField = new PbField<Int32Value>(Globals.AElfDPoSMiningIntervalString)
        });
        
        public async Task InitializeAElfDPoS(byte[] blockProducer, byte[] dPoSInfo, byte[] miningInterval)
        {
            await _consensus.Initialize(new List<byte[]> {blockProducer, dPoSInfo, miningInterval});
        }

        public async Task UpdateAElfDPoS(byte[] currentRoundInfo, byte[] nextRoundInfo, byte[] nextExtraBlockProducer)
        {
            await _consensus.Update(new List<byte[]>
            {
                currentRoundInfo,
                nextRoundInfo,
                nextExtraBlockProducer
            });
        }

        public async Task<BoolValue> Validation(byte[] accountAddress, byte[] timestamp)
        {
            return new BoolValue
            {
                Value = await _consensus.Validation(new List<byte[]> {accountAddress, timestamp})
            };
        }

        public async Task PublishOutValueAndSignature(byte[] roundNumber, byte[] accountAddress, byte[] outValue, byte[] signature)
        {
            await _consensus.Publish(new List<byte[]>
            {
                roundNumber,
                accountAddress,
                outValue,
                signature
            });
        }
        
        public async Task PublishInValue(byte[] roundNumber, byte[] accountAddress, byte[] inValue)
        {
            await _consensus.Publish(new List<byte[]>
            {
                roundNumber,
                accountAddress,
                inValue
            });
        }
    }
}