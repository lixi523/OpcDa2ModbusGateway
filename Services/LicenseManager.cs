using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using OpcDaToModbusGateway.Models;

namespace OpcDaToModbusGateway.Services
{
    /// <summary>
    /// 授权管理器 — 处理授权码验证、试用期计时、授权状态维护。
    /// UI 层订阅 StatusChanged 事件更新状态栏，订阅 GatewayStopRequested 事件在试用到期时停止网关。
    /// </summary>
    public class LicenseManager : IDisposable
    {
        private readonly LogManager _log;
        private readonly ConfigManager _configMgr;
        private readonly Action _requestGatewayStop;
        private readonly string _pcid;

        private bool _isLicensed;
        private bool _trialExpired;
        private Stopwatch _trialStopwatch;
        private Timer _licenseTimer;

        /// <summary>授权状态变化（已授权/试用中/试用到期）。</summary>
        public event Action<string, Color> StatusChanged;

        /// <summary>请求停止网关（试用到期时触发）。</summary>
        public event Action GatewayStopRequested;

        /// <summary>当前是否已授权。</summary>
        public bool IsLicensed => _isLicensed;

        /// <summary>试用是否已到期。</summary>
        public bool IsTrialExpired => _trialExpired;

        /// <summary>机器码 PCID。</summary>
        public string PCID => _pcid;

        public LicenseManager(LogManager log, ConfigManager configMgr, Action requestGatewayStop)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _configMgr = configMgr ?? throw new ArgumentNullException(nameof(configMgr));
            _requestGatewayStop = requestGatewayStop;

            try { _pcid = LicenseAlgorithm.GeneratePCID(); }
            catch { _pcid = "UNKNOWN"; }

            _log.Append($"[授权] 机器码 PCID: {_pcid}");

            Initialize();
        }

        private void Initialize()
        {
            string savedCode = _configMgr.Config?.AuthorizationCode;
            if (!string.IsNullOrEmpty(savedCode) && LicenseAlgorithm.VerifyAuthCode(_pcid, savedCode))
            {
                _isLicensed = true;
                StatusChanged?.Invoke("● 授权: 已授权", Color.Green);
                _log.Append("[授权] 授权码验证通过，已授权");
                return;
            }

            _isLicensed = false;
            if (!string.IsNullOrEmpty(savedCode))
            {
                _log.Append("[授权] 已保存的授权码无效（可能硬件变更），进入试用模式");
                _configMgr.Config.AuthorizationCode = null;
                _configMgr.Save();
            }
            else
            {
                _log.Append("[授权] 未检测到授权码，进入试用模式");
            }

            StartTrialTimer();
        }

        private void StartTrialTimer()
        {
            _trialStopwatch = Stopwatch.StartNew();
            _trialExpired = false;

            _licenseTimer = new Timer { Interval = 1000 };
            _licenseTimer.Tick += LicenseTimer_Tick;
            _licenseTimer.Start();

            UpdateTrialStatus();
            _log.Append($"[授权] 试用倒计时 {AppConstants.TrialPeriodMinutes} 分钟已开始");
        }

        private void LicenseTimer_Tick(object sender, EventArgs e)
        {
            TimeSpan remaining = TimeSpan.FromMinutes(AppConstants.TrialPeriodMinutes) - _trialStopwatch.Elapsed;

            if (remaining.TotalSeconds <= 0)
            {
                _licenseTimer?.Stop();
                _trialExpired = true;
                StatusChanged?.Invoke("● 授权: 试用到期", Color.Red);
                _log.Append("[授权] ★★★ 试用期已到，网关将自动关闭 ★★★");
                GatewayStopRequested?.Invoke();
                return;
            }

            UpdateTrialStatus(remaining);
        }

        private void UpdateTrialStatus(TimeSpan? remaining = null)
        {
            if (remaining == null)
                remaining = TimeSpan.FromMinutes(AppConstants.TrialPeriodMinutes) - _trialStopwatch.Elapsed;

            int totalSeconds = (int)remaining.Value.TotalSeconds;
            string text = $"● 试用: {totalSeconds / 60:D2}:{totalSeconds % 60:D2}";
            Color color = remaining.Value.TotalMinutes <= 5 ? Color.Red
                       : remaining.Value.TotalMinutes <= 10 ? Color.Orange
                       : Color.DarkOrange;

            StatusChanged?.Invoke(text, color);
        }

        /// <summary>
        /// 验证并应用授权码。
        /// </summary>
        /// <param name="authCode">授权码。</param>
        /// <returns>验证是否成功。</returns>
        public bool ApplyAuthorizationCode(string authCode)
        {
            if (LicenseAlgorithm.VerifyAuthCode(_pcid, authCode))
            {
                _isLicensed = true;
                _trialExpired = false;
                _licenseTimer?.Stop();
                _licenseTimer?.Dispose();
                _licenseTimer = null;

                _configMgr.Config.AuthorizationCode = authCode;
                _configMgr.Save();

                StatusChanged?.Invoke("● 授权: 已授权", Color.Green);
                _log.Append("[授权] ★ 授权码验证成功，软件已授权 ★");
                return true;
            }
            else
            {
                _log.Append("[授权] 授权码验证失败");
                return false;
            }
        }

        public void Dispose()
        {
            _licenseTimer?.Stop();
            _licenseTimer?.Dispose();
            _licenseTimer = null;
        }
    }
}