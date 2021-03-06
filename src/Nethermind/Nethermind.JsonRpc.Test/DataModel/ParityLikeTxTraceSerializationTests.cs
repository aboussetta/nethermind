/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using NUnit.Framework;
using Block = Nethermind.Core.Block;

namespace Nethermind.JsonRpc.Test.DataModel
{
    [TestFixture]
    public class ParityLikeTxTraceSerializationTests : SerializationTestBase
    {
        [Test]
        public void Can_serialize()
        {
            ParityTraceAction subtrace = new ParityTraceAction();
            subtrace.Value = 67890;
            subtrace.CallType = "call";
            subtrace.From = TestObject.AddressC;
            subtrace.To = TestObject.AddressD;
            subtrace.Input = Bytes.Empty;
            subtrace.Gas = 10000;
            subtrace.TraceAddress = new int[] {0, 0};

            ParityLikeTxTrace result = new ParityLikeTxTrace();
            result.Action = new ParityTraceAction();
            result.Action.Value = 12345;
            result.Action.CallType = "init";
            result.Action.From = TestObject.AddressA;
            result.Action.To = TestObject.AddressB;
            result.Action.Input = new byte[] {1, 2, 3, 4, 5, 6};
            result.Action.Gas = 40000;
            result.Action.TraceAddress = new int[] {0};
            result.Action.Subtraces.Add(subtrace);

            result.BlockHash = TestObject.KeccakB;
            result.BlockNumber = 123456;
            result.TransactionHash = TestObject.KeccakC;
            result.TransactionPosition = 5;
            result.Action.TraceAddress = new int[] {1, 2, 3};

            ParityAccountStateChange stateChange = new ParityAccountStateChange();
            stateChange.Balance = new ParityStateChange<UInt256>(1, 2);
            stateChange.Nonce = new ParityStateChange<UInt256>(0, 1);
            stateChange.Storage = new Dictionary<UInt256, ParityStateChange<byte[]>>();
            stateChange.Storage[1] = new ParityStateChange<byte[]>(new byte[] {1}, new byte[] {2});

            result.StateChanges = new Dictionary<Address, ParityAccountStateChange>();
            result.StateChanges.Add(TestObject.AddressC, stateChange);

            TestOneWaySerialization(result, "{\"trace\":[{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":40000,\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"blockHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"blockNumber\":\"0x1e240\",\"result\":{\"gasUsed\":0,\"output\":null},\"subtraces\":1,\"traceAddress\":\"[1, 2, 3]\",\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"transactionPosition\":5,\"type\":\"init\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":10000,\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"blockHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"blockNumber\":\"0x1e240\",\"result\":{\"gasUsed\":0,\"output\":null},\"subtraces\":0,\"traceAddress\":\"[0, 0]\",\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"transactionPosition\":5,\"type\":\"call\"}],\"stateDiff\":{\"0x76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":\"=\",\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}}}");
        }

        [Test]
        [Todo(Improve.Refactor, "Different action serializers")]
        public void Can_serialize_reward()
        {
            Block block = Build.A.Block.WithNumber(UInt256.Parse("4563918244f40000".AsSpan(), NumberStyles.AllowHexSpecifier)).TestObject;
            IBlockTracer blockTracer = new ParityLikeBlockTracer(ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            blockTracer.StartNewBlockTrace(block);
            ITxTracer txTracer = blockTracer.StartNewTxTrace(null);
            txTracer.ReportBalanceChange(TestObject.AddressA, 0, 3.Ether());
            blockTracer.EndTxTrace();
            blockTracer.ReportReward(TestObject.AddressA, "block", UInt256.One);

            ParityLikeTxTrace trace = ((ParityLikeBlockTracer)blockTracer).BuildResult().SingleOrDefault();
            
            TestOneWaySerialization(trace, "{\"trace\":[{\"action\":{\"callType\":\"reward\",\"from\":null,\"gas\":0,\"input\":null,\"to\":null,\"value\":\"0x1\"},\"blockHash\":\"0xfed4f714d3626e046786ef043cbb30a4b87cdc288469d0b70e4529bbd4e15396\",\"blockNumber\":\"0x4563918244f40000\",\"result\":{\"gasUsed\":0,\"output\":null},\"subtraces\":0,\"traceAddress\":\"[]\",\"transactionHash\":null,\"transactionPosition\":null,\"type\":\"reward\"}],\"stateDiff\":{\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\":{\"balance\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x29a2241af62c0000\"}},\"code\":\"=\",\"nonce\":\"=\",\"storage\":\"=\"}}}");
        }
        
        [Test]
        public void Can_serialize_reward_state_only()
        {
            Block block = Build.A.Block.WithNumber(UInt256.Parse("4563918244f40000".AsSpan(), NumberStyles.AllowHexSpecifier)).TestObject;
            IBlockTracer blockTracer = new ParityLikeBlockTracer(ParityTraceTypes.StateDiff);
            blockTracer.StartNewBlockTrace(block);
            ITxTracer txTracer = blockTracer.StartNewTxTrace(null);
            txTracer.ReportBalanceChange(TestObject.AddressA, 0, 3.Ether());
            blockTracer.EndTxTrace();
            blockTracer.ReportReward(TestObject.AddressA, "block", UInt256.One);

            ParityLikeTxTrace trace = ((ParityLikeBlockTracer)blockTracer).BuildResult().SingleOrDefault();
            
            TestOneWaySerialization(trace, "{\"trace\":null,\"stateDiff\":{\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\":{\"balance\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x29a2241af62c0000\"}},\"code\":\"=\",\"nonce\":\"=\",\"storage\":\"=\"}}}");
        }
    }
}