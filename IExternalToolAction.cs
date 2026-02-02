using System.Collections.Generic;

namespace XTool
{
    public interface IExternalToolAction
    {
        string Name { get; }

        /// <summary>
        /// 執行動作。
        /// input  : 從輸入 JSON 轉出來的 key/value。
        /// return : 要輸出到 JSON 的 key/value（單層），Program 會再加上 success / message 等欄位。
        /// </summary>
        Dictionary<string, string> Execute(Dictionary<string, string> input);
    }
}
