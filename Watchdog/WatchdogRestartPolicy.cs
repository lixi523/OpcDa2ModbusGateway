namespace OpcDaToModbusGateway.Watchdog
{
    internal enum WatchdogDecision
    {
        None,
        SuppressRestart,
        Restart,
        Rearm
    }

    internal sealed class WatchdogRestartPolicy
    {
        public bool WaitingForManualStart { get; private set; }

        public WatchdogDecision Evaluate(bool isProcessRunning, bool gracefulExitSignaled)
        {
            if (isProcessRunning)
            {
                if (!WaitingForManualStart) return WatchdogDecision.None;
                WaitingForManualStart = false;
                return WatchdogDecision.Rearm;
            }

            if (WaitingForManualStart)
            {
                if (gracefulExitSignaled) return WatchdogDecision.SuppressRestart;

                // 主程序启动时会清除优雅退出事件；即使它在下一次轮询前再次崩溃，
                // 信号已清除也证明发生过一次手工启动，应重新武装崩溃守护。
                WaitingForManualStart = false;
                return WatchdogDecision.Restart;
            }
            if (gracefulExitSignaled)
            {
                WaitingForManualStart = true;
                return WatchdogDecision.SuppressRestart;
            }

            return WatchdogDecision.Restart;
        }
    }
}
