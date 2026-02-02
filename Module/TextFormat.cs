
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using XTool;

namespace XTool.Module
{
    /// <summary>
    /// TextFormat
    /// 字串處理工具（支援單步 op 或多步 pipeline）
    ///
    /// 必要參數：
    ///   text=...
    ///
    /// 單步模式：
    ///   op=upper|lower|trim|replace|regexReplace|substring|truncate|padLeft|padRight|prefix|suffix|removePrefix|removeSuffix|splitJoin|format|sanitizeFileName|defaultIfEmpty|normalizeWhitespace|title|swapCase
    ///   搭配參數依 op 而定（例如 replaceOld/replaceNew）
    ///
    /// Pipeline 模式（推薦）：
    ///   pipeline=[ { "op":"trim" }, { "op":"upper" }, { "op":"replace", "old":"-", "new":"_" } ]
    ///
    /// 輸出：
    ///   valueText (必回)
    ///   verbose=true 時額外回：valueRaw, length, steps
    /// </summary>
    public class TextFormat : IExternalToolAction
    {
        public string Name { get { return "TextFormat"; } }

        public Dictionary<string, string> Execute(Dictionary<string, string> input)
        {
            if (input == null)
                throw new ExternalToolException("NO_INPUT", "Input dictionary is null.");

            string text;
            if (!input.TryGetValue("text", out text))
                throw new ExternalToolException("MISSING_TEXT", "Field 'text' is required.");

            // verbose
            bool verbose = false;
            string verboseStr;
            if (input.TryGetValue("verbose", out verboseStr) && !string.IsNullOrEmpty(verboseStr))
                bool.TryParse(verboseStr, out verbose);

            // optional: maxInputLen safeguard
            int maxInputLen = GetInt(input, "maxInputLen", 200000, 1, 5_000_000); // default 200k
            if (text != null && text.Length > maxInputLen)
                throw new ExternalToolException("TEXT_TOO_LONG", $"text length {text.Length} exceeds maxInputLen {maxInputLen}.");

            string result = text ?? string.Empty;
            var stepsApplied = new List<string>();

            // 1) pipeline (recommended)
            string pipelineStr;
            if (input.TryGetValue("pipeline", out pipelineStr) && !string.IsNullOrWhiteSpace(pipelineStr))
            {
                ApplyPipeline(ref result, pipelineStr, stepsApplied);
            }
            else
            {
                // 2) single op mode
                string op;
                if (input.TryGetValue("op", out op) && !string.IsNullOrWhiteSpace(op))
                {
                    ApplySingleOp(ref result, op.Trim(), input, stepsApplied);
                }
                // else: no-op, return original text
            }

            var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            output["valueText"] = result;

            if (verbose)
            {
                output["valueRaw"] = result;
                output["length"] = (result ?? string.Empty).Length.ToString(CultureInfo.InvariantCulture);
                output["steps"] = string.Join(" -> ", stepsApplied);
            }

            return output;
        }

        // ---------------- Pipeline ----------------

        private static void ApplyPipeline(ref string text, string pipelineStr, List<string> steps)
        {
            pipelineStr = pipelineStr.Trim();

            // Allow either JSON array OR a simple pipe string like: "trim|upper|replace(old=-,new=_)" (optional)
            if (pipelineStr.StartsWith("["))
            {
                JArray arr;
                try { arr = JArray.Parse(pipelineStr); }
                catch (Exception ex)
                {
                    throw new ExternalToolException("INVALID_PIPELINE", "pipeline JSON parse failed: " + ex.Message);
                }

                foreach (var item in arr)
                {
                    if (item == null) continue;

                    if (item.Type == JTokenType.String)
                    {
                        // allow "trim" style
                        ApplyOpObject(ref text, new JObject { ["op"] = item.ToString() }, steps);
                        continue;
                    }

                    if (item.Type != JTokenType.Object)
                        throw new ExternalToolException("INVALID_PIPELINE", "Each pipeline step must be an object or string.");

                    ApplyOpObject(ref text, (JObject)item, steps);
                }
            }
            else
            {
                // simple format: trim|upper|lower
                foreach (var token in pipelineStr.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var opName = token.Trim();
                    if (opName.Length == 0) continue;
                    ApplyOpObject(ref text, new JObject { ["op"] = opName }, steps);
                }
            }
        }

        private static void ApplyOpObject(ref string text, JObject step, List<string> steps)
        {
            string op = step["op"]?.ToString();
            if (string.IsNullOrWhiteSpace(op))
                throw new ExternalToolException("MISSING_OP", "Pipeline step missing 'op'.");

            op = op.Trim();

            // For each op, pull args from step object
            switch (op.ToLowerInvariant())
            {
                case "trim":
                    text = (text ?? string.Empty).Trim();
                    steps.Add("trim");
                    break;

                case "trimstart":
                    text = (text ?? string.Empty).TrimStart();
                    steps.Add("trimStart");
                    break;

                case "trimend":
                    text = (text ?? string.Empty).TrimEnd();
                    steps.Add("trimEnd");
                    break;

                case "upper":
                    text = (text ?? string.Empty).ToUpperInvariant();
                    steps.Add("upper");
                    break;

                case "lower":
                    text = (text ?? string.Empty).ToLowerInvariant();
                    steps.Add("lower");
                    break;

                case "title":
                    text = ToTitleCaseInvariant(text ?? string.Empty);
                    steps.Add("title");
                    break;

                case "swapcase":
                    text = SwapCase(text ?? string.Empty);
                    steps.Add("swapCase");
                    break;

                case "normalizewhitespace":
                    {
                        bool trim = GetBool(step, "trim", true);
                        text = NormalizeWhitespace(text ?? string.Empty, trim);
                        steps.Add("normalizeWhitespace");
                        break;
                    }

                case "replace":
                    {
                        string oldVal = step["old"]?.ToString() ?? "";
                        string newVal = step["new"]?.ToString() ?? "";
                        bool ignoreCase = GetBool(step, "ignoreCase", false);

                        if (oldVal.Length == 0)
                            throw new ExternalToolException("MISSING_ARG", "replace requires 'old'.");

                        text = Replace(text ?? string.Empty, oldVal, newVal, ignoreCase);
                        steps.Add($"replace('{oldVal}'->'{newVal}',ignoreCase={ignoreCase})");
                        break;
                    }

                case "regexreplace":
                    {
                        string pattern = step["pattern"]?.ToString();
                        string replacement = step["replacement"]?.ToString() ?? "";
                        string optionsStr = step["options"]?.ToString() ?? "";
                        int timeoutMs = GetInt(step, "timeoutMs", 200, 1, 5000);

                        if (string.IsNullOrEmpty(pattern))
                            throw new ExternalToolException("MISSING_ARG", "regexReplace requires 'pattern'.");

                        var options = ParseRegexOptions(optionsStr);
                        try
                        {
                            var rgx = new Regex(pattern, options, TimeSpan.FromMilliseconds(timeoutMs));
                            text = rgx.Replace(text ?? string.Empty, replacement);
                        }
                        catch (Exception ex)
                        {
                            throw new ExternalToolException("REGEX_ERROR", ex.Message);
                        }

                        steps.Add("regexReplace");
                        break;
                    }

                case "substring":
                    {
                        int start = GetInt(step, "start", 0, 0, int.MaxValue);
                        int length = GetInt(step, "length", -1, -1, int.MaxValue);
                        text = SubstringSafe(text ?? string.Empty, start, length);
                        steps.Add($"substring({start},{length})");
                        break;
                    }

                case "truncate":
                    {
                        int maxLen = GetInt(step, "maxLen", 0, 0, int.MaxValue);
                        string ellipsis = step["ellipsis"]?.ToString() ?? "";
                        text = Truncate(text ?? string.Empty, maxLen, ellipsis);
                        steps.Add($"truncate({maxLen})");
                        break;
                    }

                case "padleft":
                    {
                        int width = GetInt(step, "width", 0, 0, 1_000_000);
                        char padChar = GetChar(step, "char", ' ');
                        text = (text ?? string.Empty).PadLeft(width, padChar);
                        steps.Add($"padLeft({width},'{padChar}')");
                        break;
                    }

                case "padright":
                    {
                        int width = GetInt(step, "width", 0, 0, 1_000_000);
                        char padChar = GetChar(step, "char", ' ');
                        text = (text ?? string.Empty).PadRight(width, padChar);
                        steps.Add($"padRight({width},'{padChar}')");
                        break;
                    }

                case "prefix":
                    {
                        string v = step["value"]?.ToString() ?? "";
                        text = v + (text ?? string.Empty);
                        steps.Add("prefix");
                        break;
                    }

                case "suffix":
                    {
                        string v = step["value"]?.ToString() ?? "";
                        text = (text ?? string.Empty) + v;
                        steps.Add("suffix");
                        break;
                    }

                case "removeprefix":
                    {
                        string v = step["value"]?.ToString() ?? "";
                        bool ignoreCase = GetBool(step, "ignoreCase", false);
                        text = RemovePrefix(text ?? string.Empty, v, ignoreCase);
                        steps.Add("removePrefix");
                        break;
                    }

                case "removesuffix":
                    {
                        string v = step["value"]?.ToString() ?? "";
                        bool ignoreCase = GetBool(step, "ignoreCase", false);
                        text = RemoveSuffix(text ?? string.Empty, v, ignoreCase);
                        steps.Add("removeSuffix");
                        break;
                    }

                case "splitjoin":
                    {
                        string delimiter = step["delimiter"]?.ToString();
                        string joiner = step["joiner"]?.ToString() ?? "";
                        if (delimiter == null)
                            throw new ExternalToolException("MISSING_ARG", "splitJoin requires 'delimiter'.");
                        var parts = (text ?? string.Empty).Split(new[] { delimiter }, StringSplitOptions.None);
                        text = string.Join(joiner, parts);
                        steps.Add("splitJoin");
                        break;
                    }

                case "format":
                    {
                        string pattern = step["pattern"]?.ToString() ?? "{0}";
                        try
                        {
                            text = string.Format(CultureInfo.InvariantCulture, pattern, text ?? string.Empty);
                        }
                        catch (Exception ex)
                        {
                            throw new ExternalToolException("INVALID_PATTERN", ex.Message);
                        }
                        steps.Add("format");
                        break;
                    }

                case "sanitizefilename":
                    {
                        string replacement = step["replacement"]?.ToString() ?? "_";
                        text = SanitizeFileName(text ?? string.Empty, replacement);
                        steps.Add("sanitizeFileName");
                        break;
                    }

                case "defaultifempty":
                    {
                        string v = step["value"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(text))
                            text = v;
                        steps.Add("defaultIfEmpty");
                        break;
                    }

                default:
                    throw new ExternalToolException("UNKNOWN_OP", "Unknown op: " + op);
            }
        }

        // ---------------- Single op mode (key=value) ----------------

        private static void ApplySingleOp(ref string text, string op, IDictionary<string, string> input, List<string> steps)
        {
            // Build a JObject from flat input keys for convenience
            // (So we can reuse ApplyOpObject)
            var step = new JObject { ["op"] = op };

            // Common args mapping (for key=value style)
            // - replace: replaceOld, replaceNew, replaceIgnoreCase
            // - regexReplace: regexPattern, regexReplacement, regexOptions, regexTimeoutMs
            // - substring: start, length
            // - truncate: maxLen, ellipsis
            // - pad: width, char
            // - prefix/suffix/removePrefix/removeSuffix: value, ignoreCase
            // - splitJoin: delimiter, joiner
            // - format: pattern
            // - sanitizeFileName: replacement
            // - defaultIfEmpty: value
            MapIfExists(input, step, "old", "replaceOld");
            MapIfExists(input, step, "new", "replaceNew");
            MapIfExists(input, step, "ignoreCase", "replaceIgnoreCase");

            MapIfExists(input, step, "pattern", "regexPattern");
            MapIfExists(input, step, "replacement", "regexReplacement");
            MapIfExists(input, step, "options", "regexOptions");
            MapIfExists(input, step, "timeoutMs", "regexTimeoutMs");

            MapIfExists(input, step, "start", "start");
            MapIfExists(input, step, "length", "length");

            MapIfExists(input, step, "maxLen", "maxLen");
            MapIfExists(input, step, "ellipsis", "ellipsis");

            MapIfExists(input, step, "width", "width");
            MapIfExists(input, step, "char", "char");

            MapIfExists(input, step, "value", "value");
            MapIfExists(input, step, "ignoreCase", "ignoreCase");

            MapIfExists(input, step, "delimiter", "delimiter");
            MapIfExists(input, step, "joiner", "joiner");

            MapIfExists(input, step, "pattern", "pattern");
            MapIfExists(input, step, "replacement", "replacement");

            ApplyOpObject(ref text, step, steps);
        }

        private static void MapIfExists(IDictionary<string, string> input, JObject target, string toKey, string fromKey)
        {
            string v;
            if (input.TryGetValue(fromKey, out v) && v != null)
                target[toKey] = v;
        }

        // ---------------- Helpers ----------------

        private static int GetInt(IDictionary<string, string> dict, string key, int defaultValue, int min, int max)
        {
            string s;
            if (!dict.TryGetValue(key, out s) || string.IsNullOrWhiteSpace(s))
                return defaultValue;

            int v;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                throw new ExternalToolException("INVALID_INT", $"{key} must be integer.");

            if (v < min || v > max)
                throw new ExternalToolException("INT_RANGE", $"{key} out of range [{min},{max}].");

            return v;
        }

        private static int GetInt(JObject jo, string key, int defaultValue, int min, int max)
        {
            var t = jo[key];
            if (t == null || t.Type == JTokenType.Null) return defaultValue;
            int v;
            if (!int.TryParse(t.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                throw new ExternalToolException("INVALID_INT", $"{key} must be integer.");
            if (v < min || v > max)
                throw new ExternalToolException("INT_RANGE", $"{key} out of range [{min},{max}].");
            return v;
        }

        private static bool GetBool(JObject jo, string key, bool defaultValue)
        {
            var t = jo[key];
            if (t == null || t.Type == JTokenType.Null) return defaultValue;
            bool v;
            if (!bool.TryParse(t.ToString(), out v))
            {
                // allow 0/1
                if (t.ToString() == "0") return false;
                if (t.ToString() == "1") return true;
                return defaultValue;
            }
            return v;
        }

        private static char GetChar(JObject jo, string key, char defaultValue)
        {
            var t = jo[key];
            if (t == null || t.Type == JTokenType.Null) return defaultValue;
            var s = t.ToString();
            return string.IsNullOrEmpty(s) ? defaultValue : s[0];
        }

        private static RegexOptions ParseRegexOptions(string opt)
        {
            RegexOptions o = RegexOptions.None;
            if (string.IsNullOrWhiteSpace(opt)) return o;

            // simple flags: i,m,s
            foreach (char c in opt)
            {
                if (c == 'i' || c == 'I') o |= RegexOptions.IgnoreCase;
                else if (c == 'm' || c == 'M') o |= RegexOptions.Multiline;
                else if (c == 's' || c == 'S') o |= RegexOptions.Singleline;
            }
            return o;
        }

        private static string Replace(string input, string oldValue, string newValue, bool ignoreCase)
        {
            if (!ignoreCase) return input.Replace(oldValue, newValue);

            // case-insensitive replace via regex escape
            string pattern = Regex.Escape(oldValue);
            var rgx = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
            return rgx.Replace(input, newValue);
        }

        private static string SubstringSafe(string s, int start, int length)
        {
            if (s == null) return string.Empty;
            if (start >= s.Length) return string.Empty;
            if (start < 0) start = 0;

            if (length < 0) return s.Substring(start);
            if (start + length > s.Length) length = s.Length - start;
            return s.Substring(start, length);
        }

        private static string Truncate(string s, int maxLen, string ellipsis)
        {
            if (s == null) return string.Empty;
            if (maxLen <= 0) return string.Empty;
            if (s.Length <= maxLen) return s;

            if (string.IsNullOrEmpty(ellipsis)) return s.Substring(0, maxLen);
            if (ellipsis.Length >= maxLen) return ellipsis.Substring(0, maxLen);

            return s.Substring(0, maxLen - ellipsis.Length) + ellipsis;
        }

        private static string RemovePrefix(string s, string prefix, bool ignoreCase)
        {
            if (string.IsNullOrEmpty(prefix)) return s;
            if (s == null) return string.Empty;

            if (!ignoreCase)
                return s.StartsWith(prefix, StringComparison.Ordinal) ? s.Substring(prefix.Length) : s;

            return s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? s.Substring(prefix.Length) : s;
        }

        private static string RemoveSuffix(string s, string suffix, bool ignoreCase)
        {
            if (string.IsNullOrEmpty(suffix)) return s;
            if (s == null) return string.Empty;

            if (!ignoreCase)
                return s.EndsWith(suffix, StringComparison.Ordinal) ? s.Substring(0, s.Length - suffix.Length) : s;

            return s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? s.Substring(0, s.Length - suffix.Length) : s;
        }

        private static string NormalizeWhitespace(string s, bool trim)
        {
            if (s == null) return string.Empty;
            // Replace all whitespace runs with single space
            var rgx = new Regex(@"\s+", RegexOptions.None, TimeSpan.FromMilliseconds(200));
            s = rgx.Replace(s, " ");
            return trim ? s.Trim() : s;
        }

        private static string SanitizeFileName(string s, string replacement)
        {
            if (s == null) return string.Empty;
            if (replacement == null) replacement = "_";
            var invalid = Path.GetInvalidFileNameChars();

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (invalid.Contains(c))
                    sb.Append(replacement);
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static string ToTitleCaseInvariant(string s)
        {
            // Simple "title case": split by whitespace, capitalize first letter of each token
            if (string.IsNullOrEmpty(s)) return s;
            var parts = Regex.Split(s, @"(\s+)");
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (p.Length == 1) parts[i] = p.ToUpperInvariant();
                else parts[i] = char.ToUpperInvariant(p[0]) + p.Substring(1).ToLowerInvariant();
            }
            return string.Concat(parts);
        }

        private static string SwapCase(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (char.IsLetter(c))
                {
                    chars[i] = char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c);
                }
            }
            return new string(chars);
        }
    }
}
