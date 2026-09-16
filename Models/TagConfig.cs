using System;
using System.Collections.Generic;
using System.Linq;

namespace OpcDaToModbusGateway.Models
{
    // =====================================================================
    // 配置模型类总览
    // =====================================================================
    //
    // 本文件定义了网关的全部配置模型，对应 config.json 的结构：
    //
    //   AppConfig              —— 顶层配置，包含 DA/Modbus TCP 子配置和全局选项
    //   ├── OpcDaConfig        —— OPC DA 连接配置（服务器地址、刷新频率、标签列表）
    //   │   └── TagConfig      —— 单个 DA 标签的配置（ItemId、显示名、数据类型）
    //   └── ModbusTcpConfig        —— Modbus TCP 服务器配置（端口、监听地址、从站ID等）
    //
    // 配置通过 System.Text.Json 反序列化，属性名与 JSON key 一一对应。
    // GetEffective* 方法为每个可选配置项提供带默认值的安全访问入口，
    // 避免各处散落 null/0 值判断逻辑。
    // =====================================================================

    /// <summary>
        /// 单个 OPC DA 标签的配置，对应 config.json 中 OpcDa.Tags 数组的每一项。
        ///
        /// <para>一个 TagConfig 描述了从 OPC DA 服务器读取的一个数据点，
        /// 以及它在 Modbus TCP 网关服务器中对应的寄存器映射。</para>
        /// </summary>
    public class TagConfig
    {
        /// <summary>
        /// OPC DA 标签的 ItemId（如 "Random.Int32"），
        /// 是 DA 服务器内部用于标识数据点的唯一字符串。
        /// </summary>
        public string ItemId { get; set; }

        /// <summary>在 Modbus 映射中显示的名称，为空时回退到 <see cref="ItemId"/>。</summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// 数据类型，可选值：Boolean, Int16, Int32, UInt16, UInt32, Float, Double, String, DateTime。
        /// 决定了 <see cref="DataBridge"/> 在转发数据时如何做类型转换。
        /// </summary>
        public string DataType { get; set; }
        /// <summary>Modbus register address (0-based).</summary>
        public ushort ModbusAddress { get; set; }
        /// <summary>Modbus register type: Coil, DiscreteInput, HoldingRegister, InputRegister.</summary>
        public string ModbusRegisterType { get; set; }
        /// <summary>
        /// Modbus 侧数据类型（Bool/Int16/Int32/Float/Double/String/DateTime）。
        /// 由 <see cref="DataTypeConverter.GetModbusDataType"/> 根据 OPC 数据类型自动推断，
        /// 也可通过 CSV 导入时自定义。为空时回退到推断值。
        /// </summary>
        public string ModbusDataType { get; set; }

        /// <summary>
        /// 获取有效的 Modbus 数据类型：优先使用 <see cref="ModbusDataType"/>，
        /// 为空时根据 <see cref="DataType"/> 自动推断。
        /// </summary>
        public string GetEffectiveModbusDataType()
        {
            return string.IsNullOrEmpty(ModbusDataType)
                ? DataTypeConverter.GetModbusDataType(DataType)
                : ModbusDataType;
        }

        /// <summary>
        /// 尝试获取有效的 Modbus 数据类型，不抛出异常。
        /// 当 <see cref="ModbusDataType"/> 为空且 <see cref="DataType"/> 无法推断
        /// （Variant/Object/空/未知，可能在 DA 连接后由 CanonicalDataType 回写修正）时返回 false。
        /// </summary>
        public bool TryGetEffectiveModbusDataType(out string modbusDataType)
        {
            if (!string.IsNullOrEmpty(ModbusDataType))
            {
                modbusDataType = ModbusDataType;
                return true;
            }
            return DataTypeConverter.TryGetModbusDataType(DataType, out modbusDataType);
        }

        public int GetEffectiveAddressWidth()
        {
            // Bit 地址空间固定占 1，但声明类型仍必须是有定义 wire encoding 的类型。
            int registerWidth = DataTypeConverter.GetModbusRegisterWidth(GetEffectiveModbusDataType());
            var registerType = GetEffectiveRegisterType();
            return registerType == global::OpcDaToModbusGateway.ModbusRegisterType.Coil ||
                   registerType == global::OpcDaToModbusGateway.ModbusRegisterType.DiscreteInput
                ? 1
                : registerWidth;
        }

        /// <summary>
        /// 尝试获取标签占用的地址宽度，不抛出异常。
        /// Bit 地址空间（Coil/DiscreteInput）固定为 1；寄存器空间按有效类型宽度。
        /// 类型无法解析（Variant/Object/空等）时返回 false 并以宽度 1 保守计算，
        /// 真实宽度待 DA 连接回写类型后再由 <see cref="GetEffectiveAddressWidth"/> 覆盖。
        /// 显式声明或推断为 String/DateTime（无 wire encoding）时仍抛出异常。
        /// </summary>
        public bool TryGetEffectiveAddressWidth(out int width)
        {
            var registerType = GetEffectiveRegisterType();
            if (registerType == global::OpcDaToModbusGateway.ModbusRegisterType.Coil ||
                registerType == global::OpcDaToModbusGateway.ModbusRegisterType.DiscreteInput)
            {
                width = 1;
                return true;
            }
            if (!TryGetEffectiveModbusDataType(out string modbusType))
            {
                width = 1;
                return false;
            }
            width = DataTypeConverter.GetModbusRegisterWidth(modbusType);
            return true;
        }

        public global::OpcDaToModbusGateway.ModbusRegisterType GetEffectiveRegisterType()
        {
            if (string.IsNullOrEmpty(ModbusRegisterType))
                return global::OpcDaToModbusGateway.ModbusRegisterType.HoldingRegister;
            switch (ModbusRegisterType.Trim().ToLowerInvariant())
            {
                case "coil": return global::OpcDaToModbusGateway.ModbusRegisterType.Coil;
                case "discreteinput":
                case "discrete": return global::OpcDaToModbusGateway.ModbusRegisterType.DiscreteInput;
                case "holdingregister":
                case "holding": return global::OpcDaToModbusGateway.ModbusRegisterType.HoldingRegister;
                case "inputregister":
                case "input": return global::OpcDaToModbusGateway.ModbusRegisterType.InputRegister;
                default: return global::OpcDaToModbusGateway.ModbusRegisterType.HoldingRegister;
            }
        }

        /// <summary>
        /// 运行时内部唯一标识，用于在 <see cref="DataBridge"/> 和 <see cref="GatewayModbusTcpServer"/>
        /// 中索引标签。持久化到配置文件，加载后不再重新分配。
        ///
        /// <para>由 <see cref="AssignTagKeys"/> 在加载/选择标签后统一分配：
        /// - 若所有 ItemId 唯一，TagKey = ItemId；
        /// - 若存在重复 ItemId，所有 TagKey 统一使用 "索引_ItemId" 格式，
        ///   确保同一 ItemId 出现在多个 Modbus 寄存器时仍可区分。</para>
        ///
        /// <para>N-8 持久化：AssignTagKeys 现在仅对 TagKey 为 null/empty 的标签分配，
        /// 已有 TagKey 的标签保持不变。新分配的 Key 会通过 ConfigManager 立即写入磁盘。</para>
        /// </summary>
        public string TagKey { get; set; }

        /// <summary>
        /// 为标签列表中尚未分配 TagKey 的标签分配唯一标识。
        ///
        /// <para>N-8 修改：现在仅对 TagKey 为 null 或空字符串的标签进行分配，
        /// 已持久化 TagKey 的标签保持不变。返回值指示是否进行了新的分配。
        /// 分配策略与之前一致：
        /// - 如果所有 ItemId 都唯一，TagKey 直接等于 ItemId（最简洁）；
        /// - 如果存在重复的 ItemId（同一 DA 标签映射到多个 Modbus 寄存器），
        ///   所有标签统一使用 "列表索引_ItemId" 格式（如 "3_Random.Int32"），
        ///   确保 TagKey 全局唯一。</para>
        ///
        /// <para>此方法必须在标签列表确定后、创建 <see cref="DataBridge"/> 之前调用。</para>
        /// </summary>
        /// <param name="tags">需要检查并分配 TagKey 的标签列表。</param>
        /// <returns>如果有任何新 TagKey 被分配则返回 true，调用方应触发持久化。</returns>
        public static bool AssignTagKeys(List<TagConfig> tags)
        {
            if (tags == null || tags.Count == 0) return false;

            // N-8: 先收集需要分配 TagKey 的标签（仅当 TagKey 为空时）
            var unassigned = new List<(int index, TagConfig tag)>();
            for (int i = 0; i < tags.Count; i++)
            {
                if (string.IsNullOrEmpty(tags[i].TagKey))
                    unassigned.Add((i, tags[i]));
            }

            if (unassigned.Count == 0) return false;

            // 统计每个 ItemId 出现的次数，用于判断是否存在重复
            var counts = tags.GroupBy(t => t.ItemId ?? "").ToDictionary(g => g.Key, g => g.Count());

            // 使用原始列表索引（i）而非 unassigned 的局部索引，
            // 确保索引含义一致：TagKey 中的数字 = 列表中的位置
            foreach (var (i, tag) in unassigned)
            {
                string itemId = tag.ItemId ?? "";
                if (counts[itemId] == 1)
                    tag.TagKey = itemId;
                else
                    tag.TagKey = $"{i}_{itemId}";
            }

            return true;
        }
    }

    /// <summary>
    /// OPC DA 数据获取方式。
    /// Async = 异步订阅（服务器主动推送，经 ValuesChanged 回调）；
    /// Sync  = 同步轮询（网关按刷新频率定时 group.Read 主动拉取）。
    /// 默认 Async，与历史行为一致。
    /// </summary>
    public enum DaAcquisitionMode
    {
        /// <summary>异步订阅：依赖 OPC DA 服务器的数据变化回调推送。</summary>
        Async,
        /// <summary>同步轮询：网关定时主动读取，不依赖服务器回调。</summary>
        Sync
    }

    /// <summary>
    /// OPC DA 连接配置，对应 config.json 中的 "OpcDa" 节点。
    /// 包含 DA 服务器的连接信息和要读取的标签列表。
    /// </summary>
    public class OpcDaConfig
    {
        /// <summary>OPC DA 服务器的 ProgId（如 "Matrikon.OPC.Simulation.1"），用于 COM 注册表查找。</summary>
        public string ServerProgId { get; set; }

        /// <summary>
        /// OPC DA 服务器所在主机名。
        /// 默认 localhost 表示本机服务器；设为远程主机名时通过 DCOM 连接。
        /// </summary>
        public string ServerHost { get; set; }

        /// <summary>数据刷新频率（毫秒），决定 DA 客户端轮询或订阅的时间间隔。</summary>
        public int UpdateRateMs { get; set; }

        /// <summary>
        /// 数据获取方式：Async(异步订阅推送) / Sync(同步轮询拉取)。
        /// 仅作字符串存储，实际解析经 <see cref="GetEffectiveMode"/>，
        /// 容错笔误/缺失（未知值统一回退 Async）。
        /// </summary>
        public string Mode { get; set; }

        /// <summary>
        /// 获取有效的数据获取模式。
        /// 当 <see cref="Mode"/> 为 "Sync"(不区分大小写) 时返回 <see cref="DaAcquisitionMode.Sync"/>，
        /// 其余（空值、无法识别）一律回退 <see cref="DaAcquisitionMode.Async"/>，
        /// 确保配置文件缺失该字段或笔误时不会中断网关启动。
        /// </summary>
        public DaAcquisitionMode GetEffectiveMode()
        {
            if (string.Equals(Mode, "Sync", StringComparison.OrdinalIgnoreCase))
                return DaAcquisitionMode.Sync;
            return DaAcquisitionMode.Async;
        }

        /// <summary>
        /// 要读取的标签列表。
        /// P5 修复：默认初始化为空列表而非 null，避免在序列化、UI 绑定和遍历时的各处空值检查。
        /// </summary>
        public List<TagConfig> Tags { get; set; } = new List<TagConfig>();
    }

    /// <summary>
    /// Modbus TCP 服务器配置，对应 config.json 中的 "ModbusTcp" 节点。
    ///
    /// <para>包含 Modbus TCP 服务器的网络监听、端口、从站 ID 等配置。
    /// 每个可选配置项都提供了 GetEffective* 方法，返回经过默认值填充后的安全值，
    /// 避免使用者需要自行处理 null 或 0 值的情况。</para>
    /// </summary>
    public class ModbusTcpConfig
    {
        public int Port { get; set; }
        public string ListenAddress { get; set; }
        public byte SlaveId { get; set; }
        public int MaxHoldingRegisters { get; set; }
        public int MaxCoils { get; set; }
        public int MaxInputRegisters { get; set; }
        public int MaxDiscreteInputs { get; set; }
        public string GetEffectiveListenAddress()
        {
            return string.IsNullOrEmpty(ListenAddress) ? "127.0.0.1" : ListenAddress;
        }
        public int GetEffectivePort()
        {
            return Port > 0 ? Port : 502;
        }
        public string GetEndpointUrl()
        {
            int port = Port > 0 ? Port : 502;
            string host = string.IsNullOrEmpty(ListenAddress) ? "127.0.0.1" : ListenAddress;
            return string.Format("modbus.tcp://{0}:{1}/", host, port);
        }
    }

    /// <summary>
    /// 整体应用配置，对应 config.json 的根对象。
    ///
    /// <para>包含 OPC DA 连接配置、Modbus TCP 服务器配置以及全局行为选项
    /// （自动连接、自动启动、看门狗守护、授权码等）。</para>
    /// </summary>
    public class AppConfig
    {
        /// <summary>OPC DA 连接配置。</summary>
        public OpcDaConfig OpcDa { get; set; }

        /// <summary>Modbus TCP 服务器配置。</summary>
        public ModbusTcpConfig ModbusTcp { get; set; }

        /// <summary>最后一次成功连接的 OPC DA 服务器 ProgId，用于下次启动时自动填充。</summary>
        public string LastConnectedProgId { get; set; }

        /// <summary>程序启动时是否自动连接 OPC DA 服务器。</summary>
        public bool AutoConnectDa { get; set; }

        /// <summary>程序启动时是否自动启动 Modbus TCP 网关服务器。</summary>
        public bool AutoStartModbus { get; set; }

        /// <summary>是否随 Windows 开机自动启动（通过启动文件夹快捷方式实现）。</summary>
        public bool AutoStartWithWindows { get; set; }

        /// <summary>是否启用看门狗进程守护（主进程崩溃时由看门狗自动重启）。</summary>
        public bool EnableWatchdog { get; set; }

        /// <summary>授权码，验证通过后保存到配置文件，下次启动时自动验证。</summary>
        public string AuthorizationCode { get; set; }

        /// <summary>
        /// 试用期起始时间（UTC），用于持久化试用倒计时。
        /// 首次进入试用模式时写入，授权成功后清空。
        /// 格式：ISO 8601 UTC，例如 "2026-09-16T10:30:00Z"。
        /// </summary>
        public string TrialStartUtc { get; set; }
    }
}
