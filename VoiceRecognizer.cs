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

        // NEW: Per-user Discord buffering to avoid interleaving/overwriting
        private static readonly ConcurrentDictionary<string, ConcurrentQueue<(byte[] data, int length)>> _discordUserQueues = new ConcurrentDictionary<string, ConcurrentQueue<(byte[], int)>>();
        private static volatile string _activeDiscordSource = null;

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

        static VoiceRecognizer()
        {
            // Faster timer for any remaining queued items (immediate processing is primary path)
            _audioProcessingTimer = new Timer(ProcessExternalAudioQueue, null, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5));
        }

        public static void Start(string modelPath, string triggerName = "john")
        {
            try
            {
                if (!Directory.Exists(modelPath))
                {
                    Console.WriteLine($"[Vosk] Model path not found: {modelPath}");
                    return;
                }

                Console.WriteLine($"🎤 VoiceRecognizer: Starting with shared model manager...");
                
                // Use VoskModelManager to get a shared recognizer instance
                _recognizer = VoskModelManager.CreateRecognizer(modelPath, 16000.0f);
                _currentModelPath = modelPath;
                
                if (_recognizer == null)
                {
                    Console.WriteLine($"❌ VoiceRecognizer: Failed to create recognizer via VoskModelManager");
                    return;
                }

                _waveIn = new WaveInEvent
                {
                    DeviceNumber = 0,
                    WaveFormat = new WaveFormat(16000, 1)
                };

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
                    }
                    // Removed Discord RMS callback - Discord audio is processed separately
                );

                _waveIn.DataAvailable += (s, a) =>
                {
                    // Process microphone audio directly (synchronously) only if enabled
                    if (_microphoneInputEnabled)
                    {
                        lock (_processingLock)
                        {
                            if (!_isProcessing)
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
                    }
                };

                _waveIn.StartRecording();
                _microphoneRecording = true;
                Console.WriteLine("✅ VoiceRecognizer: Clean pipeline active - microphone and Discord audio separated");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VoiceRecognizer.Start failed: {ex.Message}");
            }
        }

        public static void Stop()
        {
            try
            {
                _waveIn?.StopRecording();
                _microphoneRecording = false;
                _waveIn?.Dispose();
                _recognizer?.Dispose();
                
                // Release reference to shared model
                if (!string.IsNullOrEmpty(_currentModelPath))
                {
                    VoskModelManager.ReleaseModel(_currentModelPath);
                }
                
                _microphoneRecording = false;
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
                    if (!_isProcessing)
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
            try
            {
                // Only process if we're not already processing microphone audio
                if (_isProcessing || _voiceProcessor == null)
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

                if (!string.IsNullOrEmpty(_activeDiscordSource) && _discordUserQueues.TryGetValue(_activeDiscordSource, out var userQueue))
                {
                    int processedForUser = 0;
                    // Process a generous batch for the active user to keep phrases intact
                    while (processedForUser < 50 && userQueue.TryDequeue(out var item))
                    {
                        lock (_processingLock)
                        {
                            if (_isProcessing) break;
                            _isProcessing = true;
                            try
                            {
                                // Update speaker hint based on active source
                                var username = _activeDiscordSource.Substring("Discord:".Length);
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
                                    if (!_isProcessing && _voiceProcessor != null)
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
                while (processed < 10 && _externalAudioQueue.TryDequeue(out var audioItem))
                {
                    lock (_processingLock)
                    {
                        if (!_isProcessing)
                        {
                            _isProcessing = true;
                            try
                            {
                                if (audioItem.source.StartsWith("Discord:") && _discordInputEnabled)
                                {
                                    var username = audioItem.source.Substring("Discord:".Length);
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
        }

        /// <summary>
        /// Check if the voice recognition system is ready to process external audio
        /// </summary>
        public static bool IsReady()
        {
            return _voiceProcessor != null && _recognizer != null;
        }

        /// <summary>
        /// Get statistics about external audio processing
        /// </summary>
        public static (int queueSize, bool isProcessing) GetExternalAudioStats()
        {
            return (_externalAudioQueue.Count, _isProcessing);
        }

        /// <summary>
        /// Enable or disable microphone input processing
        /// </summary>
        public static void SetMicrophoneInputEnabled(bool enabled)
        {
            _microphoneInputEnabled = enabled;
            Console.WriteLine($"🎤 Microphone input {(enabled ? "enabled" : "disabled")}");
            
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

        /// <summary>
        /// Enable or disable Discord input processing
        /// </summary>
        public static void SetDiscordInputEnabled(bool enabled)
        {
            _discordInputEnabled = enabled;
            Console.WriteLine($"🤖 Discord input {(enabled ? "enabled" : "disabled")}");
        }

        /// <summary>
        /// Get current microphone input state
        /// </summary>
        public static bool IsMicrophoneInputEnabled()
        {
            return _microphoneInputEnabled;
        }

        /// <summary>
        /// Get current Discord input state
        /// </summary>
        public static bool IsDiscordInputEnabled()
        {
            return _discordInputEnabled;
        }
    }
}
