// HeadlessVoiceRecognizer.cs - Minimal STT for Linux headless mode
// Wraps Vosk without Discord/WebRTC/barge-in complexity

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kinectv1.Voice;
using NAudio.Wave;
using Vosk;

namespace Kinectv1.Headless
{
    /// <summary>
    /// Minimal voice recognizer for headless Linux mode.
    /// Uses Vosk for STT and NAudio for microphone capture.
    /// </summary>
    public sealed class HeadlessVoiceRecognizer : IDisposable
    {
        private static HeadlessVoiceRecognizer _instance;
        private static readonly object _lock = new object();

        public static HeadlessVoiceRecognizer Instance
        {
            get
            {
                lock (_lock)
                {
                    _instance ??= new HeadlessVoiceRecognizer();
                    return _instance;
                }
            }
        }

        // Events
        public event Action<string> OnTranscription;
        public event Action<string> OnPartialTranscription;
        public event Action<float> OnRmsLevel;

        // State
        private bool _ready;
        private bool _micEnabled;
        private WaveInEvent _waveIn;
        private Model _sttModel;
        private VoskRecognizer _recognizer;
        private readonly object _voskLock = new object();
        private readonly ConcurrentQueue<AudioFrame> _audioQueue = new();
        private CancellationTokenSource _procCts;
        private Task _procTask;
        private volatile bool _processing;

        // VAD parameters
        private bool _speechActive;
        private double _vadRmsThreshold = 200.0;
        private double _vadRmsThresholdLow = 150.0;
        private int _vadSilenceMs = 800;
        private int _vadDebounceMs = 30;
        private int _consecutiveSilentFrames = 0;
        private int _consecutiveLoudFrames = 0;
        private const int FRAME_DURATION_MS = 20;

        // Constants
        private const int SampleRate = 16000;
        private readonly StringBuilder _accumulatedText = new();
        private string _lastPartialText = string.Empty;

        private class AudioFrame
        {
            public byte[] Data { get; set; }
            public int Length { get; set; }
            public float Rms { get; set; }
        }

        private HeadlessVoiceRecognizer() { }

        /// <summary>
        /// Start the voice recognizer with the specified model path.
        /// </summary>
        public void Start(string modelPath)
        {
            if (_ready) return;

            try
            {
                var resolvedPath = ResolveModelPath(modelPath);
                if (!Directory.Exists(resolvedPath))
                {
                    Console.WriteLine($"[HeadlessVoiceRecognizer] Model not found: {resolvedPath}");
                    return;
                }

                // Initialize Vosk
                lock (_voskLock)
                {
                    Vosk.Vosk.SetLogLevel(0);
                    _sttModel = new Model(resolvedPath);
                    _recognizer = new VoskRecognizer(_sttModel, SampleRate);
                    _recognizer.SetMaxAlternatives(0);
                    _recognizer.SetWords(false);
                }

                // Load VAD settings from config
                LoadVadSettings();

                _ready = true;
                Console.WriteLine($"[HeadlessVoiceRecognizer] Started with model: {resolvedPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HeadlessVoiceRecognizer] Failed to start: {ex.Message}");
                Stop();
            }
        }

        /// <summary>
        /// Enable/disable microphone input.
        /// </summary>
        public void SetMicrophoneEnabled(bool enabled)
        {
            _micEnabled = enabled;
            
            if (enabled && _waveIn == null)
            {
                StartMicCapture();
            }
            else if (!enabled && _waveIn != null)
            {
                StopMicCapture();
            }
            
            Console.WriteLine(enabled ? "[HeadlessVoiceRecognizer] Microphone enabled" : "[HeadlessVoiceRecognizer] Microphone disabled");
        }

        /// <summary>
        /// Stop the recognizer and release resources.
        /// </summary>
        public void Stop()
        {
            SetMicrophoneEnabled(false);
            
            _procCts?.Cancel();
            _procTask?.Wait(TimeSpan.FromSeconds(2));
            _procCts?.Dispose();
            
            lock (_voskLock)
            {
                _recognizer?.Dispose();
                _sttModel?.Dispose();
                _recognizer = null;
                _sttModel = null;
            }
            
            _ready = false;
            Console.WriteLine("[HeadlessVoiceRecognizer] Stopped");
        }

        public bool IsReady => _ready;
        public bool IsMicrophoneEnabled => _micEnabled;

        /// <summary>
        /// Process audio from external sources (WebRTC, Discord, etc.)
        /// </summary>
        public void ProcessAudio(byte[] pcm16, int length, AudioSourceType source, string sourceId)
        {
            if (!_ready || length <= 0) return;
            
            // Create a copy of the audio data since the original might be reused
            var dataCopy = new byte[length];
            Buffer.BlockCopy(pcm16, 0, dataCopy, 0, length);
            
            var frame = new AudioFrame
            {
                Data = dataCopy,
                Length = length,
                Rms = CalculateRms(dataCopy, length)
            };
            
            _audioQueue.Enqueue(frame);
            EnsureAudioProcessor();
        }

        private void StartMicCapture()
        {
            try
            {
                _waveIn = new WaveInEvent
                {
                    DeviceNumber = 0, // Default device
                    WaveFormat = new WaveFormat(SampleRate, 16, 1),
                    BufferMilliseconds = FRAME_DURATION_MS,
                    NumberOfBuffers = 3
                };
                _waveIn.DataAvailable += OnWaveInData;
                _waveIn.StartRecording();
                Console.WriteLine($"[HeadlessVoiceRecognizer] Microphone started: {SampleRate}Hz");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HeadlessVoiceRecognizer] Mic start failed: {ex.Message}");
                StopMicCapture();
            }
        }

        private void StopMicCapture()
        {
            try
            {
                if (_waveIn != null)
                {
                    _waveIn.DataAvailable -= OnWaveInData;
                    _waveIn.StopRecording();
                    _waveIn.Dispose();
                    _waveIn = null;
                }
            }
            catch { }
        }

        private void OnWaveInData(object sender, WaveInEventArgs e)
        {
            if (!_micEnabled) return;

            var rms = CalculateRms(e.Buffer, e.BytesRecorded);
            var frame = new AudioFrame
            {
                Data = e.Buffer,
                Length = e.BytesRecorded,
                Rms = rms
            };
            
            _audioQueue.Enqueue(frame);
            EnsureAudioProcessor();
            
            try { OnRmsLevel?.Invoke(rms); } catch { }
        }

        private void EnsureAudioProcessor()
        {
            if (_procTask != null && !_procTask.IsCompleted) return;

            _procCts?.Cancel();
            _procCts = new CancellationTokenSource();
            var ct = _procCts.Token;

            _procTask = Task.Run(() => ProcessAudioLoop(ct), ct);
        }

        private void ProcessAudioLoop(CancellationToken ct)
        {
            _processing = true;
            
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (_audioQueue.TryDequeue(out var frame))
                    {
                        ProcessFrame(frame);
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
            }
            finally
            {
                _processing = false;
            }
        }

        private void ProcessFrame(AudioFrame frame)
        {
            // VAD logic
            bool wasActive = _speechActive;
            UpdateVad(frame.Rms);

            if (!_speechActive)
            {
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
            }

            // Feed to Vosk
            FeedVosk(frame.Data, frame.Length);
        }

        private void UpdateVad(float rms)
        {
            if (rms >= _vadRmsThreshold)
                _consecutiveLoudFrames++;
            else
                _consecutiveLoudFrames = 0;

            if (rms < _vadRmsThresholdLow)
                _consecutiveSilentFrames++;
            else
                _consecutiveSilentFrames = 0;

            bool wasActive = _speechActive;

            if (!_speechActive && _consecutiveLoudFrames >= (_vadDebounceMs / FRAME_DURATION_MS))
            {
                _speechActive = true;
                _consecutiveSilentFrames = 0;
            }
            else if (_speechActive && _consecutiveSilentFrames >= (_vadSilenceMs / FRAME_DURATION_MS))
            {
                _speechActive = false;
                _consecutiveLoudFrames = 0;
            }
        }

        private void FeedVosk(byte[] pcm16, int length)
        {
            try
            {
                bool accepted;
                string json = null;
                string pjson = null;

                lock (_voskLock)
                {
                    if (_recognizer == null) return;
                    accepted = _recognizer.AcceptWaveform(pcm16, length);
                    
                    if (accepted)
                        json = _recognizer.Result();
                    else
                        pjson = _recognizer.PartialResult();
                }

                if (accepted)
                {
                    var text = ExtractText(json);
                    if (!string.IsNullOrWhiteSpace(text) && !text.Equals("the", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_accumulatedText.Length > 0) _accumulatedText.Append(" ");
                        _accumulatedText.Append(text.Trim());
                        
                        var fullText = _accumulatedText.ToString();
                        _accumulatedText.Clear();
                        _lastPartialText = string.Empty;
                        
                        try { OnTranscription?.Invoke(fullText); } catch { }
                    }
                }
                else
                {
                    var partial = ExtractPartialText(pjson);
                    if (!string.IsNullOrWhiteSpace(partial) && partial != _lastPartialText)
                    {
                        _lastPartialText = partial;
                        var combined = _accumulatedText.Length > 0 
                            ? _accumulatedText + " " + partial 
                            : partial;
                        try { OnPartialTranscription?.Invoke(combined); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HeadlessVoiceRecognizer] Feed error: {ex.Message}");
            }
        }

        private void FlushFinal()
        {
            try
            {
                string finalText = null;

                lock (_voskLock)
                {
                    if (_recognizer == null) return;
                    var json = _recognizer.FinalResult();
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
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HeadlessVoiceRecognizer] Flush error: {ex.Message}");
            }
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

        private static float CalculateRms(byte[] pcm16, int length)
        {
            double sum = 0;
            int samples = length / 2;
            
            for (int i = 0; i < samples; i++)
            {
                short sample = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
                sum += sample * sample;
            }
            
            return samples > 0 ? (float)Math.Sqrt(sum / samples) : 0f;
        }

        private void LoadVadSettings()
        {
            try
            {
                var cfg = App.SettingsProvider?.Current;
                if (cfg?.Audio != null)
                {
                    var vt = cfg.Audio.VoiceThreshold;
                    if (vt > 0)
                    {
                        _vadRmsThreshold = Math.Max(0, Math.Min(1.0, vt)) * 10000.0;
                        _vadRmsThresholdLow = _vadRmsThreshold * 0.4;
                    }
                }
                
                if (cfg?.Asr != null)
                {
                    if (cfg.Asr.VadDebounceTimeoutMs >= 10)
                        _vadDebounceMs = cfg.Asr.VadDebounceTimeoutMs;
                    if (cfg.Asr.VadSilenceTimeoutMs >= 50)
                        _vadSilenceMs = cfg.Asr.VadSilenceTimeoutMs;
                }
                
                Console.WriteLine($"[HeadlessVoiceRecognizer] VAD: threshold={_vadRmsThreshold:F0}, silence={_vadSilenceMs}ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HeadlessVoiceRecognizer] VAD settings error: {ex.Message}");
            }
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

        public void Dispose()
        {
            Stop();
        }
    }
}
