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
        /// <summary>PCID 生成失败（WMI 全失败）时进入拒绝态，拒绝进入试用模式。</summary>
        private bool _pcidFailed;
        private bool _isLicensed;
        private bool _trialExpired;
        private DateTime _trialStartUtc;
        private DateTime _maxObservedTrialTimeUtc;
        /// <summary>使用 System.Threading.Timer 而非 WinForms Timer，避免非 UI 线程构造时 Tick 不触发。</summary>
        private System.Threading.Timer _licenseTimer;
        private readonly object _licenseStatusLock = new object();

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

        /// <summary>
        /// #13 修复：主动刷新当前授权状态并触发 StatusChanged 事件。
        /// 调用方（MainForm）应在订阅 StatusChanged 事件之后立即调用一次，
        /// 确保已授权模式下状态栏不会因构造函数内早触发的初始事件被错过而永久停留"检测中"。
        /// 幂等：多次调用只会重复推送当前状态，不产生副作用。
        /// </summary>
        public void RefreshStatus()
        {
            if (_pcidFailed)
            {
                StatusChanged?.Invoke("● 授权: 无法识别硬件，请检查 WMI 服务", Color.Red);
                return;
            }
            if (_isLicensed)
            {
                StatusChanged?.Invoke("● 授权: 已授权", Color.Green);
                return;
            }
            if (_trialExpired)
            {
                StatusChanged?.Invoke("● 授权: 试用到期", Color.Red);
                return;
            }
            // 试用中：重新计算剩余时间并推送
            UpdateTrialStatus();
        }

        public LicenseManager(LogManager log, ConfigManager configMgr, Action requestGatewayStop)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _configMgr = configMgr ?? throw new ArgumentNullException(nameof(configMgr));
            _requestGatewayStop = requestGatewayStop;

            try { _pcid = LicenseAlgorithm.GeneratePCID(); }
            catch (Exception ex)
            {
                _pcid = null;
                _pcidFailed = true;
                _log.Append($"[授权] 机器码 PCID 生成失败，进入拒绝态: {ex.Message}");
            }

            _log.Append($"[授权] 机器码 PCID: {_pcid}");

            Initialize();
        }

        private void Initialize()
        {
            // PCID 生成失败：进入拒绝态，不启动试用定时器，避免绕过一机一码授权模型。
            if (_pcidFailed)
            {
                _trialExpired = true; // 视为“到期”以拦截网关启动
                StatusChanged?.Invoke("● 授权: 无法识别硬件，请检查 WMI 服务", Color.Red);
                _log.Append("[授权] PCID 生成失败，拒绝进入试用模式");
                return;
            }

            string savedCode = _configMgr.Config?.AuthorizationCode;
            if (!string.IsNullOrEmpty(savedCode) && LicenseAlgorithm.VerifyAuthCode(_pcid, savedCode))
            {
                _isLicensed = true;
                // 授权成功时清空试用起始时间
                if (!string.IsNullOrEmpty(_configMgr.Config.TrialStartUtc))
                {
                    _configMgr.Config.TrialStartUtc = null;
                    _configMgr.Save();
                }
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
            _trialExpired = false;

            // 尝试从配置读取已保存的试用起始时间
            DateTime trialStartUtc = DateTime.MinValue;
            bool hasSavedStart = !string.IsNullOrEmpty(_configMgr.Config?.TrialStartUtc)
                && DateTime.TryParse(_configMgr.Config.TrialStartUtc, out trialStartUtc);

            if (hasSavedStart)
            {
                _trialStartUtc = trialStartUtc;
                _maxObservedTrialTimeUtc = DateTime.UtcNow; // 时钟回拨检测：记录最大已观测时间
                _log.Append($"[授权] 检测到已保存试用起始时间: {trialStartUtc:O}，已用 {(DateTime.UtcNow - trialStartUtc).TotalMinutes:F1} 分钟");
            }
            else
            {
                // 首次试用，记录起始时间
                _trialStartUtc = DateTime.UtcNow;
                _maxObservedTrialTimeUtc = _trialStartUtc;
                _configMgr.Config.TrialStartUtc = _trialStartUtc.ToString("o"); // ISO 8601 UTC
                _configMgr.Save();
                _log.Append($"[授权] 试用倒计时 {AppConstants.TrialPeriodMinutes} 分钟已开始（起始时间已持久化）");
            }

            _licenseTimer = new System.Threading.Timer(_ => LicenseTimerTickCallback(), null, 0, 1000);

            UpdateTrialStatus();
        }

        private void LicenseTimerTickCallback()
        {
            lock (_licenseStatusLock)
            {
                DateTime nowUtc = DateTime.UtcNow;

                // 时钟回拨检测：当前时间比已观测最大时间还早，说明系统时钟被回拨
                if (nowUtc < _maxObservedTrialTimeUtc)
                {
                    _log.Append($"[授权] ⚠️ 检测到系统时钟回拨（当前 {nowUtc:O} < 最大观测 { _maxObservedTrialTimeUtc:O}），按已观测时间继续倒计时");
                    nowUtc = _maxObservedTrialTimeUtc; // 使用最大已观测时间作为当前时间基准
                }
                _maxObservedTrialTimeUtc = nowUtc;

                TimeSpan remaining = TimeSpan.FromMinutes(AppConstants.TrialPeriodMinutes) - (nowUtc - _trialStartUtc);

                if (remaining.TotalSeconds <= 0)
                {
                    _licenseTimer?.Dispose();
                    _licenseTimer = null;
                    _trialExpired = true;
                    StatusChanged?.Invoke("● 授权: 试用到期", Color.Red);
                    _log.Append("[授权] ★★★ 试用期已到，网关将自动关闭 ★★★");
                    GatewayStopRequested?.Invoke();
                    return;
                }

                UpdateTrialStatus(remaining);
            }
        }

        private void UpdateTrialStatus(TimeSpan? remaining = null)
        {
            if (remaining == null)
                remaining = TimeSpan.FromMinutes(AppConstants.TrialPeriodMinutes) - (DateTime.UtcNow - _trialStartUtc);

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
                lock (_licenseStatusLock)
                {
                    _licenseTimer?.Dispose();
                    _licenseTimer = null;
                }

                _configMgr.Config.AuthorizationCode = authCode;
                _configMgr.Config.TrialStartUtc = null;
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
            lock (_licenseStatusLock)
            {
                _licenseTimer?.Dispose();
                _licenseTimer = null;
            }
        }
    }
}