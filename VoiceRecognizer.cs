// VoiceRecognizer.cs - Simplified and cleaned up
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Vosk;
using Kinectv1.Settings; // new settings enums and snapshot access

namespace Kinectv1
{
    public static class VoiceRecognizer
    {
        private static VoskRecognizer _recognizer;
        private static WaveInEvent _waveIn;
        private static VoiceProcessor _voiceProcessor;
        private static string _currentModelPath;

        // Bounded audio queues with backpressure using BlockingCollection for .NET Framework compatibility
        private class BoundedQueue<T>
        {
            private readonly BlockingCollection<T> _queue;
            private readonly int _capacity;
            public BoundedQueue(int capacity)
            {
                _capacity = Math.Max(1, capacity);
                _queue = new BlockingCollection<T>(new ConcurrentQueue<T>());
            }
            public bool TryAdd(T item)
            {
                // DropOldest-like behavior: if we exceed capacity, try to remove one before adding
                while (_queue.Count >= _capacity)
                {
                    T _;
                    _queue.TryTake(out _);
                }
                return _queue.TryAdd(item);
            }
            public bool TryTake(out T item) => _queue.TryTake(out item);
            public int Count => _queue.Count;
            public void CompleteAdding() { try { _queue.CompleteAdding(); } catch { } }
        }

        private static readonly BoundedQueue<Tuple<byte[], int, string>> _externalAudioQueue = new BoundedQueue<Tuple<byte[], int, string>>(50);
        private static readonly Timer _audioProcessingTimer;
        private static readonly Timer _healthSnapshotTimer; // NEW: Periodic health snapshots
        private static readonly object _processingLock = new object();
        private static volatile bool _isProcessing = false;
        private static volatile bool _shutdownRequested = false; // NEW: prevent processing during shutdown

        // Bounded per-user Discord buffering with backpressure - prevents runaway memory usage
        private static readonly ConcurrentDictionary<string, BoundedQueue<Tuple<byte[], int>>> _discordUserQueues = new ConcurrentDictionary<string, BoundedQueue<Tuple<byte[], int>>>();
        private static volatile string _activeDiscordSource = null;
        private static int _queueWorkRunning = 0; // prevent re-entrant timer callbacks
        
        // Backpressure configuration and metrics
        private const int MAX_EXTERNAL_QUEUE_SIZE = 50; // ~1 second of 20ms audio chunks  
        private const int MAX_PER_USER_QUEUE_SIZE = 25; // ~0.5 seconds per Discord user
        private static long _totalAudioDrops = 0;
        private static long _totalDiscordDrops = 0;

        // Events
        public static Action<float> OnRmsLevel;
        public static Action<float> OnDiscordRmsLevel; // NEW: Separate Discord RMS event
        public static Action<string> OnTranscription;
        public static Action<string> OnNameHeard;
        public static Action<string, float> OnSpeakerMatch;
        public static Action<float[]> OnVoiceEmbedding;
        public static Action<string, float, string> OnSpeakerResolvedForOllama; // NEW: final resolved speaker for dispatch

        // Input control flags
        private static bool _microphoneInputEnabled = true;
        // Default Discord input to disabled on startup; UI/ApplyAudioMode will enable when selected
        private static bool _discordInputEnabled = false;
        private static bool _microphoneRecording = false;
        private static AudioInMode _cachedAudioMode = AudioInMode.LocalMic; // Cache for performance

        // Ensure SpeakerEmbedder loads only once
        private static volatile bool _speakerEmbedderLoaded = false;

        static VoiceRecognizer()
        {
            // Faster timer for any remaining queued items (immediate processing is primary path)
            _audioProcessingTimer = new Timer(ProcessExternalAudioQueue, null, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5));
            
            // Health snapshot timer (every 5 seconds)
            _healthSnapshotTimer = new Timer(EmitHealthSnapshot, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        // Load persisted audio mode, then reconcile with current enable flags
        public static void RefreshAudioMode()
        {
            try
            {
                var modeJson = Kinectv1.App.SettingsProvider?.Current?.App?.InputMode;
                _cachedAudioMode = modeJson.HasValue ? (AudioInMode)modeJson.Value : AudioInMode.LocalMic;
            }
            catch { _cachedAudioMode = AudioInMode.LocalMic; }

            // Reconcile with live flags so gating matches current UI state
            UpdateCachedModeFromFlags();
        }

        public static void Start(string modelPath, string triggerName = "")
        {
            try
            {
                _shutdownRequested = false;
                
                // Initialize cached audio mode for performance
                RefreshAudioMode();

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
                        
                        // Update identity fusion tracker with voice recognition
                        IdentityFusionTracker.UpdateVoice(null, speaker, score);
                        
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
                var error = AppError.ASR("ASR_START_FAILED", 
                    $"Voice recognizer start failed: {ex.Message}",
                    "Check microphone permissions and audio device configuration.", ex);
                Console.WriteLine($"VoiceRecognizer: {error.GetDisplayString()}");
            }
        }

        private static void EnsureSpeakerEmbedderLoaded()
        {
            if (_speakerEmbedderLoaded) return;
            try
            {
                var modelPath = Kinectv1.App.SettingsProvider?.Current?.Face?.SpeakerEmbeddingModelPath;
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
                        var error = AppError.ASR("ASR_MODEL_NOT_FOUND", 
                            $"Speaker embedder model not found at: {fullPath}",
                            "Verify speaker embedding model path on Diagnostics page.");
                        Console.WriteLine(error.GetDisplayString());
                    }
                }
            }
            catch (Exception ex)
            {
                var error = AppError.ASR("ASR_SPEAKER_EMBEDDER_ERROR", 
                    $"Speaker embedder load error: {ex.Message}",
                    "Check speaker embedding model file and path configuration.", ex);
                Console.WriteLine(error.GetDisplayString());
            }
        }

        private static void EnsureWaveInInitialized()
        {
            if (_waveIn != null) return;

            try
            {
                // Try to use configured input device; fall back to default/first
                int deviceNumber = 0;
                try
                {
                    var configured = AudioDeviceManager.GetConfiguredInputDevice();
                    if (configured != null)
                    {
                        deviceNumber = configured.DeviceNumber;
                        Console.WriteLine($"🎤 Using configured mic device #{deviceNumber}: {configured.DeviceName}");
                    }
                    else
                    {
                        if (WaveIn.DeviceCount <= 0)
                        {
                            Console.WriteLine("❌ No microphone input devices found (WaveIn.DeviceCount == 0)");
                            return; // Cannot start recording
                        }
                        Console.WriteLine("🎤 Using default mic device #0 (no configured device)");
                        deviceNumber = 0;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Could not resolve configured mic device, defaulting to #0: {ex.Message}");
                    if (WaveIn.DeviceCount <= 0) return;
                    deviceNumber = 0;
                }

                _waveIn = new WaveInEvent
                {
                    DeviceNumber = deviceNumber,
                    WaveFormat = new WaveFormat(16000, 1),
                    BufferMilliseconds = 20,
                    NumberOfBuffers = 4
                };

                _waveIn.DataAvailable += (s, a) =>
                {
                    if (_shutdownRequested) return;
                    if (!_microphoneInputEnabled) return;

                    // Always compute and emit RMS for UI visualization
                    try
                    {
                        float rmsForUi = AudioUtils.CalculateRms(a.Buffer, a.BytesRecorded);
                        OnRmsLevel?.Invoke(rmsForUi);
                    }
                    catch { }

                    // Only feed mic audio into the STT pipeline when in LocalMic mode
                    if (_cachedAudioMode != AudioInMode.LocalMic)
                    {
                        return; // Skip recognition but keep UI RMS updates
                    }

                    // If STT pipeline isn't ready, nothing else to do
                    if (_voiceProcessor == null || _recognizer == null)
                    {
                        return;
                    }

                    // Non-blocking: enqueue mic audio for background processing to avoid freezing RMS/UI
                    try
                    {
                        var audioCopy = new byte[a.BytesRecorded];
                        Buffer.BlockCopy(a.Buffer, 0, audioCopy, 0, a.BytesRecorded);
                        if (!_externalAudioQueue.TryAdd(Tuple.Create(audioCopy, a.BytesRecorded, "Mic")))
                        {
                            Interlocked.Increment(ref _totalAudioDrops);
                            Telemetry.Counter("counter.queue.drop.mic");
                        }
                        else
                        {
                            Telemetry.Gauge("gauge.queue.depth.mic", _externalAudioQueue.Count);
                        }
                    }
                    catch { }
                };

                try
                {
                    _waveIn.StartRecording();
                    _microphoneRecording = true;
                    Console.WriteLine("🎤 Microphone recording started (RMS ready)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Failed to start microphone recording: {ex.Message}");
                    try { _waveIn.Dispose(); } catch { }
                    _waveIn = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ EnsureWaveInInitialized failed: {ex.Message}");
            }
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

                // Clean up queues
                try { _externalAudioQueue.CompleteAdding(); } catch { }
                
                foreach (var kvp in _discordUserQueues)
                {
                    try { kvp.Value.CompleteAdding(); } catch { }
                }
                _discordUserQueues.Clear();

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
                    var queue = _discordUserQueues.GetOrAdd(source, _ => new BoundedQueue<Tuple<byte[], int>>(MAX_PER_USER_QUEUE_SIZE));

                    if (!queue.TryAdd(Tuple.Create(copy, bytesRecorded)))
                    {
                        Interlocked.Increment(ref _totalDiscordDrops);
                        Telemetry.Counter("counter.queue.drop.discord");
                        Console.WriteLine($"⚠️ Discord audio backpressure: Queue add failed for {source} (total drops: {_totalDiscordDrops})");
                    }
                    else
                    {
                        Telemetry.Gauge("gauge.queue.depth.discord", queue.Count);
                    }
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
                        // If currently processing microphone audio, queue for later with backpressure
                        var audioCopy = new byte[bytesRecorded];
                        Array.Copy(audioData, audioCopy, bytesRecorded);
                        if (!_externalAudioQueue.TryAdd(Tuple.Create(audioCopy, bytesRecorded, source)))
                        {
                            Interlocked.Increment(ref _totalAudioDrops);
                            Telemetry.Counter("counter.queue.drop.mic");
                            Console.WriteLine($"⚠️ External audio backpressure: Queue add failed (total drops: {_totalAudioDrops})");
                        }
                        else
                        {
                            Telemetry.Gauge("gauge.queue.depth.mic", _externalAudioQueue.Count);
                        }
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
                if (_activeDiscordSource == null)
                {
                    foreach (var kvp in _discordUserQueues)
                    {
                        if (kvp.Value != null && kvp.Value.Count > 0)
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
                    Tuple<byte[], int> item;
                    while (!_shutdownRequested && processedForUser < 50 && userQueue.TryTake(out item))
                    {
                        lock (_processingLock)
                        {
                            if (_shutdownRequested) break;
                            if (_isProcessing || _voiceProcessor == null) break;
                            _isProcessing = true;
                            try
                            {
                                var username = activeSource.StartsWith("Discord:") && activeSource.Length > 8
                                    ? activeSource.Substring("Discord:".Length)
                                    : activeSource;
                                SpeakerIdentifier.SetDiscordSpeakerHint(username);
                                _voiceProcessor.ProcessAudio(item.Item1, item.Item2);
                                processedForUser++;
                            }
                            finally
                            {
                                _isProcessing = false;
                            }
                        }
                    }

                    // If we've drained this user's queue, inject a short silence to force flush and switch users
                    if (userQueue.Count == 0)
                    {
                        Task.Run(() =>
                        {
                            try
                            {
                                Thread.Sleep(200); // allow silence timeout window
                                var silence = new byte[1600]; // ~50ms at 16kHz mono s16
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
                Tuple<byte[], int, string> audioItem;
                while (!_shutdownRequested && processed < 10 && _externalAudioQueue.TryTake(out audioItem))
                {
                    lock (_processingLock)
                    {
                        if (_shutdownRequested) return;
                        if (!_isProcessing && _voiceProcessor != null)
                        {
                            _isProcessing = true;
                            try
                            {
                                if (audioItem.Item3.StartsWith("Discord:") && _discordInputEnabled)
                                {
                                    var username = audioItem.Item3.Length > 8 ? audioItem.Item3.Substring("Discord:".Length) : audioItem.Item3;
                                    SpeakerIdentifier.SetDiscordSpeakerHint(username);
                                }
                                
                                _voiceProcessor.ProcessAudio(audioItem.Item1, audioItem.Item2);
                                processed++;
                            }
                            finally
                            { 
                                _isProcessing = false;
                            }
                        }
                        else
                        {
                            // Re-queue for later; Drop if cannot add
                            if (!_externalAudioQueue.TryAdd(audioItem))
                            {
                                Interlocked.Increment(ref _totalAudioDrops);
                                Telemetry.Counter("counter.queue.drop.mic");
                                Console.WriteLine($"⚠️ External audio backpressure: Dropped re-queued chunk (total drops: {_totalAudioDrops})");
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error processing external audio queue: {ex.Message}");
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

            // Reflect current flags into cached mode so STT gating matches UI immediately
            UpdateCachedModeFromFlags();
            
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
            Console.WriteLine($"🤖 Discord input {(enabled ? "enabled" : "disabled")}" );
            
            // Refresh cached audio mode when input settings change
            UpdateCachedModeFromFlags();
        }

        // Sync cached audio mode with current enable flags to avoid dependence on persisted settings during runtime
        private static void UpdateCachedModeFromFlags()
        {
            try
            {
                // Respect explicit single-choice flags first
                if (_discordInputEnabled && !_microphoneInputEnabled)
                {
                    _cachedAudioMode = AudioInMode.DiscordVoice;
                }
                else if (_microphoneInputEnabled && !_discordInputEnabled)
                {
                    _cachedAudioMode = AudioInMode.LocalMic;
                }
                else
                {
                    // Fallback to persisted selection (supports third-party modes like Mumble/SystemLoopback)
                    var modeJson = Kinectv1.App.SettingsProvider?.Current?.App?.InputMode;
                    _cachedAudioMode = modeJson.HasValue ? (AudioInMode)modeJson.Value : AudioInMode.LocalMic;
                }
            }
            catch { _cachedAudioMode = AudioInMode.LocalMic; }
        }

        public static bool IsMicrophoneInputEnabled() { return _microphoneInputEnabled; }
        public static bool IsDiscordInputEnabled() { return _discordInputEnabled; }

        /// <summary>
        /// Emit periodic health snapshot for telemetry monitoring
        /// </summary>
        private static void EmitHealthSnapshot(object state)
        {
            try
            {
                // Get telemetry snapshot
                var snapshot = Telemetry.GetSnapshot();
                
                // Create key counters summary for health monitoring
                // Get backpressure metrics for telemetry
                var backpressureMetrics = GetBackpressureMetrics();
                var ttsMetrics = Discord.DiscordNetBotManager.GetTtsBackpressureMetrics();
                
                var healthData = new
                {
                    // Discord audio processing
                    chunks_processed = Telemetry.GetCounter("discord_audio.chunks_processed"),
                    drop_count = Telemetry.GetCounter("discord_audio.null_input") + Telemetry.GetCounter("discord_audio.oversized_chunks"),
                    clip_count = Telemetry.GetCounter("discord_audio.clip_count"),
                    
                    // ASR processing
                    asr_final_ms = Telemetry.GetCounter("asr_final.count") > 0 
                        ? Telemetry.GetAccumulator("asr_final.total_ms") / Telemetry.GetCounter("asr_final.count")
                        : 0,
                    asr_partial_count = Telemetry.GetCounter("asr.partial_results"),
                    asr_final_count = Telemetry.GetCounter("asr.final_results"),
                    debounce_skips = Telemetry.GetCounter("asr.debounce_skips"),
                    final_flushes = Telemetry.GetCounter("asr.final_flushes"),
                    
                    // TTS processing
                    tts_generate_ms = Telemetry.GetCounter("tts_generate.count") > 0 
                        ? Telemetry.GetAccumulator("tts_generate.total_ms") / Telemetry.GetCounter("tts_generate.count")
                        : 0,
                    tts_segments = Telemetry.GetCounter("tts.segments_processed"),
                    tts_requests = Telemetry.GetCounter("tts.generate_requests"),
                    
                    // ONNX session management
                    onnx_sessions_created = Telemetry.GetCounter("onnx.sessions_created"),
                    cuda_ep_fallbacks = Telemetry.GetCounter("onnx.cpu_fallbacks"),
                    
                    // Backpressure metrics (NEW)
                    external_queue_count = backpressureMetrics.externalQueueCount,
                    discord_queue_count = backpressureMetrics.totalDiscordQueues,  
                    discord_queue_items = backpressureMetrics.totalDiscordItems,
                    audio_drops_total = backpressureMetrics.totalAudioDrops,
                    discord_drops_total = backpressureMetrics.totalDiscordDrops,
                    tts_queue_count = ttsMetrics.ttsQueueCount,
                    tts_drops_total = ttsMetrics.totalTtsDrops,
                    
                    // System health
                    microphone_enabled = _microphoneInputEnabled,
                    discord_enabled = _discordInputEnabled,
                    processing_active = _isProcessing,
                    model_loaded = _recognizer != null
                };

                Telemetry.Event("health.snapshot", healthData);
            }
            catch (Exception ex)
            {
                // Silently log health snapshot errors to avoid disrupting main flow
                Console.WriteLine($"Health snapshot error: {ex.Message}");
            }
        }

        /// <summary>
        /// Get backpressure metrics for monitoring queue health
        /// </summary>
        public static (int externalQueueCount, int totalDiscordQueues, int totalDiscordItems, long totalAudioDrops, long totalDiscordDrops) GetBackpressureMetrics()
        {
            try
            {
                var externalCount = _externalAudioQueue?.Count ?? 0;
                var discordQueueCount = _discordUserQueues.Count;
                var totalDiscordItems = 0;
                
                foreach (var kvp in _discordUserQueues)
                {
                    totalDiscordItems += kvp.Value?.Count ?? 0;
                }
                
                return (externalCount, discordQueueCount, totalDiscordItems, _totalAudioDrops, _totalDiscordDrops);
            }
            catch
            {
                return (0, 0, 0, _totalAudioDrops, _totalDiscordDrops);
            }
        }
    }
}
