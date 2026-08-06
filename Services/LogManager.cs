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
        }

        public void Append(string message)
        {
            if (_disposed) return;

            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string fileLine = $"[{now}] {message}";
            string uiLine = $"[{now.Substring(11)}] {message}\r\n";

            if (_textBox != null && !_textBox.IsDisposed)
            {
                try
                {
                    if (_textBox.InvokeRequired)
                        _textBox.BeginInvoke((Action)(() => UpdateTextBox(uiLine)));
                    else
                        UpdateTextBox(uiLine);
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
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

            _queue.CompleteAdding();

            if (!_writerThread.Join(TimeSpan.FromSeconds(5)))
                System.Diagnostics.Debug.WriteLine("[LogManager] 警告: 写入线程在 5 秒内未结束");

            try { _fileWriter?.Flush(); } catch { }
            try { _fileWriter?.Dispose(); } catch { }
            _fileWriter = null;
            _queue.Dispose();
        }

        private void UpdateTextBox(string text)
        {
            _textBox.AppendText(text);
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