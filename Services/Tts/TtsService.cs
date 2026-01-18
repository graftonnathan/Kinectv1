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
using Kinectv1; // for TtsPlaybackController
using NAudio.Wave; // added for streaming playback

namespace Kinectv1.Tts
{
    public static class TtsService
    {
        private static bool _diagEnabled = true; // toggle for verbose logging
        private static string ShortHash(string s)
        {
            if (string.IsNullOrEmpty(s)) return "null";
            unchecked
            {
                int h = 23; foreach (var c in s) h = h * 31 + c; return (h & 0xFFFF).ToString("X4");
            }
        }
        private static void Log(string tag, string msg)
        {
            if (!_diagEnabled) return;
            try { Console.WriteLine($"[TTS][{tag}] {msg}"); } catch { }
        }
        public static void EnableDiagnostics(bool on) => _diagEnabled = on;

        // Public events
        public static event Action OnTtsSpeakingStarted;
        public static event Action OnTtsSpeakingFinished;
        public static event Action<string> OnTtsError;

        // NEW: per-utterance cancellation for local playback (barge-in)
        private static readonly object _speakLock = new object();
        private static CancellationTokenSource _currentLocalSpeakCts;
        // Track last cancellation time to soften trimming on immediate follow-up
        private static DateTime _lastCancel = DateTime.MinValue;
        internal static bool RecentlyCancelled() => (DateTime.UtcNow - _lastCancel).TotalMilliseconds < 600;
        internal static void MarkExternalCancel()
        {
            _lastCancel = DateTime.UtcNow;
            Log("CANCEL", $"External mark ts={_lastCancel:O}");
        }

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

        // LEGACY-COMPAT: new helper used by TtsPlaybackController for preemptive (non-streaming) local speak (now streaming)
        private static async Task<bool> LocalSpeakAsync(string text, string speakerName, CancellationToken ct)
        {
            var hash = ShortHash(text);
            Log("LOCAL", $"(Controller) Speak request len={text?.Length} hash={hash}");
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!IsEnabled()) { OnTtsError?.Invoke("TTS disabled in settings"); return false; }
            if (!EnsureInitialized()) { OnTtsError?.Invoke("TTS init failed"); return false; }

            // Create linked CTS so external cancel (barge-in) works
            CancellationTokenSource linked = null;
            CancellationToken lct;
            lock (_speakLock)
            {
                linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _currentLocalSpeakCts = linked;
                lct = linked.Token;
            }

            try
            {
                lct.ThrowIfCancellationRequested();
                OnTtsSpeakingStarted?.Invoke();

                var snap = Kinectv1.App.SettingsProvider?.Current?.Tts;
                if (snap == null || !snap.Enabled) return false;

                // Resolve speaker (same logic as GenerateAudioInternal)
                string voicePath = null;
                if (!string.IsNullOrWhiteSpace(speakerName) && _voiceFiles.TryGetValue(speakerName, out var vp)) voicePath = vp;
                else if (!string.IsNullOrWhiteSpace(snap.Speaker) && _voiceFiles.TryGetValue(snap.Speaker, out var vs)) voicePath = vs;
                else if (_voiceFiles.Count > 0) voicePath = _voiceFiles.Values.First();
                if (voicePath == null) { OnTtsError?.Invoke("Voice style not found"); return false; }

                var segments = SplitIntoSegments(text);
                if (segments.Count == 0) return false;
                double? configuredVol = null; try { configuredVol = snap.LocalVolume; } catch { }
                float volume = (float)Math.Max(0.0, configuredVol.HasValue ? configuredVol.Value : 1.0);

                // Prepare playback objects
                var waveFormat = new WaveFormat(SampleRate, 16, 1);
                var provider = new BufferedWaveProvider(waveFormat)
                {
                    DiscardOnBufferOverflow = false,
                    BufferDuration = TimeSpan.FromSeconds(Math.Min(30, Math.Max(5, segments.Count * 3))) // heuristic
                };
                using var waveOut = new WaveOutEvent { DesiredLatency = 100 };
                try { waveOut.Init(provider); } catch (Exception ex) { OnTtsError?.Invoke("Audio init failed: " + ex.Message); return false; }
                var playbackStoppedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waveOut.PlaybackStopped += (s, e) => playbackStoppedTcs.TrySetResult(true);
                waveOut.Play();

                bool anyQueued = false;

                // Helper local function to queue float audio (chunked with async backpressure)
                async Task QueueFloatAudioAsync(float[] arr)
                {
                    if (arr == null || arr.Length == 0) return;
                    const int chunkSamples = 2048; // ~85ms at 24kHz
                    int pos = 0;
                    var pcmChunk = new byte[chunkSamples * 2];
                    while (pos < arr.Length && !lct.IsCancellationRequested)
                    {
                        int take = Math.Min(chunkSamples, arr.Length - pos);
                        int neededBytes = take * 2;
                        // Backpressure: wait until there is room for this chunk (async)
                        while (!lct.IsCancellationRequested && provider.BufferedBytes > provider.BufferLength - neededBytes)
                        {
                            await Task.Delay(15, lct).ConfigureAwait(false);
                        }
                        if (lct.IsCancellationRequested) break;
                        int bpLocal = 0;
                        for (int i = 0; i < take; i++)
                        {
                            float v = arr[pos + i] * volume;
                            if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                            short s16 = (short)Math.Round(v * 32767f);
                            pcmChunk[bpLocal++] = (byte)(s16 & 0xFF);
                            pcmChunk[bpLocal++] = (byte)((s16 >> 8) & 0xFF);
                        }
                        provider.AddSamples(pcmChunk, 0, neededBytes);
                        anyQueued = true;
                        pos += take;
                    }
                }

                // Capture settings for background thread
                var localSnap = snap;
                var localVoicePath = voicePath;

                for (int i = 0; i < segments.Count; i++)
                {
                    lct.ThrowIfCancellationRequested();
                    var seg = segments[i];
                    var t = seg.Trim();
                    if (t == "." || t == "…")
                    {
                        int dots = 1;
                        while (t == "." && i + dots < segments.Count && segments[i + dots].Trim() == ".") dots++;
                        if (localSnap.MinClausePaddingMs > 0)
                        {
                            int padSamples = (int)Math.Round(SampleRate * (localSnap.MinClausePaddingMs / 1000.0));
                            if (padSamples > 0) await QueueFloatAudioAsync(new float[padSamples]).ConfigureAwait(false);
                        }
                        i += dots - 1;
                        continue;
                    }
                    Log("SEG", $"Stream synth {i + 1}/{segments.Count} chars={seg.Length}");
                    
                    // Run synthesis on thread pool to avoid blocking
                    var segAudio = await Task.Run(() => SynthesizeOne(seg, localVoicePath, localSnap), lct).ConfigureAwait(false);
                    
                    lct.ThrowIfCancellationRequested();
                    if (segAudio != null && segAudio.Length > 0)
                    {
                        await QueueFloatAudioAsync(segAudio).ConfigureAwait(false);
                    }
                    // Inter-segment padding (silence) if not last
                    if (i < segments.Count - 1 && localSnap.MinClausePaddingMs > 0)
                    {
                        int padSamples = (int)Math.Round(SampleRate * (localSnap.MinClausePaddingMs / 1000.0));
                        if (padSamples > 0)
                        {
                            var pad = new float[padSamples]; // zeroed
                            await QueueFloatAudioAsync(pad).ConfigureAwait(false);
                        }
                    }
                }

                lct.ThrowIfCancellationRequested();

                // Wait for provider to drain
                while (!lct.IsCancellationRequested)
                {
                    if (provider.BufferedBytes == 0)
                        break;
                    await Task.Delay(40, lct).ConfigureAwait(false);
                }

                // Stop playback gracefully
                try { waveOut.Stop(); } catch { }
                // Ensure playback stopped event processed
                try { await Task.WhenAny(playbackStoppedTcs.Task, Task.Delay(200)).ConfigureAwait(false); } catch { }

                if (lct.IsCancellationRequested) return false;
                if (!anyQueued) return false;
                OnTtsSpeakingFinished?.Invoke();
                Log("LOCAL", $"Stream speak complete hash={hash}");
                return true;
            }
            catch (OperationCanceledException)
            {
                Log("LOCAL", "Cancelled (stream)");
                return false;
            }
            catch (Exception ex)
            {
                Log("ERROR", "Streaming local speak error " + ex.Message);
                OnTtsError?.Invoke(ex.Message);
                return false;
            }
            finally
            {
                lock (_speakLock)
                {
                    if (_currentLocalSpeakCts == linked)
                        _currentLocalSpeakCts = null;
                }
                try { linked?.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Convert text to speech locally with automatic preemption using TtsPlaybackController (legacy Coqui-compatible API).
        /// </summary>
        public static Task<bool> SpeakWithPreemptionAsync(string text, string speakerName = null)
        {
            return TtsPlaybackController.StartUtterance(text, speakerName, LocalSpeakAsync);
        }

        // Streaming variant routed through playback controller (minimal wrapper for legacy callers)
        public static Task<bool> SpeakStreamingWithPreemptionControllerAsync(string text, string speakerName = null)
        {
            return TtsPlaybackController.StartUtterance(text, speakerName, LocalSpeakAsync);
        }

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
                if (cts == null)
                {
                    Log("CANCEL", "Local cancel requested but no active CTS");
                    return;
                }
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
                _lastCancel = DateTime.UtcNow; // mark for grace window
                var st = new System.Diagnostics.StackTrace(1, true);
                Log("CANCEL", $"Local at {_lastCancel:HH:mm:ss.fff} stackTop={st.GetFrame(0)?.GetMethod()?.Name}");
            }
            catch (Exception ex) { Log("ERROR", "CancelLocal exception " + ex.Message); }
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
                Log("INIT", "Initializing session");
                ApplyRuntimeSettings();
                ResolveModelLocationsFromSettings();
                LoadVocab();
                LoadVoices();
                if (!File.Exists(_modelPath)) { OnTtsError?.Invoke("Kokoro model not found"); return false; }
                CreateSession(Kinectv1.App.SettingsProvider?.Current?.Tts?.Execution == Kinectv1.Settings.TtsExecution.GPU);
                _initialized = true;
                Log("INIT", $"Initialized model={Path.GetFileName(_modelPath)} gpu={_usingGpu} voices={_voiceFiles.Count} vocab={_vocab.Count}");
                return true;
            }
            catch (Exception ex)
            {
                OnTtsError?.Invoke("TTS init failed: " + ex.Message);
                Log("ERROR", "Init failed " + ex.Message);
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
            text = text.Replace("?", "-").Replace("?", "-");
            text = text.Replace("?", "...");
            // Strip characters that confuse tokenizer
            text = text.Replace("\"", string.Empty).Replace("*", string.Empty);
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
            // Removed previous hard trim at 510 to allow full tokenization; chunking handled in SynthesizeOne.
            var ids = new long[idsInner.Count + 2];
            for (int i = 0; i < idsInner.Count; i++) ids[i + 1] = idsInner[i];
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
            var hash = ShortHash(text);
            Log("GEN", $"Start len={text?.Length} hash={hash} speaker={speaker}");
            if (string.IsNullOrWhiteSpace(text)) { Log("GEN", "Empty text"); return Array.Empty<float>(); }
            if (!EnsureInitialized()) { Log("GEN", "Init failed"); return Array.Empty<float>(); }
            var start = Stopwatch.StartNew();

            var snap = Kinectv1.App.SettingsProvider?.Current?.Tts;
            if (snap == null || !snap.Enabled) return Array.Empty<float>();

            // Resolve speaker once
            string voicePath = null;
            if (!string.IsNullOrWhiteSpace(speaker) && _voiceFiles.TryGetValue(speaker, out var vp)) voicePath = vp;
            else if (!string.IsNullOrWhiteSpace(snap.Speaker) && _voiceFiles.TryGetValue(snap.Speaker, out var vs)) voicePath = vs;
            else if (_voiceFiles.Count > 0) voicePath = _voiceFiles.Values.First();
            if (voicePath == null) { OnTtsError?.Invoke("Voice style not found"); return Array.Empty<float>(); }

            var allSegments = SplitIntoSegments(text);
            Log("SEG", $"Segments={allSegments.Count}");
            if (allSegments.Count <= 1)
            {
                return SynthesizeOne(text, voicePath, snap);
            }

            var final = new List<float>(allSegments.Count * 24000); // rough reserve
            for (int i = 0; i < allSegments.Count; i++)
            {
                var seg = allSegments[i];
                var t = seg.Trim();
                if (t == "." || t == "…")
                {
                    int dots = 1; while (t == "." && i + dots < allSegments.Count && allSegments[i + dots].Trim() == ".") dots++;
                    if (snap.MinClausePaddingMs > 0)
                    {
                        int padSamples = (int)Math.Round(SampleRate * (snap.MinClausePaddingMs / 1000.0));
                        if (padSamples > 0) final.AddRange(new float[padSamples]);
                    }
                    i += dots - 1; // skip dot run
                    continue;
                }
                Log("SEG", $"Synth {i+1}/{allSegments.Count} chars={seg.Length}");
                var audio = SynthesizeOne(seg, voicePath, snap);
                Log("SEG", $"Done {i+1}/{allSegments.Count} samples={audio.Length}");
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
            var total = final.ToArray();
            Log("GEN", $"Done hash={hash} samples={total.Length} ms={start.ElapsedMilliseconds}");
            return total;
        }

        private static float[] SynthesizeOne(string text, string voicePath, Kinectv1.Settings.TtsSettings snap)
        {
            var segHash = ShortHash(text);
            var segSw = Stopwatch.StartNew();
            Log("SEG", $"Synthesize hash={segHash} textLen={text.Length} voice={Path.GetFileName(voicePath)}");
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<float>();
            var ipa = GetIpa(text);
            if (string.IsNullOrWhiteSpace(ipa)) { OnTtsError?.Invoke("IPA generation failed"); Log("SEG", $"IPA fail hash={segHash}"); return Array.Empty<float>(); }

            // Map IPA to full inner token list (no truncation) for potential chunking
            var innerTokens = new List<int>();
            foreach (var ch in ipa)
            {
                if (char.IsWhiteSpace(ch)) continue;
                if (_vocab.TryGetValue(ch.ToString(), out int id)) innerTokens.Add(id);
            }
            if (innerTokens.Count == 0) { OnTtsError?.Invoke("Tokenizer produced zero tokens"); Log("SEG", $"Token fail hash={segHash}"); return Array.Empty<float>(); }

            const int MAX_INNER = 510; // model limit for inner tokens (pads at start/end)
            bool needsChunking = innerTokens.Count > MAX_INNER;
            float[] audio;
            if (!needsChunking)
            {
                int innerCount = innerTokens.Count;
                var style = LoadStyle(voicePath, innerCount);
                if (style == null) { OnTtsError?.Invoke("Style vector load failed"); Log("SEG", $"Style fail hash={segHash}"); return Array.Empty<float>(); }
                var ids = new DenseTensor<long>(new[] { 1, innerCount + 2 });
                ids[0, 0] = 0; ids[0, innerCount + 1] = 0;
                for (int i = 0; i < innerCount; i++) ids[0, i + 1] = innerTokens[i];
                for (int i = 0; i < 256; i++) _reuseStyleTensor[0, i] = style[i];
                _reuseSpeedTensor[0] = _speed <= 0 ? 1.0f : _speed;
                lock (_reuseInputs)
                {
                    _reuseInputs.Clear();
                    _reuseInputs.Add(NamedOnnxValue.CreateFromTensor("input_ids", ids));
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
            }
            else
            {
                Log("SEG", $"Chunking long segment hash={segHash} totalTokens={innerTokens.Count}");
                var final = new List<float>(innerTokens.Count * 40); // rough reserve
                int chunks = 0;
                for (int offset = 0; offset < innerTokens.Count; offset += MAX_INNER)
                {
                    int take = Math.Min(MAX_INNER, innerTokens.Count - offset);
                    var style = LoadStyle(voicePath, take);
                    if (style == null) { OnTtsError?.Invoke("Style vector load failed (chunk)"); Log("SEG", $"Style fail (chunk) hash={segHash} off={offset}"); break; }
                    var ids = new DenseTensor<long>(new[] { 1, take + 2 });
                    ids[0, 0] = 0; ids[0, take + 1] = 0;
                    for (int i = 0; i < take; i++) ids[0, i + 1] = innerTokens[offset + i];
                    for (int i = 0; i < 256; i++) _reuseStyleTensor[0, i] = style[i];
                    _reuseSpeedTensor[0] = _speed <= 0 ? 1.0f : _speed;
                    float[] chunkAudio;
                    lock (_reuseInputs)
                    {
                        _reuseInputs.Clear();
                        _reuseInputs.Add(NamedOnnxValue.CreateFromTensor("input_ids", ids));
                        _reuseInputs.Add(NamedOnnxValue.CreateFromTensor("style", _reuseStyleTensor));
                        _reuseInputs.Add(NamedOnnxValue.CreateFromTensor("speed", _reuseSpeedTensor));
                        using var results = _session.Run(_reuseInputs);
                        var first = results.First().Value as Tensor<float>;
                        if (first == null) break;
                        if (first.Rank == 2)
                        {
                            int n = first.Dimensions[1];
                            chunkAudio = new float[n];
                            for (int j = 0; j < n; j++) chunkAudio[j] = first[0, j];
                        }
                        else chunkAudio = first.ToArray();
                    }
                    if (chunkAudio != null && chunkAudio.Length > 0) final.AddRange(chunkAudio);
                    if (offset + take < innerTokens.Count)
                    {
                        // small inter-chunk pad (15ms) to avoid discontinuity
                        int padSamples = (int)(SampleRate * 0.015);
                        final.AddRange(new float[padSamples]);
                    }
                    chunks++;
                }
                audio = final.Count > 0 ? final.ToArray() : Array.Empty<float>();
                Log("SEG", $"Chunk synthesis complete hash={segHash} chunks={chunks} samples={audio.Length}");
            }

            if (audio == null || audio.Length == 0) return Array.Empty<float>();

            bool grace = RecentlyCancelled();
            Log("SEG", $"Synthesis hash={segHash} rawSamples={audio.Length} grace={grace}");
            if (!grace)
            {
                double thr = snap.TrimThreshold;
                if (thr > 0)
                {
                    audio = TrimLeading(audio, (float)thr, Math.Min(200, snap.TrimMaxMs));
                    audio = TrimTrailing(audio, (float)thr, snap.TrimLeaveMs, snap.TrimMaxMs);
                }
            }
            else
            {
                int padSamples = (int)(SampleRate * 0.015);
                if (padSamples > 0)
                {
                    var padded = new float[padSamples + audio.Length];
                    Array.Copy(audio, 0, padded, padSamples, audio.Length);
                    audio = padded;
                }
                try { Console.WriteLine("[TTS] GraceMode pad applied"); } catch { }
            }
            if (snap.MinClausePaddingMs > 0 && SplitIntoSegments(text).Count <= 1)
            {
                int padSamples2 = (int)Math.Round(SampleRate * (snap.MinClausePaddingMs / 1000.0));
                if (padSamples2 > 0)
                {
                    var padded = new float[audio.Length + padSamples2];
                    Array.Copy(audio, padded, audio.Length);
                    audio = padded;
                }
            }
            Log("SEG", $"Synthesize complete hash={segHash} samples={audio.Length} ms={segSw.ElapsedMilliseconds}");
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
                if (ch == '.' || ch == '!' || ch == '?' || ch == '…')
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
    }
}
