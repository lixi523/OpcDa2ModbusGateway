using System;

namespace OpcDaToModbusGateway.Models
{
    /// <summary>
    /// Data type identifier enum for DA value conversion.
    /// </summary>
    public enum DataTypeId
    {
        Boolean,
        SByte,
        Byte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Float,
        Double,
        String,
        DateTime
    }

    /// <summary>
    /// Data type converter — maps string names to DataTypeId and provides default values.
    /// Thread-safe: all dictionaries are populated once in the static constructor.
    /// </summary>
    public static class DataTypeConverter
    {
        /// <summary>String name to DataTypeId mapping (case-insensitive).</summary>
        private static readonly System.Collections.Generic.Dictionary<string, DataTypeId> NameToType;

        /// <summary>DataTypeId to default value mapping.</summary>
        private static readonly System.Collections.Generic.Dictionary<DataTypeId, object> DefaultValues;

        static DataTypeConverter()
        {
            NameToType = new System.Collections.Generic.Dictionary<string, DataTypeId>(StringComparer.OrdinalIgnoreCase)
            {
                ["boolean"] = DataTypeId.Boolean,
                ["bool"] = DataTypeId.Boolean,
                ["sbyte"] = DataTypeId.SByte,
                ["byte"] = DataTypeId.Byte,
                ["int16"] = DataTypeId.Int16,
                ["short"] = DataTypeId.Int16,
                ["uint16"] = DataTypeId.UInt16,
                ["ushort"] = DataTypeId.UInt16,
                ["int32"] = DataTypeId.Int32,
                ["int"] = DataTypeId.Int32,
                ["uint32"] = DataTypeId.UInt32,
                ["uint"] = DataTypeId.UInt32,
                ["float"] = DataTypeId.Float,
                ["single"] = DataTypeId.Float,
                ["real4"] = DataTypeId.Float,
                ["double"] = DataTypeId.Double,
                ["real8"] = DataTypeId.Double,
                ["string"] = DataTypeId.String,
                ["datetime"] = DataTypeId.DateTime,
            };

            DefaultValues = new System.Collections.Generic.Dictionary<DataTypeId, object>
            {
                [DataTypeId.Boolean] = false,
                [DataTypeId.SByte] = (sbyte)0,
                [DataTypeId.Byte] = (byte)0,
                [DataTypeId.Int16] = (short)0,
                [DataTypeId.UInt16] = (ushort)0,
                [DataTypeId.Int32] = 0,
                [DataTypeId.UInt32] = 0u,
                [DataTypeId.Float] = 0.0f,
                [DataTypeId.Double] = 0.0,
                [DataTypeId.String] = "",
                [DataTypeId.DateTime] = DateTime.MinValue,
            };
        }

        /// <summary>
        /// Parse a string to DataTypeId.
        /// </summary>
        public static DataTypeId ParseDataType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return DataTypeId.Int32;
            // Strip "System." prefix if present (e.g., "System.Int32" -> "Int32")
            string normalized = typeName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                ? typeName.Substring(7)
                : typeName;
            return NameToType.TryGetValue(normalized, out var result) ? result : DataTypeId.Int32;
        }

        /// <summary>
        /// Get the default value for a given DataTypeId.
        /// </summary>
        public static object GetDefaultValue(DataTypeId typeId)
        {
            return DefaultValues.TryGetValue(typeId, out var value) ? value : 0;
        }

        /// <summary>
        /// Convert a value to the target type. Conversion errors are propagated.
        /// </summary>
        public static object ConvertValue(object value, DataTypeId targetType)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            switch (targetType)
            {
                case DataTypeId.Boolean: return System.Convert.ToBoolean(value);
                case DataTypeId.SByte: return System.Convert.ToSByte(value);
                case DataTypeId.Byte: return System.Convert.ToByte(value);
                case DataTypeId.Int16: return System.Convert.ToInt16(value);
                case DataTypeId.UInt16: return System.Convert.ToUInt16(value);
                case DataTypeId.Int32: return System.Convert.ToInt32(value);
                case DataTypeId.UInt32: return System.Convert.ToUInt32(value);
                case DataTypeId.Float: return System.Convert.ToSingle(value);
                case DataTypeId.Double: return System.Convert.ToDouble(value);
                case DataTypeId.String: return value.ToString();
                case DataTypeId.DateTime: return System.Convert.ToDateTime(value);
                default: return value;
            }
        }

        /// <summary>
        /// 根据 OPC DA 数据类型推断 Modbus 侧的数据类型。
        /// 映射规则（按 Modbus 寄存器占用）：
        ///   Boolean      → Bool    (线圈, 1 bit)
        ///   SByte/Byte   → Int16   (1 寄存器)
        ///   Int16/UInt16 → Int16   (1 寄存器)
        ///   Int32/UInt32 → Int32   (2 寄存器)
        ///   Float        → Float   (2 寄存器)
        ///   Double       → Double  (4 寄存器)
        ///   String       → String
        ///   DateTime     → DateTime
        /// </summary>
        public static string NormalizeModbusDataType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) throw new NotSupportedException("Modbus 数据类型不能为空。");
            switch (typeName.Trim().ToLowerInvariant())
            {
                case "bool": case "boolean": return "Bool";
                case "byte": return "Byte";
                case "sbyte": return "SByte";
                case "int16": case "short": return "Int16";
                case "uint16": case "ushort": return "UInt16";
                case "int32": case "int": return "Int32";
                case "uint32": case "uint": return "UInt32";
                case "float": case "single": case "real4": return "Float";
                case "double": case "real8": return "Double";
                default: throw new NotSupportedException($"不支持 Modbus 数据类型 '{typeName}'（String/DateTime 未定义 wire encoding）。");
            }
        }

        public static int GetModbusRegisterWidth(string typeName)
        {
            switch (NormalizeModbusDataType(typeName))
            {
                case "Int32": case "UInt32": case "Float": return 2;
                case "Double": return 4;
                default: return 1;
            }
        }

        public static ushort[] EncodeModbusRegisters(object value, string typeName)
        {
            string type = NormalizeModbusDataType(typeName);
            switch (type)
            {
                case "Bool": return new[] { Convert.ToBoolean(value) ? (ushort)1 : (ushort)0 };
                case "Byte": return new[] { (ushort)Convert.ToByte(value) };
                case "SByte": return new[] { unchecked((ushort)(short)Convert.ToSByte(value)) };
                case "Int16": return new[] { unchecked((ushort)Convert.ToInt16(value)) };
                case "UInt16": return new[] { Convert.ToUInt16(value) };
                case "Int32": return SplitWords(unchecked((uint)Convert.ToInt32(value)), 2);
                case "UInt32": return SplitWords(Convert.ToUInt32(value), 2);
                case "Float": return SplitWords(BitConverter.ToUInt32(BitConverter.GetBytes(Convert.ToSingle(value)), 0), 2);
                case "Double": return SplitWords(BitConverter.ToUInt64(BitConverter.GetBytes(Convert.ToDouble(value)), 0), 4);
                default: throw new NotSupportedException(type);
            }
        }

        private static ushort[] SplitWords(ulong bits, int count)
        {
            var result = new ushort[count];
            for (int i = 0; i < count; i++) result[i] = (ushort)(bits >> ((count - i - 1) * 16));
            return result;
        }

        /// <summary>
        /// 尝试根据 OPC DA 数据类型推断 Modbus wire type。
        /// 返回 false 表示该类型无法映射（Variant/Object/空/未知等），
        /// 这些类型可能在 DA 连接后由 CanonicalDataType 回写为真实类型，不应作为配置错误直接拒绝。
        /// </summary>
        public static bool TryGetModbusDataType(string opcDataType, out string modbusType)
        {
            modbusType = null;
            if (string.IsNullOrWhiteSpace(opcDataType)) return false;

            string trimmed = opcDataType.Trim();
            string normalized = trimmed.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                ? trimmed.Substring(7)
                : trimmed;
            if (!NameToType.TryGetValue(normalized, out DataTypeId id)) return false;

            switch (id)
            {
                case DataTypeId.Boolean: modbusType = "Bool"; return true;
                case DataTypeId.SByte:
                case DataTypeId.Int16: modbusType = "Int16"; return true;
                case DataTypeId.Byte:
                case DataTypeId.UInt16: modbusType = "UInt16"; return true;
                case DataTypeId.Int32: modbusType = "Int32"; return true;
                case DataTypeId.UInt32: modbusType = "UInt32"; return true;
                case DataTypeId.Float: modbusType = "Float"; return true;
                case DataTypeId.Double: modbusType = "Double"; return true;
                case DataTypeId.String: modbusType = "String"; return true;
                case DataTypeId.DateTime: modbusType = "DateTime"; return true;
                default: return false;
            }
        }

        public static string GetModbusDataType(string opcDataType)
        {
            if (!TryGetModbusDataType(opcDataType, out string modbusType))
            {
                string display = string.IsNullOrWhiteSpace(opcDataType) ? "(空)" : opcDataType;
                throw new NotSupportedException($"不支持 OPC DA 数据类型 '{display}'，无法推断 Modbus wire type。");
            }
            return modbusType;
        }
    }
}
