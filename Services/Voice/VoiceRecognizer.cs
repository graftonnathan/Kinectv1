using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Vosk;
using Kinectv1.Discord;
using Kinectv1.Settings;
using Kinectv1.Voice;
using Kinectv1.Services.Debug;

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
        private static bool _rmsLoggingEnabled = false;
        private static DateTime _lastRmsLog = DateTime.MinValue;
        private static float _peakRmsForLog = 0f;
        private static int _framesSinceRmsLog = 0;

        public static void EnableDiagnostics(bool on) => _diagEnabled = on;

        public static void EnableRmsLogging(bool on)
        {
            _rmsLoggingEnabled = on;
            if (on)
            {
                Console.WriteLine($"[VAD] RMS logging enabled. Current threshold={_vadRmsThreshold:F0}, low={_vadRmsThresholdLow:F0}");
            }
        }

        private static void VRLog(string tag, string msg)
        {
            if (!_diagEnabled) return;
            try { Console.WriteLine($"[VR][{tag}] {msg}"); } catch { }
        }

        private static void LogRmsIfEnabled(float rms, AudioSourceType source)
        {
            if (!_rmsLoggingEnabled) return;

            _framesSinceRmsLog++;
            if (rms > _peakRmsForLog) _peakRmsForLog = rms;

            var now = DateTime.UtcNow;
            if ((now - _lastRmsLog).TotalSeconds >= 1.0)
            {
                var vadStatus = _speechActive ? "ACTIVE" : "silent";
                Console.WriteLine($"[RMS] source={source} peak={_peakRmsForLog:F0} frames={_framesSinceRmsLog} vad={vadStatus} thr={_vadRmsThreshold:F0}");
                _peakRmsForLog = 0f;
                _framesSinceRmsLog = 0;
                _lastRmsLog = now;
            }
        }

        #endregion

        #region Events

        public static event Action<string> OnTranscription;
        public static event Action<string> OnPartialTranscription;
        public static event Action<float> OnRmsLevel;
        public static Action<float> OnDiscordRmsLevel;
        public static event Action<float> OnWebRtcRmsLevel;
        public static event Action<string, float, string> OnSpeakerResolvedForOllama;
        public static event Action<float[]> OnVoiceEmbedding;

        #endregion

        #region State

        private static readonly object _lock = new object();
        private static WaveInEvent _waveIn;
        private static bool _ready;
        private static bool _micEnabled = true;
        private static bool _discordEnabled;
        private static bool _webRtcEnabled;

        private static Model _sttModel;
        private static VoskRecognizer _recognizer;
        private static readonly object _voskLock = new object();
        private static string _modelPath;
        private const int SampleRate = 16000;

        private static readonly ConcurrentQueue<NormalizedAudioFrame> _audioQueue = new();
        private static CancellationTokenSource _procCts;
        private static Task _procTask;
        private static volatile bool _processing;

        private static int _vadDebounceMs = 30;
        private static int _vadSilenceMs = 800;
        private static bool _speechActive;
        private static double _vadRmsThreshold = 200.0;
        private static bool _vadBypass = false;

        private static int _consecutiveSilentFrames = 0;
        private static int _consecutiveLoudFrames = 0;
        private const int FRAME_DURATION_MS = 20;
        private static double _vadRmsThresholdLow = 150.0;

        private const int FRAME_MS = 20;
        private const int PREROLL_MS = 600;
        private const int PREROLL_FRAMES = PREROLL_MS / FRAME_MS;
        private static readonly byte[][] _preRollFrames = new byte[PREROLL_FRAMES][];
        private static readonly int[] _preRollLengths = new int[PREROLL_FRAMES];
        private static int _preRollCount = 0;
        private static int _preRollIndex = 0;

        private static DateTime _lastBargeInCancel = DateTime.MinValue;
        private static readonly StringBuilder _accumulatedText = new StringBuilder();
        private static string _lastPartialText = string.Empty;

        private static double _extBargeInRmsThreshold = 650.0;
        private static int _extBargeInDebounceFrames = 4;
        private static int _extBargeInLoudFrames = 0;
        private static DateTime _extBargeInIgnoreUntilUtc = DateTime.MinValue;

        private static readonly object _webRtcLock = new object();
        private static readonly StringBuilder _webRtcAccumulatedText = new StringBuilder();
        private static DateTime _webRtcLastVoskResultTime = DateTime.MinValue;
        private static int _webRtcSilenceFlushMs = 1200;
        private static bool _webRtcHasPendingText = false;
        private static string _webRtcLastPartialText = string.Empty;

        private static bool _webRtcSpeechActive = false;
        private static int _webRtcSilentMs = 0;
        private static int _webRtcLoudMs = 0;
        private const int WebRtcVadStartMs = 60;

        // Raw RMS-based silence tracker for WebRTC (independent of Vosk partial updates).
        // We use this only for conservative flush decisions to avoid mid-word cutoffs.
        private static int _webRtcRawSilentMs = 0;
        private static int _webRtcLastFeedFrameMs = FRAME_DURATION_MS;

        private static volatile AudioSourceType _lastFrameSource = AudioSourceType.LocalMic;
        public static AudioSourceType LastFrameSource => _lastFrameSource;

        private const int WebRtcPreRollMs = 250;
        private const int WebRtcTailDelayMs = 180;
        private const int WebRtcFeedBlockMs = 100;
        private static readonly object _webRtcPcmLock = new();
        private static readonly List<byte> _webRtcPreRollPcm = new();
        private static readonly List<byte> _webRtcFeedBuffer = new();
        private static int _webrtcBoundaryArmed = 0;

        #endregion

        private static bool _speakerEmbedderHooked = false;

        private static void EnsureSpeakerEmbedderHooked()
        {
            if (_speakerEmbedderHooked) return;
            _speakerEmbedderHooked = true;
            SpeakerEmbedder.OnEmbedding += OnSpeakerEmbeddingReceived;
        }

        private static void OnSpeakerEmbeddingReceived(float[] embedding)
        {
            if (embedding == null || embedding.Length == 0) return;
            try { OnVoiceEmbedding?.Invoke(embedding); } catch { }
        }

        private static void FlushWebRtcAccumulatedText(string reason)
        {
            try
            {
                string accumulatedText;
                string lastPartial;
                lock (_webRtcLock)
                {
                    accumulatedText = _webRtcAccumulatedText.ToString().Trim();
                    lastPartial = _webRtcLastPartialText?.Trim() ?? string.Empty;

                    _webRtcAccumulatedText.Clear();
                    _webRtcHasPendingText = false;
                    _webRtcLastPartialText = string.Empty;
                }

                try { WebRtcPadSilenceAndHarvest(250); } catch { }

                string finalText = null;
                string rawJson = null;
                lock (_voskLock)
                {
                    var rec = _recognizer;
                    if (rec == null) return;

                    rawJson = rec.FinalResult();
                    finalText = ExtractText(rawJson);
                }

                string textToEmit = null;
                string textSource = "none";

                if (!string.IsNullOrWhiteSpace(finalText))
                {
                    textToEmit = finalText.Trim();
                    textSource = "finalResult";
                }
                else if (!string.IsNullOrWhiteSpace(accumulatedText))
                {
                    textToEmit = accumulatedText;
                    textSource = "accumulated";
                }
                else if (!string.IsNullOrWhiteSpace(lastPartial))
                {
                    textToEmit = lastPartial;
                    textSource = "partial";
                }

                if (!string.IsNullOrWhiteSpace(textToEmit) && !textToEmit.Equals("the", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[FLUSH] reason={reason}, source={textSource}, text='{(textToEmit.Length > 50 ? textToEmit.Substring(0, 50) + "..." : textToEmit)}'");
                    try { OnTranscription?.Invoke(textToEmit); } catch { }
                }
                else
                {
                    Console.WriteLine($"[FLUSH] reason={reason}, source={textSource}, text=(empty or filtered)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FLUSH] Error: {ex.Message}");
            }
        }

        private static bool IsSafePartialBoundary(string partial, string lastEmittedPartial)
        {
            if (string.IsNullOrWhiteSpace(partial)) return false;

            // Vosk partials often do NOT include trailing whitespace.
            // Treat as safe if:
            //  1) it ends with whitespace (rare but fine), OR
            //  2) it has been stable (unchanged) since the last time we emitted a partial.
            // This is a conservative proxy for "word boundary" without requiring punctuation.
            char last = partial[partial.Length - 1];
            if (char.IsWhiteSpace(last)) return true;

            if (!string.IsNullOrWhiteSpace(lastEmittedPartial) &&
                string.Equals(partial, lastEmittedPartial, StringComparison.Ordinal))
                return true;

            return false;
        }

        private static void MaybeFlushWebRtcOnSilence()
        {
            if (!_webRtcEnabled) return;
            if (_webRtcSilenceFlushMs <= 0) return;

            bool shouldConsiderFlush;
            int rawSilentMs = 0;
            string lastPartial = string.Empty;
            int frameMs = FRAME_DURATION_MS;
            string lastEmittedPartial = string.Empty;
            lock (_webRtcLock)
            {
                shouldConsiderFlush =
                    (_webRtcAccumulatedText.Length > 0) ||
                    (!string.IsNullOrWhiteSpace(_webRtcLastPartialText)) ||
                    (_webRtcHasPendingText);

                if (!shouldConsiderFlush) return;

                rawSilentMs = _webRtcRawSilentMs;
                lastPartial = _webRtcLastPartialText;
                frameMs = _webRtcLastFeedFrameMs;
                lastEmittedPartial = _lastPartialText;
            }

            var now = DateTime.UtcNow;
            var timeSinceLastResult = _webRtcLastVoskResultTime == DateTime.MinValue
                ? double.PositiveInfinity
                : (now - _webRtcLastVoskResultTime).TotalMilliseconds;

            // Prefer actual audio silence (RMS-driven) over Vosk partial timing.
            // Only allow an RMS-driven flush at a "safe boundary" to avoid mid-word cuts:
            //  - either we already have finalized accumulated text, OR
            //  - the current partial ends with whitespace (word boundary).
            bool rmsSilenceReached = rawSilentMs >= _webRtcSilenceFlushMs;

            if (rmsSilenceReached)
            {
                lock (_webRtcLock)
                {
                    if (_webRtcAccumulatedText.Length > 0 || !string.IsNullOrWhiteSpace(_webRtcLastPartialText))
                    {
                        bool hasFinalized = _webRtcAccumulatedText.Length > 0;
                        bool safeBoundary = hasFinalized || IsSafePartialBoundary(_webRtcLastPartialText, lastEmittedPartial);

                        // If the user configured an extremely small flush timeout (e.g., 1ms for testing),
                        // don't require stability; sustained RMS silence is already the guard.
                        if (!safeBoundary && _webRtcSilenceFlushMs <= 50 && _webRtcHasPendingText)
                            safeBoundary = true;

                        if (safeBoundary)
                        {
                            Console.WriteLine($"[FLUSH-TRIGGER] SILENCE: rawSilentMs={rawSilentMs}ms >= threshold={_webRtcSilenceFlushMs}ms, hasFinalized={hasFinalized}, safeBoundary={safeBoundary}");
                            FlushWebRtcAccumulatedText("silence");
                            _webRtcRawSilentMs = 0;
                        }
                        else
                        {
                            // Not at a safe boundary yet; keep waiting.
                            // Raw silence may continue accruing; we will flush once the partial stabilizes to whitespace.
                            VRLog("WEBRTC", $"Silence reached but not at safe boundary (raw={_webRtcRawSilentMs}ms). Holding.");
                        }
                    }
                }
            }
        }

        private static void ResetWebRtcState()
        {
            lock (_webRtcLock)
            {
                _webRtcAccumulatedText.Clear();
                _webRtcLastVoskResultTime = DateTime.MinValue;
                _webRtcHasPendingText = false;
                _webRtcLastPartialText = string.Empty;
                _webRtcSpeechActive = false;
                _webRtcSilentMs = 0;
                _webRtcLoudMs = 0;
                _webRtcRawSilentMs = 0;
                _webRtcLastFeedFrameMs = FRAME_DURATION_MS;
            }
            lock (_webRtcPcmLock)
            {
                _webRtcPreRollPcm.Clear();
                _webRtcFeedBuffer.Clear();
            }
            VRLog("WEBRTC", "State reset");
        }

        private static void AppendWebRtcPreRoll(byte[] pcm16, int lengthBytes)
        {
            lock (_webRtcPcmLock)
            {
                for (int i = 0; i < lengthBytes; i++)
                    _webRtcPreRollPcm.Add(pcm16[i]);

                int bytesPerMs = (SampleRate * 2) / 1000;
                int maxBytes = WebRtcPreRollMs * bytesPerMs;

                if (_webRtcPreRollPcm.Count > maxBytes)
                {
                    int remove = _webRtcPreRollPcm.Count - maxBytes;
                    _webRtcPreRollPcm.RemoveRange(0, remove);
                }
            }
        }

        private static void ProcessWebRtcBuffered(byte[] pcm16, int lengthBytes)
        {
            AppendWebRtcPreRoll(pcm16, lengthBytes);

            lock (_webRtcPcmLock)
            {
                for (int i = 0; i < lengthBytes; i++)
                    _webRtcFeedBuffer.Add(pcm16[i]);

                int bytesPerMs = (SampleRate * 2) / 1000;
                int blockBytes = WebRtcFeedBlockMs * bytesPerMs;

                while (_webRtcFeedBuffer.Count >= blockBytes)
                {
                    var block = new byte[blockBytes];
                    _webRtcFeedBuffer.CopyTo(0, block, 0, blockBytes);
                    _webRtcFeedBuffer.RemoveRange(0, blockBytes);

                    FeedRecognizer(block, block.Length, AudioSourceType.WebRtc);
                }
            }
        }

        private static void WebRtcPadSilenceAndHarvest(int silenceMs = 250)
        {
            try
            {
                int bytesPerMs = (SampleRate * 2) / 1000;
                int totalBytes = Math.Max(0, silenceMs) * bytesPerMs;
                if (totalBytes <= 0) return;

                var zeros = new byte[totalBytes];

                bool accepted;
                string json = null;

                lock (_voskLock)
                {
                    var rec = _recognizer;
                    if (rec == null) return;

                    accepted = rec.AcceptWaveform(zeros, zeros.Length);
                    if (accepted)
                        json = rec.Result();
                }

                if (!accepted || string.IsNullOrWhiteSpace(json)) return;

                var t = ExtractText(json)?.Trim();
                if (string.IsNullOrWhiteSpace(t)) return;

                lock (_webRtcLock)
                {
                    if (_webRtcAccumulatedText.Length > 0) _webRtcAccumulatedText.Append(" ");
                    _webRtcAccumulatedText.Append(t);
                    _webRtcHasPendingText = true;
                    _webRtcLastPartialText = string.Empty;
                    _webRtcLastVoskResultTime = DateTime.UtcNow;
                }
            }
            catch { }
        }

        private static double GetWebRtcSilenceThreshold()
        {
            // WebRTC frames use RMS in the 0..10000 scale.
            // The general VAD thresholds (_vadRmsThreshold/_vadRmsThresholdLow) are in the same scale for mic.
            // However, historically some paths used much smaller thresholds; ensure we return a sensible
            // WebRTC silence threshold here based on the *current* mic-derived value.
            //
            // Treat silence as "below low threshold" but clamp to a minimum so near-zero noise still counts.
            var thr = _vadRmsThresholdLow;
            if (thr < 50.0) thr = 50.0;
            return thr;
        }

        private static void UpdateWebRtcVad(float rms, int frameMs)
        {
            // Track raw sustained silence independent of Vosk partial timing.
            // We consider silence only when we're below the low threshold.
            lock (_webRtcLock)
            {
                _webRtcLastFeedFrameMs = frameMs;
                // IMPORTANT: WebRTC RMS is on a 0..10000 scale.
                // Use a WebRTC-appropriate silence threshold; otherwise silence never accrues and flush won't trigger.
                var silenceThr = GetWebRtcSilenceThreshold();
                if (rms < silenceThr) _webRtcRawSilentMs += frameMs;
                else _webRtcRawSilentMs = 0;
            }

            if (_webRtcSpeechActive)
            {
                if (rms < _vadRmsThresholdLow) _webRtcSilentMs += frameMs;
                else _webRtcSilentMs = 0;

                if (_webRtcSilentMs >= _webRtcSilenceFlushMs)
                {
                    _webRtcSpeechActive = false;
                    _webRtcLoudMs = 0;
                }
            }
            else
            {
                if (rms >= _vadRmsThreshold) _webRtcLoudMs += frameMs;
                else _webRtcLoudMs = 0;

                if (_webRtcLoudMs >= WebRtcVadStartMs)
                {
                    _webRtcSpeechActive = true;
                    _webRtcSilentMs = 0;
                }
            }
        }

        private static void ReplayWebRtcPreRollIntoRecognizer()
        {
            byte[] seed;
            lock (_webRtcPcmLock)
            {
                seed = _webRtcPreRollPcm.ToArray();
            }

            if (seed.Length <= 0) return;

            FeedRecognizer(seed, seed.Length, AudioSourceType.WebRtc);
        }

        private static void ProcessWebRtcMirroredVad(byte[] pcm16, int lengthBytes, float rms, int frameDurationMs)
        {
            bool wasActive = _webRtcSpeechActive;
            UpdateWebRtcVad(rms, frameDurationMs);

            // Silence flush should be driven by real audio silence (RMS), not only by Vosk producing results.
            // Call this from the VAD path as well so we can flush even when Vosk hasn't emitted a partial yet.
            MaybeFlushWebRtcOnSilence();

            if (!_webRtcSpeechActive)
            {
                AppendWebRtcPreRoll(pcm16, lengthBytes);

                if (wasActive)
                {
                    Console.WriteLine($"[FLUSH-TRIGGER] VAD_SPEECH_END: Speech ended (RMS below threshold), marking pending");
                    lock (_webRtcLock)
                    {
                        _webRtcLastVoskResultTime = DateTime.UtcNow;
                        _webRtcHasPendingText = true;
                    }
                }

                return;
            }

            if (!wasActive && _webRtcSpeechActive)
            {
                Console.WriteLine($"[FLUSH-TRIGGER] VAD_SPEECH_START: Speech started (RMS={rms:F0})");
                lock (_webRtcLock)
                {
                    _webRtcAccumulatedText.Clear();
                    _webRtcHasPendingText = false;
                    _webRtcLastPartialText = string.Empty;
                }

                ReplayWebRtcPreRollIntoRecognizer();
            }

            ProcessWebRtcBuffered(pcm16, lengthBytes);
        }

        private static void FlushFinal()
        {
            try
            {
                string finalText = null;

                lock (_voskLock)
                {
                    var rec = _recognizer;
                    if (rec == null) return;

                    var json = rec.FinalResult();
                    finalText = ExtractText(json);
                }

                if (!string.IsNullOrWhiteSpace(finalText))
                {
                    if (_accumulatedText.Length > 0) _accumulatedText.Append(" ");
                    _accumulatedText.Append(finalText.Trim());
                }

                var fullText = _accumulatedText.ToString().Trim();
                _accumulatedText.Clear();
                _lastPartialText = string.Empty;

                if (!string.IsNullOrWhiteSpace(fullText) && !fullText.Equals("the", StringComparison.OrdinalIgnoreCase))
                {
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

            _lastFrameSource = frame.Source;

            int samples = frame.Length / 2;
            int frameDurationMs = (samples * 1000) / SampleRate;
            if (frameDurationMs < 1) frameDurationMs = FRAME_DURATION_MS;

            try { DebugAudioCapture.CaptureRaw(frame.Pcm16, frame.Length); } catch { }
            LogRmsIfEnabled(frame.Rms, frame.Source);

            try
            {
                switch (frame.Source)
                {
                    case AudioSourceType.LocalMic: OnRmsLevel?.Invoke(frame.Rms); break;
                    case AudioSourceType.Discord: OnDiscordRmsLevel?.Invoke(frame.Rms); break;
                    case AudioSourceType.WebRtc: OnWebRtcRmsLevel?.Invoke(frame.Rms); break;
                }
            }
            catch { }

            if (frame.Source.IsExternal())
            {
                if (frame.Source == AudioSourceType.WebRtc)
                    MaybeBargeInOnExternalVoice(frame.Rms);

                try { SpeakerEmbedder.AddPcm16(frame.Pcm16, frame.Length); } catch { }

                if (frame.Source == AudioSourceType.WebRtc)
                {
                    ProcessWebRtcMirroredVad(frame.Pcm16, frame.Length, frame.Rms, frameDurationMs);
                }
                else
                {
                    FeedRecognizer(frame.Pcm16, frame.Length, frame.Source);
                }
                return;
            }

            if (_vadBypass)
            {
                try { SpeakerEmbedder.AddPcm16(frame.Pcm16, frame.Length); } catch { }
                FeedRecognizer(frame.Pcm16, frame.Length, frame.Source);
                return;
            }

            bool wasActive = _speechActive;
            UpdateVadFromRms(frame.Rms, frameDurationMs);

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
                bool isExternal = source.IsExternal();
                bool isWebRtc = source == AudioSourceType.WebRtc;

                bool accepted;
                string json = null;
                string pjson = null;

                lock (_voskLock)
                {
                    var rec = _recognizer;
                    if (rec == null) return;

                    accepted = rec.AcceptWaveform(pcm16leMono, bytes);

                    if (accepted)
                    {
                        json = rec.Result();
                      }
                    else
                      {
                        pjson = rec.PartialResult();
                      }
                }

                if (isWebRtc)
                {
                    MaybeFlushWebRtcOnSilence();
                }

                if (accepted)
                {
                    var text = ExtractText(json);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var t = text.Trim();
                        if (t.Equals("the", StringComparison.OrdinalIgnoreCase) && t.Length <= 3) return;

                        if (isWebRtc)
                        {
                            Console.WriteLine($"[FLUSH-TRIGGER] VOSK_ACCEPTED: Vosk finalized utterance, text='{(t.Length > 40 ? t.Substring(0,40)+"..." : t)}'");
                            _webRtcLastVoskResultTime = DateTime.UtcNow;

                            if (_webRtcSilenceFlushMs <= 0)
                            {
                                Console.WriteLine($"[FLUSH-TRIGGER] VOSK_IMMEDIATE: silenceFlushMs=0, emitting immediately");
                                try { OnTranscription?.Invoke(t); } catch { }
                            }
                            else
                            {
                                lock (_webRtcLock)
                                {
                                    if (_webRtcAccumulatedText.Length > 0) _webRtcAccumulatedText.Append(" ");
                                    _webRtcAccumulatedText.Append(t);
                                    _webRtcHasPendingText = true;
                                    _webRtcLastPartialText = string.Empty;
                                }
                            }

                            MaybeBargeIn(t);
                        }
                        else if (isExternal)
                        {
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

                    if (!isWebRtc)
                    {
                        var partial = isExternal ? string.Empty : _accumulatedText.ToString();
                        if (!string.IsNullOrWhiteSpace(partial) && partial != _lastPartialText)
                        {
                            _lastPartialText = partial;
                            try { OnPartialTranscription?.Invoke(partial); } catch { }
                        }
                    }
                    else if (_webRtcSilenceFlushMs > 0)
                    {
                        lock (_webRtcLock)
                        {
                            var webRtcPartial = _webRtcAccumulatedText.ToString();
                            if (!string.IsNullOrWhiteSpace(webRtcPartial) && webRtcPartial != _lastPartialText)
                            {
                                _lastPartialText = webRtcPartial;
                                try { OnPartialTranscription?.Invoke(webRtcPartial); } catch { }
                            }
                        }
                    }
                }
                else
                {
                    var ptext = ExtractPartialText(pjson);

                    if (isWebRtc && !string.IsNullOrWhiteSpace(ptext))
                    {
                        var pt = ptext.Trim();
                        lock (_webRtcLock)
                        {
                            if (!string.IsNullOrEmpty(pt) && pt != _webRtcLastPartialText)
                            {
                                _webRtcLastVoskResultTime = DateTime.UtcNow;
                                _webRtcLastPartialText = pt;
                                _webRtcHasPendingText = true;
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(ptext))
                    {
                        var pt = ptext.Trim();

                        if (isWebRtc && _webRtcSilenceFlushMs > 0)
                        {
                            lock (_webRtcLock)
                            {
                                var combined = _webRtcAccumulatedText.Length > 0
                                    ? _webRtcAccumulatedText.ToString() + " " + pt
                                    : pt;
                                if (combined != _lastPartialText)
                                {
                                    _lastPartialText = combined;
                                    try { OnPartialTranscription?.Invoke(combined); } catch { }
                                }
                            }
                        }
                        else if (!isWebRtc)
                        {
                            string combinedPartial = isExternal ? pt
                                : (_accumulatedText.Length > 0 ? _accumulatedText.ToString() + " " + pt : pt);

                            if (combinedPartial != _lastPartialText)
                            {
                                _lastPartialText = combinedPartial;
                                try { OnPartialTranscription?.Invoke(combinedPartial); } catch { }
                            }
                        }

                        MaybeBargeIn(pt);
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
                                        case AudioSourceType.Discord: OnDiscordRmsLevel?.Invoke(frame.Rms); break;
                                        case AudioSourceType.WebRtc: OnWebRtcRmsLevel?.Invoke(frame.Rms); break;
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
                            if (spinWait.NextSpinWillYield) Thread.Sleep(1);
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

        private static string ResolveModelPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return null;
                var expanded = Environment.ExpandEnvironmentVariables(path);
                if (!Path.IsPathRooted(expanded))
                    expanded = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, expanded);
                return Path.GetFullPath(expanded);
            }
            catch { return path; }
        }

        public static void SetWebRtcInputEnabled(bool enabled)
        {
            var wasEnabled = _webRtcEnabled;
            _webRtcEnabled = enabled;
            VRLog("WEBRTC", enabled ? "Enabled" : "Disabled");

            try { Services.Transcription.TranscriptionService.Instance.SetWebRtcActive(enabled); } catch { }

            if (enabled && !wasEnabled)
            {
                ResetWebRtcState();
            }
            else if (!enabled && wasEnabled)
            {
                lock (_webRtcLock)
                {
                    var remaining = _webRtcAccumulatedText.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(remaining))
                    {
                        Console.WriteLine($"[FLUSH-TRIGGER] WEBRTC_DISABLED: Flushing remaining text on disable");
                        try { OnTranscription?.Invoke(remaining); } catch { }
                    }
                    _webRtcAccumulatedText.Clear();
                }
            }
        }

        #region Public API

        public static void Start(string modelPath)
        {
            var fromSettings = App.SettingsProvider?.Current?.Stt?.ModelPath;
            var path = string.IsNullOrWhiteSpace(modelPath) ? fromSettings : modelPath;
            LoadVadParamsFromSettings();
            TryInitializeVosk(path);
            SetWebRtcInputEnabled(false);
            EnsureAudioProcessor();
            EnsureSpeakerEmbedderHooked();
            if (_micEnabled) StartMicCapture();
            _ready = true;
            VRLog("START", $"Ready mic={_micEnabled} discord={_discordEnabled} webrtc={_webRtcEnabled}");
        }

        public static void ReloadFromSettings()
        {
            Console.WriteLine("[VoiceRecognizer] ReloadFromSettings called");
            LoadVadParamsFromSettings();
            TryInitializeVosk(App.SettingsProvider?.Current?.Stt?.ModelPath);
        }

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

        public static void SetDiscordInputEnabled(bool enabled) => _discordEnabled = enabled;

        public static void SetVadBypass(bool bypass)
        {
            _vadBypass = bypass;
            Console.WriteLine(bypass ? "[VAD] BYPASS ENABLED" : "[VAD] Bypass disabled");
        }

        /// <summary>
        /// Set the WebRTC silence flush timeout (how long to wait after silence before emitting text).
        /// 0 = emit immediately on each Vosk result (no accumulation).
        /// </summary>
        public static void SetWebRtcSilenceFlushMs(int ms)
        {
            _webRtcSilenceFlushMs = Math.Max(0, Math.Min(2000, ms));
            Console.WriteLine($"[WebRTC] Silence flush timeout set to {_webRtcSilenceFlushMs}ms");
        }

        public static bool IsVadBypassed() => _vadBypass;
        public static bool IsReady() => _ready;
        public static bool IsMicrophoneInputEnabled() => _micEnabled;
        public static bool IsDiscordInputEnabled() => _discordEnabled;
        public static bool IsWebRtcInputEnabled() => _webRtcEnabled;

        /// <summary>
        /// Force a WebRTC boundary: flush accumulated text and reset the recognizer.
        /// Uses a tail delay to avoid chopping word endings.
        /// This is useful for speaker change detection in WebRTC mode.
        /// </summary>
        public static void ForceWebRtcBoundary(string reason = "boundary")
        {
            // TEMPORARILY DISABLED - testing silence-only flush
            Console.WriteLine($"[FLUSH-TRIGGER] SPEAKER_CHANGE: DISABLED (reason={reason})");
            return;

            /*
            if (!_webRtcEnabled) return;

            if (Interlocked.Exchange(ref _webrtcBoundaryArmed, 1) == 1)
                return;

            Console.WriteLine($"[FLUSH-TRIGGER] SPEAKER_CHANGE: reason={reason}, waiting {WebRtcTailDelayMs}ms tail delay");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(WebRtcTailDelayMs).ConfigureAwait(false);

                    Console.WriteLine($"[FLUSH-TRIGGER] SPEAKER_CHANGE: executing flush after tail delay");
                    WebRtcPadSilenceAndHarvest(250);
                    FlushWebRtcAccumulatedText(reason);
                    ResetWebRtcRecognizer();
                }
                catch (Exception ex)
                {
                    VRLog("ERROR", $"ForceWebRtcBoundary: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _webrtcBoundaryArmed, 0);
                }
            });
            */
        }

        private static void ResetWebRtcRecognizer()
        {
            try
            {
                lock (_voskLock)
                {
                    if (_sttModel == null) return;

                    try { _recognizer?.Dispose(); } catch { }

                    _recognizer = new VoskRecognizer(_sttModel, SampleRate);
                    _recognizer.SetMaxAlternatives(0);
                    _recognizer.SetWords(true);  // Enable word-level timing for diarization
                }

                lock (_webRtcLock)
                {
                    _webRtcAccumulatedText.Clear();
                    _webRtcHasPendingText = false;
                    _webRtcLastVoskResultTime = DateTime.MinValue;
                    _webRtcLastPartialText = string.Empty;
                }

                lock (_webRtcPcmLock)
                {
                    _webRtcFeedBuffer.Clear();
                }

                byte[] seed;
                lock (_webRtcPcmLock)
                {
                    seed = _webRtcPreRollPcm.ToArray();
                }

                if (seed.Length > 0)
                {
                    lock (_voskLock)
                    {
                        _recognizer?.AcceptWaveform(seed, seed.Length);
                    }
                    VRLog("WEBRTC", $"Recognizer reset, seeded with {seed.Length} bytes pre-roll");
                }
                else
                {
                    VRLog("WEBRTC", "Recognizer reset for boundary (no pre-roll)");
                }
            }
            catch (Exception ex)
            {
                VRLog("ERROR", $"ResetWebRtcRecognizer: {ex.Message}");
            }
        }

        public static void ProcessAudio(NormalizedAudioFrame frame)
        {
            if (frame.Length <= 0) return;
            _audioQueue.Enqueue(frame);
            EnsureAudioProcessor();
        }

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

                lock (_voskLock)
                {
                    if (_sttModel != null && string.Equals(_modelPath, resolved, StringComparison.OrdinalIgnoreCase)) return;

                    try { _recognizer?.Dispose(); } catch { }
                    try { _sttModel?.Dispose(); } catch { }
                    _recognizer = null; _sttModel = null;

                    Vosk.Vosk.SetLogLevel(0);
                    _sttModel = new Model(resolved);
                    _recognizer = new VoskRecognizer(_sttModel, SampleRate);
                    _recognizer.SetMaxAlternatives(0);
                    _recognizer.SetWords(true);  // Enable word-level timing for diarization
                    _modelPath = resolved;
                    Console.WriteLine($"[VoiceRecognizer] Vosk model loaded (words=true): {resolved}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoiceRecognizer] Failed to load Vosk model: {ex.Message}");
                lock (_voskLock)
                {
                    try { _recognizer?.Dispose(); } catch { }
                    try { _sttModel?.Dispose(); } catch { }
                    _recognizer = null; _sttModel = null; _modelPath = null;
                }
            }
        }

        private static void LoadVadParamsFromSettings()
        {
            try
            {
                var snap = App.SettingsProvider?.Current;
                if (snap == null) return;

                var vt = snap.Audio?.VoiceThreshold;
                if (vt.HasValue)
                {
                    _vadRmsThreshold = Math.Max(0, Math.Min(1.0, vt.Value)) * 10000.0;
                    _vadRmsThresholdLow = _vadRmsThreshold * 0.4;
                }

                var asr = snap.Asr;
                if (asr != null)
                {
                    if (asr.VadDebounceTimeoutMs >= 10) _vadDebounceMs = asr.VadDebounceTimeoutMs;
                    if (asr.VadSilenceTimeoutMs >= 50)
                    {
                        _vadSilenceMs = asr.VadSilenceTimeoutMs;
                    }
                    Console.WriteLine($"[VAD] Loaded: silence={_vadSilenceMs}ms");
                }

                var tr = snap.Transcription;
                if (tr != null)
                {
                    _webRtcSilenceFlushMs = Math.Max(0, Math.Min(2000, tr.WebRtcSilenceFlushMs));
                }
                Console.WriteLine($"[WebRTC] Silence flush timeout: {_webRtcSilenceFlushMs}ms");

                _extBargeInRmsThreshold = Math.Max(600.0, _vadRmsThreshold * 2.4);
                _extBargeInDebounceFrames = 4;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VAD] ERROR: {ex.Message}");
            }
        }

        private static void MaybeBargeInOnExternalVoice(float rms)
        {
            try
            {
                var asr = App.SettingsProvider?.Current?.Asr;
                if (asr?.BargeInEnabled != true) return;
                if (DateTime.UtcNow < _extBargeInIgnoreUntilUtc) return;
                if (!TtsPlaybackController.HasActiveUtterance()) { _extBargeInLoudFrames = 0; return; }

                if (rms >= _extBargeInRmsThreshold) _extBargeInLoudFrames++;
                else _extBargeInLoudFrames = 0;

                if (_extBargeInLoudFrames < _extBargeInDebounceFrames) return;

                var now = DateTime.UtcNow;
                if ((now - _lastBargeInCancel).TotalMilliseconds < 800) return;

                _extBargeInLoudFrames = 0;
                _lastBargeInCancel = now;
                _extBargeInIgnoreUntilUtc = now.AddMilliseconds(600);

                TtsPlaybackController.CancelCurrent();
                try { DiscordNetBotManager.CancelCurrentTts(); } catch { }
                try { Tts.TtsService.MarkExternalCancel(); } catch { }
            }
            catch { }
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
            double activeThreshold = _speechActive ? _vadRmsThresholdLow : _vadRmsThreshold;

            if (rms >= activeThreshold)
            {
                _consecutiveSilentFrames = 0;
                _consecutiveLoudFrames++;
                if (!_speechActive && _consecutiveLoudFrames >= debounceFramesNeeded)
                {
                    _speechActive = true;
                    VRLog("VAD", $"Speech STARTED");
                }
            }
            else
            {
                _consecutiveLoudFrames = 0;
                _consecutiveSilentFrames++;
                if (_speechActive && _consecutiveSilentFrames >= silenceFramesNeeded)
                {
                    _speechActive = false;
                    VRLog("VAD", $"Speech ENDED after {_consecutiveSilentFrames} silent frames");
                }
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

        #endregion
    }
}
