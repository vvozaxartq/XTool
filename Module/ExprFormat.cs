using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using XTool;

namespace XTool.Module
{
    /// <summary>
    /// ExprFormat (Advanced)
    ///
    /// 預設輸出精簡：只回傳 valueText。
    /// 需要除錯資訊時：verbose=true
    ///
    /// 支援：+ - * /、()、一元正負號、小數、科學記號 e/E、函數、常數、%Key% 佔位符。
    ///
    /// 重要參數：
    ///   placeholderMode = number|expr（預設 number）
    ///     - number：%Key% 必須是數字（DownX= 空或非數字會直接報錯）
    ///     - expr  ：%Key% 可為算式片段 (7+2)/10
    ///
    /// 支援函數：
    ///   ABS(x), SQR(x), POW(x,y), SQRT(x)
    ///   SUM(x,y,...), AVG(x,y,...), MIN(x,y,...), MAX(x,y,...)
    ///   CLAMP(x,lo,hi), ROUND(x[,digits]), TRUNC(x[,digits])
    /// 常數：PI, E（enableConstants=false 可禁用）
    ///
    /// 進階控管：funcAllow/funcDeny（逗號分隔，大小寫不敏感；funcDeny 優先）
    /// </summary>
    public class ExprFormat : IExternalToolAction
    {
        public string Name { get { return "ExprFormat"; } }

        public Dictionary<string, string> Execute(Dictionary<string, string> input)
        {
            if (input == null)
                throw new ExternalToolException("NO_INPUT", "Input dictionary is null.");

            string expr;
            if (!input.TryGetValue("expr", out expr) || string.IsNullOrWhiteSpace(expr))
                throw new ExternalToolException("MISSING_EXPR", "Field 'expr' is required.");

            string format; input.TryGetValue("format", out format);
            string pattern; input.TryGetValue("pattern", out pattern);

            // truncate
            int truncateDigits = -1;
            string truncateStr;
            if (input.TryGetValue("truncate", out truncateStr) && !string.IsNullOrEmpty(truncateStr))
            {
                int tmp;
                if (!int.TryParse(truncateStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out tmp) || tmp < 0)
                    throw new ExternalToolException("INVALID_TRUNCATE", "truncate must be integer >= 0.");
                truncateDigits = tmp;
            }

            // verbose (default false)
            bool verbose = false;
            string verboseStr;
            if (input.TryGetValue("verbose", out verboseStr) && !string.IsNullOrEmpty(verboseStr))
                bool.TryParse(verboseStr, out verbose);

            // placeholderMode
            string placeholderMode;
            input.TryGetValue("placeholderMode", out placeholderMode);
            if (string.IsNullOrWhiteSpace(placeholderMode))
                placeholderMode = "number";

            // func allow/deny
            string funcAllowStr; input.TryGetValue("funcAllow", out funcAllowStr);
            string funcDenyStr; input.TryGetValue("funcDeny", out funcDenyStr);
            var allowSet = ParseNameList(funcAllowStr, true);
            var denySet = ParseNameList(funcDenyStr, false);

            // constants
            bool enableConstants = true;
            string enableConstStr;
            if (input.TryGetValue("enableConstants", out enableConstStr) && !string.IsNullOrEmpty(enableConstStr))
                bool.TryParse(enableConstStr, out enableConstants);

            // expand placeholders
            string expandedExpr = string.Equals(placeholderMode, "expr", StringComparison.OrdinalIgnoreCase)
                ? ExpandPlaceholdersAsExpression(expr, input)
                : ExpandPlaceholdersAsNumber(expr, input);

            double value;
            try
            {
                value = EvaluateExpression(expandedExpr, allowSet, denySet, enableConstants);
            }
            catch (ExternalToolException) { throw; }
            catch (Exception ex)
            {
                throw new ExternalToolException("EVAL_ERROR", "Expression evaluate failed: " + ex.Message);
            }

            // truncate first
            if (truncateDigits >= 0)
                value = TruncateDecimals(value, truncateDigits);

            string raw = value.ToString("G15", CultureInfo.InvariantCulture);

            string formatted = raw;
            if (!string.IsNullOrEmpty(format))
            {
                try { formatted = value.ToString(format, CultureInfo.InvariantCulture); }
                catch (Exception ex)
                {
                    throw new ExternalToolException("INVALID_FORMAT", "Invalid format string: " + ex.Message);
                }
            }

            string finalText = formatted;
            if (!string.IsNullOrEmpty(pattern))
            {
                try
                {
                    bool hasFormat = pattern.IndexOf("{0:", StringComparison.Ordinal) >= 0;
                    finalText = hasFormat
                        ? string.Format(CultureInfo.InvariantCulture, pattern, value)
                        : string.Format(CultureInfo.InvariantCulture, pattern, formatted);
                }
                catch (Exception ex)
                {
                    throw new ExternalToolException("INVALID_PATTERN", "Invalid pattern string: " + ex.Message);
                }
            }

            // minimal output
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            result["valueText"] = finalText;

            if (verbose)
            {
                result["expr"] = expandedExpr;
                result["valueRaw"] = raw;
                result["valueFormatted"] = formatted;
            }

            return result;
        }

        // strict placeholder: must be a number (prevents DownX= or DownX=abc)
        private static string ExpandPlaceholdersAsNumber(string expr, IDictionary<string, string> input)
        {
            return Regex.Replace(expr, @"@([^@]+)@", m =>
            {
                string key = m.Groups[1].Value.Trim();
                string val;

                if (!input.TryGetValue(key, out val) || string.IsNullOrWhiteSpace(val))
                    throw new ExternalToolException("MISSING_PLACEHOLDER", "Placeholder '@" + key + "@' has no value.");

                string trimmed = val.Trim();
                double num;

                if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out num))
                    throw new ExternalToolException("INVALID_PLACEHOLDER_NUMBER",
                        "Placeholder '@" + key + "@' must be a number, but got '" + trimmed + "'.");

                if (double.IsNaN(num) || double.IsInfinity(num))
                    throw new ExternalToolException("INVALID_PLACEHOLDER_NUMBER",
                        "Placeholder '@" + key + "@' is NaN/Infinity: '" + trimmed + "'.");

                // 用 G15 避免 7.2000000000000002
                return num.ToString("G15", CultureInfo.InvariantCulture);
            });
        }


        // relaxed placeholder: allow expression fragments (still disallow empty)
        private static string ExpandPlaceholdersAsExpression(string expr, IDictionary<string, string> input)
        {
            return Regex.Replace(expr, @"@([^@]+)@", m =>
            {
                string key = m.Groups[1].Value.Trim();
                string val;

                if (!input.TryGetValue(key, out val) || string.IsNullOrWhiteSpace(val))
                    throw new ExternalToolException("MISSING_PLACEHOLDER", "Placeholder '@" + key + "@' has no value.");

                return val.Trim();
            });
        }


        private static HashSet<string> ParseNameList(string list, bool defaultAll)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(list))
            {
                if (defaultAll) set.Add("*");
                return set;
            }
            var parts = list.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i].Trim();
                if (p.Length > 0) set.Add(p);
            }
            return set;
        }

        private static double TruncateDecimals(double value, int digits)
        {
            if (digits < 0) return value;
            double factor = Math.Pow(10.0, digits);
            if (double.IsInfinity(factor) || factor == 0.0) return value;
            double tmp = value * factor;
            tmp = Math.Truncate(tmp);
            return tmp / factor;
        }

        // ================= Parser =================
        private class ExpressionState
        {
            public readonly string Text;
            public int Position;
            public ExpressionState(string text) { Text = text ?? string.Empty; Position = 0; }
            public void SkipSpaces() { while (Position < Text.Length && char.IsWhiteSpace(Text[Position])) Position++; }
            public bool IsEnd() { return Position >= Text.Length; }
            public char CurrentChar { get { return Position < Text.Length ? Text[Position] : '\0'; } }
        }

        private static double EvaluateExpression(string expr, HashSet<string> allowSet, HashSet<string> denySet, bool enableConstants)
        {
            var s = new ExpressionState(expr);
            double value = ParseExpression(s, allowSet, denySet, enableConstants);
            s.SkipSpaces();
            if (!s.IsEnd())
                throw new ExternalToolException("UNEXPECTED_CHAR", "Unexpected character at position " + s.Position + ".");
            return value;
        }

        // Expression := Term (('+' | '-') Term)*
        private static double ParseExpression(ExpressionState s, HashSet<string> allowSet, HashSet<string> denySet, bool enableConstants)
        {
            double value = ParseTerm(s, allowSet, denySet, enableConstants);
            while (true)
            {
                s.SkipSpaces();
                char c = s.CurrentChar;
                if (c == '+' || c == '-')
                {
                    s.Position++;
                    double right = ParseTerm(s, allowSet, denySet, enableConstants);
                    value = (c == '+') ? value + right : value - right;
                }
                else break;
            }
            return value;
        }

        // Term := Factor (('*' | '/') Factor)*
        private static double ParseTerm(ExpressionState s, HashSet<string> allowSet, HashSet<string> denySet, bool enableConstants)
        {
            double value = ParseFactor(s, allowSet, denySet, enableConstants);
            while (true)
            {
                s.SkipSpaces();
                char c = s.CurrentChar;
                if (c == '*' || c == '/')
                {
                    s.Position++;
                    double right = ParseFactor(s, allowSet, denySet, enableConstants);
                    if (c == '*') value *= right;
                    else
                    {
                        if (right == 0.0)
                            throw new ExternalToolException("DIVIDE_BY_ZERO", "Division by zero.");
                        value /= right;
                    }
                }
                else break;
            }
            return value;
        }

        // Factor := ['+'|'-'] Factor | '(' Expression ')' | Function | Constant | Number
        private static double ParseFactor(ExpressionState s, HashSet<string> allowSet, HashSet<string> denySet, bool enableConstants)
        {
            s.SkipSpaces();
            char c = s.CurrentChar;

            // unary
            if (c == '+' || c == '-')
            {
                s.Position++;
                double inner = ParseFactor(s, allowSet, denySet, enableConstants);
                return (c == '-') ? -inner : inner;
            }

            // parentheses
            if (c == '(')
            {
                s.Position++;
                double v = ParseExpression(s, allowSet, denySet, enableConstants);
                s.SkipSpaces();
                if (s.CurrentChar != ')')
                    throw new ExternalToolException("MISSING_PAREN", "Missing closing parenthesis.");
                s.Position++;
                return v;
            }

            // identifier
            if (char.IsLetter(c))
            {
                int start = s.Position;
                while (!s.IsEnd() && (char.IsLetter(s.CurrentChar) || s.CurrentChar == '_'))
                    s.Position++;
                string ident = s.Text.Substring(start, s.Position - start).ToUpperInvariant();

                s.SkipSpaces();
                if (s.CurrentChar == '(')
                {
                    // function call
                    s.Position++;
                    var args = new List<double>();
                    s.SkipSpaces();

                    if (s.CurrentChar != ')')
                    {
                        while (true)
                        {
                            double arg = ParseExpression(s, allowSet, denySet, enableConstants);
                            args.Add(arg);
                            s.SkipSpaces();
                            if (s.CurrentChar == ',') { s.Position++; s.SkipSpaces(); continue; }
                            if (s.CurrentChar == ')') break;
                            throw new ExternalToolException("MISSING_PAREN", "Missing closing parenthesis for function.");
                        }
                    }

                    s.Position++; // ')'
                    return EvalFunction(ident, args, allowSet, denySet);
                }
                else
                {
                    if (!enableConstants)
                        throw new ExternalToolException("CONST_DISABLED", "Constants are disabled.");

                    if (ident == "PI") return Math.PI;
                    if (ident == "E") return Math.E;
                    throw new ExternalToolException("UNKNOWN_IDENT", "Unknown identifier '" + ident + "'.");
                }
            }

            // number
            int startNum = s.Position;
            bool hasDot = false;
            bool hasExp = false;
            while (!s.IsEnd())
            {
                c = s.CurrentChar;
                if (c >= '0' && c <= '9') s.Position++;
                else if (c == '.')
                {
                    if (hasDot) break;
                    hasDot = true; s.Position++;
                }
                else if (c == 'e' || c == 'E')
                {
                    if (hasExp || s.Position == startNum) break;
                    hasExp = true; s.Position++;
                    if (!s.IsEnd())
                    {
                        char next = s.CurrentChar;
                        if (next == '+' || next == '-') s.Position++;
                    }
                }
                else break;
            }

            if (startNum == s.Position)
                throw new ExternalToolException("EXPECTED_NUMBER", "Expected number at position " + startNum + ".");

            string token = s.Text.Substring(startNum, s.Position - startNum);
            double val;
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
                throw new ExternalToolException("INVALID_NUMBER", "Invalid number '" + token + "'.");

            if (double.IsNaN(val) || double.IsInfinity(val))
                throw new ExternalToolException("INVALID_NUMBER", "Number is NaN/Infinity: '" + token + "'.");

            return val;
        }

        private static double EvalFunction(string name, List<double> args, HashSet<string> allowSet, HashSet<string> denySet)
        {
            if (denySet != null && denySet.Contains(name))
                throw new ExternalToolException("FUNC_DISABLED", "Function '" + name + "' is disabled.");

            bool allAllowed = (allowSet != null && allowSet.Contains("*"));
            if (!allAllowed && (allowSet == null || !allowSet.Contains(name)))
                throw new ExternalToolException("FUNC_NOT_ALLOWED", "Function '" + name + "' is not allowed.");

            switch (name)
            {
                case "ABS":
                    RequireArgs(name, args, 1, 1);
                    return Math.Abs(args[0]);

                case "SQR":
                    RequireArgs(name, args, 1, 1);
                    return args[0] * args[0];

                case "POW":
                    RequireArgs(name, args, 2, 2);
                    return Math.Pow(args[0], args[1]);

                case "SQRT":
                    RequireArgs(name, args, 1, 1);
                    if (args[0] < 0)
                        throw new ExternalToolException("DOMAIN_ERROR", "SQRT domain error (x < 0).");
                    return Math.Sqrt(args[0]);

                case "SUM":
                    RequireArgs(name, args, 1, int.MaxValue);
                    double sum = 0.0;
                    for (int i = 0; i < args.Count; i++) sum += args[i];
                    return sum;

                case "AVG":
                    RequireArgs(name, args, 1, int.MaxValue);
                    double ssum = 0.0;
                    for (int i = 0; i < args.Count; i++) ssum += args[i];
                    return ssum / args.Count;

                case "MIN":
                    RequireArgs(name, args, 1, int.MaxValue);
                    double mn = args[0];
                    for (int i = 1; i < args.Count; i++) if (args[i] < mn) mn = args[i];
                    return mn;

                case "MAX":
                    RequireArgs(name, args, 1, int.MaxValue);
                    double mx = args[0];
                    for (int i = 1; i < args.Count; i++) if (args[i] > mx) mx = args[i];
                    return mx;

                case "CLAMP":
                    RequireArgs(name, args, 3, 3);
                    if (args[1] > args[2])
                        throw new ExternalToolException("ARG_ORDER", "CLAMP requires lo <= hi.");
                    return Math.Min(Math.Max(args[0], args[1]), args[2]);

                case "ROUND":
                    if (args.Count == 1)
                        return Math.Round(args[0], 0, MidpointRounding.AwayFromZero);
                    if (args.Count == 2)
                        return Math.Round(args[0], (int)Math.Round(args[1]), MidpointRounding.AwayFromZero);
                    throw new ExternalToolException("ARG_COUNT", "ROUND(x[,digits])");

                case "TRUNC":
                    if (args.Count == 1)
                        return Math.Truncate(args[0]);
                    if (args.Count == 2)
                        return TruncateDecimals(args[0], (int)Math.Round(args[1]));
                    throw new ExternalToolException("ARG_COUNT", "TRUNC(x[,digits])");

                default:
                    throw new ExternalToolException("UNKNOWN_FUNC", "Unknown function '" + name + "'.");
            }
        }

        private static void RequireArgs(string name, List<double> args, int min, int max)
        {
            if (args == null || args.Count < min || args.Count > max)
                throw new ExternalToolException("ARG_COUNT", name + " requires " + min + ".." + max + " args.");
        }
    }
}
