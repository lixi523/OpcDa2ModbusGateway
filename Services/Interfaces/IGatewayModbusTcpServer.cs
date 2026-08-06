using System;
using System.Threading.Tasks;
using OpcDaToModbusGateway;
using OpcDaToModbusGateway.Models;

namespace OpcDaToModbusGateway.Services.Interfaces
{
    /// <summary>
    /// Modbus TCP 服务器接�?—�?抽象�?Modbus TCP 从站启动、停止、寄存器管理的核心契约�?
    ///
    /// 设计目的�?
    /// 1. 解�?GatewayManager �?Modbus 服务器实现，允许在测试中注入 Mock 服务器�?
    /// 2. 明确寄存器创建、状态报告等关键操作�?
    ///
    /// 实现类：<see cref="GatewayModbusTcpServer"/>
    /// </summary>
    public interface IGatewayModbusTcpServer : IDisposable
    {
        /// <summary>服务器当前是否处于运行状态（监听端口中）�?/summary>
        bool IsRunning { get; }

        /// <summary>已注册的变量节点总数�?/summary>
        int VariableCount { get; }

        /// <summary>Modbus 从站 ID�?/summary>
        byte SlaveId { get; }

        /// <summary>状态变化事件，用于�?UI 层报告启�?停止/错误等信息�?/summary>
        event Action<string> OnStatusChanged;

        /// <summary>配置变更回调�?/summary>
        Action OnConfigChanged { get; set; }

        /// <summary>异步启动 Modbus TCP 服务器�?/summary>
        Task StartAsync();

        /// <summary>异步停止 Modbus TCP 服务器�?/summary>
        Task StopAsync();

        /// <summary>注册一个变量节点到 Modbus 寄存器映射。tagKey 在同一实例内必须唯一�?/summary>
        void AddVariableNode(string tagKey, ushort modbusAddress, ModbusRegisterType registerType, string modbusDataType, object initialValue);

        /// <summary>更新 Modbus 寄存器中某个变量的值�?/summary>
        ModbusWriteResult UpdateValue(string tagKey, object value, bool isGood, DateTime timestamp);
    }
}
