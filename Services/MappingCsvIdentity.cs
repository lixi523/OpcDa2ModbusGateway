using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OpcDaToModbusGateway.Services
{
    public static class MappingCsvIdentity
    {
        public static string FormatSequence(int sequence, string tagKey)
        {
            if (string.IsNullOrEmpty(tagKey)) throw new ArgumentException("TagKey 不能为空。", nameof(tagKey));
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(tagKey)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return sequence + "|" + encoded;
        }

        public static bool TryParseSequence(string value, out int sequence, out string tagKey)
        {
            sequence = 0;
            tagKey = null;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string[] parts = value.Trim().Split(new[] { '|' }, 2);
            if (!int.TryParse(parts[0], out sequence)) return false;
            if (parts.Length == 1) return true;
            try
            {
                string base64 = parts[1].Replace('-', '+').Replace('_', '/');
                base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
                tagKey = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                return !string.IsNullOrEmpty(tagKey);
            }
            catch (FormatException) { return false; }
        }

        public static void ValidateLegacyItemIds(IEnumerable<string> currentItemIds, IEnumerable<string> csvItemIds)
        {
            if (HasDuplicates(currentItemIds) || HasDuplicates(csvItemIds))
                throw new InvalidOperationException("旧纯数字 CSV 格式无法区分重复 ItemId，请重新导出包含 TagKey 的映射表。");
        }

        private static bool HasDuplicates(IEnumerable<string> values)
        {
            return values.GroupBy(v => v ?? "", StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
        }
    }
}
