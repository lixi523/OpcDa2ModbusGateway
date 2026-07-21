using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using OpcDaToModbusGateway.Services.Interfaces;
using OpcDaToModbusGateway.Models;

namespace OpcDaToModbusGateway.Services
{
    /// <summary>
    /// 运行状态快照采集器 — H-36 新增。
    /// 每 5 分钟自动采集进程健康指标写入 health/ 目录，保留最近 24 小时（288 个文件）。
    /// PLAN 3.5: 每日凌晨 00:05 对昨日快照聚合，追加到 health_daily.jsonl；
    /// 7天/30天 工作集增长率超阈值时触发 OnAlert 事件（黄色 20%、红色 50%）。
    /// </summary>
    public class HealthSnapshot : IHealthSnapshot
    {
        private readonly GatewayManager _gateway;
        private readonly LogManager _log;
        private readonly Timer _timer;
        private readonly string _healthDir;
        private readonly string _dailyFile;
        private readonly object _captureLock = new object();

        private int _lastTotalUpdates;
        private int _disposedInt;

        private DateTime? _lastCaptureTime;
        private double? _lastWorkingSetMB;
        private string _lastAggregatedDate;

        public event Action<string, int> OnAlert;

        private readonly List<DailySummary> _dailyCache = new List<DailySummary>();

        public DateTime? LastCaptureTime => _lastCaptureTime;
        public double? LastWorkingSetMB => _lastWorkingSetMB;

        private const int SnapshotIntervalMinutes = 5;
        private const int MaxSnapshots = 288;
        private const int MaxDailyRetentionDays = 90;
        private const double GrowthAlertWarning = 0.20;
        private const double GrowthAlertCritical = 0.50;

        public HealthSnapshot(GatewayManager gateway, LogManager log)
        {
            _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            _log = log ?? throw new ArgumentNullException(nameof(log));

            _healthDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "health");
            if (!Directory.Exists(_healthDir))
                Directory.CreateDirectory(_healthDir);
            _dailyFile = Path.Combine(_healthDir, "health_daily.jsonl");

            LoadDailyCache();

            _timer = new Timer(_ => Capture(), null, 1000, SnapshotIntervalMinutes * 60 * 1000);
            _log.Append("[健康快照] 已启动，每 5 分钟采集一次");
        }

        private void LoadDailyCache()
        {
            try
            {
                if (!File.Exists(_dailyFile)) return;
                var lines = File.ReadAllLines(_dailyFile)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => { try { return JsonConvert.DeserializeObject<DailySummary>(l); } catch { return null; } })
                    .Where(x => x != null)
                    .OrderBy(x => x.Date)
                    .ToList();
                _dailyCache.AddRange(lines);
            }
            catch { }
        }

        private void Capture()
        {
            if (Volatile.Read(ref _disposedInt) == 1) return;
            if (!Monitor.TryEnter(_captureLock)) return;
            try
            {
                var snapshot = new SnapshotData
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    TimestampUtc = DateTime.UtcNow.ToString("o"),
                };

                using (var proc = Process.GetCurrentProcess())
                {
                    proc.Refresh();
                    snapshot.WorkingSetMB = Math.Round(proc.WorkingSet64 / 1048576.0, 2);
                    snapshot.PrivateMemoryMB = Math.Round(proc.PrivateMemorySize64 / 1048576.0, 2);
                    snapshot.GcTotalMemoryMB = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 2);
                    snapshot.ThreadCount = proc.Threads.Count;
                    snapshot.HandleCount = proc.HandleCount;
                }

                snapshot.IsRunning = _gateway.IsRunning;

                if (_gateway.DaClient != null)
                    snapshot.DaConnected = _gateway.DaClient.IsConnected;

                if (_gateway.Bridge != null)
                {
                    var bridge = _gateway.Bridge;
                    int currentTotal = bridge.TotalUpdates;
                    snapshot.TotalUpdates = currentTotal;
                    int delta = currentTotal - _lastTotalUpdates;
                    snapshot.UpdateRatePerSec = Math.Round(delta / (SnapshotIntervalMinutes * 60.0), 1);
                    _lastTotalUpdates = currentTotal;
                    snapshot.ErrorCount = bridge.ErrorCount;
                    snapshot.LastUpdateTime = bridge.LastUpdateTime.ToString("yyyy-MM-dd HH:mm:ss");
                }

                if (_gateway.ModbusServer != null)
                {
                    snapshot.ModbusVariableCount = _gateway.ModbusServer.VariableCount;
                    snapshot.ModbusSlaveId = _gateway.ModbusServer.SlaveId;
                    // snapshot.DaTagsChildren removed for Modbus
                }

                ThreadPool.GetAvailableThreads(out int workerAvail, out int ioAvail);
                ThreadPool.GetMaxThreads(out int workerMax, out int ioMax);
                snapshot.ThreadPoolWorkerBusy = workerMax - workerAvail;
                snapshot.ThreadPoolWorkerMax = workerMax;

                string fileName = $"health_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                string filePath = Path.Combine(_healthDir, fileName);
                File.WriteAllText(filePath, JsonConvert.SerializeObject(snapshot, Formatting.Indented));

                _lastCaptureTime = DateTime.Now;
                _lastWorkingSetMB = snapshot.WorkingSetMB;

                RotateOldSnapshots();

                DateTime now = DateTime.Now;
                if (now.Hour == 0 && now.Minute >= 5 && now.Minute < 10)
                {
                    string yesterday = now.Date.AddDays(-1).ToString("yyyy-MM-dd");
                    if (yesterday != _lastAggregatedDate)
                        AppendDailySummary(yesterday);
                }
            }
            catch (Exception ex)
            {
                try { _log?.Append($"[健康快照] 采集失败: {ex.Message}"); } catch { }
            }
            finally
            {
                Monitor.Exit(_captureLock);
            }
        }

        private void RotateOldSnapshots()
        {
            try
            {
                var files = Directory.GetFiles(_healthDir, "health_*.json");
                if (files.Length <= MaxSnapshots) return;

                Array.Sort(files, (a, b) =>
                {
                    var fa = new FileInfo(a);
                    var fb = new FileInfo(b);
                    return fa.CreationTime.CompareTo(fb.CreationTime);
                });

                int toDelete = files.Length - MaxSnapshots;
                for (int i = 0; i < toDelete; i++)
                    try { File.Delete(files[i]); } catch { }
            }
            catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposedInt, 1) == 1) return;
            try { _timer?.Dispose(); } catch { }
        }

        public DailySummary GenerateDailySummary()
        {
            if (!Monitor.TryEnter(_captureLock)) return null;
            try
            {
                string targetDate = DateTime.Now.Date.AddDays(-1).ToString("yyyy-MM-dd");
                return AppendDailySummary(targetDate);
            }
            catch (Exception ex)
            {
                try { _log?.Append($"[健康快照] 手动聚合失败: {ex.Message}"); } catch { }
                return null;
            }
            finally
            {
                Monitor.Exit(_captureLock);
            }
        }

        private DailySummary AppendDailySummary(string targetDate)
        {
            if (targetDate == _lastAggregatedDate) return null;

            string prefix = "health_" + targetDate.Replace("-", "");
            var files = Directory.GetFiles(_healthDir, prefix + "*.json");
            if (files.Length == 0)
            {
                _log?.Append($"[健康快照] 聚合日期 {targetDate} 无快照数据，跳过");
                return null;
            }

            var dataPoints = new List<SnapshotData>();
            foreach (var f in files)
            {
                try
                {
                    var d = JsonConvert.DeserializeObject<SnapshotData>(File.ReadAllText(f));
                    if (d != null) dataPoints.Add(d);
                }
                catch { }
            }
            if (dataPoints.Count == 0) return null;

            // Count DA disconnects (status flips)
            int disconnects = 0;
            bool? prevConnected = null;
            foreach (var d in dataPoints.OrderBy(x => x.Timestamp))
            {
                if (prevConnected.HasValue && d.DaConnected != prevConnected.Value)
                    disconnects++;
                prevConnected = d.DaConnected;
            }

            var sum = new DailySummary
            {
                Date = targetDate,
                SampleCount = dataPoints.Count,
                WorkingSetAvgMB = Math.Round(dataPoints.Average(x => x.WorkingSetMB), 2),
                WorkingSetMaxMB = Math.Round(dataPoints.Max(x => x.WorkingSetMB), 2),
                WorkingSetMinMB = Math.Round(dataPoints.Min(x => x.WorkingSetMB), 2),
                PrivateMemoryAvgMB = Math.Round(dataPoints.Average(x => x.PrivateMemoryMB), 2),
                GcTotalMemoryAvgMB = Math.Round(dataPoints.Average(x => x.GcTotalMemoryMB), 2),
                UpdateRateAvgPerSec = Math.Round(dataPoints.Average(x => x.UpdateRatePerSec), 2),
                TotalUpdatesEndOfDay = dataPoints.Max(x => x.TotalUpdates),
                ErrorCountEndOfDay = dataPoints.Max(x => x.ErrorCount),
                DaDisconnectedCount = disconnects,
            };

            // Compute growth rate from history
            if (_dailyCache.Count > 0)
            {
                var history = _dailyCache.Where(x => x.Date != sum.Date).ToList();
                if (history.Count >= 7)
                {
                    double last7 = history.Skip(Math.Max(0, history.Count - 7)).Average(x => x.WorkingSetAvgMB);
                    double last30 = history.Skip(Math.Max(0, history.Count - 30)).Average(x => x.WorkingSetAvgMB);
                    if (last30 > 0.1)
                    {
                        double rate = (last7 - last30) / last30;
                        sum.GrowthRate = Math.Round(rate, 4);
                        sum.GrowthAlert = rate >= GrowthAlertCritical ? "critical"
                                       : rate >= GrowthAlertWarning ? "warning" : "none";
                    }
                }
            }

            File.AppendAllText(_dailyFile, JsonConvert.SerializeObject(sum) + Environment.NewLine);

            _dailyCache.Add(sum);
            if (_dailyCache.Count > MaxDailyRetentionDays)
                _dailyCache.RemoveRange(0, _dailyCache.Count - MaxDailyRetentionDays);

            try
            {
                File.WriteAllLines(_dailyFile, _dailyCache.Select(x => JsonConvert.SerializeObject(x)));
            }
            catch { }

            _lastAggregatedDate = targetDate;
            _log?.Append($"[健康快照] 已聚合 {targetDate}（{dataPoints.Count} 个样本，平均工作集 {sum.WorkingSetAvgMB:F1}MB，告警={sum.GrowthAlert}）");

            int severity = sum.GrowthAlert == "critical" ? 2 : (sum.GrowthAlert == "warning" ? 1 : 0);
            if (severity > 0)
                OnAlert?.Invoke(
                    $"内存增长率告警：{targetDate} 7天均值 {sum.WorkingSetAvgMB:F1}MB 较30天增长 {(sum.GrowthRate * 100):F1}%",
                    severity);

            return sum;
        }
    }
}