using System;
using System.Collections.Generic;
using OpcDaToModbusGateway.Models;

namespace OpcDaToModbusGateway.Services.Interfaces
{
    /// <summary>
    /// OPC DA 客户端接口 —— 抽象出与 OPC DA 服务器建立订阅并接收数据变化的核心契约。
    ///
    /// 设计目的（PLAN 3.1 接口抽象）：
    /// 1. 解耦 GatewayManager 与具体 OPC DA 客户端实现，使单元测试可注入 Fake 模拟器。
    /// 2. 明确核心生命周期方法（Start/Stop/TryReconnect）与状态通知事件。
    ///
    /// 实现约束：
    /// - 实现类必须是线程安全的，OnDataChanged 回调会在 COM 线程池线程触发。
    /// - BrowseAllItems 是实现类静态方法，接口不约束（.NET 4.7.2 = C# 7.3 不支持接口静态方法）。
    ///
    /// 实现类：<see cref="OpcDaClient"/>
    /// 测试桩：<see cref="FakeOpcDaClient"/>
    /// </summary>
    public interface IOpcDaClient : IDisposable
    {
        /// <summary>当前是否已成功连接到 OPC DA 服务器。</summary>
        bool IsConnected { get; }

        /// <summary>数据变化事件：参数 (TagKey, 值, 质量三态, 时间戳)。</summary>
        event Action<string, object, OpcQualityKind, DateTime> OnDataChanged;

        /// <summary>连接状态变化事件，用于向 UI 层报告连接/断开/错误等状态信息。</summary>
        event Action<string> OnStatusChanged;

        /// <summary>配置变更事件：当标签数据类型等配置被自动修正时触发，用于触发配置持久化。</summary>
        event Action OnConfigChanged;

        /// <summary>
        /// 启动客户端：连接服务器、创建订阅、按指定数据获取方式注册回调或启动轮询。
        /// </summary>
        /// <param name="updateRateMs">刷新频率（毫秒），异步模式作为订阅推送周期、同步模式作为轮询周期。</param>
        /// <param name="mode">数据获取方式（异步订阅 / 同步轮询）。</param>
        void Start(int updateRateMs, DaAcquisitionMode mode);

        /// <summary>停止客户端：取消订阅、断开连接。失败时不应抛异常阻断后续资源释放。</summary>
        void Stop();

        /// <summary>尝试重新连接。返回 true 表示重连成功，false 表示仍在重连或已达最大重试次数。</summary>
        bool TryReconnect(int updateRateMs);
    }
}
