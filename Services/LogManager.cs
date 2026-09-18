using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace OpcDaToModbusGateway.Services
{
    /// <summary>
    /// 日志管理器 — 双输出通道（UI 实时显示 + 异步文件持久化）。
    /// 使用 BlockingCollection(10000) 作为生产者-消费者队列，后台线程逐条消费并写入按日期命名的日志文件。
    /// </summary>
    public class LogManager : IDisposable
    {
        private readonly TextBox _textBox;
        private readonly BlockingCollection<string> _queue = new BlockingCollection<string>(AppConstants.LogQueueCapacity);
        private readonly Thread _writerThread;
        private readonly string _logDir;
        private StreamWriter _fileWriter;
        private string _currentDate;
        private volatile bool _disposed;
        private int _disposeGuard;
        private int _droppedCount;

        // #19 修复：日志裁剪优化 — 环形缓冲 + 批量刷新
        // 原实现每条日志 get/set _textBox.Text 全文（几万字符级），高频日志拖垮 UI 线程。
        // 改为：UI 侧用 StringBuilder 累积，定时器每 200ms 合并一次刷新；
        // 仅当累积行数超阈值时才做一次裁剪（Substring），消除每条全量 get/set。
        private readonly StringBuilder _uiBuffer = new StringBuilder();
        private readonly System.Threading.Timer _uiFlushTimer;
        private const int LogUiFlushIntervalMs = 200;

        public LogManager(TextBox textBox)
        {
            _textBox = textBox ?? throw new ArgumentNullException(nameof(textBox));
            _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            if (!Directory.Exists(_logDir))
                Directory.CreateDirectory(_logDir);

            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "LogWriter"
            };
            _writerThread.Start();

            // #19：UI 侧批量刷新定时器，每 200ms 把累积的日志行一次性追加到 TextBox
            _uiFlushTimer = new System.Threading.Timer(_ => FlushUiBuffer(), null, LogUiFlushIntervalMs, LogUiFlushIntervalMs);
        }

        public void Append(string message)
        {
            if (_disposed) return;

            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string fileLine = $"[{now}] {message}";
            string uiLine = $"[{now.Substring(11)}] {message}\r\n";

            if (_textBox != null && !_textBox.IsDisposed)
            {
                // #19：累积到 StringBuilder，由定时器批量刷新 UI，避免每条日志 get/set 全文
                lock (_uiBuffer)
                {
                    _uiBuffer.Append(uiLine);
                }
            }

            try
            {
                if (!_queue.TryAdd(fileLine))
                {
                    int dropped = Interlocked.Increment(ref _droppedCount);
                    if (dropped % 100 == 1)
                    {
                        _queue.TryAdd($"[WARNING] 日志队列已满，累计丢弃 {dropped} 条");
                        try
                        {
                            string warnPath = Path.Combine(_logDir, "dropped_warnings.log");
                            File.AppendAllText(warnPath,
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 日志队列已满，累计丢弃 {dropped} 条\r\n");
                        }
                        catch { }
                    }
                }
            }
            catch (InvalidOperationException) { }
        }

        public void CleanupOldFiles()
        {
            ThreadPool.QueueUserWorkItem(_ => DoCleanup());
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeGuard, 1) != 0) return;
            _disposed = true;

            // #19：先释放 UI 批量刷新定时器，避免 Dispose 后回调仍访问 _textBox
            try { _uiFlushTimer?.Dispose(); } catch { }

            _queue.CompleteAdding();

            if (!_writerThread.Join(TimeSpan.FromSeconds(5)))
                System.Diagnostics.Debug.WriteLine("[LogManager] 警告: 写入线程在 5 秒内未结束");

            try { _fileWriter?.Flush(); } catch { }
            try { _fileWriter?.Dispose(); } catch { }
            _fileWriter = null;
            _queue.Dispose();
        }

        /// <summary>
        /// #19：由 UI 批量刷新定时器触发，把累积的日志行一次性追加到 TextBox。
        /// 仅当累积行数达到 LogUiTrimThreshold 时才做裁剪（Substring 一次）。
        /// 在 UI 线程执行（经 BeginInvoke 封送），保证跨线程安全。
        /// </summary>
        private void FlushUiBuffer()
        {
            string batch;
            lock (_uiBuffer)
            {
                if (_uiBuffer.Length == 0) return;
                batch = _uiBuffer.ToString();
                _uiBuffer.Clear();
            }

            if (_textBox == null) return;
            try
            {
                if (_textBox.IsDisposed) return;
                if (_textBox.InvokeRequired)
                    _textBox.BeginInvoke((Action)(() => AppendUiBatch(batch)));
                else
                    AppendUiBatch(batch);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        /// <summary>
        /// #19：在 UI 线程执行的实际追加。批量写入后按需裁剪。
        /// </summary>
        private void AppendUiBatch(string batch)
        {
            _textBox.AppendText(batch);
            // 仅当累积文本超阈值时裁剪一次，避免每条日志全文 get/set
            if (_textBox.TextLength > AppConstants.LogUiMaxChars)
                _textBox.Text = _textBox.Text.Substring(_textBox.TextLength - AppConstants.LogUiTrimChars);
        }

        private void WriterLoop()
        {
            int writeCount = 0;

            try
            {
                foreach (string line in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        string today = DateTime.Now.ToString("yyyy-MM-dd");
                        if (_currentDate != today)
                        {
                            _fileWriter?.Flush();
                            _fileWriter?.Dispose();
                            string logFile = Path.Combine(_logDir, $"gateway_{today}.log");
                            _fileWriter = new StreamWriter(logFile, true, new UTF8Encoding(true));
                            _fileWriter.AutoFlush = false;
                            _currentDate = today;
                        }

                        _fileWriter.WriteLine(line);

                        if (++writeCount % 10 == 0)
                            _fileWriter.Flush();

                        if (writeCount >= AppConstants.LogCleanupInterval)
                        {
                            writeCount = 0;
                            DoCleanup();
                        }
                    }
                    catch (IOException)
                    {
                        try
                        {
                            _fileWriter?.Flush();
                            _fileWriter?.Dispose();
                        }
                        catch { }
                        _fileWriter = null;

                        try
                        {
                            string logFile = Path.Combine(_logDir, $"gateway_{DateTime.Now:yyyy-MM-dd}.log");
                            _fileWriter = new StreamWriter(logFile, true, new UTF8Encoding(true));
                            _fileWriter.AutoFlush = false;
                        }
                        catch
                        {
                            System.Diagnostics.Debug.WriteLine($"[LogManager] 无法重建日志文件: {line}");
                        }
                    }
                    catch (NullReferenceException)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LogManager] 文件写入器为null，日志丢弃: {line}");
                    }
                    catch { }
                }
            }
            catch (InvalidOperationException) { }
        }

        private void DoCleanup()
        {
            try
            {
                if (!Directory.Exists(_logDir)) return;

                DateTime cutoff = DateTime.Now.AddDays(-AppConstants.LogRetentionDays);
                foreach (string file in Directory.GetFiles(_logDir, "gateway_*.log"))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        if (fi.LastWriteTime < cutoff)
                            fi.Delete();
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}