﻿/*
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

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [TestFixture]
    public class ReceiptsMessageSerializerTests
    {
        private static void Test(TransactionReceipt[][] receipts)
        {
            ReceiptsMessage message = new ReceiptsMessage(receipts);
            ReceiptsMessageSerializer serializer = new ReceiptsMessageSerializer();
            var serialized = serializer.Serialize(message);
            ReceiptsMessage deserialized = serializer.Deserialize(serialized);

            if (receipts == null)
            {
                Assert.AreEqual(0, deserialized.Receipts.Length);
            }
            else
            {
                Assert.AreEqual(receipts.Length, deserialized.Receipts.Length, "length");
                for (int i = 0; i < receipts.Length; i++)
                {
                    if (receipts[i] == null)
                    {
                        Assert.IsNull(deserialized.Receipts[i], $"receipts[{i}]");
                    }
                    else
                    {
                        for (int j = 0; j < receipts[i].Length; j++)
                        {
                            if (receipts[i][j] == null)
                            {
                                Assert.IsNull(deserialized.Receipts[i][j], $"receipts[{i}][{j}]");
                            }
                            else
                            {
                                Assert.AreEqual(receipts[i][j].Bloom, deserialized.Receipts[i][j].Bloom, $"receipts[{i}][{j}].Bloom");
                                Assert.Null(deserialized.Receipts[i][j].Error, $"receipts[{i}][{j}].Error");
                                Assert.AreEqual(0, deserialized.Receipts[i][j].Index, $"receipts[{i}][{j}].Index");
                                Assert.AreEqual(receipts[i][j].Logs.Length, deserialized.Receipts[i][j].Logs.Length, $"receipts[{i}][{j}].Logs.Length");
                                Assert.Null(deserialized.Receipts[i][j].Recipient, $"receipts[{i}][{j}].Recipient");
                                Assert.Null(deserialized.Receipts[i][j].Sender, $"receipts[{i}][{j}].Sender");
                                Assert.Null(deserialized.Receipts[i][j].BlockHash, $"receipts[{i}][{j}].BlockHash");
                                Assert.AreEqual(UInt256.Zero, deserialized.Receipts[i][j].BlockNumber, $"receipts[{i}][{j}].BlockNumber");
                                Assert.Null(deserialized.Receipts[i][j].ContractAddress, $"receipts[{i}][{j}].ContractAddress");
                                Assert.AreEqual(0L, deserialized.Receipts[i][j].GasUsed, $"receipts[{i}][{j}].GasUsed");
                                Assert.AreEqual(receipts[i][j].StatusCode, deserialized.Receipts[i][j].StatusCode, $"receipts[{i}][{j}].StatusCode");
                                Assert.AreEqual(receipts[i][j].GasUsedTotal, deserialized.Receipts[i][j].GasUsedTotal, $"receipts[{i}][{j}].GasUsedTotal");
                                Assert.AreEqual(receipts[i][j].PostTransactionState, deserialized.Receipts[i][j].PostTransactionState, $"receipts[{i}][{j}].PostTransactionState");
                            }
                        }
                    }
                }
            }
        }

        [Test]
        public void Roundtrip()
        {            
            TransactionReceipt[][] data = {new[] {Build.A.TransactionReceipt.WithAllFieldsFilled.TestObject, Build.A.TransactionReceipt.WithAllFieldsFilled.TestObject}, new[] {Build.A.TransactionReceipt.WithAllFieldsFilled.TestObject, Build.A.TransactionReceipt.WithAllFieldsFilled.TestObject}};
            Test(data);
        }

        [Test]
        public void Roundtrip_with_null_top_level()
        {
            Test(null);
        }

        [Test]
        public void Roundtrip_with_nulls()
        {
            TransactionReceipt[][] data = {new[] {Build.A.TransactionReceipt.WithAllFieldsFilled.TestObject, Build.A.TransactionReceipt.WithAllFieldsFilled.TestObject}, null, new[] {null, Build.A.TransactionReceipt.WithAllFieldsFilled.TestObject}};
            Test(data);
        }
        
        [Test]
        public void Roundtrip_mainnet_sample()
        {
            byte[] bytes = Bytes.FromHexString("f9012ef9012bf90128a08ccc6709a5df7acef07f97c5681356b6c37cfac15b554aff68e986f57116df2e825208b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0");
            ReceiptsMessageSerializer serializer = new ReceiptsMessageSerializer();
            ReceiptsMessage message = serializer.Deserialize(bytes);
            byte[] serialized = serializer.Serialize(message);
            Assert.AreEqual(bytes,  serialized);
        }   
    }
}