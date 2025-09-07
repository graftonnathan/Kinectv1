using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Kinectv1
{
    public static class VoiceRecognizer
    {
        // Events consumed by UI and integrations
        public static event Action<string> OnTranscription; // Not implemented here (Vosk integration out of scope)
        public static event Action<float> OnRmsLevel;
        // Raised by Discord/Mumble integrations externally
        public static Action<float> OnDiscordRmsLevel;
        public static event Action<string, float> OnSpeakerMatch; // Not implemented
        public static event Action<string, float, string> OnSpeakerResolvedForOllama; // Not implemented
        public static event Action<string> OnNameHeard; // Not implemented
        public static event Action<float[]> OnVoiceEmbedding; // Not implemented

        // State
        private static readonly object _lock = new object();
        private static WaveInEvent _waveIn;
        private static bool _ready;
        private static bool _micEnabled = true;
        private static bool _discordEnabled;

        // External audio processing (Discord/Mumble)
        private static readonly ConcurrentQueue<(byte[] data, int length, string source)> _externalQueue = new();
        private static CancellationTokenSource _procCts;
        private static Task _procTask;
        private static volatile bool _processing;

        public static void Start(string modelPath)
        {
            // Start external processing loop
            EnsureExternalProcessor();

            // Start mic capture if enabled
            if (_micEnabled)
            {
                StartMicCapture();
            }
            _ready = true;
        }

        public static void Start(string modelPath, string extra)
        {
            Start(modelPath);
        }

        public static void SetMicrophoneInputEnabled(bool enabled)
        {
            _micEnabled = enabled;
            lock (_lock)
            {
                if (enabled)
                {
                    if (_waveIn == null)
                        StartMicCapture();
                }
                else
                {
                    StopMicCapture();
                }
            }
        }

        public static void SetDiscordInputEnabled(bool enabled)
        {
            _discordEnabled = enabled;
        }

        public static bool IsReady() => _ready;
        public static bool IsMicrophoneInputEnabled() => _micEnabled;
        public static bool IsDiscordInputEnabled() => _discordEnabled;

        public static void ProcessExternalAudio(byte[] pcm, int length, string source = null)
        {
            if (pcm == null || length <= 0) return;
            _externalQueue.Enqueue((pcm, length, source ?? "external"));
            EnsureExternalProcessor();
        }

        public static (int queueSize, bool isProcessing) GetExternalAudioStats()
        {
            return (_externalQueue.Count, _processing);
        }

        private static void StartMicCapture()
        {
            try
            {
                // Default to 16kHz mono for STT-friendly stream
                var inputDevice = AudioDeviceManager.GetConfiguredInputDevice();
                _waveIn = new WaveInEvent
                {
                    DeviceNumber = inputDevice?.DeviceNumber ?? 0,
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 50 // low latency buffers
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

        private static void OnWaveInData(object sender, WaveInEventArgs e)
        {
            // Compute RMS from 16-bit PCM mono
            if (e.BytesRecorded <= 0) return;
            float rms = ComputeRms16(e.Buffer, e.BytesRecorded);
            try { OnRmsLevel?.Invoke(rms); } catch { }

            // Future: feed STT recognizer here and raise OnTranscription
            // Future: compute voice embeddings and invoke OnVoiceEmbedding
        }

        private static float ComputeRms16(byte[] buffer, int length)
        {
            int samples = length / 2; // 16-bit
            if (samples == 0) return 0f;
            double sumSquares = 0.0;
            for (int i = 0; i < samples; i++)
            {
                short sample = BitConverter.ToInt16(buffer, i * 2);
                double norm = sample / 32768.0; // -1..1
                sumSquares += norm * norm;
            }
            double mean = sumSquares / samples;
            return (float)(Math.Sqrt(mean) * 10000.0); // scale for UI bar expected range
        }

        private static void EnsureExternalProcessor()
        {
            if (_procTask != null && !_procTask.IsCompleted) return;
            _procCts?.Cancel();
            _procCts = new CancellationTokenSource();
            var ct = _procCts.Token;
            _procTask = Task.Run(() =>
            {
                _processing = true;
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        if (_externalQueue.TryDequeue(out var item))
                        {
                            try
                            {
                                float rms = ComputeRms16(item.data, item.length);
                                try { OnDiscordRmsLevel?.Invoke(rms); } catch { }
                                // Future: push into STT recognizer pipeline
                            }
                            catch { }
                        }
                        else
                        {
                            Thread.Sleep(5);
                        }
                    }
                }
                finally { _processing = false; }
            }, ct);
        }
    }
}
