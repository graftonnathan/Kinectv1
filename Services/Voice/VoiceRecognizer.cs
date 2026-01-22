using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Vosk;
using Kinectv1.Discord;
using Kinectv1.Settings;
using Kinectv1.Voice;

namespace Kinectv1
{
    /// <summary>
    /// Central voice recognition service using Vosk STT.
    /// Accepts audio via ProcessAudio(NormalizedAudioFrame) - the unified entry point.
    /// </summary>
    public static class VoiceRecognizer
    {
        #region Diagnostics

        private static bool _diagEnabled = false;
        public static void EnableDiagnostics(bool on) => _diagEnabled = on;
        
        private static void VRLog(string tag, string msg)
        {
            if (!_diagEnabled) return;
            try { Console.WriteLine($"[VR][{tag}] {msg}"); } catch { }
        }

        #endregion

        #region Events

        /// <summary>Final transcription result.</summary>
        public static event Action<string> OnTranscription;
        
        /// <summary>Partial transcription (in-progress).</summary>
        public static event Action<string> OnPartialTranscription;
        
        /// <summary>RMS level from local microphone (0-10000 scale).</summary>
        public static event Action<float> OnRmsLevel;
        
        /// <summary>
        /// RMS level from Discord audio (0-10000 scale).
        /// NOTE: Public Action (not event) for backward compatibility with DiscordNetBotManager.
        /// </summary>
        public static Action<float> OnDiscordRmsLevel;
        
        /// <summary>RMS level from WebRTC audio (0-10000 scale).</summary>
        public static event Action<float> OnWebRtcRmsLevel;
        
        /// <summary>Speaker resolved for Ollama dispatch.</summary>
        public static event Action<string, float, string> OnSpeakerResolvedForOllama;
        
        /// <summary>Voice embedding computed.</summary>
        public static event Action<float[]> OnVoiceEmbedding;

        #endregion

        #region State

        private static readonly object _lock = new object();
        private static WaveInEvent _waveIn;
        private static bool _ready;
        private static bool _micEnabled = true;
        private static bool _discordEnabled;
        private static bool _webRtcEnabled;

        // Vosk state
        private static Model _sttModel;
        private static VoskRecognizer _recognizer;
        private static string _modelPath;
        private const int SampleRate = 16000;

        // Unified audio queue
        private static readonly ConcurrentQueue<NormalizedAudioFrame> _audioQueue = new();
        private static CancellationTokenSource _procCts;
        private static Task _procTask;
        private static volatile bool _processing;

        // VAD parameters
        private static int _vadDebounceMs = 30;
        private static int _vadSilenceMs = 800;
        private static bool _speechActive;
        private static double _vadRmsThreshold = 200.0;

        // Frame-based VAD tracking
        private static int _consecutiveSilentFrames = 0;
        private static int _consecutiveLoudFrames = 0;
        private const int FRAME_DURATION_MS = 20;

        // Pre-roll buffer
        private const int FRAME_MS = 20;
        private const int PREROLL_MS = 600;
        private const int PREROLL_FRAMES = PREROLL_MS / FRAME_MS;
        private static readonly byte[][] _preRollFrames = new byte[PREROLL_FRAMES][];
        private static readonly int[] _preRollLengths = new int[PREROLL_FRAMES];
        private static int _preRollCount = 0;
        private static int _preRollIndex = 0;

        // Barge-in debounce
        private static DateTime _lastBargeInCancel = DateTime.MinValue;

        // Accumulated transcription
        private static readonly StringBuilder _accumulatedText = new StringBuilder();
        private static string _lastPartialText = string.Empty;

        #endregion

        static VoiceRecognizer()
        {
            SpeakerEmbedder.OnEmbedding += e => { try { OnVoiceEmbedding?.Invoke(e); } catch { } };
        }

        #region Public API

        /// <summary>
        /// Start the voice recognizer with the specified Vosk model path.
        /// </summary>
        public static void Start(string modelPath)
        {
            var fromSettings = App.SettingsProvider?.Current?.Stt?.ModelPath;
            var path = string.IsNullOrWhiteSpace(modelPath) ? fromSettings : modelPath;
            LoadVadParamsFromSettings();
            TryInitializeVosk(path);
            SetWebRtcInputEnabled(false);
            EnsureAudioProcessor();
            if (_micEnabled) StartMicCapture();
            _ready = true;
            VRLog("START", $"Ready mic={_micEnabled} discord={_discordEnabled} webrtc={_webRtcEnabled}");
        }

        /// <summary>Reload VAD and model settings.</summary>
        public static void ReloadFromSettings()
        {
            LoadVadParamsFromSettings();
            TryInitializeVosk(App.SettingsProvider?.Current?.Stt?.ModelPath);
        }

        /// <summary>Enable or disable local microphone input.</summary>
        public static void SetMicrophoneInputEnabled(bool enabled)
        {
            _micEnabled = enabled;
            Console.WriteLine(enabled ? "[Mic] Enabled" : "[Mic] Disabled");
            lock (_lock)
            {
                if (enabled) { if (_waveIn == null) StartMicCapture(); }
                else StopMicCapture();
            }
        }

        /// <summary>Enable or disable Discord audio input.</summary>
        public static void SetDiscordInputEnabled(bool enabled) => _discordEnabled = enabled;

        /// <summary>Enable or disable WebRTC audio input.</summary>
        public static void SetWebRtcInputEnabled(bool enabled)
        {
            _webRtcEnabled = enabled;
            VRLog("WEBRTC", enabled ? "Enabled" : "Disabled");
        }

        public static bool IsReady() => _ready;
        public static bool IsMicrophoneInputEnabled() => _micEnabled;
        public static bool IsDiscordInputEnabled() => _discordEnabled;
        public static bool IsWebRtcInputEnabled() => _webRtcEnabled;

        /// <summary>
        /// Process audio using the normalized frame format.
        /// This is the preferred entry point for all audio sources.
        /// </summary>
        public static void ProcessAudio(NormalizedAudioFrame frame)
        {
            if (frame.Length <= 0) return;
            _audioQueue.Enqueue(frame);
            EnsureAudioProcessor();
        }

        /// <summary>Get audio queue statistics.</summary>
        public static (int queueSize, bool isProcessing) GetExternalAudioStats() => (_audioQueue.Count, _processing);

        #endregion

        #region Private Implementation

        private static void TryInitializeVosk(string configuredPath)
        {
            try
            {
                var resolved = ResolveModelPath(configuredPath);
                if (string.IsNullOrWhiteSpace(resolved) || !Directory.Exists(resolved))
                {
                    Console.WriteLine($"[VoiceRecognizer] Vosk model not found: '{resolved}'");
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
                _recognizer.SetWords(false);
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
                var snap = App.SettingsProvider?.Current;
                if (snap == null) return;

                var vt = snap.Audio?.VoiceThreshold;
                if (vt.HasValue) _vadRmsThreshold = Math.Max(0, Math.Min(1.0, vt.Value)) * 10000.0;

                var asr = snap.Asr;
                if (asr != null)
                {
                    if (asr.VadDebounceTimeoutMs >= 10) _vadDebounceMs = asr.VadDebounceTimeoutMs;
                    if (asr.VadSilenceTimeoutMs >= 100) _vadSilenceMs = asr.VadSilenceTimeoutMs;
                }
                
                Console.WriteLine($"[VAD] threshold={_vadRmsThreshold:F0} (normalized={_vadRmsThreshold/10000.0:F2}), debounce={_vadDebounceMs}ms, silence={_vadSilenceMs}ms");
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
                    BufferMilliseconds = FRAME_MS,
                    NumberOfBuffers = 3
                };
                _waveIn.DataAvailable += OnWaveInData;
                _waveIn.StartRecording();
                Console.WriteLine($"[Mic] Started capture: {SampleRate}Hz, {FRAME_MS}ms frames");
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

        private static void OnWaveInData(object sender, WaveInEventArgs e)
        {
            var frame = NormalizedAudioFrame.Create(e.Buffer, e.BytesRecorded, AudioSourceType.LocalMic);
            ProcessAudioInternal(frame);
        }

        private static void UpdateVadFromRms(float rms, int frameDurationMs)
        {
            int silenceFramesNeeded = _vadSilenceMs / Math.Max(1, frameDurationMs);
            int debounceFramesNeeded = Math.Max(1, _vadDebounceMs / Math.Max(1, frameDurationMs));

            if (rms >= _vadRmsThreshold)
            {
                _consecutiveSilentFrames = 0;
                _consecutiveLoudFrames++;
                if (!_speechActive && _consecutiveLoudFrames >= debounceFramesNeeded)
                    _speechActive = true;
            }
            else
            {
                _consecutiveLoudFrames = 0;
                _consecutiveSilentFrames++;
                if (_speechActive && _consecutiveSilentFrames >= silenceFramesNeeded)
                    _speechActive = false;
            }
        }

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
                var frameData = _preRollFrames[idx];
                int len = _preRollLengths[idx];
                if (frameData != null && len > 0)
                {
                    FeedRecognizer(frameData, len, AudioSourceType.Preroll);
                    try { SpeakerEmbedder.AddPcm16(frameData, len); } catch { }
                }
            }
        }

        private static void ClearPreRoll()
        {
            _preRollCount = 0;
            _preRollIndex = 0;
        }

        private static void FlushFinal()
        {
            try
            {
                var rec = _recognizer;
                if (rec == null) return;

                var json = rec.FinalResult();
                var finalText = ExtractText(json);
                if (!string.IsNullOrWhiteSpace(finalText))
                {
                    if (_accumulatedText.Length > 0) _accumulatedText.Append(" ");
                    _accumulatedText.Append(finalText.Trim());
                }

                var fullText = _accumulatedText.ToString().Trim();
                _accumulatedText.Clear();
                _lastPartialText = string.Empty;

                if (!string.IsNullOrWhiteSpace(fullText))
                {
                    if (fullText.Equals("the", StringComparison.OrdinalIgnoreCase))
                    {
                        VRLog("SUPPRESS", "single-word final 'the'");
                        return;
                    }
                    try { OnTranscription?.Invoke(fullText); } catch { }
                    VRLog("FINAL", $"'{(fullText.Length > 80 ? fullText.Substring(0, 80) + "..." : fullText)}'");
                }
            }
            catch (Exception ex) { VRLog("ERROR", $"FlushFinal: {ex.Message}"); }
            finally { ClearPreRoll(); }
        }

        private static void ProcessAudioInternal(NormalizedAudioFrame frame)
        {
            if (frame.Length <= 0) return;

            int samples = frame.Length / 2;
            int frameDurationMs = (samples * 1000) / SampleRate;
            if (frameDurationMs < 1) frameDurationMs = FRAME_DURATION_MS;

            // Route RMS to appropriate UI meter
            try
            {
                switch (frame.Source)
                {
                    case AudioSourceType.LocalMic:
                        OnRmsLevel?.Invoke(frame.Rms);
                        break;
                    case AudioSourceType.Discord:
                        OnDiscordRmsLevel?.Invoke(frame.Rms);
                        break;
                    case AudioSourceType.WebRtc:
                        OnWebRtcRmsLevel?.Invoke(frame.Rms);
                        break;
                }
            }
            catch { }

            // External sources skip VAD, feed all audio to Vosk
            if (frame.Source.IsExternal())
            {
                try { SpeakerEmbedder.AddPcm16(frame.Pcm16, frame.Length); } catch { }
                FeedRecognizer(frame.Pcm16, frame.Length, frame.Source);
                return;
            }

            // Local mic: VAD gating
            bool wasActive = _speechActive;
            UpdateVadFromRms(frame.Rms, frameDurationMs);

            // Barge-in / suppression logic
            try
            {
                var bargeInEnabled = App.SettingsProvider?.Current?.Asr?.BargeInEnabled ?? false;
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
                    StorePreRoll(frame.Pcm16, frame.Length);
                    return;
                }
            }
            catch { }

            if (!_speechActive)
            {
                StorePreRoll(frame.Pcm16, frame.Length);
                if (wasActive && !_speechActive) FlushFinal();
                return;
            }

            if (!wasActive && _speechActive)
            {
                _accumulatedText.Clear();
                _lastPartialText = string.Empty;
                ReplayPreRoll();
            }

            try { SpeakerEmbedder.AddPcm16(frame.Pcm16, frame.Length); } catch { }
            FeedRecognizer(frame.Pcm16, frame.Length, frame.Source);
        }

        private static void FeedRecognizer(byte[] pcm16leMono, int bytes, AudioSourceType source)
        {
            try
            {
                var rec = _recognizer;
                if (rec == null) return;

                bool isExternal = source.IsExternal();
                bool accepted = rec.AcceptWaveform(pcm16leMono, bytes);

                // Diagnostics only (avoid console spam in hot path)
                if (source == AudioSourceType.WebRtc && _diagEnabled)
                {
                    VRLog("VOSK", $"AcceptWaveform({bytes} bytes) = {accepted}");
                }

                if (accepted)
                {
                    var json = rec.Result();
                    var text = ExtractText(json);

                    // Diagnostics only (the json can be large and this is very chatty)
                    if (source == AudioSourceType.WebRtc && _diagEnabled)
                    {
                        VRLog("VOSK", $"Result='{text}' json={(json?.Length > 100 ? json.Substring(0, 100) + "..." : json)}");
                    }

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var t = text.Trim();
                        if (t.Equals("the", StringComparison.OrdinalIgnoreCase) && t.Length <= 3) return;

                        if (isExternal)
                        {
                            if (_diagEnabled && source == AudioSourceType.WebRtc)
                                VRLog("FINAL", $"External final: '{t}'");

                            try { OnTranscription?.Invoke(t); } catch { }
                            MaybeBargeIn(t);
                        }
                        else
                        {
                            if (_accumulatedText.Length > 0) _accumulatedText.Append(" ");
                            _accumulatedText.Append(t);
                            MaybeBargeIn(_accumulatedText.ToString());
                        }
                    }

                    var partial = isExternal ? string.Empty : _accumulatedText.ToString();
                    if (!string.IsNullOrWhiteSpace(partial) && partial != _lastPartialText)
                    {
                        _lastPartialText = partial;
                        try { OnPartialTranscription?.Invoke(partial); } catch { }
                    }
                }
                else
                {
                    var pjson = rec.PartialResult();
                    var ptext = ExtractPartialText(pjson);

                    // Diagnostics only (partials are extremely chatty)
                    if (source == AudioSourceType.WebRtc && _diagEnabled && !string.IsNullOrWhiteSpace(ptext))
                    {
                        VRLog("PARTIAL", ptext);
                    }

                    if (!string.IsNullOrWhiteSpace(ptext))
                    {
                        var pt = ptext.Trim();

                        string combinedPartial = isExternal ? pt
                            : (_accumulatedText.Length > 0 ? _accumulatedText.ToString() + " " + pt : pt);

                        if (combinedPartial != _lastPartialText)
                        {
                            _lastPartialText = combinedPartial;
                            try { OnPartialTranscription?.Invoke(combinedPartial); } catch { }
                        }

                        MaybeBargeIn(combinedPartial);
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

                var asr = App.SettingsProvider?.Current?.Asr;
                if (asr?.BargeInEnabled != true) return;
                if (!TtsPlaybackController.HasActiveUtterance()) return;

                var lower = text.Trim().ToLowerInvariant();
                if (lower == "a" || lower == "uh" || lower == "um" || lower == "the" ||
                    lower == "i" || lower == "is" || lower == "it" || lower == "and" ||
                    lower == "to" || lower == "in" || lower == "on" || lower == "of") return;

                if (text.Length < 3) return;

                bool hasLetter = false;
                foreach (var c in text) if (char.IsLetter(c)) { hasLetter = true; break; }
                if (!hasLetter) return;

                var now = DateTime.UtcNow;
                if ((now - _lastBargeInCancel).TotalMilliseconds < 1000) return;

                _lastBargeInCancel = now;
                VRLog("BARGE", $"Cancel on recognized='{(text.Length > 20 ? text.Substring(0, 20) + "..." : text)}'");
                TtsPlaybackController.CancelCurrent();
                try { DiscordNetBotManager.CancelCurrentTts(); } catch { }
                try { Tts.TtsService.MarkExternalCancel(); } catch { }
            }
            catch { }
        }

        private static string ExtractText(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                int textIdx = json.IndexOf("\"text\"", StringComparison.Ordinal);
                if (textIdx < 0) return null;
                int colonIdx = json.IndexOf(':', textIdx + 6);
                if (colonIdx < 0) return null;
                int startQuote = json.IndexOf('"', colonIdx + 1);
                if (startQuote < 0) return null;
                int endQuote = json.IndexOf('"', startQuote + 1);
                if (endQuote < 0) return null;
                var text = json.Substring(startQuote + 1, endQuote - startQuote - 1);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch { return null; }
        }

        private static string ExtractPartialText(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                int partialIdx = json.IndexOf("\"partial\"", StringComparison.Ordinal);
                if (partialIdx < 0) return null;
                int colonIdx = json.IndexOf(':', partialIdx + 9);
                if (colonIdx < 0) return null;
                int startQuote = json.IndexOf('"', colonIdx + 1);
                if (startQuote < 0) return null;
                int endQuote = json.IndexOf('"', startQuote + 1);
                if (endQuote < 0) return null;
                var text = json.Substring(startQuote + 1, endQuote - startQuote - 1);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch { return null; }
        }

        private static void EnsureAudioProcessor()
        {
            if (_procTask != null && !_procTask.IsCompleted) return;

            _procCts?.Cancel();
            _procCts = new CancellationTokenSource();
            var ct = _procCts.Token;

            _procTask = Task.Run(() =>
            {
                _processing = true;
                VRLog("LOOP", "Audio processor start");
                int framesProcessed = 0;
                DateTime lastLog = DateTime.MinValue;
                var spinWait = new SpinWait();

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        if (_audioQueue.TryDequeue(out var frame))
                        {
                            try
                            {
                                bool shouldProcess = frame.Source switch
                                {
                                    AudioSourceType.WebRtc => _webRtcEnabled,
                                    AudioSourceType.Discord => _discordEnabled,
                                    AudioSourceType.LocalMic => _micEnabled,
                                    _ => true
                                };

                                if (!shouldProcess)
                                {
                                    switch (frame.Source)
                                    {
                                        case AudioSourceType.Discord:
                                            OnDiscordRmsLevel?.Invoke(frame.Rms);
                                            break;
                                        case AudioSourceType.WebRtc:
                                            OnWebRtcRmsLevel?.Invoke(frame.Rms);
                                            break;
                                    }
                                    continue;
                                }

                                framesProcessed++;
                                ProcessAudioInternal(frame);
                            }
                            catch (Exception ex) { VRLog("ERROR", $"ProcessFrame: {ex.Message}"); }
                            
                            spinWait.Reset();
                        }
                        else
                        {
                            spinWait.SpinOnce();
                            
                            if (spinWait.NextSpinWillYield)
                            {
                                Thread.Sleep(1);
                            }
                        }

                        var now = DateTime.UtcNow;
                        if (_diagEnabled && (now - lastLog).TotalSeconds >= 10)
                        {
                            VRLog("STATS", $"Processed {framesProcessed} frames, queue={_audioQueue.Count}");
                            framesProcessed = 0;
                            lastLog = now;
                        }
                    }
                }
                finally
                {
                    _processing = false;
                    VRLog("LOOP", "Audio processor end");
                }
            }, ct);
        }

        #endregion
    }
}
