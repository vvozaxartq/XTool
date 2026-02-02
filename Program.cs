using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace XTool
{
    internal class Program
    {
        static int Main(string[] args)
        {
            try
            {
                if (args == null || args.Length == 0)
                {
                    WriteJsonError("NO_INPUT", "No argument.", null);
                    return -1;
                }

                Dictionary<string, string> inputDict = null;

                // 先嘗試用 JSON 模式解析
                inputDict = TryParseJsonArgs(args);

                // 如果不是 JSON，就改用 key=value 模式解析
                if (inputDict == null)
                {
                    try
                    {
                        inputDict = ParseKeyValueArgs(args);
                    }
                    catch (Exception ex)
                    {
                        WriteJsonError("ARG_PARSE_ERROR", ex.Message, null);
                        return -2;
                    }
                }

                // 取得 action 名稱
                string actionName;
                if (!inputDict.TryGetValue("action", out actionName) || string.IsNullOrEmpty(actionName))
                {
                    WriteJsonError("NO_ACTION", "Argument 'action' is required.", null);
                    return -3;
                }

                // 初始化動作註冊表
                try
                {
                    ActionRegistry.Initialize();
                }
                catch (Exception ex)
                {
                    WriteJsonError("REGISTRY_INIT_ERROR", ex.Message, actionName);
                    return -4;
                }

                // 取得指定動作
                IExternalToolAction action = ActionRegistry.GetAction(actionName);
                if (action == null)
                {
                    WriteJsonError("ACTION_NOT_FOUND", "Unknown action: " + actionName, actionName);
                    return -5;
                }

                // 執行動作
                Dictionary<string, string> resultData;
                try
                {
                    resultData = action.Execute(inputDict);
                }
                catch (ExternalToolException ex)
                {
                    // 動作主動回報的可預期錯誤
                    WriteJsonError(ex.ErrorCode, ex.Message, actionName);
                    return -10;
                }
                catch (Exception ex)
                {
                    // 未預期錯誤
                    WriteJsonError("ACTION_EXCEPTION", ex.Message, actionName);
                    return -11;
                }

                // 成功輸出
                var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                output["status"] = "Done";
                output["action"] = actionName;
                output["message"] = "";

                if (resultData != null)
                {
                    foreach (KeyValuePair<string, string> kv in resultData)
                    {
                        if (!output.ContainsKey(kv.Key))
                        {
                            output[kv.Key] = kv.Value;
                        }
                    }
                }

                string json = JsonConvert.SerializeObject(output,Formatting.Indented);
                Console.WriteLine(json);
                return 0;
            }
            catch (Exception ex)
            {
                // 保底：若連錯誤處理程式自己出錯，仍嘗試輸出 FAIL JSON
                try
                {
                    WriteJsonError("FATAL", ex.Message, null);
                }
                catch
                {
                    // 最後防線也失敗就沒辦法了
                }
                return -99;
            }
        }

        /// <summary>
        /// 嘗試把整個 args 當成一個 JSON 字串來解析。
        /// 若不是合法 JSON，回傳 null。
        /// </summary>
        private static Dictionary<string, string> TryParseJsonArgs(string[] args)
        {
            // 把所有參數用空白黏起來，支援：
            //   ExternalTool.exe "{...json...}"
            //   ExternalTool.exe { ...json... }
            string raw = string.Join(" ", args).Trim();

            // 簡單判斷一下格式，不是以 { 開頭就直接跳過
            if (string.IsNullOrEmpty(raw) || raw[0] != '{')
            {
                return null;
            }

            try
            {
                var jo = JObject.Parse(raw);
                return JObjectToDictionary(jo);
            }
            catch
            {
                // JSON 解析失敗就回傳 null，讓外面走 key=value 模式
                return null;
            }
        }

        /// <summary>
        /// 解析 key=value 模式參數：
        ///   action=MacAdd mac=0C-1F-01_FF-12-FF count=3
        /// 會轉成：
        ///   ["action"] = "MacAdd"
        ///   ["mac"]    = "0C-1F-01_FF-12-FF"
        ///   ["count"]  = "3"
        /// </summary>
        private static Dictionary<string, string> ParseKeyValueArgs(string[] args)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string raw in args)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                int eq = raw.IndexOf('=');
                if (eq <= 0)
                {
                    // 沒有 '=' 的參數略過；必要時可以改成直接丟錯
                    continue;
                }

                string key = raw.Substring(0, eq).Trim();
                string value = raw.Substring(eq + 1); // 保留原始內容，不 Trim

                if (key.Length == 0)
                {
                    continue;
                }

                // 同一 key 多次出現時，以最後一次為主
                dict[key] = value;
            }

            if (dict.Count == 0)
            {
                throw new Exception("No valid key=value arguments.");
            }

            return dict;
        }

        private static Dictionary<string, string> JObjectToDictionary(JObject jo)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in jo)
            {
                if (kv.Value == null)
                {
                    dict[kv.Key] = string.Empty;
                }
                else
                {
                    dict[kv.Key] = kv.Value.ToString();
                }
            }
            return dict;
        }

        private static void WriteJsonError(string errorCode, string message, string actionName)
        {
            var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            output["status"] = "Error";
            output["action"] = actionName ?? string.Empty;
            output["errorCode"] = errorCode;
            output["message"] = message ?? string.Empty;

            string json = JsonConvert.SerializeObject(output);
            Console.WriteLine(json);
        }
    }
}
