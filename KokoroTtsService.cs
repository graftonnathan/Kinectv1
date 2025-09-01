using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Newtonsoft.Json.Linq;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1
{
    internal static class KokoroTtsService
    {
        private static readonly object _lock = new object();
        private static InferenceSession _session;
        private static InferenceSession _cpuSession; // cached CPU session for per-segment fallback
        private static readonly object _cpuLock = new object();
        private static bool _initialized;
        private static bool _usingGpu;
        private static string _baseDir = Path.Combine("models", "tts", "kokoro");
        private static string _modelPath = Path.Combine("models", "tts", "kokoro", "onnx", "model_q8f16.onnx");
        private static int _nativeSampleRate = 24000;

        private static Dictionary<string, int> _vocab = new Dictionary<string, int>();
        private static readonly Dictionary<string, string> _voiceFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string _defaultVoiceKey = "af_bella";

        private static float _speed = 1.05f; // speed multiplier (configurable)
        private static readonly float[] _defaultStyleVector = CreateDefaultStyleVector();
        private static readonly List<float> _sharedOutputBuffer = new List<float>(1024 * 1024);
        private static DenseTensor<float> _reuseStyleTensor = new DenseTensor<float>(new[] { 1, 256 });
        private static DenseTensor<float> _reuseSpeedTensor = new DenseTensor<float>(new[] { 1 });
        private static readonly List<NamedOnnxValue> _reuseInputsList = new List<NamedOnnxValue>(3);

        private static readonly Dictionary<string, string> _ipaCache = new Dictionary<string, string>();
        private static readonly Queue<string> _ipaCacheKeys = new Queue<string>();
        private static readonly object _ipaCacheLock = new object();
        private const int MaxIpaCacheSize = 256;

        private static readonly BlockingCollection<IpaRequest> _ipaQueue = new BlockingCollection<IpaRequest>();
        private static Thread _ipaWorkerThread;
        private static volatile bool _ipaWorkerRunning = false;
        private static readonly object _ipaWorkerLock = new object();
        private static EspeakIpaNet48 _ipaService;
        private static bool _loggedEspeakMissing = false;
        private static readonly Dictionary<string, float[]> _voiceBinCache = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        private static bool _retryingAfterGpuFallback = false;
        private static volatile bool _ttsDebug = true; // verbose debug

        private static int _ipaServiceTimeoutMs = 1500;   // configurable
        private static int _ipaOneShotTimeoutMs = 1500;   // configurable

        private class IpaRequest
        {
            public string Text { get; set; }
            public TaskCompletionSource<string> Tcs { get; set; }
            public CancellationToken CancellationToken { get; set; }
        }

        private const bool EnableCpuSegmentRetry = false;

        private static float[] CreateDefaultStyleVector()
        {
            var vector = new float[256];
            for (int i = 0; i < 256; i++) vector[i] = 0.01f;
            return vector;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private static void ApplyRuntimeSettings()
        {
            try
            {
                // Prefer unified JSON snapshot
                var snap = Kinectv1.App.SettingsProvider?.Current;
                if (snap != null && snap.Tts != null)
                {
                    _speed = snap.Tts.Speed;
                    _ipaServiceTimeoutMs = snap.Tts.IpaServiceTimeoutMs;
                    _ipaOneShotTimeoutMs = snap.Tts.IpaOneShotTimeoutMs;
                    return;
                }

                // Fallback to legacy keys
                _speed = AppSettings.LoadTtsSpeed();
                _ipaServiceTimeoutMs = AppSettings.LoadTtsIpaServiceTimeoutMs();
                _ipaOneShotTimeoutMs = AppSettings.LoadTtsIpaOneShotTimeoutMs();
            }
            catch { }
        }

        private static void PreloadOrtNative()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var candidateDirs = new[]
                {
                    Path.Combine(baseDir, "runtimes", "win-x64", "native"),
                    Path.Combine(baseDir, "lib", "onnxruntime"),
                    baseDir,
                };

                string PickOrtDll()
                {
                    string selectedWithTrt = null;
                    string selectedWithCuda = null;

                    foreach (var dir in candidateDirs)
                    {
                        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                        var ort = Path.Combine(dir, "onnxruntime.dll");
                        if (!File.Exists(ort)) continue;

                        var h = LoadLibrary(ort);
                        if (h == IntPtr.Zero) continue;
                        var hasTrtExport = GetProcAddress(h, "OrtSessionOptionsAppendExecutionProvider_TensorRT") != IntPtr.Zero;
                        var hasCudaExport = GetProcAddress(h, "OrtSessionOptionsAppendExecutionProvider_CUDA") != IntPtr.Zero;
                        FreeLibrary(h);

                        if (hasTrtExport) { selectedWithTrt = ort; break; }
                        if (hasCudaExport && selectedWithCuda == null) { selectedWithCuda = ort; }
                    }

                    if (!string.IsNullOrEmpty(selectedWithTrt)) return selectedWithTrt;
                    if (!string.IsNullOrEmpty(selectedWithCuda)) return selectedWithCuda;

                    foreach (var dir in candidateDirs)
                    {
                        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                        var ort = Path.Combine(dir, "onnxruntime.dll");
                        if (File.Exists(ort)) return ort;
                    }
                    return null;
                }

                var selected = PickOrtDll();
                if (!string.IsNullOrEmpty(selected))
                {
                    var h = LoadLibrary(selected);
                    if (h != IntPtr.Zero)
                    {
                        Console.WriteLine("[Onnx] Preloaded onnxruntime: " + selected);
                    }
                }
            }
            catch { }
        }

        public static bool Initialize()
        {
            lock (_lock)
            {
                if (_initialized) return true;
                try
                {
                    ApplyRuntimeSettings();
                    ResolveModelLocationsFromSettings();

                    LoadVocab(Path.Combine(_baseDir, "tokenizer.json"));
                    LoadVoices();

                    if (!File.Exists(_modelPath))
                    {
                        var error = AppError.TTS("TTS_MODEL_NOT_FOUND",
                            $"TTS model not found: {_modelPath}",
                            "Verify model path on Diagnostics page or download the Kokoro model.");
                        Console.WriteLine($"[Kokoro] {error.GetDisplayString()}");
                        return false;
                    }

                    // Prefer JSON execution provider
                    bool useGpu;
                    try
                    {
                        var exec = Kinectv1.App.SettingsProvider?.Current?.Tts.Execution;
                        useGpu = (exec == Kinectv1.Settings.TtsExecution.GPU);
                    }
                    catch { useGpu = AppSettings.LoadTtsUseGpu(); }

                    CreateSession(useGpu);

                    try
                    {
                        var ids = new long[] { 0, 0 };
                        var style = new float[256];
                        var inputIds = new DenseTensor<long>(new[] { 1, ids.Length });
                        for (int i = 0; i < ids.Length; i++) inputIds[0, i] = ids[i];
                        var styleTensor = new DenseTensor<float>(new[] { 1, 256 });
                        for (int i = 0; i < 256; i++) styleTensor[0, i] = style[i];
                        var speedTensor = new DenseTensor<float>(new[] { 1 });
                        speedTensor[0] = 1.0f;
                        var inputs = new List<NamedOnnxValue>
                        {
                            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                            NamedOnnxValue.CreateFromTensor("style", styleTensor),
                            NamedOnnxValue.CreateFromTensor("speed", speedTensor)
                        };
                        using var _ = _session.Run(inputs);
                        Console.WriteLine("[Kokoro] Warmup inference complete");
                    }
                    catch (Exception wex)
                    {
                        Console.WriteLine($"[Kokoro] Warmup skipped: {wex.Message}");
                    }

                    try
                    {
                        EnsureIpaService();
                        if (_ipaService != null)
                        {
                            var _ = _ipaService.GetIpaAsync(".", TimeSpan.FromSeconds(1));
                        }
                    }
                    catch { }

                    _initialized = true;
                    Console.WriteLine("[Kokoro] Initialized");
                    return true;
                }
                catch (Exception ex)
                {
                    var error = AppError.TTS("TTS_INIT_FAILED",
                        $"Kokoro TTS initialization failed: {ex.Message}",
                        "Check model files and GPU settings, or verify model path on Diagnostics page.", ex);
                    Console.WriteLine($"[Kokoro] {error.GetDisplayString()}");
                    return false;
                }
            }
        }

        private static void CreateSession(bool requestedGpu)
        {
            try { _session?.Dispose(); } catch { }
            _session = null;
            PreloadOrtNative();
            _session = OnnxSessionFactory.Create(_modelPath, requestedGpu, out _usingGpu);
            Console.WriteLine($"[Kokoro] Execution provider: {(_usingGpu ? "GPU" : "CPU")}");
        }

        public static bool RecreateSessionFromSettings()
        {
            lock (_lock)
            {
                if (!_initialized)
                {
                    return Initialize();
                }
                try
                {
                    bool useGpu;
                    try
                    {
                        useGpu = (Kinectv1.App.SettingsProvider?.Current?.Tts.Execution == Kinectv1.Settings.TtsExecution.GPU);
                    }
                    catch { useGpu = AppSettings.LoadTtsUseGpu(); }

                    CreateSession(useGpu);
                    Console.WriteLine($"[Kokoro] Session recreated using {(useGpu && _usingGpu ? "GPU" : "CPU")} ");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Kokoro] Failed to recreate session: {ex.Message}");
                    return false;
                }
            }
        }

        public static bool IsUsingGpu() => _usingGpu;
        public static int GetSampleRate() => _nativeSampleRate;

        private static string NormalizeKokoroBaseDir(string folder)
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
                var snap = Kinectv1.App.SettingsProvider?.Current;
                if (snap?.Tts != null)
                {
                    var folder = snap.Tts.ModelFolder;
                    if (!string.IsNullOrWhiteSpace(folder))
                    {
                        var baseDir = NormalizeKokoroBaseDir(folder);
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

                    var cfg = snap.Tts.ModelPath;
                    if (!string.IsNullOrWhiteSpace(cfg))
                    {
                        var fullCfg = Path.IsPathRooted(cfg) ? cfg : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, cfg);
                        string baseDir = null;

                        if (File.Exists(fullCfg))
                        {
                            baseDir = Path.GetDirectoryName(fullCfg);
                            _modelPath = fullCfg;
                        }
                        else if (Directory.Exists(fullCfg))
                        {
                            if (File.Exists(Path.Combine(fullCfg, "model_q8f16.onnx")))
                            {
                                _modelPath = Path.Combine(fullCfg, "model_q8f16.onnx");
                                baseDir = Directory.GetParent(fullCfg)?.FullName ?? fullCfg;
                            }
                            else if (Directory.Exists(Path.Combine(fullCfg, "onnx")))
                            {
                                var onnx = Path.Combine(fullCfg, "onnx", "model_q8f16.onnx");
                                _modelPath = onnx;
                                baseDir = fullCfg;
                            }
                            else
                            {
                                baseDir = fullCfg;
                                _modelPath = Path.Combine(fullCfg, "onnx", "model_q8f16.onnx");
                            }
                        }

                        if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir))
                        {
                            _baseDir = NormalizeKokoroBaseDir(baseDir);
                        }
                    }

                    return; // prefer JSON path if present
                }

                // Legacy fallback
                var folderLegacy = AppSettings.LoadTtsModelFolder();
                if (!string.IsNullOrWhiteSpace(folderLegacy))
                {
                    var baseDir = NormalizeKokoroBaseDir(folderLegacy);
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

                var cfgLegacy = AppSettings.LoadTtsModelPath();
                if (!string.IsNullOrWhiteSpace(cfgLegacy))
                {
                    var fullCfg = Path.IsPathRooted(cfgLegacy) ? cfgLegacy : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, cfgLegacy);
                    string baseDir = null;

                    if (File.Exists(fullCfg))
                    {
                        baseDir = Path.GetDirectoryName(fullCfg);
                        _modelPath = fullCfg;
                    }
                    else if (Directory.Exists(fullCfg))
                    {
                        if (File.Exists(Path.Combine(fullCfg, "model_q8f16.onnx")))
                        {
                            _modelPath = Path.Combine(fullCfg, "model_q8f16.onnx");
                            baseDir = Directory.GetParent(fullCfg)?.FullName ?? fullCfg;
                        }
                        else if (Directory.Exists(Path.Combine(fullCfg, "onnx")))
                        {
                            var onnx = Path.Combine(fullCfg, "onnx", "model_q8f16.onnx");
                            _modelPath = onnx;
                            baseDir = fullCfg;
                        }
                        else
                        {
                            baseDir = fullCfg;
                            _modelPath = Path.Combine(fullCfg, "onnx", "model_q8f16.onnx");
                        }
                    }

                    if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir))
                    {
                        _baseDir = NormalizeKokoroBaseDir(baseDir);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kokoro] ResolveModelLocationsFromSettings error: {ex.Message}");
            }
        }

        private static void LoadVocab(string tokenizerPath)
        {
            try
            {
                if (!File.Exists(tokenizerPath))
                {
                    var dirName = Path.GetFileName(Path.GetDirectoryName(tokenizerPath));
                    if (string.Equals(dirName, "onnx", StringComparison.OrdinalIgnoreCase))
                    {
                        var parent = Directory.GetParent(Path.GetDirectoryName(tokenizerPath))?.FullName;
                        var alt = string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, "tokenizer.json");
                        if (!string.IsNullOrEmpty(alt) && File.Exists(alt)) tokenizerPath = alt;
                    }
                }

                if (!File.Exists(tokenizerPath)) { Console.WriteLine($"[Kokoro] tokenizer.json missing: {tokenizerPath}"); return; }
                var json = File.ReadAllText(tokenizerPath, Encoding.UTF8);
                var jobj = JObject.Parse(json);
                var vocabObj = (JObject)jobj.SelectToken("model.vocab");
                _vocab.Clear();
                foreach (var prop in vocabObj.Properties())
                {
                    _vocab[prop.Name] = (int)prop.Value;
                }
                Console.WriteLine($"[Kokoro] Vocab loaded: {_vocab.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kokoro] LoadVocab failed: {ex.Message}");
            }
        }

        private static void LoadVoices()
        {
            try
            {
                _voiceFiles.Clear();
                var dir = Path.Combine(_baseDir, "voices");
                if (!Directory.Exists(dir))
                {
                    var name = new DirectoryInfo(_baseDir).Name;
                    if (string.Equals(name, "onnx", StringComparison.OrdinalIgnoreCase))
                    {
                        var parent = Directory.GetParent(_baseDir)?.FullName;
                        if (!string.IsNullOrEmpty(parent))
                        {
                            var altDir = Path.Combine(parent, "voices");
                            if (Directory.Exists(altDir)) dir = altDir;
                        }
                    }
                }
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.GetFiles(dir, "*.bin"))
                    {
                        var key = Path.GetFileNameWithoutExtension(file);
                        _voiceFiles[key] = file;
                    }
                }
                Console.WriteLine($"[Kokoro] Voices loaded: {_voiceFiles.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kokoro] LoadVoices error: {ex.Message}");
            }
        }

        public static IEnumerable<string> GetVoices() => _voiceFiles.Keys.OrderBy(k => k);
        public static void SetDefaultVoice(string key) { if (!string.IsNullOrWhiteSpace(key) && _voiceFiles.ContainsKey(key)) _defaultVoiceKey = key; }
        public static void SetSpeed(float s) { _speed = Math.Max(0.5f, Math.Min(2.0f, s)); }

        private static IEnumerable<Segment> SplitIntoClauses(string text)
        {
            text = Regex.Replace(text, "\\s+", " ").Trim();
            text = Regex.Replace(text, "([!?.,;:]){2,}", "$1");
            if (string.IsNullOrWhiteSpace(text)) yield break;

            // Split ONLY on strong end punctuation to avoid extra pauses on commas/semicolons/colons
            var parts = Regex.Split(text, "([.!?])");
            var buffer = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                if (string.IsNullOrEmpty(p)) continue;
                if (Regex.IsMatch(p, "^[.!?]$"))
                {
                    buffer.Append(p);
                    var s = buffer.ToString().Trim();
                    if (!string.IsNullOrEmpty(s))
                    {
                        bool isStrong = true; // only strong punctuation reaches here
                        yield return Segment.FromText(s, explicitBreak: isStrong);
                    }
                    buffer.Clear();
                }
                else
                {
                    buffer.Append(p);
                }
            }
            var rest = buffer.ToString().Trim();
            if (!string.IsNullOrEmpty(rest)) yield return Segment.FromText(rest, explicitBreak: false);
        }

        private static float[] TrimLeadingSilence(float[] audio, int sampleRate, float threshold = 0.002f, int maxTrimMs = 150)
        {
            try
            {
                if (audio == null || audio.Length == 0) return audio;
                int maxTrim = (int)Math.Round(sampleRate * (maxTrimMs / 1000.0));
                int start = 0;
                int scanned = 0;
                for (int i = 0; i < audio.Length && scanned < maxTrim; i++)
                {
                    if (Math.Abs(audio[i]) > threshold)
                    {
                        start = i;
                        break;
                    }
                    scanned++;
                    start = i;
                }
                if (start <= 0) return audio;
                int keep = audio.Length - start;
                if (keep <= 0) return Array.Empty<float>();
                var trimmed = new float[keep];
                Array.Copy(audio, start, trimmed, 0, keep);
                return trimmed;
            }
            catch
            {
                return audio;
            }
        }

        private static List<Segment> BuildSegments(string text)
        {
            var segments = new List<Segment>();
            if (string.IsNullOrWhiteSpace(text)) return segments;

            // Handle SSML <break time="..."/>
            var breakRegex = new Regex("<break\\s+time=\"(?<time>[^\"]+)\"\\s*/>", RegexOptions.IgnoreCase);
            int idx = 0;
            foreach (Match m in breakRegex.Matches(text))
            {
                if (m.Index > idx)
                {
                    var chunk = text.Substring(idx, m.Index - idx);
                    segments.AddRange(SplitIntoClauses(chunk));
                }
                var ms = ParseDurationToMs(m.Groups["time"].Value);
                segments.Add(Segment.Break(ms));
                idx = m.Index + m.Length;
            }
            if (idx < text.Length)
            {
                var tail = text.Substring(idx);
                segments.AddRange(SplitIntoClauses(tail));
            }

            return segments;
        }

        private static int ParseDurationToMs(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            value = value.Trim().ToLowerInvariant();
            if (value.EndsWith("ms") && int.TryParse(value.Substring(0, value.Length - 2), out var ms)) return Math.Max(0, ms);
            if (value.EndsWith("s") && double.TryParse(value.Substring(0, value.Length - 1), out var s)) return (int)Math.Round(s * 1000);
            if (int.TryParse(value, out var plain)) return Math.Max(0, plain);
            return 0;
        }

        private static string SanitizeInputText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            text = text.Replace('\u201C', '"').Replace('\u201D', '"');
            text = text.Replace('\u2018', '\'').Replace('\u2019', '\'');
            text = text.Replace("–", "-").Replace("—", "-");
            text = text.Replace("…", "...");
            text = text.Replace("«", "\"").Replace("»", "\"");
            text = text.Replace("‚", ",").Replace("„", "\"");
            text = text.Replace("‹", "'").Replace("›", "'");
            text = Regex.Replace(text, @"[^\x00-\x7F]+", " ");
            text = Regex.Replace(text, @"[\x00-\x1F\x7F]", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text;
        }

        private static bool TryGetCachedIpa(string text, out string ipa)
        {
            ipa = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string cacheKey = text.ToLowerInvariant().Trim();
            lock (_ipaCacheLock)
            {
                return _ipaCache.TryGetValue(cacheKey, out ipa);
            }
        }

        private static void CacheIpa(string text, string ipa)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(ipa)) return;
            string cacheKey = text.ToLowerInvariant().Trim();
            lock (_ipaCacheLock)
            {
                if (_ipaCache.ContainsKey(cacheKey))
                {
                    _ipaCache[cacheKey] = ipa;
                    return;
                }
                if (_ipaCache.Count >= MaxIpaCacheSize)
                {
                    if (_ipaCacheKeys.Count > 0)
                    {
                        string oldestKey = _ipaCacheKeys.Dequeue();
                        _ipaCache.Remove(oldestKey);
                    }
                }
                _ipaCache[cacheKey] = ipa;
                _ipaCacheKeys.Enqueue(cacheKey);
            }
        }

        private static void EnsureIpaWorker()
        {
            if (_ipaWorkerRunning) return;
            lock (_ipaWorkerLock)
            {
                if (_ipaWorkerRunning) return;
                _ipaWorkerRunning = true;
                _ipaWorkerThread = new Thread(IpaWorkerLoop)
                {
                    IsBackground = true,
                    Name = "eSpeak-IPA-Worker"
                };
                _ipaWorkerThread.Start();
                if (_ttsDebug) Console.WriteLine("[TTS] eSpeak IPA worker thread started");
            }
        }

        private static void IpaWorkerLoop()
        {
            try
            {
                while (_ipaWorkerRunning)
                {
                    try
                    {
                        if (_ipaQueue.TryTake(out IpaRequest request, 1000))
                        {
                            ProcessIpaRequest(request);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TTS] IPA worker error: {ex.Message}");
                        Thread.Sleep(100);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TTS] IPA worker thread crashed: {ex.Message}");
            }
            finally
            {
                lock (_ipaWorkerLock)
                {
                    _ipaWorkerRunning = false;
                }
                if (_ttsDebug) Console.WriteLine("[TTS] eSpeak IPA worker thread stopped");
            }
        }

        private static void ProcessIpaRequest(IpaRequest request)
        {
            if (request?.Tcs == null) return;
            try
            {
                if (request.CancellationToken.IsCancellationRequested)
                {
                    request.Tcs.TrySetCanceled();
                    return;
                }
                EnsureIpaService();
                if (_ipaService == null)
                {
                    request.Tcs.TrySetResult(null);
                    return;
                }
                var timeout = TimeSpan.FromMilliseconds(Math.Max(200, _ipaServiceTimeoutMs));
                var ipa = _ipaService.GetIpaAsync(request.Text, timeout).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(ipa))
                {
                    var normalized = NormalizeIpa(ipa);
                    request.Tcs.TrySetResult(normalized);
                }
                else
                {
                    request.Tcs.TrySetResult(null);
                }
            }
            catch (TimeoutException)
            {
                request.Tcs.TrySetResult(null);
            }
            catch (Exception ex)
            {
                request.Tcs.TrySetException(ex);
            }
        }

        private static string NormalizeIpa(string ipa)
        {
            if (string.IsNullOrWhiteSpace(ipa)) return ipa;
            ipa = ipa.Replace("/", " ");
            ipa = Regex.Replace(ipa, "\\s+", " ").Trim();
            ipa = Regex.Replace(ipa, @"[Γòö├¬]", " ");
            ipa = Regex.Replace(ipa, @"\\x[0-9a-fA-F]{2}", " ");
            ipa = Regex.Replace(ipa, "\\s+", " ").Trim();
            return ipa;
        }

        private static void EnsureIpaService()
        {
            if (_ipaService != null) return;
            try
            {
                string exe = null;
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var localDir = Path.Combine(baseDir, "models", "tts", "Espeak NG");
                var localNg = Path.Combine(localDir, "espeak-ng.exe");
                var localClassic = Path.Combine(localDir, "espeak.exe");
                if (File.Exists(localNg)) exe = localNg;
                else if (File.Exists(localClassic)) exe = localClassic;
                else exe = "espeak-ng.exe";

                if (_ttsDebug)
                {
                    Console.WriteLine($"[TTS] eSpeak expected: {localDir}");
                    Console.WriteLine($"[TTS] eSpeak resolved: {exe}");
                }

                _ipaService = new EspeakIpaNet48(exe, "en-us");
                try { _ = _ipaService.GetIpaAsync(".", TimeSpan.FromMilliseconds(800)); } catch { }
            }
            catch (Exception ex)
            {
                if (!_loggedEspeakMissing)
                {
                    _loggedEspeakMissing = true;
                    Console.WriteLine($"[Kokoro] Failed to start eSpeak NG IPA service: {ex.Message}");
                }
            }
        }

        private static string GetIpaOnce(string text, int timeoutMs = 2000)
        {
            try
            {
                var exe = "espeak-ng.exe";
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var localDir = Path.Combine(baseDir, "models", "tts", "Espeak NG");
                var localNg = Path.Combine(localDir, "espeak-ng.exe");
                var localClassic = Path.Combine(localDir, "espeak.exe");
                if (File.Exists(localNg)) exe = localNg; else if (File.Exists(localClassic)) exe = localClassic;

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--ipa -q -v en-us \"" + text.Replace("\r", " ").Replace("\n", " ") + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.EnvironmentVariables["ESPEAK_NG_OUTPUT_CHARSET"] = "utf-8";

                using (var p = new Process { StartInfo = psi })
                {
                    p.Start();
                    var cts = new CancellationTokenSource(timeoutMs);
                    var readTask = p.StandardOutput.ReadToEndAsync();
                    var done = Task.WhenAny(readTask, Task.Delay(Timeout.Infinite, cts.Token)).GetAwaiter().GetResult();
                    if (done != readTask)
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                    var s = readTask.GetAwaiter().GetResult();
                    return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
                }
            }
            catch { return null; }
        }

        private static string RunEspeak(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = SanitizeInputText(text);
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (TryGetCachedIpa(text, out string cachedIpa))
            {
                if (_ttsDebug) Console.WriteLine($"[TTS] Cache hit for: '{text}' -> '{cachedIpa}'");
                return cachedIpa;
            }
            return GetIpaViaWorker(text);
        }

        private static string GetIpaViaWorker(string text)
        {
            try
            {
                EnsureIpaWorker();
                var tcs = new TaskCompletionSource<string>();
                var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(200, _ipaServiceTimeoutMs)));
                var request = new IpaRequest { Text = text, Tcs = tcs, CancellationToken = cts.Token };
                if (!_ipaQueue.TryAdd(request, 100))
                {
                    Console.WriteLine("[Kokoro] IPA queue full, using fallback");
                    return GetIpaDirectFallback(text);
                }
                try
                {
                    var result = tcs.Task.GetAwaiter().GetResult();
                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        CacheIpa(text, result);
                        return result;
                    }
                }
                catch (AggregateException ex)
                {
                    Console.WriteLine($"[Kokoro] Worker IPA failed: {ex.InnerException?.Message ?? ex.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Kokoro] Worker IPA error: {ex.Message}");
                }
                return GetIpaDirectFallback(text);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kokoro] IPA worker setup failed: {ex.Message}");
                return GetIpaDirectFallback(text);
            }
        }

        private static string GetIpaDirectFallback(string text)
        {
            try
            {
                EnsureIpaService();
                if (_ipaService != null)
                {
                    var timeout = TimeSpan.FromMilliseconds(Math.Max(200, _ipaServiceTimeoutMs));
                    var ipa = _ipaService.GetIpaAsync(text, timeout).GetAwaiter().GetResult();
                    if (!string.IsNullOrWhiteSpace(ipa))
                    {
                        var normalized = NormalizeIpa(ipa);
                        CacheIpa(text, normalized);
                        return normalized;
                    }
                }
            }
            catch (TimeoutException)
            {
                Console.WriteLine("[Kokoro] Direct eSpeak timeout, trying one-shot");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kokoro] Direct eSpeak error: {ex.Message}");
            }

            var once = GetIpaOnce(text, timeoutMs: Math.Max(200, _ipaOneShotTimeoutMs));
            if (!string.IsNullOrWhiteSpace(once))
            {
                var normalized = NormalizeIpa(once);
                CacheIpa(text, normalized);
                return normalized;
            }

            // Disable Simple G2P fallback to avoid poor pronunciation/gibberish
            return null;
        }

        private static long[] MapIpaToIds(string ipa, int maxLen)
        {
            if (string.IsNullOrWhiteSpace(ipa)) return null;
            var symbols = new List<string>();
            foreach (var ch in ipa)
            {
                if (char.IsWhiteSpace(ch)) continue;
                symbols.Add(ch.ToString());
            }
            var innerIds = new List<long>(symbols.Count);
            var unknown = new HashSet<string>();
            foreach (var s in symbols)
            {
                int id;
                if (_vocab.TryGetValue(s, out id)) innerIds.Add(id); else unknown.Add(s);
            }
            if (unknown.Count > 0)
            {
                Console.WriteLine($"[Kokoro] Unknown IPA symbols ({unknown.Count}): {string.Join(" ", unknown.Take(15))}");
            }
            if (innerIds.Count == 0) return null;
            int maxInner = Math.Max(0, maxLen - 2);
            if (innerIds.Count > maxInner)
            {
                innerIds.RemoveRange(maxInner, innerIds.Count - maxInner);
            }
            var ids = new long[innerIds.Count + 2];
            ids[0] = 0;
            for (int i = 0; i < innerIds.Count; i++) ids[i + 1] = innerIds[i];
            ids[ids.Length - 1] = 0;
            Console.WriteLine($"[Kokoro] Token ids: inner={innerIds.Count}, total={ids.Length}");
            return ids;
        }

        private static float[] LoadStyleVectorAt(string path, int innerTokenCount)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                if (!_voiceBinCache.TryGetValue(path, out var floats))
                {
                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Length < 256 * 4) return null;
                    floats = new float[bytes.Length / 4];
                    Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
                    _voiceBinCache[path] = floats;
                }
                int vectorCount = floats.Length / 256;
                if (vectorCount <= 0) return null;
                // Restore dynamic selection based on innerTokenCount
                int idx = Math.Min(Math.Max(0, innerTokenCount), vectorCount - 1);
                var style = new float[256];
                Array.Copy(floats, idx * 256, style, 0, 256);
                return style;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kokoro] LoadStyleVectorAt failed: {ex.Message}");
                return null;
            }
        }

        private static bool TryRunModelOnSession(InferenceSession session, DenseTensor<long> inputIds, DenseTensor<float> styleTensor, DenseTensor<float> speedTensor, out float[] audio)
        {
            audio = null;
            try
            {
                lock (_reuseInputsList)
                {
                    _reuseInputsList.Clear();
                    _reuseInputsList.Add(NamedOnnxValue.CreateFromTensor("input_ids", inputIds));
                    _reuseInputsList.Add(NamedOnnxValue.CreateFromTensor("style", styleTensor));
                    _reuseInputsList.Add(NamedOnnxValue.CreateFromTensor("speed", speedTensor));

                    using var results = session.Run(_reuseInputsList);
                    var first = results.First().Value as Tensor<float>;
                    if (first == null) return false;
                    if (first.Rank == 2)
                    {
                        int n = first.Dimensions[1];
                        audio = new float[n];
                        for (int i = 0; i < n; i++) audio[i] = first[0, i];
                    }
                    else
                    {
                        audio = first.ToArray();
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryRunModel(DenseTensor<long> inputIds, DenseTensor<float> styleTensor, DenseTensor<float> speedTensor, out float[] audio)
        {
            try
            {
                return TryRunModelOnSession(_session, inputIds, styleTensor, speedTensor, out audio);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kokoro] Inference error: {ex.Message}");
                audio = null;
                return false;
            }
        }

        private static void EnsureCpuSession()
        {
            if (_cpuSession != null) return;
            lock (_cpuLock)
            {
                if (_cpuSession != null) return;
                try
                {
                    var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED };
                    try { so.AppendExecutionProvider_CPU(0); } catch { }
                    _cpuSession = new InferenceSession(_modelPath, so);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Kokoro] Failed to create CPU fallback session: {ex.Message}");
                }
            }
        }

        private static void DisposeCpuSession()
        {
            try { _cpuSession?.Dispose(); } catch { }
            _cpuSession = null;
        }

        private static bool IsDegenerateAudio(float[] audio)
        {
            if (audio == null || audio.Length == 0) return true;
            int nonZero = 0;
            for (int i = 0; i < audio.Length; i++)
            {
                var s = audio[i];
                if (float.IsNaN(s) || float.IsInfinity(s)) return true;
                if (Math.Abs(s) > 1e-9f) nonZero++;
            }
            return nonZero == 0;
        }

        private static float[] TrimTrailingSilence(float[] audio, int sampleRate, float threshold = 0.002f, int leaveMs = 10, int maxTrimMs = 600)
        {
            try
            {
                if (audio == null || audio.Length == 0) return audio;
                int leave = (int)Math.Round(sampleRate * (leaveMs / 1000.0));
                int maxTrim = (int)Math.Round(sampleRate * (maxTrimMs / 1000.0));
                int end = audio.Length - 1;
                int trimmed = 0;
                for (int i = end; i >= 0 && trimmed < maxTrim; i--)
                {
                    if (Math.Abs(audio[i]) > threshold)
                    {
                        int desiredEnd = Math.Min(audio.Length - 1, i + leave);
                        if (desiredEnd < audio.Length - 1)
                        {
                            Array.Resize(ref audio, desiredEnd + 1);
                        }
                        return audio;
                    }
                    trimmed++;
                }
                int keep = Math.Min(audio.Length, Math.Max(leave, audio.Length - maxTrim + leave));
                if (keep < audio.Length)
                {
                    Array.Resize(ref audio, keep);
                }
                return audio;
            }
            catch
            {
                return audio;
            }
        }

        public static float[] GenerateAudio(string text, string voiceKey)
        {
            using (var scope = Telemetry.LatencyScope("tts_generate", emitEvent: true))
            {
                ApplyRuntimeSettings();
                if (!_initialized && !Initialize()) { Telemetry.Counter("tts.initialization_failures"); return Array.Empty<float>(); }
                if (string.IsNullOrWhiteSpace(text)) { Telemetry.Counter("tts.empty_text"); return Array.Empty<float>(); }
                Telemetry.Counter("tts.generate_requests");
                var vk = (!string.IsNullOrWhiteSpace(voiceKey) && _voiceFiles.ContainsKey(voiceKey)) ? voiceKey : _defaultVoiceKey;

                // Snapshot JSON tunables once per Generate call
                var snap = Kinectv1.App.SettingsProvider?.Current?.Tts;
                float trimThr = (float)(snap?.TrimThreshold ?? AppSettings.LoadTtsTrimThreshold());
                int trimLeave = snap?.TrimLeaveMs ?? AppSettings.LoadTtsTrimLeaveMs();
                int trimMax = snap?.TrimMaxMs ?? AppSettings.LoadTtsTrimMaxMs();
                int padMsSnap = snap?.MinClausePaddingMs ?? AppSettings.LoadTtsMinClausePaddingMs();

                var segments = BuildSegments(text);
                var output = new List<float>();
                int segmentCount = 0;

                foreach (var seg in segments)
                {
                    if (seg.IsBreak)
                    {
                        var ms = Math.Max(0, seg.BreakMs);
                        int samples = (int)Math.Round((_nativeSampleRate / 1000.0) * ms);
                        if (samples > 0)
                        {
                            var silence = ArrayPool<float>.Shared.Rent(samples);
                            try
                            {
                                Array.Clear(silence, 0, samples);
                                for (int i = 0; i < samples; i++) output.Add(silence[i]);
                            }
                            finally { ArrayPool<float>.Shared.Return(silence); }
                        }
                        continue;
                    }

                    var sentence = seg.Text;
                    if (string.IsNullOrWhiteSpace(sentence)) continue;

                    segmentCount++;
                    EnsureIpaService();
                    var ipa = RunEspeak(sentence);
                    if (string.IsNullOrWhiteSpace(ipa)) { Telemetry.Counter("tts.espeak_failures"); continue; }

                    var ids = MapIpaToIds(ipa, 512);
                    if (ids == null || ids.Length < 2) { Telemetry.Counter("tts.mapping_failures"); continue; }

                    int innerTokenCount = Math.Max(0, ids.Length - 2);
                    var style = LoadStyleVectorAt(_voiceFiles.ContainsKey(vk) ? _voiceFiles[vk] : null, innerTokenCount) ?? _defaultStyleVector;

                    var inputIds = new DenseTensor<long>(new[] { 1, ids.Length });
                    for (int i = 0; i < ids.Length; i++) inputIds[0, i] = ids[i];
                    for (int i = 0; i < 256; i++) _reuseStyleTensor[0, i] = style[i];
                    _reuseSpeedTensor[0] = (snap?.Speed ?? AppSettings.LoadTtsSpeed());

                    float[] audio = null;
                    bool ok = TryRunModel(inputIds, _reuseStyleTensor, _reuseSpeedTensor, out audio);

                    if (EnableCpuSegmentRetry && _usingGpu && (!ok || audio == null || audio.Length == 0 || IsDegenerateAudio(audio)))
                    {
                        Console.WriteLine("[Kokoro] GPU segment produced degenerate/empty audio - retrying segment on CPU without switching session");
                        Telemetry.Counter("tts.gpu_fallbacks");
                        EnsureCpuSession();
                        if (_cpuSession != null)
                        {
                            ok = TryRunModelOnSession(_cpuSession, inputIds, _reuseStyleTensor, _reuseSpeedTensor, out audio);
                        }
                    }

                    if (!ok || audio == null || audio.Length == 0)
                    {
                        Telemetry.Counter("tts.generation_failures");
                        continue;
                    }

                    // Trim both leading (to reduce punctuation gap) and trailing silence
                    audio = TrimLeadingSilence(audio, _nativeSampleRate, trimThr, Math.Min(200, trimMax));
                    audio = TrimTrailingSilence(audio, _nativeSampleRate, trimThr, trimLeave, trimMax);

                    output.AddRange(audio);

                    if (!seg.HasExplicitBreak)
                    {
                        int samples = (int)Math.Round((_nativeSampleRate / 1000.0) * padMsSnap);
                        if (samples > 0)
                        {
                            for (int i = 0; i < samples; i++) output.Add(0f);
                        }
                    }
                }

                Telemetry.Counter("tts.segments_processed", segmentCount);
                Telemetry.Accumulator("tts.output_samples_total", output.Count);
                return output.ToArray();
            }
        }

        public static IEnumerable<float[]> GenerateAudioSegments(string text, string voiceKey)
        {
            return GenerateAudioSegments(text, voiceKey, CancellationToken.None);
        }

        public static IEnumerable<float[]> GenerateAudioSegments(string text, string voiceKey, CancellationToken cancellationToken)
        {
            ApplyRuntimeSettings();
            if (!_initialized && !Initialize()) yield break;
            if (string.IsNullOrWhiteSpace(text)) yield break;
            var vk = (!string.IsNullOrWhiteSpace(voiceKey) && _voiceFiles.ContainsKey(voiceKey)) ? voiceKey : _defaultVoiceKey;

            var snap = Kinectv1.App.SettingsProvider?.Current?.Tts;
            float trimThr = (float)(snap?.TrimThreshold ?? AppSettings.LoadTtsTrimThreshold());
            int trimLeave = snap?.TrimLeaveMs ?? AppSettings.LoadTtsTrimLeaveMs();
            int trimMax = snap?.TrimMaxMs ?? AppSettings.LoadTtsTrimMaxMs();
            int padMsSnap = snap?.MinClausePaddingMs ?? AppSettings.LoadTtsMinClausePaddingMs();

            if (cancellationToken.IsCancellationRequested) yield break;
            var segments = BuildSegments(text);

            foreach (var seg in segments)
            {
                if (cancellationToken.IsCancellationRequested) yield break;

                if (seg.IsBreak)
                {
                    var ms = Math.Max(0, seg.BreakMs);
                    int samples = (int)Math.Round((_nativeSampleRate / 1000.0) * ms);
                    if (samples > 0)
                    {
                        var silence = ArrayPool<float>.Shared.Rent(samples);
                        try
                        {
                            Array.Clear(silence, 0, samples);
                            var result = new float[samples];
                            Array.Copy(silence, result, samples);
                            yield return result;
                        }
                        finally { ArrayPool<float>.Shared.Return(silence); }
                    }
                    else
                    {
                        yield return Array.Empty<float>();
                    }
                    continue;
                }

                var sentence = seg.Text;
                if (string.IsNullOrWhiteSpace(sentence)) continue;

                if (cancellationToken.IsCancellationRequested) yield break;

                EnsureIpaService();
                var ipa = RunEspeak(sentence);
                if (string.IsNullOrWhiteSpace(ipa)) continue;

                var ids = MapIpaToIds(ipa, 512);
                if (ids == null || ids.Length < 2) continue;

                int innerTokenCount = Math.Max(0, ids.Length - 2);
                var style = LoadStyleVectorAt(_voiceFiles.ContainsKey(vk) ? _voiceFiles[vk] : null, innerTokenCount) ?? _defaultStyleVector;

                var inputIds = new DenseTensor<long>(new[] { 1, ids.Length });
                for (int i = 0; i < ids.Length; i++) inputIds[0, i] = ids[i];
                for (int i = 0; i < 256; i++) _reuseStyleTensor[0, i] = style[i];
                _reuseSpeedTensor[0] = (snap?.Speed ?? AppSettings.LoadTtsSpeed());

                if (cancellationToken.IsCancellationRequested) yield break;

                float[] audio = null;
                bool ok = TryRunModel(inputIds, _reuseStyleTensor, _reuseSpeedTensor, out audio);

                if (EnableCpuSegmentRetry && _usingGpu && (!ok || audio == null || audio.Length == 0 || IsDegenerateAudio(audio)))
                {
                    Console.WriteLine("[Kokoro] GPU segment produced degenerate/empty audio - retrying segment on CPU without switching session");
                    Telemetry.Counter("tts.gpu_fallbacks");
                    EnsureCpuSession();
                    if (_cpuSession != null)
                    {
                        ok = TryRunModelOnSession(_cpuSession, inputIds, _reuseStyleTensor, _reuseSpeedTensor, out audio);
                    }
                }

                if (cancellationToken.IsCancellationRequested) yield break;

                if (!ok || audio == null || audio.Length == 0)
                {
                    Telemetry.Counter("tts.generation_failures");
                    continue;
                }

                // Trim both leading and trailing silence per segment
                audio = TrimLeadingSilence(audio, _nativeSampleRate, trimThr, Math.Min(200, trimMax));
                audio = TrimTrailingSilence(audio, _nativeSampleRate, trimThr, trimLeave, trimMax);

                yield return audio;

                if (!seg.HasExplicitBreak)
                {
                    int samples = (int)Math.Round((_nativeSampleRate / 1000.0) * padMsSnap);
                    if (samples > 0)
                    {
                        var padding = new float[samples];
                        // zeros by default
                        yield return padding;
                    }
                }
            }
        }

        private class Segment
        {
            public bool IsBreak { get; private set; }
            public int BreakMs { get; private set; }
            public string Text { get; private set; }
            public bool HasExplicitBreak { get; private set; }

            public static Segment Break(int ms) => new Segment { IsBreak = true, BreakMs = ms };
            public static Segment FromText(string t, bool explicitBreak) => new Segment { IsBreak = false, Text = t, HasExplicitBreak = explicitBreak };
        }

        public static void Dispose()
        {
            lock (_ipaWorkerLock)
            {
                _ipaWorkerRunning = false;
            }
            try
            {
                if (_ipaWorkerThread != null && _ipaWorkerThread.IsAlive)
                {
                    if (!_ipaWorkerThread.Join(2000))
                    {
                        Console.WriteLine("[TTS] IPA worker thread did not stop gracefully");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TTS] Error stopping IPA worker: {ex.Message}");
            }

            try { _ipaService?.Dispose(); } catch { }
            _ipaService = null;
            try { _session?.Dispose(); } catch { }
            _session = null;
            try { DisposeCpuSession(); } catch { }
            try { _ipaQueue?.Dispose(); } catch { }
        }
    }
}
