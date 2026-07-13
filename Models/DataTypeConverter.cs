using System;
using System.Collections.Generic;
using Opc.Ua;

namespace OpcDaToUaGateway.Models
{
    /// <summary>
    /// OPC UA 数据类型映射的单一真源（Single Source of Truth）。
    ///
    /// <para>P0-2 重构：原先 GetDataTypeId / GetDefaultValue / ParseDataType 三处重复的
    /// switch-case 映射分散在 GatewayNodeManager 和 GatewayOpcUaServer 中。
    /// 现在全部收敛到此静态类，使用 Dictionary 缓存实现 O(1) 查找。</para>
    ///
    /// <para>线程安全：所有字典在静态构造器中一次性填充，此后再无写入操作，
    /// 因此可以安全地在多线程环境中并发读取，无需任何同步机制。</para>
    /// </summary>
    public static class DataTypeConverter
    {
        // ================================================================
        //  BuiltInType → DataType NodeId 映射
        // ================================================================
        private static readonly Dictionary<BuiltInType, NodeId> DataTypeNodeIds;

        // ================================================================
        //  BuiltInType → 默认值映射
        // ================================================================
        private static readonly Dictionary<BuiltInType, object> DefaultValues;

        // ================================================================
        //  字符串名称 → BuiltInType 映射（支持别名，大小写不敏感）
        // ================================================================
        private static readonly Dictionary<string, BuiltInType> NameToType;

        /// <summary>
        /// 静态构造器 — 一次性填充所有字典，后续零分配纯查找。
        /// </summary>
        static DataTypeConverter()
        {
            DataTypeNodeIds = new Dictionary<BuiltInType, NodeId>
            {
                [BuiltInType.Boolean]  = DataTypeIds.Boolean,
                [BuiltInType.SByte]    = DataTypeIds.SByte,
                [BuiltInType.Byte]     = DataTypeIds.Byte,
                [BuiltInType.Int16]    = DataTypeIds.Int16,
                [BuiltInType.Int32]    = DataTypeIds.Int32,
                [BuiltInType.Int64]    = DataTypeIds.Int64,
                [BuiltInType.UInt16]   = DataTypeIds.UInt16,
                [BuiltInType.UInt32]   = DataTypeIds.UInt32,
                [BuiltInType.UInt64]   = DataTypeIds.UInt64,
                [BuiltInType.Float]    = DataTypeIds.Float,
                [BuiltInType.Double]   = DataTypeIds.Double,
                [BuiltInType.String]   = DataTypeIds.String,
                [BuiltInType.DateTime] = DataTypeIds.DateTime,
            };

            DefaultValues = new Dictionary<BuiltInType, object>
            {
                [BuiltInType.Boolean]  = false,
                [BuiltInType.SByte]    = (sbyte)0,
                [BuiltInType.Byte]     = (byte)0,
                [BuiltInType.Int16]    = (short)0,
                [BuiltInType.Int32]    = (int)0,
                [BuiltInType.Int64]    = 0L,
                [BuiltInType.UInt16]   = (ushort)0,
                [BuiltInType.UInt32]   = (uint)0,
                [BuiltInType.UInt64]   = 0UL,
                [BuiltInType.Float]    = 0.0f,
                [BuiltInType.Double]   = 0.0,
                [BuiltInType.String]   = string.Empty,
                [BuiltInType.DateTime] = DateTime.MinValue,
            };

            NameToType = new Dictionary<string, BuiltInType>(StringComparer.OrdinalIgnoreCase)
            {
                ["boolean"] = BuiltInType.Boolean,
                ["bool"]    = BuiltInType.Boolean,
                ["sbyte"]   = BuiltInType.SByte,
                ["byte"]    = BuiltInType.Byte,
                ["int16"]   = BuiltInType.Int16,
                ["short"]   = BuiltInType.Int16,
                ["int32"]   = BuiltInType.Int32,
                ["int"]     = BuiltInType.Int32,
                ["int64"]   = BuiltInType.Int64,
                ["long"]    = BuiltInType.Int64,
                ["uint16"]  = BuiltInType.UInt16,
                ["ushort"]  = BuiltInType.UInt16,
                ["uint32"]  = BuiltInType.UInt32,
                ["uint"]    = BuiltInType.UInt32,
                ["uint64"]  = BuiltInType.UInt64,
                ["ulong"]   = BuiltInType.UInt64,
                ["float"]   = BuiltInType.Float,
                ["real4"]   = BuiltInType.Float,
                ["double"]  = BuiltInType.Double,
                ["real8"]   = BuiltInType.Double,
                ["string"]  = BuiltInType.String,
                ["datetime"] = BuiltInType.DateTime,
            };
        }

        /// <summary>
        /// 将 BuiltInType 映射为对应的 UA 标准 DataType NodeId。
        /// 未识别的类型回退到 BaseDataType（等同于 Variant）。
        /// </summary>
        /// <param name="type">UA 内置数据类型枚举。</param>
        /// <returns>UA 标准 DataType 的 NodeId。</returns>
        public static NodeId GetDataTypeNodeId(BuiltInType type)
        {
            return DataTypeNodeIds.TryGetValue(type, out var nodeId)
                ? nodeId
                : DataTypeIds.BaseDataType;
        }

        /// <summary>
        /// 为指定数据类型生成安全的默认值，用于变量节点创建时的初始赋值。
        /// 未识别的类型返回 null（将序列化为 UA Variant null）。
        /// </summary>
        /// <param name="type">UA 内置数据类型枚举。</param>
        /// <returns>该类型的零值对象。</returns>
        public static object GetDefaultValue(BuiltInType type)
        {
            return DefaultValues.TryGetValue(type, out var value) ? value : null;
        }

        /// <summary>
        /// 将配置文件中指定的数据类型名称解析为 UA BuiltInType 枚举。
        /// 支持常见别名（如 "bool" → Boolean, "real4" → Float）。
        /// 无法识别的类型回退到 Variant（UA 的通用类型，可容纳任意类型值）。
        /// </summary>
        /// <param name="typeName">数据类型名称（大小写不敏感）。</param>
        /// <returns>对应的 BuiltInType 枚举值。</returns>
        public static BuiltInType ParseDataType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return BuiltInType.Variant;
            return NameToType.TryGetValue(typeName.Trim(), out var type)
                ? type
                : BuiltInType.Variant;
        }
    }
}
