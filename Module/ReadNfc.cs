using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using XTool;

namespace XTool.Module
{
    // XTool.exe action=ReadNfc reader=ACR122U waitMs=30000
    // 輸出精簡：CARD_ID / CARD_TYPE / CARD_DESC / ATR_HEX / INFO
    internal class ReadNfc : IExternalToolAction
    {
        public string Name { get { return "ReadNfc"; } }

        public Dictionary<string, string> Execute(Dictionary<string, string> input)
        {
            IntPtr hContext = IntPtr.Zero;
            IntPtr hCard = IntPtr.Zero;

            try
            {
                string readerKeyword = GetString(input, "reader", "ACR122U");
                int waitMs = GetInt(input, "waitMs", 0);

                int rc = WinSCard.SCardEstablishContext(WinSCard.SCARD_SCOPE_SYSTEM, IntPtr.Zero, IntPtr.Zero, out hContext);
                if (rc != WinSCard.SCARD_S_SUCCESS)
                    return ErrorResult("SCARD_ESTABLISH_FAIL", "rc=0x" + rc.ToString("X8"));

                string[] readers = WinSCard.ListReaders(hContext);
                if (readers == null || readers.Length == 0)
                    return ErrorResult("NO_READER", "No PC/SC reader found.");

                string readerName = PickReader(readers, readerKeyword);
                if (string.IsNullOrEmpty(readerName))
                    return ErrorResult("READER_NOT_FOUND", "Keyword='" + readerKeyword + "'");

                // 等卡片
                uint activeProtocol = 0;
                int elapsed = 0;
                while (true)
                {
                    rc = WinSCard.SCardConnect(
                        hContext, readerName,
                        WinSCard.SCARD_SHARE_SHARED,
                        WinSCard.SCARD_PROTOCOL_T0 | WinSCard.SCARD_PROTOCOL_T1,
                        out hCard, out activeProtocol);

                    if (rc == WinSCard.SCARD_S_SUCCESS)
                        break;

                    if (rc == WinSCard.SCARD_E_NO_SMARTCARD || rc == WinSCard.SCARD_W_REMOVED_CARD)
                    {
                        if (waitMs <= 0)
                            return ErrorResult("NO_CARD", "No card present.");

                        Thread.Sleep(100);
                        elapsed += 100;
                        if (elapsed >= waitMs)
                            return ErrorResult("WAIT_TIMEOUT", "Timeout waiting card. waitMs=" + waitMs);
                        continue;
                    }

                    return ErrorResult("SCARD_CONNECT_FAIL", "rc=0x" + rc.ToString("X8"));
                }

                // 取 ATR / 狀態（用於判斷卡型）
                uint state, protocol;
                byte[] atr = WinSCard.GetAtr(hCard, out state, out protocol);

                string atrHex = (atr != null && atr.Length > 0) ? ToHex(atr) : "";
                string cardGuess = (atr != null && atr.Length > 0) ? GuessCardFromAtr(atr) : "";
                string cardDesc = CardTypeDesc(cardGuess);

                // 讀 UID（主要卡ID）
                int apduRc;
                byte[] uidResp = TransmitApdu(hCard, activeProtocol, new byte[] { 0xFF, 0xCA, 0x00, 0x00, 0x04 }, out apduRc);

                string uidHex = "";
                if (uidResp != null && Is9000(uidResp))
                {
                    byte[] uid = uidResp.Take(uidResp.Length - 2).ToArray();
                    uidHex = ToHex(uid);
                }
                else
                {
                    // UID 讀不到就回錯（因為你要的「主要卡ID」就是它）
                    string sw = (uidResp != null) ? GetSw(uidResp) : "";
                    if (apduRc != WinSCard.SCARD_S_SUCCESS)
                        return ErrorResult("UID_TRANSMIT_FAIL", "rc=0x" + apduRc.ToString("X8"));
                    return ErrorResult("UID_FAIL", string.IsNullOrEmpty(sw) ? "Unknown" : ("SW=" + sw));
                }

                // 摘要訊息（你要的「一些訊息」）
                string info = BuildInfo(cardGuess);

                // 精簡輸出
                var outDict = new Dictionary<string, string>();
                outDict["result"] = "OK";
                outDict["CARD_ID"] = uidHex;           // 主要卡ID
                outDict["CARD_TYPE"] = cardGuess;      // 程式用（mifare_1k / ultralight...）
                outDict["CARD_DESC"] = cardDesc;       // 人看的描述
                outDict["ATR_HEX"] = atrHex;           // 輔助辨識用
               // outDict["INFO"] = info;                // 一句摘要

                // 如果你也想保留連到哪個 reader，可取消註解：
                // outDict["READER"] = string.IsNullOrEmpty(activeReader) ? readerName : activeReader;

                return outDict;
            }
            catch (Exception ex)
            {
                return ErrorResult("UNEXPECTED_EXCEPTION", ex.Message);
            }
            finally
            {
                if (hCard != IntPtr.Zero)
                    WinSCard.SCardDisconnect(hCard, WinSCard.SCARD_LEAVE_CARD);
                if (hContext != IntPtr.Zero)
                    WinSCard.SCardReleaseContext(hContext);
            }
        }

        // ===== helpers =====

        private static Dictionary<string, string> ErrorResult(string code, string message)
        {
            var d = new Dictionary<string, string>();
            d["result"] = "ERROR";
            d["ERROR_MESSAGE"] = string.IsNullOrEmpty(message) ? code : (code + ": " + message);
            return d;
        }

        private static string BuildInfo(string cardGuess)
        {
            if (string.IsNullOrEmpty(cardGuess))
                return "已讀到 UID；卡型未知（可用 ATR 再判斷）。";

            if (cardGuess == "mifare_1k" || cardGuess == "mifare_4k" || cardGuess == "mifare_mini")
                return "MIFARE Classic：通常需要 KeyA/KeyB 才能讀取區塊內容（門禁卡常見）。";

            if (cardGuess == "mifare_ultralight_or_ntag")
                return "Type 2（Ultralight/NTAG）：可進一步讀 NDEF（若卡片有寫入）。";

            if (cardGuess == "topaz_or_jewel")
                return "Topaz/Jewel：屬於其他 NFC 類型。";

            if (cardGuess.StartsWith("felica_", StringComparison.OrdinalIgnoreCase))
                return "FeliCa：屬於 Sony 系列卡型。";

            return "已讀到 UID；卡型=" + cardGuess;
        }

        private static string CardTypeDesc(string cardGuess)
        {
            switch (cardGuess)
            {
                case "mifare_1k": return "MIFARE Classic 1K";
                case "mifare_4k": return "MIFARE Classic 4K";
                case "mifare_mini": return "MIFARE Mini";
                case "mifare_ultralight_or_ntag": return "MIFARE Ultralight / NTAG (Type2)";
                case "topaz_or_jewel": return "Topaz / Jewel";
                case "felica_212k": return "FeliCa 212K";
                case "felica_424k": return "FeliCa 424K";
                default: return string.IsNullOrEmpty(cardGuess) ? "" : cardGuess;
            }
        }

        private static string GetString(Dictionary<string, string> input, string key, string def)
        {
            if (input != null && input.TryGetValue(key, out string v) && !string.IsNullOrEmpty(v))
                return v;
            return def;
        }

        private static int GetInt(Dictionary<string, string> input, string key, int def)
        {
            if (input != null && input.TryGetValue(key, out string v) && int.TryParse(v, out int n))
                return n;
            return def;
        }

        private static string PickReader(string[] readers, string keyword)
        {
            if (readers == null || readers.Length == 0) return null;

            if (!string.IsNullOrEmpty(keyword))
            {
                var r = readers.FirstOrDefault(x =>
                    x.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    x.IndexOf("PICC", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrEmpty(r)) return r;

                r = readers.FirstOrDefault(x => x.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrEmpty(r)) return r;
            }

            var picc = readers.FirstOrDefault(x => x.IndexOf("PICC", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrEmpty(picc)) return picc;

            return readers[0];
        }

        // 不 throw：rc != success -> 回傳 null
        private static byte[] TransmitApdu(IntPtr hCard, uint activeProtocol, byte[] apdu, out int rc)
        {
            rc = WinSCard.SCARD_S_SUCCESS;

            WinSCard.SCARD_IO_REQUEST ioSend = new WinSCard.SCARD_IO_REQUEST();
            ioSend.dwProtocol = activeProtocol;
            ioSend.cbPciLength = (uint)Marshal.SizeOf(typeof(WinSCard.SCARD_IO_REQUEST));

            byte[] recv = new byte[258];
            int recvLen = recv.Length;

            rc = WinSCard.SCardTransmit(hCard, ref ioSend, apdu, apdu.Length, IntPtr.Zero, recv, ref recvLen);
            if (rc != WinSCard.SCARD_S_SUCCESS)
                return null;

            if (recvLen <= 0) return new byte[0];
            return recv.Take(recvLen).ToArray();
        }

        private static bool Is9000(byte[] resp)
        {
            if (resp == null || resp.Length < 2) return false;
            return resp[resp.Length - 2] == 0x90 && resp[resp.Length - 1] == 0x00;
        }

        private static string GetSw(byte[] resp)
        {
            if (resp == null || resp.Length < 2) return "";
            return resp[resp.Length - 2].ToString("X2") + resp[resp.Length - 1].ToString("X2");
        }

        private static string ToHex(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            var sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++)
                sb.Append(data[i].ToString("X2"));
            return sb.ToString();
        }

        private static string GuessCardFromAtr(byte[] atr)
        {
            byte[] rid = new byte[] { 0xA0, 0x00, 0x00, 0x03, 0x06 };
            int idx = IndexOf(atr, rid);
            if (idx < 0) return "";

            int ssPos = idx + rid.Length;
            if (ssPos + 2 >= atr.Length) return "";

            byte ss = atr[ssPos];
            byte c0 = atr[ssPos + 1];
            byte c1 = atr[ssPos + 2];

            string cardName = c0.ToString("X2") + c1.ToString("X2");
            switch (cardName)
            {
                case "0001": return "mifare_1k";
                case "0002": return "mifare_4k";
                case "0003": return "mifare_ultralight_or_ntag";
                case "0026": return "mifare_mini";
                case "F004": return "topaz_or_jewel";
                case "F011": return "felica_212k";
                case "F012": return "felica_424k";
                default:
                    return "unknown(cn=" + cardName + ",ss=" + ss.ToString("X2") + ")";
            }
        }

        private static int IndexOf(byte[] src, byte[] pat)
        {
            if (src == null || pat == null || src.Length < pat.Length) return -1;
            for (int i = 0; i <= src.Length - pat.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < pat.Length; j++)
                {
                    if (src[i + j] != pat[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }
    }
}



    internal static class WinSCard
    {
        public const int SCARD_S_SUCCESS = 0x00000000;

        public const int SCARD_E_NO_SMARTCARD = unchecked((int)0x8010000C);
        public const int SCARD_W_REMOVED_CARD = unchecked((int)0x80100069);

        public const uint SCARD_SCOPE_SYSTEM = 2;
        public const uint SCARD_SHARE_SHARED = 2;

        public const uint SCARD_PROTOCOL_T0 = 0x0001;
        public const uint SCARD_PROTOCOL_T1 = 0x0002;

        public const uint SCARD_LEAVE_CARD = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct SCARD_IO_REQUEST
        {
            public uint dwProtocol;
            public uint cbPciLength;
        }

        [DllImport("winscard.dll")]
        public static extern int SCardEstablishContext(uint dwScope, IntPtr pvReserved1, IntPtr pvReserved2, out IntPtr phContext);

        [DllImport("winscard.dll")]
        public static extern int SCardReleaseContext(IntPtr phContext);

        // Unicode multi-string 版
        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        public static extern int SCardListReaders(
            IntPtr hContext,
            string mszGroups,
            IntPtr mszReaders,
            ref int pcchReaders);

        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        public static extern int SCardConnect(
            IntPtr hContext,
            string szReader,
            uint dwShareMode,
            uint dwPreferredProtocols,
            out IntPtr phCard,
            out uint pdwActiveProtocol);

        [DllImport("winscard.dll")]
        public static extern int SCardDisconnect(IntPtr hCard, uint dwDisposition);

        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        public static extern int SCardStatus(
            IntPtr hCard,
            StringBuilder szReaderName,
            ref int pcchReaderLen,
            out uint pdwState,
            out uint pdwProtocol,
            byte[] pbAtr,
            ref int pcbAtrLen);

        [DllImport("winscard.dll")]
        public static extern int SCardTransmit(
            IntPtr hCard,
            ref SCARD_IO_REQUEST pioSendPci,
            byte[] pbSendBuffer,
            int cbSendLength,
            IntPtr pioRecvPci,
            byte[] pbRecvBuffer,
            ref int pcbRecvLength);

        public static string[] ListReaders(IntPtr hContext)
        {
            int charCount = 0;

            int rc = SCardListReaders(hContext, null, IntPtr.Zero, ref charCount);
            if (rc != SCARD_S_SUCCESS || charCount <= 0)
                return new string[0];

            IntPtr p = Marshal.AllocHGlobal(charCount * 2);
            try
            {
                rc = SCardListReaders(hContext, null, p, ref charCount);
                if (rc != SCARD_S_SUCCESS)
                    return new string[0];

                // multi-string： "Reader1\0Reader2\0\0"
                string multi = Marshal.PtrToStringUni(p, charCount - 1);
                if (string.IsNullOrEmpty(multi))
                    return new string[0];

                return multi.Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }

        public static byte[] GetAtr(IntPtr hCard, out uint state, out uint protocol)
        {
            state = 0;
            protocol = 0;

            int readerLen = 256;
            StringBuilder readerName = new StringBuilder(readerLen);

            byte[] atr = new byte[64];
            int atrLen = atr.Length;

            int rc = SCardStatus(hCard, readerName, ref readerLen, out state, out protocol, atr, ref atrLen);
            if (rc != SCARD_S_SUCCESS || atrLen <= 0) return new byte[0];

            return atr.Take(atrLen).ToArray();
        }
    }

