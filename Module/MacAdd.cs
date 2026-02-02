
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace XTool.Module
{
    /// <summary>
    /// action=MacAdd mac="000000000001" count=N [case=upper|lower]
    /// 單純 MAC 連續 +N、多筆輸出。
    ///
    /// 輸入：
    ///   action : "MacAdd"
    ///   mac    : 原始 MAC 字串，可含 - / _ / : 或完全不帶分隔
    ///   count  : 要產生幾筆結果 (整數 > 0)，例如 3 代表 MAC1 ~ MAC3
    ///   case   : (選填) "upper" 或 "lower"，預設 "upper"
    ///
    /// 輸出：
    ///   mac_input : 原始輸入 mac（原封不動，保留使用者輸入）
    ///   count     : 實際 count
    ///   case      : 最終採用的大小寫設定（"upper" 或 "lower"）
    ///
    ///   MAC0            : 依大小寫設定套用於原始 mac（保留原分隔符樣式）
    ///   MAC0_Plain      : 原始 mac 轉為純 HEX（不含任何分隔）
    ///   MAC0_Colon      : 純 HEX 以 ':' 分隔（XX:XX:...）
    ///   MAC0_Dash       : 純 HEX 以 '-' 分隔（XX-XX-...）【新增】
    ///
    ///   MAC1..MAC{count}        : 主輸出（有分隔則沿用模板；無分隔則為純 HEX）
    ///   MACi_Plain               : 純 HEX
    ///   MACi_Colon               : ':' 分隔
    ///   MACi_Dash                : '-' 分隔【新增】
    /// </summary>
    public class MacAdd : IExternalToolAction
    {
        public string Name => "MacAdd";

        public Dictionary<string, string> Execute(Dictionary<string, string> input)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 1) mac
            if (!input.TryGetValue("mac", out var mac) || string.IsNullOrEmpty(mac))
                throw new ExternalToolException("MISSING_MAC", "Field 'mac' is required.");

            // 2) count
            if (!input.TryGetValue("count", out var countStr) || !int.TryParse(countStr, out var count) || count <= 0)
                throw new ExternalToolException("INVALID_COUNT", "Field 'count' must be a positive integer.");

            // 3) case（預設 upper）
            bool toUpper = true;
            if (input.TryGetValue("case", out var caseStr) && !string.IsNullOrWhiteSpace(caseStr))
            {
                switch (caseStr.Trim().ToLowerInvariant())
                {
                    case "upper":
                    case "uppercase":
                    case "u":
                        toUpper = true; break;
                    case "lower":
                    case "lowercase":
                    case "l":
                        toUpper = false; break;
                    default:
                        throw new ExternalToolException("INVALID_CASE",
                            "Field 'case' must be 'upper' or 'lower' (also accepts 'uppercase'/'lowercase', 'u'/'l').");
                }
            }

            // 基本資訊
            result["mac_input"] = mac;
            result["count"] = count.ToString();
            result["case"] = toUpper ? "upper" : "lower";

            // 4) 過濾成 HEX 並驗證
            string hexUpper = Regex.Replace(mac, "[^0-9A-Fa-f]", string.Empty).ToUpperInvariant();
            if (hexUpper.Length == 0)
                throw new ExternalToolException("INVALID_MAC", "MAC has no hex characters.");
            if (hexUpper.Length % 2 != 0)
                throw new ExternalToolException("INVALID_MAC_LENGTH", "MAC hex length must be even.");

            // 5) 轉 byte[]
            byte[] bytes = HexStringToBytes(hexUpper);

            // 6) 判斷是否有分隔
            bool hasSeparator = HasNonHexChar(mac);

            // 依大小寫設定準備 HEX
            string hexCased = toUpper ? hexUpper : hexUpper.ToLowerInvariant();

            // MAC0
            result["MAC0"] = FormatLikeTemplate(mac, hexCased);
            result["MAC0_Plain"] = hexCased;
            result["MAC0_Colon"] = FormatWithSeparator(hexCased, ':');
            result["MAC0_Dash"] = FormatWithSeparator(hexCased, '-'); // << 新增

            // 連續輸出
            for (int i = 1; i <= count; i++)
            {
                AddOne(bytes);
                string newHex = BytesToHexString(bytes, toUpper);

                // 主輸出（沿用模板或純 HEX）
                string formatted = hasSeparator ? FormatLikeTemplate(mac, newHex)
                                                : newHex;

                result["MAC" + i] = formatted;
                result["MAC" + i + "_Plain"] = newHex;
                result["MAC" + i + "_Colon"] = FormatWithSeparator(newHex, ':');
                result["MAC" + i + "_Dash"] = FormatWithSeparator(newHex, '-'); // << 新增
            }

            return result;
        }

        private static void AddOne(byte[] bytes)
        {
            int carry = 1;
            for (int i = bytes.Length - 1; i >= 0 && carry > 0; i--)
            {
                int value = bytes[i] + carry;
                bytes[i] = (byte)(value & 0xFF);
                carry = value >> 8;
            }
        }

        private static byte[] HexStringToBytes(string hex)
        {
            int len = hex.Length / 2;
            byte[] data = new byte[len];
            for (int i = 0; i < len; i++)
            {
                string sub = hex.Substring(i * 2, 2);
                try
                {
                    data[i] = Convert.ToByte(sub, 16);
                }
                catch (Exception ex)
                {
                    throw new ExternalToolException("INVALID_MAC_HEX",
                        "MAC contains invalid hex value: " + sub + ". " + ex.Message);
                }
            }
            return data;
        }

        private static string BytesToHexString(byte[] data, bool upper)
        {
            string fmt = upper ? "X2" : "x2";
            var sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++)
                sb.Append(data[i].ToString(fmt));
            return sb.ToString();
        }

        private static bool IsHexChar(char c)
        {
            return (c >= '0' && c <= '9') ||
                   (c >= 'A' && c <= 'F') ||
                   (c >= 'a' && c <= 'f');
        }

        private static bool HasNonHexChar(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
                if (!IsHexChar(s[i])) return true;
            return false;
        }

        private static string FormatLikeTemplate(string template, string newHex)
        {
            var sb = new StringBuilder();
            int hexIndex = 0;
            for (int i = 0; i < template.Length; i++)
            {
                char c = template[i];
                if (IsHexChar(c))
                {
                    if (hexIndex < newHex.Length)
                        sb.Append(newHex[hexIndex++]);
                }
                else
                {
                    sb.Append(c);
                }
            }
            if (sb.Length == 0) sb.Append(newHex);
            return sb.ToString();
        }

        private static string FormatWithSeparator(string hex, char sep)
        {
            if (hex == null) return string.Empty;
            if (hex.Length % 2 != 0) return hex;

            var sb = new StringBuilder();
            for (int i = 0; i < hex.Length; i += 2)
            {
                if (i > 0) sb.Append(sep);
                sb.Append(hex[i]);
                sb.Append(hex[i + 1]);
            }
            return sb.ToString();
        }
    }
}

