using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpcDaToModbusGateway.Watchdog;

namespace OpcDaToModbusGateway.Tests
{
    [TestClass]
    public class WatchdogRestartPolicyTests
    {
        [TestMethod]
        public void GracefulExit_SuppressesAllAbsentCycles_UntilManualStartRearms()
        {
            var policy = new WatchdogRestartPolicy();

            Assert.AreEqual(WatchdogDecision.SuppressRestart, policy.Evaluate(false, true));
            Assert.IsTrue(policy.WaitingForManualStart);
            Assert.AreEqual(WatchdogDecision.SuppressRestart, policy.Evaluate(false, true));
            Assert.AreEqual(WatchdogDecision.SuppressRestart, policy.Evaluate(false, true));

            Assert.AreEqual(WatchdogDecision.Rearm, policy.Evaluate(true, true));
            Assert.IsFalse(policy.WaitingForManualStart);
            Assert.AreEqual(WatchdogDecision.Restart, policy.Evaluate(false, false));
        }
        [TestMethod]
        public void ManualStartThenCrashBeforePolling_RearmsWhenGracefulSignalWasCleared()
        {
            var policy = new WatchdogRestartPolicy();

            Assert.AreEqual(WatchdogDecision.SuppressRestart, policy.Evaluate(false, true));
            Assert.AreEqual(WatchdogDecision.Restart, policy.Evaluate(false, false));
            Assert.IsFalse(policy.WaitingForManualStart);
        }
    }
}
