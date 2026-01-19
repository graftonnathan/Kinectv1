using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Vosk;
using Newtonsoft.Json.Linq;
using Kinectv1.Discord; // added for DiscordNetBotManager.CancelCurrentTts
using Kinectv1.Settings;

namespace Kinectv1
{
    public static class VoiceRecognizer
    {
        // Diagnostics
        private static bool _diagEnabled = false; // Disabled by default to reduce console spam
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
        // Mumble RMS is published by MumbleClientManager; keep this pipeline focused on ASR only
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
        private static bool _mumbleEnabled; // NEW: Mumble input toggle

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
        private static int _vadDebounceMs = 50;    // from settings
        private static int _vadSilenceMs = 1500;   // increased default - natural speech has pauses up to 1.5s
        private static bool _speechActive;
        private static double _vadRmsThreshold = 200.0; // user-derived RMS threshold (0-10000), default for 0.02
        
        // Frame-based VAD tracking (not wall-clock based)
        // This prevents queue backup from causing premature VAD timeout
        private static int _consecutiveSilentFrames = 0;
        private static int _consecutiveLoudFrames = 0;
        private const int FRAME_DURATION_MS = 20; // Typical frame size after resampling

        // Pre-roll + gating (Option A)
        private const int FRAME_MS = 50;              // matches WaveIn BufferMilliseconds
        private const int PREROLL_MS = 1200;          // INCREASED: amount of audio to retain before activation (1.2 seconds)
        private const int PREROLL_FRAMES = PREROLL_MS / FRAME_MS; // 24 frames
        private static readonly byte[][] _preRollFrames = new byte[PREROLL_FRAMES][]; // circular store
        private static readonly int[] _preRollLengths = new int[PREROLL_FRAMES];
        private static int _preRollCount = 0; // number of valid frames
        private static int _preRollIndex = 0; // next write index

        // new: debounce for word-based barge-in
        private static DateTime _lastBargeInCancel = DateTime.MinValue;
        
        // Accumulated transcription - collect Vosk results until VAD says speech ended
        private static readonly StringBuilder _accumulatedText = new StringBuilder();
        private static string _lastPartialText = string.Empty;

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
            var inputMode = Kinectv1.App.SettingsProvider?.Current?.App?.InputMode;
            SetMumbleInputEnabled(false);
            EnsureExternalProcessor();
            if (_micEnabled) StartMicCapture();
            _ready = true;
            VRLog("START", $"Ready micEnabled={_micEnabled} discordEnabled={_discordEnabled} mumbleEnabled={_mumbleEnabled} modelPath={_modelPath}");
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
        public static void SetMumbleInputEnabled(bool enabled) { _mumbleEnabled = enabled; VRLog("MUMBLE", enabled ? "Enabled" : "Disabled"); } // NEW
        public static bool IsReady() => _ready;
        public static bool IsMicrophoneInputEnabled() => _micEnabled;
        public static bool IsDiscordInputEnabled() => _discordEnabled;
        public static bool IsMumbleInputEnabled() => _mumbleEnabled; // NEW

        public static void ProcessExternalAudio(byte[] pcm, int length, string source = null)
        {
            if (pcm == null || length <= 0) return;
            _externalQueue.Enqueue((pcm, length, source ?? "external"));
            EnsureExternalProcessor();
        }
        public static (int queueSize, bool isProcessing) GetExternalAudioStats() => (_externalQueue.Count, _processing);

        private static void TryInitializeVosk(string configuredPath)
        {
            try
            {
                var resolved = ResolveModelPath(configuredPath);
                if (string.IsNullOrWhiteSpace(resolved) || !Directory.Exists(resolved))
                {
                    Console.WriteLine($"[VoiceRecognizer] Vosk model path missing or not found. Configured='" +
                                      $"{configuredPath}' Resolved='{resolved}'");
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
                VRLog("SETTINGS", $"threshold={_vadRmsThreshold:F0}, debounce={_vadDebounceMs}ms, silence={_vadSilenceMs}ms");
            }
            catch (Exception ex) { VRLog("ERROR", $"LoadVadParamsFromSettings: {ex.Message}"); }
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

        /// <summary>
        /// Frame-based VAD that counts consecutive silent/loud frames instead of wall-clock time.
        /// This prevents queue backup from causing premature VAD timeout.
        /// </summary>
        private static void UpdateVadFromRms(float rms, int frameDurationMs)
        {
            // Calculate how many frames of silence/speech trigger activation/deactivation
            int silenceFramesNeeded = _vadSilenceMs / Math.Max(1, frameDurationMs);
            int debounceFramesNeeded = Math.Max(1, _vadDebounceMs / Math.Max(1, frameDurationMs));
            
            if (rms >= _vadRmsThreshold)
            {
                _consecutiveSilentFrames = 0;
                _consecutiveLoudFrames++;
                
                if (!_speechActive && _consecutiveLoudFrames >= debounceFramesNeeded)
                {
                    _speechActive = true;
                }
            }
            else
            {
                _consecutiveLoudFrames = 0;
                _consecutiveSilentFrames++;
                
                if (_speechActive && _consecutiveSilentFrames >= silenceFramesNeeded)
                {
                    _speechActive = false;
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
                
                // Get any remaining text from Vosk
                var json = rec.FinalResult();
                var finalText = ExtractText(json);
                if (!string.IsNullOrWhiteSpace(finalText))
                {
                    // Append to accumulated text
                    if (_accumulatedText.Length > 0) _accumulatedText.Append(" ");
                    _accumulatedText.Append(finalText.Trim());
                }
                
                // Emit the complete accumulated transcription
                var fullText = _accumulatedText.ToString().Trim();
                _accumulatedText.Clear();
                _lastPartialText = string.Empty;
                
                if (!string.IsNullOrWhiteSpace(fullText))
                {
                    var t = fullText.Trim();
                    if (t.Equals("the", StringComparison.OrdinalIgnoreCase) && t.IndexOf(' ') < 0)
                    {
                        VRLog("SUPPRESS", "single-word final 'the'");
                        return; // suppression
                    }
                    try { OnTranscription?.Invoke(fullText); } catch { }
                    VRLog("FINAL", $"'{(fullText.Length > 80 ? fullText.Substring(0, 80) + "..." : fullText)}'");
                }
                else
                {
                    VRLog("FINAL", "(empty)");
                }
            }
            catch (Exception ex) { VRLog("ERROR", $"FlushFinal: {ex.Message}"); }
            finally { ClearPreRoll(); }
        }

        private static void ProcessFrame(byte[] data, int length, bool isExternal, string source = null)
        {
            if (length <= 0) return;
            float rms = ComputeRms16(data, length);

            // Calculate frame duration based on sample count (16kHz, 16-bit mono = 2 bytes per sample)
            int samples = length / 2;
            int frameDurationMs = (samples * 1000) / SampleRate;
            if (frameDurationMs < 1) frameDurationMs = FRAME_DURATION_MS; // fallback

            // Route RMS to appropriate event based on source (for UI meters)
            try
            {
                if (isExternal)
                {
                    // Only Discord external audio drives Discord meter. Mumble RMS is published by MumbleClientManager.
                    if (source != null && source.StartsWith("Discord:", StringComparison.OrdinalIgnoreCase))
                    {
                        OnDiscordRmsLevel?.Invoke(rms);
                    }
                }
                else
                {
                    try { OnRmsLevel?.Invoke(rms); } catch { }
                }
            }
            catch { }

            // For external audio (Mumble/Discord): Skip VAD entirely, feed ALL audio to Vosk
            // Vosk will handle endpoint detection internally via AcceptWaveform returning true
            // This matches how Discord works (it sends continuous audio while user speaks)
            if (isExternal)
            {
                try { SpeakerEmbedder.AddPcm16(data, length); } catch { }
                FeedRecognizer(data, length, source ?? "external");
                return;
            }

            // For local mic: Use VAD gating (original behavior)
            bool wasActive = _speechActive;
            UpdateVadFromRms(rms, frameDurationMs);

            // BARGE-IN / SUPPRESSION LOGIC
            try
            {
                var bargeInEnabled = Kinectv1.App.SettingsProvider?.Current?.Asr?.BargeInEnabled ?? false;
                bool ttsSpeaking = TtsPlaybackController.HasActiveUtterance();
                if (!bargeInEnabled && ttsSpeaking)
                {
                    if (wasActive || _speechActive)
                    {
                        _speechActive = false;
                        _consecutiveSilentFrames = 0;
                        _consecutiveLoudFrames = 0;
                        try { FlushFinal(); } catch { }
                    }
                    StorePreRoll(data, length);
                    return;
                }
            }
            catch { }

            if (!_speechActive)
            {
                StorePreRoll(data, length);
                if (wasActive && !_speechActive)
                {
                    FlushFinal();
                }
                return;
            }

            if (!wasActive && _speechActive)
            {
                _accumulatedText.Clear();
                _lastPartialText = string.Empty;
                ReplayPreRoll();
            }

            try { SpeakerEmbedder.AddPcm16(data, length); } catch { }
            FeedRecognizer(data, length, "mic");
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
                bool isExternal = source != null && (source.StartsWith("teamtalk:", StringComparison.OrdinalIgnoreCase) || source.StartsWith("mumble:", StringComparison.OrdinalIgnoreCase) || source.StartsWith("Discord:", StringComparison.OrdinalIgnoreCase));
                
                bool accepted = rec.AcceptWaveform(pcm16leMono, bytes);
                if (accepted)
                {
                    var json = rec.Result();
                    var text = ExtractText(json);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var t = text.Trim();
                        // Skip single-word noise
                        if (t.Equals("the", StringComparison.OrdinalIgnoreCase) && t.IndexOf(' ') < 0)
                        {
                            return;
                        }
                        
                        if (isExternal)
                        {
                            // For external audio: Emit immediately on Vosk endpoint
                            // Don't accumulate - Vosk handles sentence boundaries
                            try { OnTranscription?.Invoke(t); } catch { }
                            MaybeBargeIn(t);
                        }
                        else
                        {
                            // For mic: Accumulate until VAD says speech ended
                            if (_accumulatedText.Length > 0) _accumulatedText.Append(" ");
                            _accumulatedText.Append(t);
                            MaybeBargeIn(_accumulatedText.ToString());
                        }
                    }
					
                    // Update partial display
                    var partial = isExternal ? string.Empty : _accumulatedText.ToString();
                    if (!string.IsNullOrWhiteSpace(partial) && partial != _lastPartialText)
                    {
                        _lastPartialText = partial;
                        try { OnPartialTranscription?.Invoke(partial); } catch { }
                    }
                }
                else
                {
                    // Get partial result for display
                    var pjson = rec.PartialResult();
                    var ptext = ExtractPartialText(pjson);
                    
                    if (!string.IsNullOrWhiteSpace(ptext))
                    {
                        var pt = ptext.Trim();
                        if (!(pt.Equals("the", StringComparison.OrdinalIgnoreCase) && pt.IndexOf(' ') < 0))
                        {
                            string combinedPartial;
                            if (isExternal)
                            {
                                combinedPartial = pt;
                            }
                            else if (_accumulatedText.Length > 0)
                            {
                                combinedPartial = _accumulatedText.ToString() + " " + pt;
                            }
                            else
                            {
                                combinedPartial = pt;
                            }
                            
                            if (combinedPartial != _lastPartialText)
                            {
                                _lastPartialText = combinedPartial;
                                try { OnPartialTranscription?.Invoke(combinedPartial); } catch { }
                            }
                            
                            MaybeBargeIn(combinedPartial);
                        }
                    }
                }
            }
            catch (Exception ex) { VRLog("ERROR", "FeedRecognizer " + ex.Message); }
        }

        private static void MaybeBargeIn(string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                var asr = Kinectv1.App.SettingsProvider?.Current?.Asr;
                bool bargeInEnabled = asr?.BargeInEnabled == true;
                if (!bargeInEnabled) return;
                
                bool hasActive = TtsPlaybackController.HasActiveUtterance();
                if (!hasActive) return;
                
                var lower = text.Trim().ToLowerInvariant();
                
                // Ignore common short fillers and noise
                if (lower == "a" || lower == "uh" || lower == "um" || lower == "the" || 
                    lower == "i" || lower == "is" || lower == "it" || lower == "and" ||
                    lower == "to" || lower == "in" || lower == "on" || lower == "of") return;
                
                // Require at least 3 characters for barge-in
                if (text.Length < 3) return;
                
                // Must have at least one letter
                bool hasLetter = false;
                foreach (var c in text) { if (char.IsLetter(c)) { hasLetter = true; break; } }
                if (!hasLetter) return;
                
                // Debounce: don't barge-in too frequently
                var now = DateTime.UtcNow;
                if ((now - _lastBargeInCancel).TotalMilliseconds < 1000) return; // Increased from 800ms
                
                _lastBargeInCancel = now;
                VRLog("BARGE", $"Cancel on recognized='{(text.Length>20?text.Substring(0,20)+"...":text)}'");
                TtsPlaybackController.CancelCurrent();
                try { DiscordNetBotManager.CancelCurrentTts(); } catch { }
                try { Kinectv1.Tts.TtsService.MarkExternalCancel(); } catch { }
            }
            catch { }
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
                int consecutiveFrames = 0;
                DateTime lastSummaryLog = DateTime.MinValue;
                int framesProcessedSinceLog = 0;
                int queueHighWaterMark = 0;
                bool lastSpeechState = false;
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        if (_externalQueue.TryDequeue(out var item))
                        {
                            try
                            {
                                bool isMumble = item.source != null && item.source.StartsWith("mumble:", StringComparison.OrdinalIgnoreCase);
                                bool isTeamTalk = item.source != null && (item.source.StartsWith("teamtalk:", StringComparison.OrdinalIgnoreCase) || item.source.StartsWith("mumble:", StringComparison.OrdinalIgnoreCase));
                                bool shouldProcess = isTeamTalk ? _mumbleEnabled : _discordEnabled;
                                if (!shouldProcess)
                                {
                                    float rmsOnly = ComputeRms16(item.data, item.length);
                                    try { if (!isMumble) OnDiscordRmsLevel?.Invoke(rmsOnly); } catch { }
                                    continue;
                                }

                                consecutiveFrames++;
                                framesProcessedSinceLog++;
                                var now = DateTime.UtcNow;

                                // Track queue depth for diagnostics
                                int currentQueueSize = _externalQueue.Count;
                                if (currentQueueSize > queueHighWaterMark) queueHighWaterMark = currentQueueSize;

                                ProcessFrame(item.data, item.length, isExternal: true, source: item.source);

                                // Log speech state transitions (important diagnostic)
                                if (_speechActive != lastSpeechState)
                                {
                                    lastSpeechState = _speechActive;
                                    VRLog("EXT", $"Speech {(_speechActive ? "STARTED" : "ENDED")} queue={currentQueueSize}");
                                }

                                // Periodic summary every 10 seconds (only when diagnostics enabled)
                                if (_diagEnabled && (now - lastSummaryLog).TotalSeconds >= 10)
                                {
                                    VRLog("STATS", $"Processed {framesProcessedSinceLog} frames, queueMax={queueHighWaterMark}, speech={_speechActive}");
                                    lastSummaryLog = now;
                                    framesProcessedSinceLog = 0;
                                    queueHighWaterMark = 0;
                                }
                            }
                            catch (Exception ex) { VRLog("ERROR", "ProcessFrame ext " + ex.Message); }
                        }
                        else
                        {
                            // Queue is empty - this is the ONLY time we should consider silence timeout
                            // If queue has items, keep processing them (don't use wall-clock time)
                            consecutiveFrames = 0;
                            Thread.Sleep(2);
                            
                            // Only flush if speech was active AND we've truly run out of audio to process
                            // The frame-based VAD will handle the actual silence detection
                        }
                    }
                }
                finally { _processing = false; VRLog("LOOP", "External processor end"); }
            }, ct);
        }
    }
}
