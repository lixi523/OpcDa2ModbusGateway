using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpcDaToModbusGateway.Models;

namespace OpcDaToModbusGateway.Tests
{
    [TestClass]
    public class OpcQualityTests
    {
        [DataTestMethod]
        [DataRow(0xC0, OpcQualityKind.Good)]
        [DataRow(0xD8, OpcQualityKind.Good)]
        [DataRow(0x40, OpcQualityKind.Uncertain)]
        [DataRow(0x00, OpcQualityKind.Bad)]
        [DataRow(0x80, OpcQualityKind.Bad)]
        public void Classify_UsesHighTwoBits(int status, OpcQualityKind expected)
        {
            Assert.AreEqual(expected, OpcQualityHelper.Classify(status));
        }
    }
}
