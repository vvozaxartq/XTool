
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using XTool;

namespace XTool.Module
{
    //XTool.exe action=RecordMultiCh key=Realtek duration=10 out=Utility\rec select=1,2
    internal class RecordMultiCh : IExternalToolAction
    {
        public string Name => "RecordMultiCh";

        private WasapiCapture _capture;
        private WaveFileWriter[] _writersMono;   // 單通道輸出 writers（每檔 1ch）
        private WaveFileWriter _writerMulti;     // 多通道輸出 writer（interleaved）

        private List<int> _selectedSourceChs;    // 被選擇的來源 ch（0-based）
        private Dictionary<int, int> _channelMap;// 來源 ch -> writersMono 索引

        // 解析用通道數（只影響拆分邏輯，不改變裝置串流）
        private int _parseChannels;

        // 來源格式資訊
        private WaveFormatEncoding _srcEncoding;
        private int _srcSampleRate;
        private int _bytesPerSampleSrc;

        // 輸出格式模式
        private enum OutputFormatMode { Source, Pcm16 }
        private OutputFormatMode _outMode = OutputFormatMode.Source;

        // 錄音停止事件的錯誤
        private string _stopErrorMessage = null;

        // 是否要輸出 ch_multi.wav（由 select 中的旗標決定）
        private bool _outputMulti = false;

        private bool _isRecording;

        public Dictionary<string, string> Execute(Dictionary<string, string> input)
        {
            // 統一回傳結構：預設為 OK，若遇錯誤再改為 ERROR
            var result = new Dictionary<string, string>
            {
                ["result"] = "OK"
            };

            try
            {
                if (!input.TryGetValue("key", out string key) || string.IsNullOrEmpty(key))
                    return ErrorResult(result, "NO_KEY", "Missing 'key=' keyword to find device.");

                input.TryGetValue("duration", out string durStr);
                int durationSec = string.IsNullOrEmpty(durStr) ? 5 : int.Parse(durStr);

                if (!input.TryGetValue("out", out string outDir) || string.IsNullOrEmpty(outDir))
                    return ErrorResult(result, "NO_OUT", "Missing 'out=' output directory.");

                // 解析 outputFormat（預設 source）
                if (input.TryGetValue("outputFormat", out string fmtStr) && !string.IsNullOrWhiteSpace(fmtStr))
                {
                    fmtStr = fmtStr.Trim().ToLowerInvariant();
                    _outMode = fmtStr == "pcm16" ? OutputFormatMode.Pcm16 : OutputFormatMode.Source;
                }
                else
                {
                    _outMode = OutputFormatMode.Source;
                }

                // 確保目錄存在 + 安全清除舊檔
                Directory.CreateDirectory(outDir);
                //SafeCleanDirectory(outDir);

                // 裝置與來源格式
                MMDevice device = FindDevice(key);
                if (device == null)
                    return ErrorResult(result, "DEVICE_NOT_FOUND", $"No device contains keyword '{key}'");

                _capture = new WasapiCapture(device);

                int deviceChannels = _capture.WaveFormat.Channels;
                _srcSampleRate = _capture.WaveFormat.SampleRate;
                _srcEncoding = _capture.WaveFormat.Encoding;
                _bytesPerSampleSrc = _capture.WaveFormat.BitsPerSample / 8;

                // 基本來源格式檢查
                if (deviceChannels <= 0)
                    return ErrorResult(result, "SRC_BAD_CHANNELS", $"Device reports invalid channels: {deviceChannels}");
                if (_srcSampleRate <= 0)
                    return ErrorResult(result, "SRC_BAD_SAMPLERATE", $"Device reports invalid sample rate: {_srcSampleRate}");
                if (_bytesPerSampleSrc <= 0)
                    return ErrorResult(result, "SRC_BAD_BITDEPTH", $"Device reports invalid bits per sample: {_capture.WaveFormat.BitsPerSample}");
                if (_srcEncoding != WaveFormatEncoding.Pcm && _srcEncoding != WaveFormatEncoding.IeeeFloat)
                    return ErrorResult(result, "SRC_UNSUPPORTED_ENCODING", $"Unsupported source encoding: {_srcEncoding}");

                // 解析用通道數（僅影響拆分解析，不改變裝置串流）
                _parseChannels = deviceChannels;
                if (input.TryGetValue("channels", out string chStr) &&
                    int.TryParse(chStr, out int forcedCh) && forcedCh > 0)
                {
                    _parseChannels = Math.Min(forcedCh, deviceChannels);
                }

                // 解析 select（通道 + multi/nomulti 旗標），嚴格驗證
                if (!TryParseSelectWithFlags(input, _parseChannels, out _selectedSourceChs, out _outputMulti, out string selErr))
                {
                    return ErrorResult(result, "SELECT_OUT_OF_RANGE", selErr);
                }

                // 準備 writersMono 與 channelMap
                _channelMap = new Dictionary<int, int>();
                _writersMono = new WaveFileWriter[_selectedSourceChs.Count];

                // 決定輸出格式（單通道）
                WaveFormat monoOutFormat = BuildOutputWaveFormat(channels: 1);

                for (int i = 0; i < _selectedSourceChs.Count; i++)
                {
                    int srcCh = _selectedSourceChs[i];
                    string file = Path.Combine(outDir, $"ch_{(srcCh + 1):00}.wav");
                    _writersMono[i] = new WaveFileWriter(file, monoOutFormat);
                    _channelMap[srcCh] = i;
                }

                // 準備多通道輸出 writer（interleaved）—僅在 _outputMulti 為 true 時建立
                string multiFilePath = null;
                if (_outputMulti)
                {
                    multiFilePath = Path.Combine(outDir, "ch_multi.wav");
                    WaveFormat multiOutFormat = BuildOutputWaveFormat(channels: _selectedSourceChs.Count);
                    _writerMulti = new WaveFileWriter(multiFilePath, multiOutFormat);
                }

                // 綁定事件並開始錄音
                _capture.DataAvailable += Capture_DataAvailable;
                _capture.RecordingStopped += Capture_RecordingStopped;

                _isRecording = true;
                _capture.StartRecording();
                var sw = Stopwatch.StartNew();
                
                while (sw.Elapsed.TotalSeconds < durationSec)
                    Thread.Sleep(10);
                Thread.Sleep(200);
                _capture.StopRecording();

                _isRecording = false;

                // 等待 RecordingStopped 完成釋放
                Thread.Sleep(500);

                // 若 RecordingStopped 有錯（例如裝置斷線），補充到結果
                if (!string.IsNullOrEmpty(_stopErrorMessage))
                {
                    ErrorResult(result, "STOP_ERROR", _stopErrorMessage);
                }

                // 成功結果（若前面沒錯）
                result["CH_COUNT"] = _writersMono.Length.ToString();
                result["OUT_DIR"] = outDir;
                result["OUTPUT_FORMAT"] = _outMode == OutputFormatMode.Source ? "source" : "pcm16";

                // FILES：單一鍵，逗號分隔所有檔名（單通道 + 若有則含 ch_multi.wav）
                var fileNames = _selectedSourceChs
                    .Select(srcCh => $"ch_{(srcCh + 1):00}.wav")
                    .ToList();
                if (_outputMulti)
                    fileNames.Add("ch_multi.wav");
                result["FILES"] = string.Join(",", fileNames);

                // PATHS：單一鍵，逗號分隔所有完整路徑（單通道 + 若有則含 ch_multi.wav）

                var filePaths = _selectedSourceChs
                    .Select(srcCh => Path.GetFullPath(Path.Combine(outDir, $"ch_{(srcCh + 1):00}.wav")))
                    .ToList();
                if (_outputMulti && !string.IsNullOrEmpty(multiFilePath))
                    filePaths.Add(Path.GetFullPath(multiFilePath));
                result["PATHS"] = string.Join(",", filePaths);


                return result;
            }
            catch (Exception ex)
            {
                return ErrorResult(result, "UNEXPECTED_EXCEPTION", ex.Message);
            }
        }

        /// <summary>
        /// 將 result 標記為錯誤並填入錯誤訊息（含錯誤代碼）。
        /// - 'result'（OK/ERROR）
        /// - 單鍵 'ERROR_MESSAGE'："{CODE}: {MESSAGE}"
        /// </summary>
        private Dictionary<string, string> ErrorResult(Dictionary<string, string> result, string code, string message)
        {
            result["result"] = "ERROR";
            result["ERROR_MESSAGE"] = string.IsNullOrEmpty(message) ? code : $"{code}: {message}";
            return result;
        }

        /// <summary>
        /// 建立輸出 WaveFormat。依 _outMode 決定：
        /// - Source：與來源位深/編碼一致（PCM 16/24/32 或 IEEE Float 32）
        /// - Pcm16：強制 16-bit PCM
        /// </summary>
        private WaveFormat BuildOutputWaveFormat(int channels)
        {
            if (_outMode == OutputFormatMode.Pcm16)
            {
                return new WaveFormat(_srcSampleRate, 16, channels);
            }

            // Source 模式：依來源建立對應格式
            if (_srcEncoding == WaveFormatEncoding.IeeeFloat && _bytesPerSampleSrc == 4)
            {
                // IEEE Float 32
                return WaveFormat.CreateIeeeFloatWaveFormat(_srcSampleRate, channels);
            }
            else if (_srcEncoding == WaveFormatEncoding.Pcm)
            {
                // PCM：維持位深（16/24/32）
                int bits = _bytesPerSampleSrc * 8;
                return new WaveFormat(_srcSampleRate, bits, channels);
            }
            else
            {
                // 不常見編碼，回退為 16-bit PCM
                return new WaveFormat(_srcSampleRate, 16, channels);
            }
        }

        private void Capture_DataAvailable(object sender, WaveInEventArgs e)
        {
            if (!_isRecording) return;

            int frameSizeSrc = _bytesPerSampleSrc * _parseChannels;
            int bytes = e.BytesRecorded - (e.BytesRecorded % frameSizeSrc); // 只處理完整 frame

            // 暫存區：mono 輸出
            byte[] monoOut2 = new byte[2]; // pcm16 每樣本 2 bytes

            // 暫存：多通道一 frame（僅在需要時建立）
            byte[] multiFrame = null;
            if (_outputMulti)
            {
                if (_outMode == OutputFormatMode.Pcm16)
                    multiFrame = new byte[_selectedSourceChs.Count * 2];
                else
                    multiFrame = new byte[_selectedSourceChs.Count * _bytesPerSampleSrc];
            }

            for (int offset = 0; offset < bytes; offset += frameSizeSrc)
            {
                int mfIndex = 0;

                // 依 _selectedSourceChs 順序輸出
                foreach (int srcCh in _selectedSourceChs)
                {
                    int pos = offset + srcCh * _bytesPerSampleSrc;

                    if (_outMode == OutputFormatMode.Pcm16)
                    {
                        // 高保真縮放成 16-bit PCM
                        short s16 = ReadSampleAsInt16(e.Buffer, pos);

                        // 單通道寫入
                        if (_channelMap.TryGetValue(srcCh, out int widx) && widx >= 0 && widx < _writersMono.Length)
                        {
                            monoOut2[0] = (byte)(s16 & 0xFF);
                            monoOut2[1] = (byte)((s16 >> 8) & 0xFF);
                            _writersMono[widx]?.Write(monoOut2, 0, 2);
                        }

                        // 多通道 frame 填入（interleaved）
                        if (_outputMulti)
                        {
                            multiFrame[mfIndex++] = (byte)(s16 & 0xFF);
                            multiFrame[mfIndex++] = (byte)((s16 >> 8) & 0xFF);
                        }
                    }
                    else
                    {
                        // Source 模式：維持原位深/編碼，直接複製樣本 bytes 到單通道檔
                        if (_channelMap.TryGetValue(srcCh, out int widx) && widx >= 0 && widx < _writersMono.Length)
                        {
                            _writersMono[widx]?.Write(e.Buffer, pos, _bytesPerSampleSrc);
                        }

                        // 多通道 frame 填入原始位深
                        if (_outputMulti)
                        {
                            Buffer.BlockCopy(e.Buffer, pos, multiFrame, mfIndex, _bytesPerSampleSrc);
                            mfIndex += _bytesPerSampleSrc;
                        }
                    }
                }

                // 寫入一個多通道 frame（需要時）
                if (_outputMulti)
                {
                    _writerMulti?.Write(multiFrame, 0, multiFrame.Length);
                }
            }
        }

        /// <summary>
        /// 將來源樣本（PCM 16/24/32 或 Float 32）轉為 int16（高保真縮放/正規化）。
        /// </summary>
        private short ReadSampleAsInt16(byte[] buffer, int pos)
        {
            switch (_srcEncoding)
            {
                case WaveFormatEncoding.IeeeFloat:
                    // float32：[-1,1] -> int16
                    float f = BitConverter.ToSingle(buffer, pos);
                    return Float32ToInt16(f);

                case WaveFormatEncoding.Pcm:
                    if (_bytesPerSampleSrc == 2)
                    {
                        // PCM 16-bit：直接讀
                        return BitConverter.ToInt16(buffer, pos);
                    }
                    else if (_bytesPerSampleSrc == 3)
                    {
                        // PCM 24-bit：3 bytes -> 32-bit，再右移 8 轉 16-bit（示意性縮放）
                        int sample24 = (sbyte)buffer[pos + 2]; // MSB with sign
                        sample24 = (sample24 << 8) | buffer[pos + 1];
                        sample24 = (sample24 << 8) | buffer[pos + 0];
                        return (short)(sample24 >> 8);
                    }
                    else if (_bytesPerSampleSrc == 4)
                    {
                        // PCM 32-bit：右移 16 轉為 16-bit（示意性縮放）
                        int s32 = BitConverter.ToInt32(buffer, pos);
                        return (short)(s32 >> 16);
                    }
                    break;
            }
            // 不支援的來源編碼：靜音
            return 0;
        }

        private static short Float32ToInt16(float sample)
        {
            // 限制在 [-1, 1]
            if (sample > 1f) sample = 1f;
            if (sample < -1f) sample = -1f;
            // 轉為 int16 範圍
            return (short)(sample * short.MaxValue);
        }

        private void Capture_RecordingStopped(object sender, StoppedEventArgs e)
        {
            try
            {
                if (_writersMono != null)
                {
                    foreach (var w in _writersMono)
                        w?.Dispose();
                }
                _writerMulti?.Dispose();
            }
            finally
            {
                _capture.Dispose();
            }

            if (e.Exception != null)
            {
                _stopErrorMessage = e.Exception.Message;
            }
        }

        private MMDevice FindDevice(string keyword)
        {
            var enumerator = new MMDeviceEnumerator();
            var list = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            return list.FirstOrDefault(d =>
                d.FriendlyName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// 解析 select：同時解析通道與 multi/nomulti 旗標（嚴格驗證）。
        /// - 通道：1-based，逗號/分號/空白分隔。
        /// - 旗標：'multi' 啟用輸出多通道檔、'nomulti' 禁用；兩者同時出現時以 'nomulti' 優先。
        /// - 無 select：視為全通道，且預設不輸出多通道檔（_outputMulti=false）。
        /// - 任一通道值 <=0 或 > sourceChannelCount 或非數字（且非旗標）→ 視為錯誤。
        /// </summary>
        private bool TryParseSelectWithFlags(
            Dictionary<string, string> input,
            int sourceChannelCount,
            out List<int> selectedZeroBased,
            out bool outputMulti,
            out string errorMessage)
        {
            selectedZeroBased = null;
            outputMulti = false;
            errorMessage = null;

            if (!input.TryGetValue("select", out string selStr) || string.IsNullOrWhiteSpace(selStr))
            {
                // 沒有 select -> 全選，預設不輸出多通道檔
                selectedZeroBased = Enumerable.Range(0, sourceChannelCount).ToList();
                outputMulti = false;
                return true;
            }

            var tokens = selStr.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var channels = new List<int>();
            var invalids = new List<string>();

            bool flagMulti = false;
            bool flagNoMulti = false;

            foreach (var raw in tokens)
            {
                var tk = raw.Trim();

                // 特殊旗標（不分大小寫）
                if (string.Equals(tk, "multi", StringComparison.OrdinalIgnoreCase))
                {
                    flagMulti = true;
                    continue;
                }
                if (string.Equals(tk, "nomulti", StringComparison.OrdinalIgnoreCase))
                {
                    flagNoMulti = true;
                    continue;
                }

                // 數字通道
                if (!int.TryParse(tk, out int oneBased))
                {
                    invalids.Add(tk);
                    continue;
                }
                if (oneBased <= 0 || oneBased > sourceChannelCount)
                {
                    invalids.Add(tk);
                    continue;
                }

                channels.Add(oneBased - 1); // 轉 0-based
            }

            if (invalids.Count > 0)
            {
                errorMessage = $"select contains invalid or out-of-range tokens: {string.Join(",", invalids)} (valid channels: 1..{sourceChannelCount}; flags: multi|nomulti)";
                return false;
            }

            // 去重 + 排序
            channels = channels.Distinct().OrderBy(i => i).ToList();

            if (channels.Count == 0)
            {
                errorMessage = "select resolved to empty channel set.";
                return false;
            }

            selectedZeroBased = channels;

            // 旗標判斷：nomulti 優先於 multi；若皆未指定則預設 false
            outputMulti = flagNoMulti ? false : flagMulti;

            return true;
        }

        /// <summary>
        /// 安全清除指定資料夾中的所有「檔案」。
        /// - 目錄不存在則直接返回。
        /// - 只刪除檔案，不處理子資料夾。
        /// - 即使有唯讀或被占用的檔案，失敗也不拋例外，不影響後續流程。
        /// </summary>
        private void SafeCleanDirectory(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
                    return;

                string[] files = Array.Empty<string>();
                try
                {
                    files = Directory.GetFiles(dir);
                }
                catch
                {
                    return;
                }

                foreach (var file in files)
                {
                    try
                    {
                        var attrs = File.GetAttributes(file);
                        if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                        }
                        File.Delete(file);
                    }
                    catch
                    {
                        // 忽略單檔失敗
                    }
                }
            }
            catch
            {
                // 最外層保護
            }
        }
    }
}
