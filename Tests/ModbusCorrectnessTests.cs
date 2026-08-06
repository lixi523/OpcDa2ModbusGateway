using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpcDaToModbusGateway.Models;
using OpcDaToModbusGateway.Services;
using OpcDaToModbusGateway.Services.Interfaces;

namespace OpcDaToModbusGateway.Tests
{
    [TestClass]
    public class ModbusCorrectnessTests
    {
        [DataTestMethod]
        [DataRow("Bool", 1)]
        [DataRow("Byte", 1)]
        [DataRow("SByte", 1)]
        [DataRow("Int16", 1)]
        [DataRow("UInt16", 1)]
        [DataRow("Int32", 2)]
        [DataRow("UInt32", 2)]
        [DataRow("Float", 2)]
        [DataRow("Single", 2)]
        [DataRow("Double", 4)]
        public void RegisterWidth_IsDefinedByDeclaredType(string type, int expected)
        {
            Assert.AreEqual(expected, DataTypeConverter.GetModbusRegisterWidth(type));
        }

        [TestMethod]
        public void ConvertValue_InvalidOrOverflow_ThrowsInsteadOfWritingZero()
        {
            Assert.ThrowsException<FormatException>(() => DataTypeConverter.ConvertValue("not-a-number", DataTypeId.Int16));
            Assert.ThrowsException<OverflowException>(() => DataTypeConverter.ConvertValue("70000", DataTypeId.UInt16));
        }

        [TestMethod]
        public void RegisterEncoding_UsesHighWordFirst()
        {
            CollectionAssert.AreEqual(new ushort[] { 0xFFFF }, DataTypeConverter.EncodeModbusRegisters(-1, "Int16"));
            CollectionAssert.AreEqual(new ushort[] { 0xFFFF }, DataTypeConverter.EncodeModbusRegisters(ushort.MaxValue, "UInt16"));
            CollectionAssert.AreEqual(new ushort[] { 0x1122, 0x3344 }, DataTypeConverter.EncodeModbusRegisters(0x11223344, "Int32"));
            CollectionAssert.AreEqual(new ushort[] { 0xFFFF, 0xFFFF }, DataTypeConverter.EncodeModbusRegisters(uint.MaxValue, "UInt32"));
            CollectionAssert.AreEqual(new ushort[] { 0x3F80, 0x0000 }, DataTypeConverter.EncodeModbusRegisters(1.0f, "Float"));
            CollectionAssert.AreEqual(new ushort[] { 0x3FF0, 0, 0, 0 }, DataTypeConverter.EncodeModbusRegisters(1.0d, "Double"));
        }

        [TestMethod]
        public void Single_IsConvertedAsFloat()
        {
            Assert.AreEqual(DataTypeId.Float, DataTypeConverter.ParseDataType("Single"));
            Assert.AreEqual(1.25f, DataTypeConverter.ConvertValue("1.25", DataTypeId.Float));
        }

        [TestMethod]
        public void DataBridge_PassesEffectiveDeclaredTypeToServer()
        {
            var client = new FakeOpcDaClient();
            var server = new RecordingModbusServer();
            var tags = new List<TagConfig>
            {
                new TagConfig
                {
                    TagKey = "tag-1",
                    ItemId = "Item.1",
                    DataType = "Int16",
                    ModbusDataType = "Double",
                    ModbusRegisterType = "HoldingRegister"
                }
            };

            using (var bridge = new DataBridge(client, server, tags))
            {
                bridge.Start();
                bridge.Start();
            }

            Assert.AreEqual(1, server.AddCount);
            Assert.AreEqual("Double", server.LastModbusDataType);
        }

        [TestMethod]
        public void FakeClient_RejectsOperationsAfterDispose()
        {
            var client = new FakeOpcDaClient();
            client.Dispose();
            Assert.ThrowsException<ObjectDisposedException>(() => client.Start(1000, DaAcquisitionMode.Async));
            Assert.ThrowsException<ObjectDisposedException>(() => client.TryReconnect(1000));
            Assert.ThrowsException<ObjectDisposedException>(() => client.RaiseDataChanged("tag", 1));
        }

        [TestMethod]
        public void FakeClient_AllowsRestartAfterStop()
        {
            var client = new FakeOpcDaClient();
            client.Start(1000, DaAcquisitionMode.Async);
            client.Stop();
            client.Start(1000, DaAcquisitionMode.Async);
            Assert.AreEqual(2, client.StartCount);
            Assert.IsTrue(client.IsConnected);
        }

        [TestMethod]
        public void UnsupportedWireTypes_AreRejected()
        {
            StringAssert.Contains(Assert.ThrowsException<NotSupportedException>(() => DataTypeConverter.GetModbusRegisterWidth("String")).Message, "String");
            StringAssert.Contains(Assert.ThrowsException<NotSupportedException>(() => DataTypeConverter.GetModbusRegisterWidth("DateTime")).Message, "DateTime");
            StringAssert.Contains(Assert.ThrowsException<NotSupportedException>(() => DataTypeConverter.GetModbusDataType("Variant")).Message, "Variant");
        }

        [TestMethod]
        public void TagWidth_BitSpaceIsOne_RegisterSpaceUsesDeclaredWidth()
        {
            var tag = new TagConfig { ModbusRegisterType = "HoldingRegister", ModbusDataType = "Double" };
            Assert.AreEqual(4, tag.GetEffectiveAddressWidth());
            tag.ModbusRegisterType = "Coil";
            Assert.AreEqual(1, tag.GetEffectiveAddressWidth());
        }

        [TestMethod]
        public void UnresolvableType_IsPendingNotRejected()
        {
            var variant = new TagConfig { DataType = "Variant" };
            Assert.IsFalse(variant.TryGetEffectiveModbusDataType(out _));
            Assert.IsFalse(variant.TryGetEffectiveAddressWidth(out int width));
            Assert.AreEqual(1, width);

            var nullType = new TagConfig { DataType = null };
            Assert.IsFalse(nullType.TryGetEffectiveAddressWidth(out _));

            var known = new TagConfig { DataType = "Int32" };
            Assert.IsTrue(known.TryGetEffectiveModbusDataType(out string mbType));
            Assert.AreEqual("Int32", mbType);
            Assert.IsTrue(known.TryGetEffectiveAddressWidth(out int knownWidth));
            Assert.AreEqual(2, knownWidth);

            // 显式 ModbusDataType 优先于无法推断的 DataType
            var explicitType = new TagConfig { DataType = "Variant", ModbusDataType = "Float" };
            Assert.IsTrue(explicitType.TryGetEffectiveModbusDataType(out string explicitMb));
            Assert.AreEqual("Float", explicitMb);
            Assert.IsTrue(explicitType.TryGetEffectiveAddressWidth(out int explicitWidth));
            Assert.AreEqual(2, explicitWidth);

            // String/DateTime 无 wire encoding，仍被拒绝
            Assert.ThrowsException<NotSupportedException>(
                () => new TagConfig { DataType = "String" }.TryGetEffectiveAddressWidth(out _));
        }

        [TestMethod]
        public void DataBridge_PendingTagIsRegisteredAfterConfigChange()
        {
            var client = new FakeOpcDaClient();
            var server = new RecordingModbusServer();
            var tag = new TagConfig { TagKey = "tag-1", ItemId = "Item.1", DataType = "Variant" };
            using (var bridge = new DataBridge(client, server, new List<TagConfig> { tag }))
            {
                bridge.Start();
                Assert.AreEqual(0, server.AddCount, "类型无法解析的标签不应注册 Modbus 节点");

                // 模拟 DA 连接后 CanonicalDataType 回写真实类型并触发配置变更
                tag.DataType = "Int16";
                client.RaiseConfigChanged();

                Assert.AreEqual(1, server.AddCount);
                Assert.AreEqual("Int16", server.LastModbusDataType);
            }
        }

        [TestMethod]
        public void CsvIdentity_RoundTripsTagKeyAndRecognizesLegacy()
        {
            string encoded = MappingCsvIdentity.FormatSequence(7, "0_Tag/中文+");
            Assert.IsTrue(MappingCsvIdentity.TryParseSequence(encoded, out int sequence, out string key));
            Assert.AreEqual(7, sequence);
            Assert.AreEqual("0_Tag/中文+", key);
            Assert.IsTrue(MappingCsvIdentity.TryParseSequence("8", out sequence, out key));
            Assert.IsNull(key);
        }

        [TestMethod]
        public void DataBridge_SeparatesDaAndModbusSnapshotsAcrossFailures()
        {
            var client = new FakeOpcDaClient();
            var server = new RecordingModbusServer();
            var tag = new TagConfig { TagKey = "tag-1", ItemId = "Item.1", DataType = "Int16", ModbusDataType = "Int16" };
            using (var bridge = new DataBridge(client, server, new List<TagConfig> { tag }))
            {
                bridge.Start();
                var firstTime = new DateTime(2026, 8, 1, 10, 0, 0);
                client.RaiseDataChanged("tag-1", "12", OpcQualityKind.Good, firstTime);
                var success = bridge.GetSnapshots()[0];
                Assert.AreEqual((short)12, success.ModbusValue);
                Assert.AreEqual("Good", success.ModbusStatus);
                Assert.AreEqual(1, bridge.TotalUpdates);

                client.RaiseDataChanged("tag-1", "99", OpcQualityKind.Bad, firstTime.AddSeconds(1));
                var bad = bridge.GetSnapshots()[0];
                Assert.AreEqual("99", bad.DaValue);
                Assert.AreEqual("BadQuality", bad.ModbusStatus);
                Assert.AreEqual((short)12, bad.ModbusValue);
                Assert.AreEqual(1, bridge.TotalUpdates);

                client.RaiseDataChanged("tag-1", "invalid", OpcQualityKind.Good, firstTime.AddSeconds(2));
                var conversion = bridge.GetSnapshots()[0];
                Assert.AreEqual("ConversionError", conversion.ModbusStatus);
                Assert.AreEqual((short)12, conversion.ModbusValue);
                Assert.AreEqual(1, bridge.ErrorCount);

                server.NextResult = ModbusWriteResult.Failed(ModbusWriteStatus.WriteError, "disk");
                client.RaiseDataChanged("tag-1", "13", OpcQualityKind.Good, firstTime.AddSeconds(3));
                var write = bridge.GetSnapshots()[0];
                Assert.AreEqual((short)13, write.DaValue);
                Assert.AreEqual("WriteError", write.ModbusStatus);
                Assert.AreEqual((short)12, write.ModbusValue);
                Assert.AreEqual(1, bridge.TotalUpdates);
                Assert.AreEqual(2, bridge.ErrorCount);
            }
        }

        [TestMethod]
        public void DataBridge_MarksUncertainQualityDistinctly()
        {
            var client = new FakeOpcDaClient();
            var server = new RecordingModbusServer();
            var tag = new TagConfig { TagKey = "tag-1", ItemId = "Item.1", DataType = "Int16", ModbusDataType = "Int16" };
            using (var bridge = new DataBridge(client, server, new List<TagConfig> { tag }))
            {
                bridge.Start();
                client.RaiseDataChanged("tag-1", "50", OpcQualityKind.Uncertain, new DateTime(2026, 8, 1, 10, 0, 0));
                var snap = bridge.GetSnapshots()[0];
                Assert.AreEqual("Uncertain", snap.DaQuality);
                Assert.AreEqual("BadQuality", snap.ModbusStatus);
            }
        }

        [TestMethod]
        public void LegacyCsv_RejectsDuplicateItemIds()
        {
            var current = new[] { "A", "A" };
            var csv = new[] { "A" };
            Assert.ThrowsException<InvalidOperationException>(() => MappingCsvIdentity.ValidateLegacyItemIds(current, csv));
            Assert.ThrowsException<InvalidOperationException>(() => MappingCsvIdentity.ValidateLegacyItemIds(new[] { "A" }, new[] { "A", "A" }));
        }

        private sealed class RecordingModbusServer : IGatewayModbusTcpServer
        {
            public bool IsRunning => true;
            public int VariableCount => AddCount;
            public byte SlaveId => 1;
            public event Action<string> OnStatusChanged { add { } remove { } }
            public Action OnConfigChanged { get; set; }
            public int AddCount { get; private set; }
            public string LastModbusDataType { get; private set; }
            public ModbusWriteResult NextResult { get; set; } = ModbusWriteResult.Succeeded();

            public System.Threading.Tasks.Task StartAsync() => System.Threading.Tasks.Task.CompletedTask;
            public System.Threading.Tasks.Task StopAsync() => System.Threading.Tasks.Task.CompletedTask;

            public void AddVariableNode(string tagKey, ushort modbusAddress, ModbusRegisterType registerType, string modbusDataType, object initialValue)
            {
                AddCount++;
                LastModbusDataType = modbusDataType;
            }

            public ModbusWriteResult UpdateValue(string tagKey, object value, bool isGood, DateTime timestamp)
                => isGood ? NextResult : ModbusWriteResult.Failed(ModbusWriteStatus.BadQuality);
            public void Dispose() { }
        }
    }
}
