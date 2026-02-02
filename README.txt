ExternalTool - 外掛式 Console 工具

目標：
- 讓產測程式以「外掛 exe」方式呼叫各種 Action。
- 透過 Input JSON 指定要執行的 action 以及參數。
- 執行完輸出單層 JSON，方便主程式解析。

建置環境：
- .NET Framework 4.7.2
- Visual Studio 2019 / 2022
- 需要安裝 NuGet 套件：Newtonsoft.Json (Json.NET)

使用方式：
1. 用 Visual Studio 開啟 ExternalTool.csproj。
2. 第一次開啟建議先「還原 NuGet 套件」（工具列：工具 -> NuGet 套件管理員 -> 管理解決方案的 NuGet 套件，或在方案總管右鍵解決方案選「還原 NuGet 套件」）。
3. 編譯專案 (Debug / Release)。

執行範例：
ExternalTool.exe "{\"action\":\"MacAdd\",\"mac\":\"0C-1F-01_FF-12-FF\",\"count\":3}"

範例輸出（單層 JSON）：
{"success":"true","action":"MacAdd","message":"OK","mac_input":"0C-1F-01_FF-12-FF","count":"3","result_1":"0C-1F-01_FF-13-00","result_2":"0C-1F-01_FF-13-01","result_3":"0C-1F-01_FF-13-02"}

擴充新的 Action：
1. 新增一個 class，實作 IExternalToolAction 介面，例如：

   using System;
   using System.Collections.Generic;

   namespace XTool
   {
       public class MyAction : IExternalToolAction
       {
           public string Name
           {
               get { return "MyAction"; }
           }

           public Dictionary<string, string> Execute(Dictionary<string, string> input)
           {
               Dictionary<string, string> result = new Dictionary<string, string>();
               // TODO: 寫你的邏輯
               result["echo"] = "Hello from MyAction";
               return result;
           }
       }
   }

2. 不需要修改 Program.cs 或 ActionRegistry。
   程式啟動時會用反射自動掃描所有 IExternalToolAction 並註冊。

輸出 JSON 規則：
- 一律單層 key/value 字串。
- Program 會自動加上的欄位：
  - success : "true" / "false"
  - action  : 請求的 action 名稱
  - message : "OK" 或錯誤訊息
  - errorCode : 僅失敗時出現
- Action 只要回傳額外的 key/value，Program 會幫你 merge。
