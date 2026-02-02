using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace XTool
{
    /// <summary>
    /// 給各個 Action 使用的自訂例外，用來回報可預期的失敗狀況。
    /// </summary>
    public class ExternalToolException : Exception
    {
        private readonly string _errorCode;

        public string ErrorCode
        {
            get { return _errorCode; }
        }

        public ExternalToolException(string errorCode, string message)
            : base(message)
        {
            _errorCode = errorCode;
        }
    }
}

