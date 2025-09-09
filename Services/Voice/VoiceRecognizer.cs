using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Vosk;
using Newtonsoft.Json.Linq;

namespace Kinectv1
{
    public static class VoiceRecognizer
    {
        // Diagnostics
        private static bool _diagEnabled = true;
        private static int _frameCounter = 0;
        public static void EnableDiagnostics(bool on) => _diagEnabled = on;
        private static void VRLog(string tag, string msg)
        {
            if (!_diagEnabled) return;
            try { Console.WriteLine($"[VR][{tag}] {msg}"); } catch { }
        }
         // Events consumed by UI and integrations
        public static event Action<string> OnTranscription; // final only
        public static event Action<string> OnPartialTranscription; // partials (only when active)
        public static event Action<float> OnRmsLevel;
        public static Action<float> OnDiscordRmsLevel;
        public static event Action<string, float> OnSpeakerMatch; // unused
        public static event Action<string, float, string> OnSpeakerResolvedForOllama; // unused here
        public static event Action<string> OnNameHeard; // unused
        public static event Action<float[]> OnVoiceEmbedding; // forwarded from embedder

        // State
        private static readonly object _lock = new object();
        private static WaveInEvent _waveIn;
        private static bool _ready;
        private static bool _micEnabled = true;
        private static bool _discordEnabled;

        // Vosk state
        private static Model _sttModel;
        private static VoskRecognizer _recognizer;
        private static string _modelPath;
        private const int SampleRate = 16000;

        // External audio queue
        private static readonly ConcurrentQueue<(byte[] data, int length, string source)> _externalQueue = new();
        private static CancellationTokenSource _procCts;
        private static Task _procTask;
        private static volatile bool _processing;

        // VAD parameters
        private static double _voiceThresh = 0.02; // legacy amplitude (unused now)
        private static int _vadDebounceMs = 120;
        private static int _vadSilenceMs = 800;
        private static DateTime _lastAbove = DateTime.MinValue;
        private static bool _speechActive;
        private static double _vadRmsThreshold = 2000.0; // user-derived RMS threshold (0-10000)

        // Pre-roll + gating (Option A)
        private const int FRAME_MS = 50;              // matches WaveIn BufferMilliseconds
        private const int PREROLL_MS = 500;           // amount of audio to retain before activation
        private const int PREROLL_FRAMES = PREROLL_MS / FRAME_MS; // 10 frames
        private static readonly byte[][] _preRollFrames = new byte[PREROLL_FRAMES][]; // circular store
        private static readonly int[] _preRollLengths = new int[PREROLL_FRAMES];
        private static int _preRollCount = 0; // number of valid frames
        private static int _preRollIndex = 0; // next write index

        static VoiceRecognizer()
        {
            SpeakerEmbedder.OnEmbedding += e => { try { OnVoiceEmbedding?.Invoke(e); } catch { } };
        }

        public static void Start(string modelPath)
        {
            var fromSettings = Kinectv1.App.SettingsProvider?.Current?.Stt?.ModelPath;
            var path = string.IsNullOrWhiteSpace(modelPath) ? fromSettings : modelPath;
            LoadVadParamsFromSettings();
            TryInitializeVosk(path);
            EnsureExternalProcessor();
            if (_micEnabled) StartMicCapture();
            _ready = true;
            VRLog("START", $"Ready micEnabled={_micEnabled} discordEnabled={_discordEnabled} modelPath={_modelPath}");
        }
        public static void Start(string modelPath, string extra) => Start(modelPath);
        public static void ReloadFromSettings()
        {
            LoadVadParamsFromSettings();
            TryInitializeVosk(Kinectv1.App.SettingsProvider?.Current?.Stt?.ModelPath);
        }
        public static void SetMicrophoneInputEnabled(bool enabled)
        {
            _micEnabled = enabled;
            try { Console.WriteLine(enabled ? "[Mic] Enabled" : "[Mic] Disabled"); } catch { }
            VRLog("MIC", enabled ? "Enabled" : "Disabled");
            lock (_lock)
            {
                if (enabled)
                {
                    if (_waveIn == null) StartMicCapture();
                }
                else StopMicCapture();
            }
        }
        public static void SetDiscordInputEnabled(bool enabled) { _discordEnabled = enabled; }
        public static bool IsReady() => _ready;
        public static bool IsMicrophoneInputEnabled() => _micEnabled;
        public static bool IsDiscordInputEnabled() => _discordEnabled;

        public static void ProcessExternalAudio(byte[] pcm, int length, string source = null)
        {
            if (pcm == null || length <= 0) return;
            _externalQueue.Enqueue((pcm, length, source ?? "external"));
            EnsureExternalProcessor();
            if ((_frameCounter++ % 100) == 0)
                VRLog("ENQ", $"queue={_externalQueue.Count} lastLen={length} src={source}");
        }
        public static (int queueSize, bool isProcessing) GetExternalAudioStats() => (_externalQueue.Count, _processing);

        private static void TryInitializeVosk(string configuredPath)
        {
            try
            {
                var resolved = ResolveModelPath(configuredPath);
                if (string.IsNullOrWhiteSpace(resolved) || !Directory.Exists(resolved))
                {
                    Console.WriteLine($"[VoiceRecognizer] Vosk model path missing or not found. Configured='{configuredPath}' Resolved='{resolved}'");
                    return;
                }
                if (_sttModel != null && string.Equals(_modelPath, resolved, StringComparison.OrdinalIgnoreCase)) return;
                try { _recognizer?.Dispose(); } catch { }
                try { _sttModel?.Dispose(); } catch { }
                _recognizer = null; _sttModel = null;
                Vosk.Vosk.SetLogLevel(0);
                _sttModel = new Model(resolved);
                _recognizer = new VoskRecognizer(_sttModel, SampleRate);
                _recognizer.SetMaxAlternatives(0);
                _recognizer.SetWords(true);
                _modelPath = resolved;
                Console.WriteLine($"[VoiceRecognizer] Vosk model loaded: {resolved}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoiceRecognizer] Failed to load Vosk model: {ex.Message}");
                try { _recognizer?.Dispose(); } catch { }
                try { _sttModel?.Dispose(); } catch { }
                _recognizer = null; _sttModel = null; _modelPath = null;
            }
        }

        private static void LoadVadParamsFromSettings()
        {
            try
            {
                var snap = Kinectv1.App.SettingsProvider?.Current; if (snap == null) return;
                var vt = snap.Audio?.VoiceThreshold; // normalized 0..1
                if (vt.HasValue) _vadRmsThreshold = Math.Max(0, Math.Min(1.0, vt.Value)) * 10000.0;
                var asr = snap.Asr; if (asr != null)
                {
                    if (asr.VadDebounceTimeoutMs >= 10) _vadDebounceMs = asr.VadDebounceTimeoutMs;
                    if (asr.VadSilenceTimeoutMs >= 50) _vadSilenceMs = asr.VadSilenceTimeoutMs;
                }
            }
            catch { }
        }

        private static string ResolveModelPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return null;
                var expanded = Environment.ExpandEnvironmentVariables(path);
                if (!Path.IsPathRooted(expanded)) expanded = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, expanded);
                return Path.GetFullPath(expanded);
            }
            catch { return path; }
        }

        private static void StartMicCapture()
        {
            try
            {
                var inputDevice = AudioDeviceManager.GetConfiguredInputDevice();
                _waveIn = new WaveInEvent
                {
                    DeviceNumber = inputDevice?.DeviceNumber ?? 0,
                    WaveFormat = new WaveFormat(SampleRate, 16, 1),
                    BufferMilliseconds = FRAME_MS
                };
                _waveIn.DataAvailable += OnWaveInData;
                _waveIn.StartRecording();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoiceRecognizer] Mic capture start failed: {ex.Message}");
                StopMicCapture();
            }
        }
        private static void StopMicCapture()
        {
            try
            {
                if (_waveIn != null)
                {
                    _waveIn.DataAvailable -= OnWaveInData;
                    try { _waveIn.StopRecording(); } catch { }
                    try { _waveIn.Dispose(); } catch { }
                    _waveIn = null;
                }
            }
            catch { }
        }

        private static void UpdateVadFromRms(float rms)
        {
            var now = DateTime.UtcNow;
            if (rms >= _vadRmsThreshold)
            {
                _lastAbove = now;
                if (!_speechActive)
                {
                    if ((_lastAbove - (now - TimeSpan.FromMilliseconds(_vadDebounceMs))).TotalMilliseconds >= 0)
                    {
                        _speechActive = true;
                        VRLog("VAD", $"ACTIVATE rms={rms:F1} thr={_vadRmsThreshold:F1}");
                    }
                }
            }
            else
            {
                if (_speechActive && (now - _lastAbove).TotalMilliseconds >= _vadSilenceMs)
                {
                    _speechActive = false;
                    VRLog("VAD", $"DEACTIVATE rms={rms:F1} silenceMs={(now - _lastAbove).TotalMilliseconds:F0}");
                }
            }
        }

        // ==== PRE-ROLL SUPPORT ====
        private static void StorePreRoll(byte[] src, int length)
        {
            if (length <= 0) return;
            var buf = new byte[length];
            Buffer.BlockCopy(src, 0, buf, 0, length);
            _preRollFrames[_preRollIndex] = buf;
            _preRollLengths[_preRollIndex] = length;
            _preRollIndex = (_preRollIndex + 1) % PREROLL_FRAMES;
            if (_preRollCount < PREROLL_FRAMES) _preRollCount++;
        }
        private static void ReplayPreRoll()
        {
            if (_preRollCount == 0) return;
            int start = (_preRollIndex - _preRollCount + PREROLL_FRAMES) % PREROLL_FRAMES;
            for (int i = 0; i < _preRollCount; i++)
            {
                int idx = (start + i) % PREROLL_FRAMES;
                var frame = _preRollFrames[idx];
                int len = _preRollLengths[idx];
                if (frame != null && len > 0)
                {
                    FeedRecognizer(frame, len, "preroll");
                    try { SpeakerEmbedder.AddPcm16(frame, len); } catch { }
                }
            }
        }
        private static void ClearPreRoll()
        {
            _preRollCount = 0; _preRollIndex = 0;
        }

        private static void FlushFinal()
        {
            try
            {
                var rec = _recognizer; if (rec == null) return;
                var json = rec.FinalResult();
                var text = ExtractText(json);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try { OnTranscription?.Invoke(text); } catch { }
                    VRLog("FINAL", $"len={text.Length} text='{(text.Length>60?text.Substring(0,60)+"...":text)}'");
                }
                else
                {
                    VRLog("FINAL", "(empty)");
                }
            }
            catch (Exception ex) { VRLog("ERROR", "FlushFinal " + ex.Message); }
            finally { ClearPreRoll(); }
        }

        private static void ProcessFrame(byte[] data, int length, bool isExternal)
        {
            if (length <= 0) return;
            float rms = ComputeRms16(data, length);
            if (!_micEnabled && !isExternal && rms > 25f)
            {
                try { Console.WriteLine($"[Mic][DisabledRms] Unexpected RMS={rms:F1} length={length}"); } catch { }
            }
            bool wasActive = _speechActive;
            UpdateVadFromRms(rms);
            try
            {
                if (isExternal) OnDiscordRmsLevel?.Invoke(rms); else OnRmsLevel?.Invoke(rms);
            }
            catch { }

            if (!_speechActive)
            {
                StorePreRoll(data, length);
                if (wasActive && !_speechActive)
                {
                    VRLog("STATE", "Transition ACTIVE->INACTIVE triggering FlushFinal");
                    FlushFinal();
                }
                return;
            }

            if (!wasActive && _speechActive)
            {
                VRLog("STATE", "Transition INACTIVE->ACTIVE replay prerollCount=" + _preRollCount);
                ReplayPreRoll();
            }

            try { SpeakerEmbedder.AddPcm16(data, length); } catch { }
            FeedRecognizer(data, length, isExternal ? "external" : "mic");
        }

        private static void OnWaveInData(object sender, WaveInEventArgs e)
        {
            ProcessFrame(e.Buffer, e.BytesRecorded, isExternal: false);
        }

        private static void FeedRecognizer(byte[] pcm16leMono, int bytes, string source)
        {
            try
            {
                var rec = _recognizer; if (rec == null) return;
                bool accepted = rec.AcceptWaveform(pcm16leMono, bytes);
                if (accepted)
                {
                    var json = rec.Result();
                    var text = ExtractText(json);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var sinceActiveMs = (DateTime.UtcNow - _lastAbove).TotalMilliseconds;
                        bool looksShort = text.Length < 4 && text.IndexOf(' ') < 0;
                        if (!_speechActive && sinceActiveMs > 250 && looksShort)
                            return;
                        try { OnTranscription?.Invoke(text); } catch { }
                        VRLog("FINAL-INCR", $"len={text.Length} src={source} text='{(text.Length>60?text.Substring(0,60)+"...":text)}'");
                    }
                }
                else if (_speechActive)
                {
                    var pjson = rec.PartialResult();
                    var ptext = ExtractPartialText(pjson);
                    if (!string.IsNullOrWhiteSpace(ptext))
                    {
                        try { OnPartialTranscription?.Invoke(ptext); } catch { }
                        if ((_frameCounter++ % 25) == 0)
                            VRLog("PART", $"len={ptext.Length} src={source} text='{(ptext.Length>50?ptext.Substring(0,50)+"...":ptext)}'");
                    }
                }
            }
            catch (Exception ex) { VRLog("ERROR", "FeedRecognizer " + ex.Message); }
        }

        private static string ExtractText(string json)
        {
            try { if (string.IsNullOrWhiteSpace(json)) return null; var obj = JObject.Parse(json); return obj["text"]?.ToString(); } catch { return null; }
        }
        private static string ExtractPartialText(string json)
        {
            try { if (string.IsNullOrWhiteSpace(json)) return null; var obj = JObject.Parse(json); return obj["partial"]?.ToString(); } catch { return null; }
        }

        private static float ComputeRms16(byte[] buffer, int length)
        {
            int samples = length / 2; if (samples == 0) return 0f; double sumSquares = 0.0;
            for (int i = 0; i < samples; i++) { short sample = BitConverter.ToInt16(buffer, i * 2); double norm = sample / 32768.0; sumSquares += norm * norm; }
            double mean = sumSquares / samples; return (float)(Math.Sqrt(mean) * 10000.0);
        }

        private static void EnsureExternalProcessor()
        {
            if (_procTask != null && !_procTask.IsCompleted) return;
            _procCts?.Cancel(); _procCts = new CancellationTokenSource(); var ct = _procCts.Token;
            _procTask = Task.Run(() =>
            {
                _processing = true;
                VRLog("LOOP", "External processor start");
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        if (_externalQueue.TryDequeue(out var item))
                        {
                            try
                            {
                                if (_discordEnabled)
                                {
                                    ProcessFrame(item.data, item.length, isExternal: true);
                                }
                            }
                            catch (Exception ex) { VRLog("ERROR", "ProcessFrame ext " + ex.Message); }
                        }
                        else
                        {
                            Thread.Sleep(5);
                            try
                            {
                                if (_discordEnabled && _speechActive)
                                {
                                    var sinceLastAbove = (DateTime.UtcNow - _lastAbove).TotalMilliseconds;
                                    if (sinceLastAbove >= _vadSilenceMs)
                                    {
                                        VRLog("SILENCE", $"Flush after idle {sinceLastAbove:F0}ms");
                                        _speechActive = false;
                                        FlushFinal();
                                    }
                                }
                            }
                            catch (Exception ex) { VRLog("ERROR", "Silence check " + ex.Message); }
                        }
                    }
                }
                finally { _processing = false; VRLog("LOOP", "External processor end"); }
            }, ct);
        }
    }
}
