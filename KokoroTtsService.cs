using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Newtonsoft.Json.Linq;
using System;
using System.Buffers;
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

        private static float _speed = 1.05f; // slight speed-up to reduce latency
        private static bool _loggedEspeakMissing = false;
        private static readonly Dictionary<string, float[]> _voiceBinCache = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        private static bool _retryingAfterGpuFallback = false;

        // Performance optimizations: cached arrays and pooled objects
        private static readonly float[] _defaultStyleVector = CreateDefaultStyleVector();
        private static readonly float[] _silencePadding = new float[(int)Math.Round(24000 * 0.01)]; // 10ms at 24kHz
        private static readonly List<float> _sharedOutputBuffer = new List<float>(1024 * 1024); // 1M samples pre-allocated

        // Force staying on CUDA EP only (no per-segment CPU retry)
        private const bool EnableCpuSegmentRetry = false;

        // Create default style vector once to avoid repeated Enumerable.Repeat().ToArray()
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

        private static bool HasOrtExport(string exportName)
        {
            try
            {
                var h = GetModuleHandle("onnxruntime.dll");
                if (h == IntPtr.Zero) h = GetModuleHandle("onnxruntime");
                if (h == IntPtr.Zero) return false;
                return GetProcAddress(h, exportName) != IntPtr.Zero;
            }
            catch { return false; }
        }

        // Preload GPU ORT native DLL from known locations to avoid accidentally loading CPU-only onnxruntime.dll
        private static void PreloadOrtNative()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var candidateDirs = new[]
                {
                    // Prefer NuGet's native assets first (usually GPU-capable)
                    Path.Combine(baseDir, "runtimes", "win-x64", "native"),
                    // Then our organized lib folder
                    Path.Combine(baseDir, "lib", "onnxruntime"),
                    // Finally the application base directory
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

                    // Fallback: any onnxruntime.dll we can find
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

                    // Create session according to settings
                    var useGpu = AppSettings.LoadTtsUseGpu();
                    CreateSession(useGpu);

                    // Warm up with a tiny inference to JIT kernels and reduce first-latency
                    try
                    {
                        var ids = new long[] { 0, 0 }; // pad-only minimal
                        var style = new float[256];
                        // Build tensors by dimensions then copy values
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

                    // Warm-up to cut first-call latency
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

        private static string FindTensorRtProviderDir()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var candidates = new List<string>
                {
                    Path.Combine(baseDir, "runtimes", "win-x64", "native"),
                    Path.Combine(baseDir, "lib", "onnxruntime"),
                    Path.Combine(baseDir, "lib"),
                    baseDir,
                };
                foreach (var dir in candidates)
                {
                    try
                    {
                        if (!Directory.Exists(dir)) continue;
                        var trt = Path.Combine(dir, "onnxruntime_providers_tensorrt.dll");
                        var shared = Path.Combine(dir, "onnxruntime_providers_shared.dll");
                        if (File.Exists(trt) && File.Exists(shared)) return dir;
                    }
                    catch { }
                }
                return null;
            }
            catch { return null; }
        }

        private static bool TryPreloadTensorRtProvider(string providerPath)
        {
            try
            {
                var h = LoadLibrary(providerPath);
                if (h == IntPtr.Zero) return false;
                FreeLibrary(h);
                return true;
            }
            catch { return false; }
        }

        private static void EnsureTensorRtOnPath()
        {
            try
            {
                // No-op: rely on organized bin/lib layout without mutating PATH at runtime.
            }
            catch { }
        }

        private static bool IsTensorRtProviderAvailable()
        {
            try
            {
                EnsureTensorRtOnPath();
                var dir = FindTensorRtProviderDir();
                if (string.IsNullOrEmpty(dir)) return false;
                var trtProvider = Path.Combine(dir, "onnxruntime_providers_tensorrt.dll");
                var shared = Path.Combine(dir, "onnxruntime_providers_shared.dll");
                if (!(File.Exists(trtProvider) && File.Exists(shared))) return false;
                // Preflight load to avoid throwing in AppendExecutionProvider_Tensorrt when deps are missing
                return TryPreloadTensorRtProvider(trtProvider);
            }
            catch { return false; }
        }

        private static void CreateSession(bool requestedGpu)
        {
            try { _session?.Dispose(); } catch { }
            _session = null;

            EnsureTensorRtOnPath();
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
                    var useGpu = AppSettings.LoadTtsUseGpu();
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

                // If pointing to the onnx subfolder, normalize to its parent as base dir
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
                // 1) Prefer explicit model folder setting (ttsmodelfolder)
                var folder = AppSettings.LoadTtsModelFolder();
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    var baseDir = NormalizeKokoroBaseDir(folder);
                    if (Directory.Exists(baseDir))
                    {
                        _baseDir = baseDir;
                        // Choose model path inside onnx subfolder (or directly under base if provided)
                        var onnxDir = Path.Combine(_baseDir, "onnx");
                        if (Directory.Exists(onnxDir) && File.Exists(Path.Combine(onnxDir, "model_q8f16.onnx")))
                            _modelPath = Path.Combine(onnxDir, "model_q8f16.onnx");
                        else if (File.Exists(Path.Combine(_baseDir, "model_q8f16.onnx")))
                            _modelPath = Path.Combine(_baseDir, "model_q8f16.onnx");
                    }
                }

                // 2) Back-compat: honor TtsModelPath if set (can be file or folder)
                var cfg = AppSettings.LoadTtsModelPath();
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
                        // If cfg is an onnx folder
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
                            // Treat as base dir and hope default name inside onnx
                            baseDir = fullCfg;
                            _modelPath = Path.Combine(fullCfg, "onnx", "model_q8f16.onnx");
                        }
                    }

                    // If kokoro directory not found, try kokoro-82M
                    if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
                    {
                        var candidate = Path.Combine("models", "tts", "kokoro-82M");
                        if (Directory.Exists(candidate))
                        {
                            baseDir = candidate;
                            _modelPath = Path.Combine(candidate, "onnx", "model_q8f16.onnx");
                        }
                    }

                    // Finalize base dir
                    if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir))
                    {
                        // Normalize if it points to onnx
                        _baseDir = NormalizeKokoroBaseDir(baseDir);
                        // Persist folder for future use (always save base dir, not onnx)
                        try { AppSettings.SaveTtsModelFolder(_baseDir); } catch { }
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
                // If tokenizer.json missing under provided path and path is under onnx, try parent folder
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
                    // If base dir is onnx, look in parent\voices
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
            // Normalize whitespace and repeated punctuation
            text = Regex.Replace(text, "\\s+", " ").Trim();
            text = Regex.Replace(text, "([!?.,;:]){2,}", "$1");
            if (string.IsNullOrWhiteSpace(text)) yield break;

            // Split on strong and weak punctuation; treat commas/dashes as non-explicit breaks
            var parts = Regex.Split(text, "([.!?;:,]|�|�)");
            var buffer = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                if (string.IsNullOrEmpty(p)) continue;
                if (Regex.IsMatch(p, "^[.!?;:,]|�|�$"))
                {
                    buffer.Append(p);
                    var s = buffer.ToString().Trim();
                    if (!string.IsNullOrEmpty(s))
                    {
                        bool isStrong = Regex.IsMatch(p, "^[.!?;:]$");
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

        private static string RunEspeak(string text)
        {
            try
            {
                EnsureIpaService();
                if (_ipaService == null) return GetIpaOnce(text); // fallback if service missing

                if (_ttsDebug)
                    Console.WriteLine($"[TTS] STT provided text for IPA: '{text?.Replace("\n"," ").Replace("\r"," ").Trim()}'");

                int len = Math.Max(0, text?.Length ?? 0);
                var timeout = TimeSpan.FromMilliseconds(Math.Min(12000, 2000 + len * 30));
                if (_ttsDebug)
                    Console.WriteLine($"[TTS] eSpeak executing with timeout={timeout.TotalMilliseconds} ms");

                var ipa = _ipaService.GetIpaAsync(text, timeout).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(ipa))
                    return NormalizeIpa(ipa);
            }
            catch (TimeoutException tex)
            {
                Console.WriteLine($"[Kokoro] eSpeak IPA timeout (retry once): {tex.Message}");
                try
                {
                    var retryTimeout = TimeSpan.FromMilliseconds(8000);
                    if (_ttsDebug) Console.WriteLine($"[TTS] eSpeak retry with timeout={retryTimeout.TotalMilliseconds} ms");
                    var ipaRetry = _ipaService.GetIpaAsync(text, retryTimeout).GetAwaiter().GetResult();
                    if (!string.IsNullOrWhiteSpace(ipaRetry))
                        return NormalizeIpa(ipaRetry);
                }
                catch (Exception rex)
                {
                    Console.WriteLine($"[Kokoro] eSpeak IPA retry failed: {rex.Message}");
                }
                // Final fallback: one-shot
                var once = GetIpaOnce(text, timeoutMs: 3000);
                if (!string.IsNullOrWhiteSpace(once))
                    return NormalizeIpa(once);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kokoro] eSpeak IPA error: {ex.Message}");
                var once = GetIpaOnce(text, timeoutMs: 2000);
                if (!string.IsNullOrWhiteSpace(once))
                    return NormalizeIpa(once);
            }
            return null;
        }

        private static IEnumerable<string> GetEspeakCandidates()
        {
            var candidates = new List<string>();
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var localDir = Path.Combine(baseDir, "models", "tts", "Espeak NG");
                var localNg = Path.Combine(localDir, "espeak-ng.exe");
                var localClassic = Path.Combine(localDir, "espeak.exe");
                if (File.Exists(localNg)) candidates.Add(localNg);
                if (File.Exists(localClassic)) candidates.Add(localClassic);
            }
            catch { }
            candidates.Add("espeak-ng.exe");
            candidates.Add("espeak.exe");
            return candidates;
        }

        private static string NormalizeIpa(string ipa)
        {
            if (string.IsNullOrWhiteSpace(ipa)) return ipa;
            // Clean spacing and remove slashes, keep UTF-8 IPA intact
            ipa = ipa.Replace("/", " ");
            ipa = Regex.Replace(ipa, "\\s+", " ").Trim();
            return ipa;
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
                if (_vocab.TryGetValue(s, out id))
                {
                    innerIds.Add(id);
                }
                else
                {
                    unknown.Add(s);
                }
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
            ids[0] = 0; // pad
            for (int i = 0; i < innerIds.Count; i++) ids[i + 1] = innerIds[i];
            ids[ids.Length - 1] = 0; // pad

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
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                    NamedOnnxValue.CreateFromTensor("style", styleTensor),
                    NamedOnnxValue.CreateFromTensor("speed", speedTensor)
                };

                using var results = session.Run(inputs);
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
            catch
            {
                return false;
            }
        }

        private static bool TryRunModel(DenseTensor<long> inputIds, DenseTensor<float> styleTensor, DenseTensor<float> speedTensor, out float[] audio)
        {
            // Use the main session (GPU if enabled)
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

        private static void FallbackToCpu()
        {
            // Legacy full-session fallback (kept for hard failures). Prefer per-segment CPU retry instead.
            try
            {
                CreateSession(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kokoro] CPU fallback failed: {ex.Message}");
            }
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
            // Treat as degenerate only if ALL samples are effectively zero
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

                // Scan from end until we hit a sample above threshold, but cap max trim
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

                // All tail within threshold up to cap; leave minimal padding
                int keep = Math.Min(audio.Length, Math.Max(leave, audio.Length - maxTrim + leave));
                if (keep < audio.Length)
                {
                    Array.Resize(ref audio, keep);
                }
                return audio;
            }
            catch
            {
                return audio; // fail-safe: return original
            }
        }

        private static EspeakIpaNet48 _ipaService;

        private static void EnsureIpaService()
        {
            if (_ipaService != null) return;
            try
            {
                // Prefer local espeak-ng bundled under models/tts/Espeak NG, else fallback to PATH
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

                // Warm-up (non-blocking best-effort)
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

        // One-shot fallback to guarantee progress if hot process wedges
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

        // Debug console flags for TTS/IPA
        private static volatile bool _ttsDebug = true; // toggle verbose debug

        public static float[] GenerateAudio(string text, string voiceKey)
        {
            using (var scope = Telemetry.LatencyScope("tts_generate", emitEvent: true))
            {
                if (!_initialized && !Initialize()) 
                {
                    Telemetry.Counter("tts.initialization_failures");
                    return Array.Empty<float>();
                }
                if (string.IsNullOrWhiteSpace(text)) 
                {
                    Telemetry.Counter("tts.empty_text");
                    return Array.Empty<float>();
                }
                
                Telemetry.Counter("tts.generate_requests");
                var vk = (!string.IsNullOrWhiteSpace(voiceKey) && _voiceFiles.ContainsKey(voiceKey)) ? voiceKey : _defaultVoiceKey;

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
                            // Use ArrayPool for break silence instead of new allocation
                            var silence = ArrayPool<float>.Shared.Rent(samples);
                            try
                            {
                                Array.Clear(silence, 0, samples);
                                for (int i = 0; i < samples; i++) output.Add(silence[i]);
                            }
                            finally
                            {
                                ArrayPool<float>.Shared.Return(silence);
                            }
                        }
                        continue;
                    }

                    var sentence = seg.Text;
                    if (string.IsNullOrWhiteSpace(sentence)) continue;

                    segmentCount++;
                    EnsureIpaService();
                    var ipa = RunEspeak(sentence);
                    if (string.IsNullOrWhiteSpace(ipa)) 
                    {
                        Telemetry.Counter("tts.espeak_failures");
                        continue;
                    }

                    var ids = MapIpaToIds(ipa, 512);
                    if (ids == null || ids.Length < 2) 
                    {
                        Telemetry.Counter("tts.mapping_failures");
                        continue;
                    }

                    int innerTokenCount = Math.Max(0, ids.Length - 2);
                    var style = LoadStyleVectorAt(_voiceFiles.ContainsKey(vk) ? _voiceFiles[vk] : null, innerTokenCount) ?? _defaultStyleVector;

                    // Build tensors by dimensions then copy values
                    var inputIds = new DenseTensor<long>(new[] { 1, ids.Length });
                    for (int i = 0; i < ids.Length; i++) inputIds[0, i] = ids[i];
                    var styleTensor = new DenseTensor<float>(new[] { 1, 256 });
                    for (int i = 0; i < 256; i++) styleTensor[0, i] = style[i];
                    var speedTensor = new DenseTensor<float>(new[] { 1 });
                    speedTensor[0] = _speed;

                    float[] audio = null;
                    bool ok = TryRunModel(inputIds, styleTensor, speedTensor, out audio);

                    if (EnableCpuSegmentRetry && _usingGpu && (!ok || audio == null || audio.Length == 0 || IsDegenerateAudio(audio)))
                    {
                        Console.WriteLine("[Kokoro] GPU segment produced degenerate/empty audio - retrying segment on CPU without switching session");
                        Telemetry.Counter("tts.gpu_fallbacks");
                        EnsureCpuSession();
                        if (_cpuSession != null)
                        {
                            ok = TryRunModelOnSession(_cpuSession, inputIds, styleTensor, speedTensor, out audio);
                        }
                    }

                    if (!ok || audio == null || audio.Length == 0) 
                    {
                        Telemetry.Counter("tts.generation_failures");
                        continue;
                    }

                    // Reduce punctuation pause by trimming tail silence
                    audio = TrimTrailingSilence(audio, _nativeSampleRate, threshold: 0.003f, leaveMs: 6, maxTrimMs: 800);

                    output.AddRange(audio);

                    // Shorter minimal pad (~10ms) only for non-explicit breaks
                    if (!seg.HasExplicitBreak)
                    {
                        // Use pre-allocated silence padding instead of new allocation
                        output.AddRange(_silencePadding);
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
            if (!_initialized && !Initialize()) yield break;
            if (string.IsNullOrWhiteSpace(text)) yield break;
            var vk = (!string.IsNullOrWhiteSpace(voiceKey) && _voiceFiles.ContainsKey(voiceKey)) ? voiceKey : _defaultVoiceKey;

            cancellationToken.ThrowIfCancellationRequested();
            var segments = BuildSegments(text);

            foreach (var seg in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (seg.IsBreak)
                {
                    var ms = Math.Max(0, seg.BreakMs);
                    int samples = (int)Math.Round((_nativeSampleRate / 1000.0) * ms);
                    if (samples > 0)
                    {
                        // Use ArrayPool for break silence instead of new allocation
                        var silence = ArrayPool<float>.Shared.Rent(samples);
                        try
                        {
                            Array.Clear(silence, 0, samples);
                            var result = new float[samples];
                            Array.Copy(silence, result, samples);
                            yield return result;
                        }
                        finally
                        {
                            ArrayPool<float>.Shared.Return(silence);
                        }
                    }
                    else
                    {
                        yield return Array.Empty<float>();
                    }
                    continue;
                }

                var sentence = seg.Text;
                if (string.IsNullOrWhiteSpace(sentence)) continue;

                cancellationToken.ThrowIfCancellationRequested();

                EnsureIpaService();
                var ipa = RunEspeak(sentence);
                if (string.IsNullOrWhiteSpace(ipa)) continue;

                cancellationToken.ThrowIfCancellationRequested();

                var ids = MapIpaToIds(ipa, 512);
                if (ids == null || ids.Length < 2) continue;

                int innerTokenCount = Math.Max(0, ids.Length - 2);
                var style = LoadStyleVectorAt(_voiceFiles.ContainsKey(vk) ? _voiceFiles[vk] : null, innerTokenCount) ?? _defaultStyleVector;

                // Build tensors by dimensions then copy values
                var inputIds = new DenseTensor<long>(new[] { 1, ids.Length });
                for (int i = 0; i < ids.Length; i++) inputIds[0, i] = ids[i];
                var styleTensor = new DenseTensor<float>(new[] { 1, 256 });
                for (int i = 0; i < 256; i++) styleTensor[0, i] = style[i];
                var speedTensor = new DenseTensor<float>(new[] { 1 });
                speedTensor[0] = _speed;

                cancellationToken.ThrowIfCancellationRequested();

                float[] audio = null;
                bool ok = TryRunModel(inputIds, styleTensor, speedTensor, out audio);

                if (EnableCpuSegmentRetry && _usingGpu && (!ok || audio == null || audio.Length == 0 || IsDegenerateAudio(audio)))
                {
                    Console.WriteLine("[Kokoro] GPU segment produced degenerate/empty audio - retrying segment on CPU without switching session");
                    Telemetry.Counter("tts.gpu_fallbacks");
                    EnsureCpuSession();
                    if (_cpuSession != null)
                    {
                        ok = TryRunModelOnSession(_cpuSession, inputIds, styleTensor, speedTensor, out audio);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (!ok || audio == null || audio.Length == 0) 
                {
                    Telemetry.Counter("tts.generation_failures");
                    continue;
                }

                // Reduce punctuation pause by trimming tail silence
                audio = TrimTrailingSilence(audio, _nativeSampleRate, threshold: 0.003f, leaveMs: 6, maxTrimMs: 800);

                yield return audio;

                // Shorter minimal pad (~10ms) only for non-explicit breaks
                if (!seg.HasExplicitBreak)
                {
                    // Return a copy of pre-allocated silence padding
                    var padding = new float[_silencePadding.Length];
                    Array.Copy(_silencePadding, padding, _silencePadding.Length);
                    yield return padding;
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
            try { _ipaService?.Dispose(); } catch { }
            _ipaService = null;
            try { _session?.Dispose(); } catch { }
            _session = null;
            try { DisposeCpuSession(); } catch { }
        }
    }
}
