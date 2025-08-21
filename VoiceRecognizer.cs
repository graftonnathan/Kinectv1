// VoiceRecognizer.cs - Simplified and cleaned up
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Vosk;

namespace Kinectv1
{
    public static class VoiceRecognizer
    {
        private static VoskRecognizer _recognizer;
        private static WaveInEvent _waveIn;
        private static VoiceProcessor _voiceProcessor;
        private static string _currentModelPath;

        // Thread-safe audio queue for external sources (like Discord)
        private static readonly ConcurrentQueue<(byte[] data, int length, string source)> _externalAudioQueue = new ConcurrentQueue<(byte[], int, string)>();
        private static readonly Timer _audioProcessingTimer;
        private static readonly object _processingLock = new object();
        private static volatile bool _isProcessing = false;
        private static volatile bool _shutdownRequested = false; // NEW: prevent processing during shutdown

        // NEW: Per-user Discord buffering to avoid interleaving/overwriting
        private static readonly ConcurrentDictionary<string, ConcurrentQueue<(byte[] data, int length)>> _discordUserQueues = new ConcurrentDictionary<string, ConcurrentQueue<(byte[], int)>>();
        private static volatile string _activeDiscordSource = null;
        private static int _queueWorkRunning = 0; // prevent re-entrant timer callbacks

        // Events
        public static Action<float> OnRmsLevel;
        public static Action<float> OnDiscordRmsLevel; // NEW: Separate Discord RMS event
        public static Action<string> OnTranscription;
        public static Action<string> OnNameHeard;
        public static Action<string, float> OnSpeakerMatch;
        public static Action<float[]> OnVoiceEmbedding;

        // Input control flags
        private static bool _microphoneInputEnabled = true;
        private static bool _discordInputEnabled = true;
        private static bool _microphoneRecording = false;

        // Ensure SpeakerEmbedder loads only once
        private static volatile bool _speakerEmbedderLoaded = false;

        static VoiceRecognizer()
        {
            // Faster timer for any remaining queued items (immediate processing is primary path)
            _audioProcessingTimer = new Timer(ProcessExternalAudioQueue, null, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5));
        }

        public static void Start(string modelPath, string triggerName = "john")
        {
            try
            {
                _shutdownRequested = false;

                if (!Directory.Exists(modelPath))
                {
                    Console.WriteLine($"[Vosk] Model path not found: {modelPath}");
                    // Still start microphone capture for RMS visualization
                    EnsureWaveInInitialized();
                    return;
                }

                Console.WriteLine($"🎤 VoiceRecognizer: Starting with shared model manager...");
                
                // Use VoskModelManager to get a shared recognizer instance
                _recognizer = VoskModelManager.CreateRecognizer(modelPath, 16000.0f);
                _currentModelPath = modelPath;
                
                if (_recognizer == null)
                {
                    Console.WriteLine($"❌ VoiceRecognizer: Failed to create recognizer via VoskModelManager");
                    // Start mic for RMS even if STT not ready
                    EnsureWaveInInitialized();
                    return;
                }

                // Ensure the speaker embedding model is loaded once
                EnsureSpeakerEmbedderLoaded();

                EnsureWaveInInitialized();

                _voiceProcessor = new VoiceProcessor(
                    _recognizer,
                    transcription =>
                    {
                        OnTranscription?.Invoke(transcription);
                    },
                    (speaker, score) =>
                    {
                        Console.WriteLine($"🎤 Matched speaker: {speaker} (score={score:F3})");
                        OnSpeakerMatch?.Invoke(speaker, score);
                    },
                    triggerName,
                    rms =>
                    {
                        // Pass microphone RMS values to GUI (only from microphone)
                        OnRmsLevel?.Invoke(rms);
                    },
                    embedding =>
                    {
                        // Pass voice embeddings for debugging
                        OnVoiceEmbedding?.Invoke(embedding);
                    },
                    // NEW: Wire Discord RMS callback so UI updates from VoiceProcessor too
                    OnDiscordRmsLevel
                );

                Console.WriteLine("✅ VoiceRecognizer: Clean pipeline active - microphone and Discord audio separated");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VoiceRecognizer.Start failed: {ex.Message}");
            }
        }

        private static void EnsureSpeakerEmbedderLoaded()
        {
            if (_speakerEmbedderLoaded) return;
            try
            {
                var modelPath = AppSettings.LoadSpeakerEmbeddingModelPath();
                if (!string.IsNullOrWhiteSpace(modelPath))
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    var fullPath = Path.IsPathRooted(modelPath) ? modelPath : Path.Combine(baseDir, modelPath);
                    if (File.Exists(fullPath))
                    {
                        SpeakerEmbedder.Load(fullPath);
                        _speakerEmbedderLoaded = true;
                    }
                    else
                    {
                        Console.WriteLine($"SpeakerEmbedder model not found at: {fullPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SpeakerEmbedder load error: {ex.Message}");
            }
        }

        private static void EnsureWaveInInitialized()
        {
            if (_waveIn != null) return;

            _waveIn = new WaveInEvent
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(16000, 1)
            };

            _waveIn.DataAvailable += (s, a) =>
            {
                if (_shutdownRequested) return;
                if (!_microphoneInputEnabled) return;

                // If STT pipeline isn't ready, still publish RMS so UI meters work
                if (_voiceProcessor == null)
                {
                    try
                    {
                        float rms = AudioUtils.CalculateRms(a.Buffer, a.BytesRecorded);
                        OnRmsLevel?.Invoke(rms);
                    }
                    catch { }
                    return;
                }

                // Process microphone audio directly (synchronously) only if enabled
                lock (_processingLock)
                {
                    if (_shutdownRequested) return;
                    if (!_isProcessing && _voiceProcessor != null)
                    {
                        _isProcessing = true;
                        try
                        {
                            _voiceProcessor.ProcessAudio(a.Buffer, a.BytesRecorded);
                        }
                        finally
                        {
                            _isProcessing = false;
                        }
                    }
                }
            };

            _waveIn.StartRecording();
            _microphoneRecording = true;
            Console.WriteLine("🎤 Microphone recording started (RMS ready)");
        }

        public static void Stop()
        {
            try
            {
                _shutdownRequested = true;

                // Pause external processing timer
                try { _audioProcessingTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }

                // Stop microphone first to prevent further callbacks
                try
                {
                    _waveIn?.StopRecording();
                    _microphoneRecording = false;
                }
                catch { }

                // Ensure no processing is in-flight and prevent new processing
                lock (_processingLock)
                {
                    _isProcessing = false;
                    // Drop processor reference so no one uses disposed recognizer
                    _voiceProcessor = null;
                }

                // Dispose recognizer safely
                try { _recognizer?.Dispose(); } catch { }

                // Release reference to shared model
                if (!string.IsNullOrEmpty(_currentModelPath))
                {
                    VoskModelManager.ReleaseModel(_currentModelPath);
                }

                // Dispose waveIn after stopping
                try { _waveIn?.Dispose(); } catch { }
                _waveIn = null;
                _recognizer = null;

                Console.WriteLine("✅ VoiceRecognizer: Stopped and released shared model reference");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VoiceRecognizer.Stop failed: {ex.Message}");
            }
        }

        /// <summary>
        /// IMMEDIATE PROCESSING: Process external audio directly without queuing delays
        /// For Discord audio, buffer per user to avoid interleaving between speakers.
        /// </summary>
        public static void ProcessExternalAudio(byte[] audioData, int bytesRecorded, string source = "External")
        {
            try
            {
                if (_shutdownRequested) return;
                if (_voiceProcessor == null || audioData == null || bytesRecorded <= 0)
                {
                    return;
                }

                // Check if Discord input is enabled for Discord audio
                if (source.StartsWith("Discord") && !_discordInputEnabled)
                {
                    return; // Skip Discord audio processing if disabled
                }

                // NEW: For Discord sources, buffer by user to keep commands isolated
                if (source.StartsWith("Discord:"))
                {
                    var copy = new byte[bytesRecorded];
                    Array.Copy(audioData, copy, bytesRecorded);
                    var queue = _discordUserQueues.GetOrAdd(source, _ => new ConcurrentQueue<(byte[], int)>());
                    queue.Enqueue((copy, bytesRecorded));
                    return; // Let timer process per-user queues
                }

                // Non-Discord external audio: immediate processing
                lock (_processingLock)
                {
                    if (_shutdownRequested) return;
                    if (!_isProcessing && _voiceProcessor != null)
                    {
                        _isProcessing = true;
                        try
                        {
                            _voiceProcessor.ProcessAudio(audioData, bytesRecorded);
                        }
                        finally
                        {
                            _isProcessing = false;
                        }
                    }
                    else
                    {
                        // If currently processing microphone audio, queue for later (but this should be rare)
                        var audioCopy = new byte[bytesRecorded];
                        Array.Copy(audioData, audioCopy, bytesRecorded);
                        _externalAudioQueue.Enqueue((audioCopy, bytesRecorded, source));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error processing {source} audio: {ex.Message}");
                _isProcessing = false; // Reset on error
            }
        }

        /// <summary>
        /// Process external audio queue in a thread-safe manner
        /// Prioritizes per-user Discord queues to avoid interleaving/overwriting.
        /// </summary>
        private static void ProcessExternalAudioQueue(object state)
        {
            // Reentrancy guard: ensure only one timer callback runs at a time
            if (Interlocked.CompareExchange(ref _queueWorkRunning, 1, 0) != 0)
                return;
            try
            {
                if (_shutdownRequested) return;
                // Only process if we're not already processing microphone audio
                if (_isProcessing || _voiceProcessor == null || _recognizer == null)
                {
                    return;
                }

                // 1) Handle Discord per-user queues first
                // Choose or maintain the active Discord source
                if (_activeDiscordSource == null)
                {
                    foreach (var kvp in _discordUserQueues)
                    {
                        if (kvp.Value != null && !kvp.Value.IsEmpty)
                        {
                            _activeDiscordSource = kvp.Key;
                            break;
                        }
                    }
                }

                var activeSource = _activeDiscordSource; // capture to avoid races
                if (!string.IsNullOrEmpty(activeSource) && _discordUserQueues.TryGetValue(activeSource, out var userQueue))
                {
                    int processedForUser = 0;
                    // Process a generous batch for the active user to keep phrases intact
                    while (!_shutdownRequested && processedForUser < 50 && userQueue.TryDequeue(out var item))
                    {
                        lock (_processingLock)
                        {
                            if (_shutdownRequested) break;
                            if (_isProcessing || _voiceProcessor == null) break;
                            _isProcessing = true;
                            try
                            {
                                // Update speaker hint based on active source
                                var username = activeSource.StartsWith("Discord:") && activeSource.Length > 8
                                    ? activeSource.Substring("Discord:".Length)
                                    : activeSource;
                                SpeakerIdentifier.SetDiscordSpeakerHint(username);
                                _voiceProcessor.ProcessAudio(item.data, item.length);
                                processedForUser++;
                            }
                            finally
                            {
                                _isProcessing = false;
                            }
                        }
                    }

                    // If we've drained this user's queue, inject a short silence to force flush and switch users
                    if (userQueue.IsEmpty)
                    {
                        // Schedule a short silence injection to allow VAD flush inside VoiceProcessor
                        Task.Run(() =>
                        {
                            try
                            {
                                Thread.Sleep(200); // allow silence timeout window
                                var silence = new byte[1600]; // 50ms of 16kHz mono s16 (approx)
                                lock (_processingLock)
                                {
                                    if (!_shutdownRequested && !_isProcessing && _voiceProcessor != null)
                                    {
                                        _isProcessing = true;
                                        try
                                        {
                                            _voiceProcessor.ProcessAudio(silence, silence.Length);
                                        }
                                        finally
                                        {
                                            _isProcessing = false;
                                        }
                                    }
                                }
                            }
                            catch { }
                        });

                        _activeDiscordSource = null; // release and allow next user to run
                    }

                    return; // Prioritize Discord per-user processing
                }

                // 2) Fallback: Process any remaining generic external items (non-Discord)
                int processed = 0;
                while (!_shutdownRequested && processed < 10 && _externalAudioQueue.TryDequeue(out var audioItem))
                {
                    lock (_processingLock)
                    {
                        if (_shutdownRequested) return;
                        if (!_isProcessing && _voiceProcessor != null)
                        {
                            _isProcessing = true;
                            try
                            {
                                if (audioItem.source.StartsWith("Discord:") && _discordInputEnabled)
                                {
                                    var username = audioItem.source.Length > 8 ? audioItem.source.Substring("Discord:".Length) : audioItem.source;
                                    SpeakerIdentifier.SetDiscordSpeakerHint(username);
                                }
                                
                                _voiceProcessor.ProcessAudio(audioItem.data, audioItem.length);
                                processed++;
                            }
                            finally
                            {
                                _isProcessing = false;
                            }
                        }
                        else
                        {
                            // If we're processing something else, put the item back
                            _externalAudioQueue.Enqueue(audioItem);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error processing external audio queue: {ex.Message}");
                // Reset processing flag in case of error
                _isProcessing = false;
            }
            finally
            {
                Interlocked.Exchange(ref _queueWorkRunning, 0);
            }
        }

        public static bool IsReady()
        {
            return _voiceProcessor != null && _recognizer != null;
        }

        public static (int queueSize, bool isProcessing) GetExternalAudioStats()
        {
            return (_externalAudioQueue.Count, _isProcessing);
        }

        public static void SetMicrophoneInputEnabled(bool enabled)
        {
            _microphoneInputEnabled = enabled;
            Console.WriteLine($"🎤 Microphone input {(enabled ? "enabled" : "disabled")}" );
            
            if (_waveIn != null)
            {
                if (enabled && !_microphoneRecording)
                {
                    _waveIn.StartRecording();
                    _microphoneRecording = true;
                    Console.WriteLine("🎤 Microphone recording started");
                }
                else if (!enabled && _microphoneRecording)
                {
                    _waveIn.StopRecording();
                    _microphoneRecording = false;
                    Console.WriteLine("🎤 Microphone recording stopped");
                }
            }
        }

        public static void SetDiscordInputEnabled(bool enabled)
        {
            _discordInputEnabled = enabled;
            Console.WriteLine($"🤖 Discord input {(enabled ? "enabled" : "disabled")}");
        }

        public static bool IsMicrophoneInputEnabled()
        {
            return _microphoneInputEnabled;
        }

        public static bool IsDiscordInputEnabled()
        {
            return _discordInputEnabled;
        }
    }
}
