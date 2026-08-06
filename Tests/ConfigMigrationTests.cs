using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using OpcDaToModbusGateway.Services;

namespace OpcDaToModbusGateway.Tests
{
    [TestClass]
    public class ConfigMigrationTests
    {
        private string _directory;
        private LogManager _log;

        [TestInitialize]
        public void Initialize()
        {
            _directory = Path.Combine(Path.GetTempPath(), "OpcDaGatewayTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _log = new LogManager(new TextBox());
        }

        [TestCleanup]
        public void Cleanup()
        {
            _log?.Dispose();
            try { Directory.Delete(_directory, true); } catch { }
        }

        [TestMethod]
        public void LegacyInlineTags_SurviveMigrationAndSecondLoad()
        {
            WriteLegacyConfig();

            using (var first = new ConfigManager(_log, _directory))
            {
                Assert.IsTrue(first.Load());
                Assert.AreEqual(1, first.Config.OpcDa.Tags.Count);
                Assert.AreEqual("Legacy.Item", first.Config.OpcDa.Tags[0].TagKey);
                Assert.IsTrue(File.Exists(Path.Combine(_directory, "tags.json")));
                Assert.AreEqual(1, first.Config.OpcDa.Tags.Count, "保存配置不得临时清空活动 Tags");
            }

            var persistedConfig = JObject.Parse(File.ReadAllText(Path.Combine(_directory, "config.json")));
            Assert.IsNull(persistedConfig["OpcDa"]?["Tags"]);

            using (var second = new ConfigManager(_log, _directory))
            {
                Assert.IsTrue(second.Load());
                Assert.AreEqual(1, second.Config.OpcDa.Tags.Count);
                Assert.AreEqual("Legacy.Item", second.Config.OpcDa.Tags[0].ItemId);
                Assert.AreEqual("Legacy.Item", second.Config.OpcDa.Tags[0].TagKey);
            }
        }

        [TestMethod]
        public void TagsWriteFailure_PreservesLegacyInlineConfig()
        {
            WriteLegacyConfig();
            Directory.CreateDirectory(Path.Combine(_directory, "tags.json.tmp"));
            string original = File.ReadAllText(Path.Combine(_directory, "config.json"));

            using (var manager = new ConfigManager(_log, _directory))
            {
                Assert.IsTrue(manager.Load());
                Assert.IsFalse(manager.SaveAllImmediate());
                Assert.AreEqual(1, manager.Config.OpcDa.Tags.Count);
            }

            Assert.AreEqual(original, File.ReadAllText(Path.Combine(_directory, "config.json")));
            Assert.IsFalse(File.Exists(Path.Combine(_directory, "tags.json")));
        }

        private void WriteLegacyConfig()
        {
            File.WriteAllText(Path.Combine(_directory, "config.json"), @"{
  ""OpcDa"": {
    ""ServerProgId"": ""Legacy.Server"",
    ""UpdateRateMs"": 1000,
    ""Tags"": [
      { ""ItemId"": ""Legacy.Item"", ""DisplayName"": ""Legacy"", ""DataType"": ""Int16"", ""ModbusAddress"": 12 }
    ]
  },
  ""ModbusTcp"": { ""Port"": 502, ""ListenAddress"": ""127.0.0.1"" }
}");
        }
    }
}
