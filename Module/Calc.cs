using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using XTool;

namespace XTool.Module
{
    public class Calc : IExternalToolAction
    {
        public string Name => "Calc";

        public Dictionary<string, string> Execute(Dictionary<string, string> input)
        {
            if (!input.TryGetValue("expr", out var expr) || string.IsNullOrWhiteSpace(expr))
                throw new ExternalToolException("NO_EXPR", "Field 'expr' is required.");

            // 運算
            double value;
            try
            {
                var dt = new DataTable();
                value = Convert.ToDouble(dt.Compute(expr, ""), CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                throw new ExternalToolException("EVAL_ERROR", $"Expression error: {ex.Message}");
            }

            // 格式化
            string format = input.ContainsKey("format") ? input["format"] : null;
            string result;

            // 判斷是否為整數
            if (!string.IsNullOrEmpty(format))
            {
                if (format.StartsWith("D", StringComparison.OrdinalIgnoreCase))
                {
                    // 只允許整數格式
                    if (Math.Abs(value % 1) < 1e-10)
                        result = ((int)value).ToString(format, CultureInfo.InvariantCulture);
                    else
                        throw new ExternalToolException("FORMAT_ERROR", "D 格式只適用於整數。");
                }
                else
                {
                    result = value.ToString(format, CultureInfo.InvariantCulture);
                }
            }
            else
            {
                // 自動判斷
                if (Math.Abs(value % 1) < 1e-10)
                    result = ((int)value).ToString("D1");
                else
                    result = value.ToString("F3", CultureInfo.InvariantCulture);

            }

            return new Dictionary<string, string>
            {
                ["result"] = result,
                ["raw"] = value.ToString(CultureInfo.InvariantCulture)
            };
        }
    }
}