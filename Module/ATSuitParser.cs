using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using XTool;

namespace XTool.Module
{
    internal class ATSuitParser : IExternalToolAction
    {
        //XTool.exe action=ATSuitParser log=D:\D19AA00F7_VO0301_FT05_RF_V0.0.1.2_20251022161648_304972.log
        class FailItem
        {
            public string TestName;
            public int Frequency;
            public string Antenna;
            public string MetricName;
            public bool AllRetryFail;
        }

        public string Name { get { return "ATSuitParser"; } }

        public Dictionary<string, string> Execute(Dictionary<string, string> input)
        {
            if (input == null)
                throw new ExternalToolException("NO_INPUT", "Input dictionary is null.");

            string logPath;
            if (!input.TryGetValue("log", out logPath) || string.IsNullOrEmpty(logPath))
                throw new ExternalToolException("NO_LOG_PATH", "Argument 'log' is required.");

            if (!File.Exists(logPath))
                throw new ExternalToolException("LOG_NOT_FOUND", "Log file not found: " + logPath);

            string text = File.ReadAllText(logPath);

            string summaryResult = DetectSummaryResult(text);
            HashSet<string> freqFilter = BuildFreqFilter(input);

            Dictionary<string, string> data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<FailItem> failList = new List<FailItem>();

            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');

            Regex headerRegex = new Regex(@"^\s*Frequency:\s*(?<freq>\d+)(?<rest>.*)$", RegexOptions.IgnoreCase);
            Regex measRegex = new Regex(
                @"^\s*(?<name>[A-Za-z0-9 _/]+?)\s+(?<value>[+-]?\d+(?:\.\d+)?)\s+(?:(?<unit>\S+)\s+)?\((?<spec>[^)]*)\)",
                RegexOptions.IgnoreCase);

            int metricCount = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                Match h = headerRegex.Match(line);
                if (!h.Success) continue;

                string freq = h.Groups["freq"].Value.Trim();
                string rest = h.Groups["rest"].Value;

                if (freqFilter != null && !freqFilter.Contains(freq))
                    continue;

                // Extract TestName from previous line
                string testName = "UNKNOWN";
                if (i > 0)
                {
                    string prev = lines[i - 1].Trim();
                    Match tn = Regex.Match(prev, @"^\s*\d+\.\s*(\S+)");
                    if (tn.Success)
                        testName = tn.Groups[1].Value.Trim();
                }

                // Parse header fields
                List<KeyValuePair<string, string>> header = new List<KeyValuePair<string, string>>();
                header.Add(new KeyValuePair<string, string>("Frequency", freq));

                if (!string.IsNullOrEmpty(rest))
                {
                    string[] parts = rest.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string seg in parts)
                    {
                        Match kv = Regex.Match(seg.Trim(), @"^(?<name>[^:]+):\s*(?<val>.+)$");
                        if (!kv.Success) continue;
                        header.Add(new KeyValuePair<string, string>(
                            kv.Groups["name"].Value.Trim(),
                            kv.Groups["val"].Value.Trim()));
                    }
                }

                // Find antenna
                string antenna = "";
                foreach (var p in header)
                {
                    if (p.Key.Equals("Antenna", StringComparison.OrdinalIgnoreCase))
                        antenna = p.Value;
                }
                if (antenna.StartsWith("ANT_"))
                    antenna = antenna.Replace("ANT_", "ANT");

                int freqInt = int.Parse(freq);

                // Prefix
                StringBuilder sb = new StringBuilder();
                foreach (var hf in header)
                {
                    if (sb.Length > 0) sb.Append("_");
                    sb.Append(SanitizeLabel(hf.Key)).Append("(").Append(hf.Value).Append(")");
                }
                sb.Append("_");
                string prefix = sb.ToString();

                bool blockFail = false;
                int retryCount = 0;
                int retryFailCount = 0;
                string lastFailMetric = "";

                int j;
                for (j = i + 1; j < lines.Length; j++)
                {
                    string mline = lines[j];
                    if (headerRegex.IsMatch(mline)) break;

                    if (mline.Contains("Retry"))
                        retryCount++;

                    Match m = measRegex.Match(mline);
                    if (!m.Success) continue;

                    string rawName = m.Groups["name"].Value.Trim();
                    string name = SanitizeLabel(rawName);
                    string value = m.Groups["value"].Value.Trim();
                    string key = prefix + name;

                    bool isNew = !data.ContainsKey(key);
                    data[key] = value;
                    if (isNew) metricCount++;

                    if (mline.Contains("<-- fail"))
                    {
                        blockFail = true;
                        lastFailMetric = name;
                        if (retryCount > 0)
                            retryFailCount++;
                    }
                }

                if (blockFail)
                {
                    bool allRetryFail = false;
                    if (retryCount > 0 && retryFailCount == retryCount)
                        allRetryFail = true;
                    else if (retryCount == 0)
                        allRetryFail = true;

                    if (allRetryFail)
                    {
                        failList.Add(new FailItem
                        {
                            TestName = testName,
                            Frequency = freqInt,
                            Antenna = antenna,
                            MetricName = lastFailMetric,
                            AllRetryFail = true
                        });
                    }
                }

                i = j - 1;
            }

            data["itemCount"] = metricCount.ToString();
            data["TestResult"] = summaryResult;

            if (summaryResult == "FAIL" && failList.Count > 0)
            {
                FailItem f = failList[0];
                string band = "UNK";
                if (f.Frequency >= 2000 && f.Frequency < 3000) band = "2G";
                else if (f.Frequency >= 5000 && f.Frequency < 5900) band = "5G";
                else if (f.Frequency >= 5900 && f.Frequency < 7200) band = "6G";

                string msg;

                if (!string.IsNullOrEmpty(f.Antenna))
                    msg = f.TestName + "_" + band + "_" + f.Antenna + "_" + f.MetricName;
                else
                    msg = f.TestName + "_" + band + "_" + f.MetricName;

                data["errorCode"] = msg;
            }

            // ⭐ 無論如何一定輸出 ErrorMessage
            if (!data.ContainsKey("errorCode"))
            {
                data["errorCode"] = "0";
            }


            return data;
        }

        private static string DetectSummaryResult(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            Regex r = new Regex(@"\*{4}\s*([A-Z ]+)\s*\*{4}", RegexOptions.IgnoreCase);
            MatchCollection m = r.Matches(normalized);
            if (m.Count == 0) return "";
            string raw = m[m.Count - 1].Groups[1].Value.Trim();
            return raw.Replace(" ", "").ToUpperInvariant();
        }

        private static HashSet<string> BuildFreqFilter(Dictionary<string, string> input)
        {
            string freqList;
            if (!input.TryGetValue("freq", out freqList) || string.IsNullOrEmpty(freqList))
                return null;

            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] parts = freqList.Split(new char[] { ',', ';', ' ' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string s in parts)
                set.Add(s.Trim());

            return set.Count == 0 ? null : set;
        }

        private static string SanitizeLabel(string label)
        {
            return string.IsNullOrEmpty(label) ? "" : label.Replace(' ', '_');
        }
    }
}
