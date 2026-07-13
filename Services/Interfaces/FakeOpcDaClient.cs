using System;
using System.Collections.Generic;
using System.Threading;
using OpcDaToUaGateway.Models;

namespace OpcDaToUaGateway.Services.Interfaces
{
    /// <summary>
    /// OPC DA 客户端的 Fake 实现 —— 用于单元测试和故障注入演示。
    ///
    /// 设计目的（PLAN 3.1）：
    /// 1. 不实际访问 COM/DCOM，可在单元测试中完全控制数据流。
    /// 2. 模拟现场故障场景：断连、质量码异常、时序抖动。
    /// 3. 暴露 <see cref="RaiseDataChanged"/> / <see cref="RaiseDisconnected"/> 等
    ///    显式触发方法，避免依赖定时器。
    ///
    /// 使用模式：
    ///   var fake = new FakeOpcDaClient();
    ///   fake.OnDataChanged += (key, val, good, ts) => { ... };
    ///   fake.Start(1000);  // 模拟启动
    ///   fake.RaiseDataChanged("Tag1", 42.0);  // 手动注入数据
    ///   fake.RaiseDisconnected();  // 手动模拟断连
    ///   fake.Dispose();
    ///
    /// 注意：当前 GatewayManager 仍使用 new OpcDaClient(...) 具体类型构造，
    /// 注入 Fake 需在 GatewayManager 引入构造函数参数（不在本次范围）。
    /// 本类先提供为后续 DI 改造的基础设施。
    /// </summary>
    public class FakeOpcDaClient : IOpcDaClient
    {
        private int _disposedInt;

        /// <inheritdoc />
        public bool IsConnected { get; set; }

        /// <inheritdoc />
        public event Action<string, object, bool, DateTime> OnDataChanged;

        /// <inheritdoc />
        public event Action<string> OnStatusChanged;

        /// <summary>已触发的数据变化总次数（便于断言）。</summary>
        public int UpdateCount { get; private set; }

        /// <summary>已发布的数据快照列表（按时间顺序）。</summary>
        public List<(string tagKey, object value, bool isGood, DateTime timestamp)> PublishedData
            = new List<(string, object, bool, DateTime)>();

        /// <summary>Start 调用次数（用于验证生命周期）。</summary>
        public int StartCount { get; private set; }

        /// <summary>Stop 调用次数。</summary>
        public int StopCount { get; private set; }

        /// <summary>TryReconnect 调用次数。</summary>
        public int ReconnectCount { get; private set; }

        /// <inheritdoc />
        public void Start(int updateRateMs)
        {
            StartCount++;
            IsConnected = true;
            OnStatusChanged?.Invoke($"[Fake] 模拟启动，更新周期 {updateRateMs}ms");
        }

        /// <inheritdoc />
        public void Stop()
        {
            StopCount++;
            IsConnected = false;
            OnStatusChanged?.Invoke("[Fake] 模拟停止");
        }

        /// <inheritdoc />
        public bool TryReconnect(int updateRateMs)
        {
            ReconnectCount++;
            IsConnected = true;
            OnStatusChanged?.Invoke($"[Fake] 模拟重连成功（第 {ReconnectCount} 次）");
            return true;
        }

        /// <summary>测试代码手动触发一次数据变化。</summary>
        /// <param name="tagKey">标签 TagKey。</param>
        /// <param name="value">值。</param>
        /// <param name="isGood">质量码是否 Good，默认为 true。</param>
        /// <param name="timestamp">时间戳，默认当前本地时间。</param>
        public void RaiseDataChanged(string tagKey, object value, bool isGood = true, DateTime? timestamp = null)
        {
            if (Interlocked.Exchange(ref _disposedInt, 0) != 0)
                throw new ObjectDisposedException(nameof(FakeOpcDaClient));

            var ts = timestamp ?? DateTime.Now;
            UpdateCount++;
            PublishedData.Add((tagKey, value, isGood, ts));
            OnDataChanged?.Invoke(tagKey, value, isGood, ts);
        }

        /// <summary>测试代码手动触发断连事件。</summary>
        public void RaiseDisconnected(string reason = "[Fake] 模拟断连")
        {
            IsConnected = false;
            OnStatusChanged?.Invoke(reason);
        }

        /// <summary>测试代码手动触发连接成功事件。</summary>
        public void RaiseConnected(string message = "[Fake] 模拟连接成功")
        {
            IsConnected = true;
            OnStatusChanged?.Invoke(message);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposedInt, 1) == 0)
            {
                IsConnected = false;
                // 清空事件订阅者，防止测试间状态泄漏
                OnDataChanged = null;
                OnStatusChanged = null;
                PublishedData.Clear();
            }
        }
    }
}
