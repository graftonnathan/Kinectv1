using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Kinectv1.Tts
{
    public static class TtsService
    {
        // Public events
        public static event Action OnTtsSpeakingStarted;
        public static event Action OnTtsSpeakingFinished;
        public static event Action<string> OnTtsError;

        // NEW: per-utterance cancellation for local playback (barge-in)
        private static readonly object _speakLock = new object();
        private static CancellationTokenSource _currentLocalSpeakCts;

        // --- Kokoro merged state ---
        private static readonly object _lock = new object();
        private static InferenceSession _session;
        private static bool _initialized;
        private static bool _usingGpu;
        private const int SampleRate = 24000;
        private static string _baseDir = Path.Combine("models", "tts", "kokoro");
        private static string _modelPath = Path.Combine("models", "tts", "kokoro", "onnx", "model_q8f16.onnx");

        // Vocab + voices
        private static readonly Dictionary<string, int> _vocab = new Dictionary<string, int>();
        private static readonly Dictionary<string, string> _voiceFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float[]> _voiceBinCache = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);

        // Reuse tensors
        private static readonly DenseTensor<float> _reuseStyleTensor = new DenseTensor<float>(new[] { 1, 256 });
        private static readonly DenseTensor<float> _reuseSpeedTensor = new DenseTensor<float>(new[] { 1 });
        private static readonly List<NamedOnnxValue> _reuseInputs = new List<NamedOnnxValue>(3);

        // Runtime tunables
        private static float _speed = 1.0f;
        private static int _ipaTimeoutMs = 1500;      // one-shot timeout only (no cache/service)
        private static int _ipaServiceTimeoutMs = 1500; // kept for settings compatibility (unused)

        // --- Public API ---
        public static int GetSampleRate() => SampleRate;
        public static bool IsEnabled() { try { return Kinectv1.App.SettingsProvider?.Current?.Tts?.Enabled ?? false; } catch { return false; } }

        public static bool RecreateSessionFromSettings()
        {
            lock (_lock)
            {
                _initialized = false; // force re-init
                try { _session?.Dispose(); } catch { }
                _session = null;
                return InitializeLocked();
            }
        }

        // Allow external barge-in to cancel current local playback
        public static void CancelCurrentLocalTts()
        {
            try
            {
                CancellationTokenSource cts = null;
                lock (_speakLock)
                {
                    cts = _currentLocalSpeakCts;
                    _currentLocalSpeakCts = null;
                }
                try { cts?.Cancel(); } catch { }
                try { cts?.Dispose(); } catch { }
            }
            catch { }
        }

        // --- Initialization ---
        private static bool EnsureInitialized()
        {
            lock (_lock)
            {
                if (_initialized) return true;
                return InitializeLocked();
            }
        }

        private static bool InitializeLocked()
        {
            try
            {
                ApplyRuntimeSettings();
                ResolveModelLocationsFromSettings();
                LoadVocab();
                LoadVoices();
                if (!File.Exists(_modelPath)) { OnTtsError?.Invoke("Kokoro model not found"); return false; }
                CreateSession(Kinectv1.App.SettingsProvider?.Current?.Tts?.Execution == Kinectv1.Settings.TtsExecution.GPU);
                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                OnTtsError?.Invoke("TTS init failed: " + ex.Message);
                return false;
            }
        }

        private static void ApplyRuntimeSettings()
        {
            try
            {
                var tts = Kinectv1.App.SettingsProvider?.Current?.Tts;
                if (tts == null) return;
                if (tts.Speed > 0) _speed = tts.Speed;
                if (tts.IpaOneShotTimeoutMs > 0) _ipaTimeoutMs = tts.IpaOneShotTimeoutMs;
                if (tts.IpaServiceTimeoutMs > 0) _ipaServiceTimeoutMs = tts.IpaServiceTimeoutMs; // retained for future ext
            }
            catch { }
        }

        private static string NormalizeBaseDir(string folder)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder)) return folder;
                var full = Path.IsPathRooted(folder) ? folder : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, folder);
                if (!Directory.Exists(full)) return folder;
                var name = new DirectoryInfo(full).Name;
                if (string.Equals(name, "onnx", StringComparison.OrdinalIgnoreCase))
                {
                    var parent = Directory.GetParent(full)?.FullName;
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) return parent;
                }
                return full;
            }
            catch { return folder; }
        }

        private static void ResolveModelLocationsFromSettings()
        {
            try
            {
                var snap = Kinectv1.App.SettingsProvider?.Current?.Tts;
                if (snap == null) return;
                if (!string.IsNullOrWhiteSpace(snap.ModelFolder))
                {
                    var baseDir = NormalizeBaseDir(snap.ModelFolder);
                    if (Directory.Exists(baseDir))
                    {
                        _baseDir = baseDir;
                        var onnxDir = Path.Combine(_baseDir, "onnx");
                        if (Directory.Exists(onnxDir) && File.Exists(Path.Combine(onnxDir, "model_q8f16.onnx")))
                            _modelPath = Path.Combine(onnxDir, "model_q8f16.onnx");
                        else if (File.Exists(Path.Combine(_baseDir, "model_q8f16.onnx")))
                            _modelPath = Path.Combine(_baseDir, "model_q8f16.onnx");
                    }
                }
                if (!string.IsNullOrWhiteSpace(snap.ModelPath))
                {
                    var cfg = Path.IsPathRooted(snap.ModelPath) ? snap.ModelPath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, snap.ModelPath);
                    if (File.Exists(cfg))
                    {
                        _modelPath = cfg;
                        _baseDir = NormalizeBaseDir(Path.GetDirectoryName(cfg));
                    }
                    else if (Directory.Exists(cfg))
                    {
                        if (File.Exists(Path.Combine(cfg, "model_q8f16.onnx")))
                        {
                            _modelPath = Path.Combine(cfg, "model_q8f16.onnx");
                            _baseDir = NormalizeBaseDir(cfg);
                        }
                        else if (Directory.Exists(Path.Combine(cfg, "onnx")))
                        {
                            var onnx = Path.Combine(cfg, "onnx", "model_q8f16.onnx");
                            _modelPath = onnx;
                            _baseDir = NormalizeBaseDir(cfg);
                        }
                    }
                }
            }
            catch { }
        }

        private static void LoadVocab()
        {
            try
            {
                var tokenizer = Path.Combine(_baseDir, "tokenizer.json");
                if (!File.Exists(tokenizer))
                {
                    var onnxDir = Path.Combine(_baseDir, "onnx", "tokenizer.json");
                    if (File.Exists(onnxDir)) tokenizer = onnxDir; else { _vocab.Clear(); return; }
                }
                var json = File.ReadAllText(tokenizer, Encoding.UTF8);
                var jobj = Newtonsoft.Json.Linq.JObject.Parse(json);
                var vocabObj = (Newtonsoft.Json.Linq.JObject)jobj.SelectToken("model.vocab");
                _vocab.Clear();
                foreach (var p in vocabObj.Properties()) _vocab[p.Name] = (int)p.Value;
            }
            catch { _vocab.Clear(); }
        }

        private static void LoadVoices()
        {
            try
            {
                _voiceFiles.Clear();
                var dir = Path.Combine(_baseDir, "voices");
                if (!Directory.Exists(dir))
                {
                    if (string.Equals(new DirectoryInfo(_baseDir).Name, "onnx", StringComparison.OrdinalIgnoreCase))
                    {
                        var parent = Directory.GetParent(_baseDir)?.FullName;
                        if (!string.IsNullOrEmpty(parent))
                        {
                            var alt = Path.Combine(parent, "voices");
                            if (Directory.Exists(alt)) dir = alt;
                        }
                    }
                }
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.bin"))
                        _voiceFiles[Path.GetFileNameWithoutExtension(f)] = f;
                }
            }
            catch { _voiceFiles.Clear(); }
        }

        private static void CreateSession(bool gpu)
        {
            try { _session?.Dispose(); } catch { }
            _session = OnnxSessionFactory.Create(_modelPath, gpu, out _usingGpu);
        }

        // --- IPA (one-shot only) ---
        private static string GetIpa(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var sanitized = Sanitize(text);
            if (string.IsNullOrWhiteSpace(sanitized)) return null;
            try
            {
                var exe = ResolveEspeakExecutable();
                if (exe == null) return null;
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--ipa -q -v en-us \"" + sanitized.Replace("\r", " ").Replace("\n", " ") + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
#if NETFRAMEWORK
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                try { psi.EnvironmentVariables["ESPEAK_NG_OUTPUT_CHARSET"] = "utf-8"; } catch { }
#else
                try { psi.Environment["ESPEAK_NG_OUTPUT_CHARSET"] = "utf-8"; } catch { }
#endif
                using var p = new Process { StartInfo = psi }; p.Start();
                var cts = new CancellationTokenSource(Math.Max(200, _ipaTimeoutMs));
                var readTask = p.StandardOutput.ReadToEndAsync();
                var done = Task.WhenAny(readTask, Task.Delay(Timeout.Infinite, cts.Token)).GetAwaiter().GetResult();
                if (done != readTask)
                {
                    try { p.Kill(); } catch { }
                    return null;
                }
                var raw = readTask.GetAwaiter().GetResult();
                return NormalizeIpa(raw);
            }
            catch { return null; }
        }

        private static string ResolveEspeakExecutable()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var candidates = new[]
                {
                    Path.Combine(baseDir, "models","tts","Espeak NG","espeak-ng.exe"),
                    Path.Combine(baseDir, "models","tts","espeak","espeak-ng.exe"),
                    "espeak-ng.exe",
                    "espeak.exe"
                };
                return candidates.FirstOrDefault(File.Exists);
            }
            catch { return null; }
        }

        private static string Sanitize(string text)
        {
            text = text.Replace('\u201C', '"').Replace('\u201D', '"');
            text = text.Replace('\u2018', '\'').Replace('\u2019', '\'');
            text = text.Replace("–", "-").Replace("—", "-");
            text = text.Replace("…", "...");
            text = Regex.Replace(text, @"[\x00-\x1F\x7F]", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text;
        }
        private static string NormalizeIpa(string ipa)
        {
            if (string.IsNullOrWhiteSpace(ipa)) return null;
            ipa = ipa.Replace("/", " ");
            ipa = Regex.Replace(ipa, "\\s+", " ").Trim();
            return ipa;
        }

        // --- Token + style ---
        private static long[] MapIpaToIds(string ipa)
        {
            if (string.IsNullOrWhiteSpace(ipa)) return null;
            var idsInner = new List<long>();
            foreach (var ch in ipa)
            {
                if (char.IsWhiteSpace(ch)) continue;
                if (_vocab.TryGetValue(ch.ToString(), out int id)) idsInner.Add(id);
            }
            if (idsInner.Count == 0) return null;
            if (idsInner.Count > 510) idsInner.RemoveRange(510, idsInner.Count - 510); // leave space for pads
            var ids = new long[idsInner.Count + 2];
            for (int i = 0; i < idsInner.Count; i++) ids[i + 1] = idsInner[i];
            // pads already zero
            return ids;
        }

        private static float[] LoadStyle(string voicePath, int innerTokenCount)
        {
            try
            {
                if (string.IsNullOrEmpty(voicePath) || !File.Exists(voicePath)) return null;
                if (!_voiceBinCache.TryGetValue(voicePath, out var floats))
                {
                    var bytes = File.ReadAllBytes(voicePath);
                    if (bytes.Length < 256 * 4) return null;
                    floats = new float[bytes.Length / 4];
                    Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
                    _voiceBinCache[voicePath] = floats;
                }
                int vectors = floats.Length / 256;
                if (vectors <= 0) return null;
                int idx = Math.Min(Math.Max(0, innerTokenCount), vectors - 1);
                var style = new float[256];
                Array.Copy(floats, idx * 256, style, 0, 256);
                return style;
            }
            catch { return null; }
        }

        // --- Trimming helpers ---
        private static float[] TrimLeading(float[] audio, float thr, int maxMs)
        {
            if (audio == null || audio.Length == 0 || thr <= 0 || maxMs <= 0) return audio;
            int maxSamples = (int)Math.Round(SampleRate * (maxMs / 1000.0));
            int i = 0; int scanned = 0;
            while (i < audio.Length && scanned < maxSamples)
            {
                if (Math.Abs(audio[i]) > thr) break;
                i++; scanned++;
            }
            if (i <= 0 || i >= audio.Length) return audio;
            int keep = audio.Length - i;
            var trimmed = new float[keep];
            Array.Copy(audio, i, trimmed, 0, keep);
            return trimmed;
        }
        private static float[] TrimTrailing(float[] audio, float thr, int leaveMs, int maxMs)
        {
            if (audio == null || audio.Length == 0 || thr <= 0 || maxMs <= 0) return audio;
            int leave = (int)Math.Round(SampleRate * (leaveMs / 1000.0));
            int maxTrim = (int)Math.Round(SampleRate * (maxMs / 1000.0));
            int i = audio.Length - 1; int trimmed = 0; int lastKeep = audio.Length - 1;
            while (i >= 0 && trimmed < maxTrim)
            {
                if (Math.Abs(audio[i]) > thr) { lastKeep = Math.Min(audio.Length - 1, i + leave); break; }
                i--; trimmed++;
            }
            if (lastKeep == audio.Length - 1 && trimmed < maxTrim) return audio; // nothing trimmed
            int newLen = Math.Min(audio.Length, lastKeep + 1);
            if (newLen < audio.Length)
            {
                var outArr = new float[newLen];
                Array.Copy(audio, outArr, newLen);
                return outArr;
            }
            return audio;
        }

        // --- Core generation ---
        private static float[] GenerateAudioInternal(string text, string speaker)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<float>();
            if (!EnsureInitialized()) return Array.Empty<float>();

            var snap = Kinectv1.App.SettingsProvider?.Current?.Tts;
            if (snap == null || !snap.Enabled) return Array.Empty<float>();

            // Resolve speaker once
            string voicePath = null;
            if (!string.IsNullOrWhiteSpace(speaker) && _voiceFiles.TryGetValue(speaker, out var vp)) voicePath = vp;
            else if (!string.IsNullOrWhiteSpace(snap.Speaker) && _voiceFiles.TryGetValue(snap.Speaker, out var vs)) voicePath = vs;
            else if (_voiceFiles.Count > 0) voicePath = _voiceFiles.Values.First();
            if (voicePath == null) { OnTtsError?.Invoke("Voice style not found"); return Array.Empty<float>(); }

            var allSegments = SplitIntoSegments(text);
            if (allSegments.Count <= 1)
            {
                return SynthesizeOne(text, voicePath, snap);
            }

            var final = new List<float>(allSegments.Count * 24000); // rough reserve
            for (int i = 0; i < allSegments.Count; i++)
            {
                var seg = allSegments[i];
                var audio = SynthesizeOne(seg, voicePath, snap);
                if (audio.Length > 0) final.AddRange(audio);
                // Inter-segment padding (not after last)
                if (i < allSegments.Count - 1 && snap.MinClausePaddingMs > 0)
                {
                    int padSamples = (int)Math.Round(SampleRate * (snap.MinClausePaddingMs / 1000.0));
                    if (padSamples > 0)
                    {
                        final.AddRange(new float[padSamples]);
                    }
                }
            }
            return final.ToArray();
        }

        private static float[] SynthesizeOne(string text, string voicePath, Kinectv1.Settings.TtsSettings snap)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<float>();
            var ipa = GetIpa(text);
            if (string.IsNullOrWhiteSpace(ipa)) { OnTtsError?.Invoke("IPA generation failed"); return Array.Empty<float>(); }
            var ids = MapIpaToIds(ipa);
            if (ids == null || ids.Length < 2) { OnTtsError?.Invoke("Tokenizer produced zero tokens"); return Array.Empty<float>(); }
            int innerCount = Math.Max(0, ids.Length - 2);
            var style = LoadStyle(voicePath, innerCount);
            if (style == null) { OnTtsError?.Invoke("Style vector load failed"); return Array.Empty<float>(); }

            var inputIds = new DenseTensor<long>(new[] { 1, ids.Length });
            for (int i = 0; i < ids.Length; i++) inputIds[0, i] = ids[i];
            for (int i = 0; i < 256; i++) _reuseStyleTensor[0, i] = style[i];
            _reuseSpeedTensor[0] = _speed <= 0 ? 1.0f : _speed;

            float[] audio;
            lock (_reuseInputs)
            {
                _reuseInputs.Clear();
                _reuseInputs.Add(NamedOnnxValue.CreateFromTensor("input_ids", inputIds));
                _reuseInputs.Add(NamedOnnxValue.CreateFromTensor("style", _reuseStyleTensor));
                _reuseInputs.Add(NamedOnnxValue.CreateFromTensor("speed", _reuseSpeedTensor));
                using var results = _session.Run(_reuseInputs);
                var first = results.First().Value as Tensor<float>;
                if (first == null) return Array.Empty<float>();
                if (first.Rank == 2)
                {
                    int n = first.Dimensions[1];
                    audio = new float[n];
                    for (int j = 0; j < n; j++) audio[j] = first[0, j];
                }
                else audio = first.ToArray();
            }
            if (audio == null || audio.Length == 0) return Array.Empty<float>();

            double thr = snap.TrimThreshold;
            if (thr > 0)
            {
                audio = TrimLeading(audio, (float)thr, Math.Min(200, snap.TrimMaxMs));
                audio = TrimTrailing(audio, (float)thr, snap.TrimLeaveMs, snap.TrimMaxMs);
            }
            // NOTE: Clause padding handled by caller for multi-segment case.
            if (snap.MinClausePaddingMs > 0 && SplitIntoSegments(text).Count <= 1)
            {
                int padSamples = (int)Math.Round(SampleRate * (snap.MinClausePaddingMs / 1000.0));
                if (padSamples > 0)
                {
                    var padded = new float[audio.Length + padSamples];
                    Array.Copy(audio, padded, audio.Length);
                    audio = padded;
                }
            }
            return audio;
        }

        // --- Helper: split text into punctuation-delimited segments (keeps end punctuation) ---
        private static List<string> SplitIntoSegments(string text)
        {
            var segments = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return segments;
            var sb = new StringBuilder();
            foreach (var ch in text)
            {
                sb.Append(ch);
                if (ch == '.' || ch == '!' || ch == '?')
                {
                    var seg = sb.ToString().Trim();
                    if (seg.Length > 0) segments.Add(seg);
                    sb.Clear();
                }
            }
            var tail = sb.ToString().Trim();
            if (tail.Length > 0) segments.Add(tail);
            return segments;
        }

        // --- Public generation wrappers ---
        public static Task<float[]> GenerateAudioDataAsync(string text, string speakerName = null, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                try
                {
                    if (ct.IsCancellationRequested) return Array.Empty<float>();
                    if (!IsEnabled()) return Array.Empty<float>();
                    return GenerateAudioInternal(text, speakerName);
                }
                catch (Exception ex)
                {
                    OnTtsError?.Invoke(ex.Message);
                    return Array.Empty<float>();
                }
            }, ct);
        }

        public static async Task<bool> SpeakStreamingWithPreemptionAsync(string text, string speakerName = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!IsEnabled()) { OnTtsError?.Invoke("TTS disabled in settings"); return false; }
            CancellationTokenSource localCts = null;
            try
            {
                // Replace previous CTS (barge-in)
                lock (_speakLock)
                {
                    var prev = _currentLocalSpeakCts;
                    _currentLocalSpeakCts = new CancellationTokenSource();
                    localCts = _currentLocalSpeakCts;
                    try { prev?.Cancel(); } catch { }
                    try { prev?.Dispose(); } catch { }
                }

                OnTtsSpeakingStarted?.Invoke();
                var audio = await GenerateAudioDataAsync(text, speakerName, localCts.Token).ConfigureAwait(false);
                if (localCts.IsCancellationRequested) return false;
                if (audio == null || audio.Length == 0) { OnTtsError?.Invoke("No audio generated"); return false; }
                var vol = Math.Max(0f, (float)(Kinectv1.App.SettingsProvider?.Current?.Tts?.LocalVolume ?? 1.0));
                if (vol != 1f) for (int i = 0; i < audio.Length; i++) audio[i] *= vol;
                await AudioDeviceManager.PlayLocallyAsync(audio, SampleRate, localCts.Token).ConfigureAwait(false);
                if (localCts.IsCancellationRequested) return false;
                OnTtsSpeakingFinished?.Invoke();
                return true;
            }
            catch (OperationCanceledException)
            {
                // Swallow clean cancellation
                return false;
            }
            catch (Exception ex)
            {
                OnTtsError?.Invoke(ex.Message);
                return false;
            }
            finally
            {
                if (localCts != null)
                {
                    lock (_speakLock)
                    {
                        if (_currentLocalSpeakCts == localCts) _currentLocalSpeakCts = null;
                    }
                    try { localCts.Dispose(); } catch { }
                }
            }
        }
    }
}
