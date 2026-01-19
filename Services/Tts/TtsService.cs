using System;
using System.Collections.Concurrent;
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
        private static int _ipaTimeoutMs = 800;       // reduced from 1500ms for faster IPA generation
        private static int _ipaServiceTimeoutMs = 800; // kept for settings compatibility (unused)

        /// <summary>
        /// Pre-warm the TTS session on startup to avoid cold-start latency on first utterance.
        /// Call this during app initialization.
        /// </summary>
        public static void Prewarm()
        {
            try
            {
                if (!IsEnabled()) return;
                Log("PREWARM", "Pre-warming TTS session...");
                var sw = Stopwatch.StartNew();
                if (EnsureInitialized())
                {
                    Log("PREWARM", $"TTS session ready in {sw.ElapsedMilliseconds}ms");
                }
                else
                {
                    Log("PREWARM", "TTS pre-warm failed - init returned false");
                }
            }
            catch (Exception ex)
            {
                Log("PREWARM", $"TTS pre-warm error: {ex.Message}");
            }
        }

        /// <summary>
        /// Async version of Prewarm for background initialization.
        /// </summary>
        public static Task PrewarmAsync()
        {
            return Task.Run(() => Prewarm());
        }

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
                    const int chunkSamples = 1024; // Reduced from 2048 for faster first-byte (~42ms at 24kHz)
                    int pos = 0;
                    var pcmChunk = new byte[chunkSamples * 2];
                    while (pos < arr.Length && !lct.IsCancellationRequested)
                    {
                        int take = Math.Min(chunkSamples, arr.Length - pos);
                        int neededBytes = take * 2;
                        // Backpressure: wait until there is room for this chunk (async)
                        while (!lct.IsCancellationRequested && provider.BufferedBytes > provider.BufferLength - neededBytes)
                        {
                            await Task.Delay(10, lct).ConfigureAwait(false); // Reduced from 15ms
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

        // --- Streaming TTS queue for sentence-by-sentence playback
        private static readonly ConcurrentQueue<string> _streamingSentenceQueue = new ConcurrentQueue<string>();
        private static volatile bool _isStreamingPlaybackActive = false;
        private static CancellationTokenSource _streamingPlaybackCts;
        private static readonly object _streamingLock = new object();
        private static string _currentStreamingSpeaker = null;
        private static volatile int _streamingGeneration = 0; // Generation counter to ignore stale sentences
        
        // Shared audio output - only one WaveOutEvent at a time to prevent layered audio
        private static WaveOutEvent _sharedWaveOut;
        private static BufferedWaveProvider _sharedProvider;
        private static readonly object _audioOutputLock = new object();

        /// <summary>
        /// Queue a sentence for streaming TTS playback.
        /// Sentences are spoken in order as they are queued.
        /// Call this from OnResponseSentenceReady event handler.
        /// </summary>
        public static void QueueSentenceForStreaming(string sentence, string speakerName = null)
        {
            if (string.IsNullOrWhiteSpace(sentence)) return;
            if (!IsEnabled()) return;

            lock (_streamingLock)
            {
                int currentGen = _streamingGeneration;
                
                _streamingSentenceQueue.Enqueue(sentence);
                Console.WriteLine($"[TTS] Queued sentence for streaming (gen={currentGen}): {sentence.Length} chars");

                // Start streaming playback if:
                // 1. No loop is currently active, OR
                // 2. The current loop is for an old generation (was cancelled by barge-in)
                bool needNewLoop = !_isStreamingPlaybackActive;
                
                if (!needNewLoop)
                {
                    // A loop is active, but check if it's for the current generation
                    // If the CTS was cancelled, we need a new loop
                    var cts = _streamingPlaybackCts;
                    if (cts == null || cts.IsCancellationRequested)
                    {
                        needNewLoop = true;
                        Console.WriteLine($"[TTS] Current loop was cancelled, starting new loop for gen={currentGen}");
                    }
                }
                
                if (needNewLoop)
                {
                    _currentStreamingSpeaker = speakerName;
                    _streamingPlaybackCts?.Cancel();
                    _streamingPlaybackCts?.Dispose();
                    _streamingPlaybackCts = new CancellationTokenSource();
                    var cts = _streamingPlaybackCts;
                    var gen = currentGen;
                    _isStreamingPlaybackActive = true;
                    
                    Console.WriteLine($"[TTS] Starting new streaming playback loop (gen={gen})");
                    
                    // Fire-and-forget the streaming playback task
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await StreamingPlaybackLoopAsync(_currentStreamingSpeaker, cts.Token, gen).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[TTS] Streaming playback error: {ex.Message}");
                        }
                        finally
                        {
                            lock (_streamingLock)
                            {
                                // Only mark inactive if we're still the current loop
                                if (_streamingPlaybackCts == cts)
                                {
                                    _isStreamingPlaybackActive = false;
                                }
                            }
                        }
                    });
                }
            }
        }

        /// <summary>
        /// Cancel any ongoing streaming TTS playback and clear the queue.
        /// Increments generation to invalidate any in-flight sentences.
        /// </summary>
        public static void CancelStreamingPlayback()
        {
            CancellationTokenSource ctsToCancel = null;
            int newGen;
            lock (_streamingLock)
            {
                ctsToCancel = _streamingPlaybackCts;
                // Clear the queue
                while (_streamingSentenceQueue.TryDequeue(out _)) { }
                // Increment generation so any sentences queued after this use the new generation
                _streamingGeneration++;
                newGen = _streamingGeneration;
                _lastCancel = DateTime.UtcNow; // Mark for grace window (same as legacy cancel)
            }
            
            // Stop the shared audio output immediately to prevent layered audio
            lock (_audioOutputLock)
            {
                try
                {
                    _sharedProvider?.ClearBuffer();
                    _sharedWaveOut?.Stop();
                }
                catch { }
            }
            
            // Cancel outside the lock to avoid deadlock
            if (ctsToCancel != null)
            {
                try { ctsToCancel.Cancel(); } catch { }
                Console.WriteLine($"[TTS] Streaming playback cancelled (barge-in), new generation={newGen}");
            }
        }

        /// <summary>
        /// Check if streaming TTS playback is currently active.
        /// </summary>
        public static bool IsStreamingPlaybackActive => _isStreamingPlaybackActive;

        /// <summary>
        /// Main streaming playback loop that processes sentences from the queue.
        /// </summary>
        private static async Task StreamingPlaybackLoopAsync(string speakerName, CancellationToken ct, int generation)
        {
            Console.WriteLine($"[TTS] Streaming playback loop started (gen={generation})");
            
            // Check if generation changed (we were cancelled before starting)
            lock (_streamingLock)
            {
                if (_streamingGeneration != generation)
                {
                    Console.WriteLine($"[TTS] Streaming playback aborted - generation changed ({generation} -> {_streamingGeneration})");
                    return;
                }
            }
            
            if (!EnsureInitialized())
            {
                Console.WriteLine("[TTS] Streaming playback aborted - init failed");
                return;
            }

            var snap = Kinectv1.App.SettingsProvider?.Current?.Tts;
            if (snap == null || !snap.Enabled)
            {
                Console.WriteLine("[TTS] Streaming playback aborted - TTS disabled");
                return;
            }

            // Resolve speaker once for the session
            string voicePath = null;
            if (!string.IsNullOrWhiteSpace(speakerName) && _voiceFiles.TryGetValue(speakerName, out var vp)) voicePath = vp;
            else if (!string.IsNullOrWhiteSpace(snap.Speaker) && _voiceFiles.TryGetValue(snap.Speaker, out var vs)) voicePath = vs;
            else if (_voiceFiles.Count > 0) voicePath = _voiceFiles.Values.First();
            if (voicePath == null)
            {
                OnTtsError?.Invoke("Voice style not found");
                Console.WriteLine("[TTS] Streaming playback aborted - voice not found");
                return;
            }

            double? configuredVol = null; try { configuredVol = snap.LocalVolume; } catch { }
            float volume = (float)Math.Max(0.0, configuredVol.HasValue ? configuredVol.Value : 1.0);

            // Setup shared audio playback - use lock to ensure only one active at a time
            BufferedWaveProvider provider;
            WaveOutEvent waveOut;
            
            lock (_audioOutputLock)
            {
                // Stop and dispose any existing audio output
                try { _sharedWaveOut?.Stop(); } catch { }
                try { _sharedProvider?.ClearBuffer(); } catch { }
                try { _sharedWaveOut?.Dispose(); } catch { }
                
                // Create new shared audio output
                var waveFormat = new WaveFormat(SampleRate, 16, 1);
                _sharedProvider = new BufferedWaveProvider(waveFormat)
                {
                    DiscardOnBufferOverflow = false,
                    BufferDuration = TimeSpan.FromSeconds(30)
                };
                provider = _sharedProvider;
                
                _sharedWaveOut = new WaveOutEvent { DesiredLatency = 100 };
                waveOut = _sharedWaveOut;
                
                try { waveOut.Init(provider); }
                catch (Exception ex)
                {
                    OnTtsError?.Invoke("Audio init failed: " + ex.Message);
                    Console.WriteLine($"[TTS] Streaming playback aborted - audio init failed: {ex.Message}");
                    _sharedWaveOut = null;
                    _sharedProvider = null;
                    return;
                }
            }

            bool speakingStartedFired = false;
            int totalSentencesProcessed = 0;

            try
            {
                var playbackStoppedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waveOut.PlaybackStopped += (s, e) => playbackStoppedTcs.TrySetResult(true);
                waveOut.Play();

                // Helper to queue float audio with backpressure
                async Task QueueFloatAudioAsync(float[] arr)
                {
                    if (arr == null || arr.Length == 0) return;
                    const int chunkSamples = 1024;
                    int pos = 0;
                    var pcmChunk = new byte[chunkSamples * 2];
                    while (pos < arr.Length && !ct.IsCancellationRequested)
                    {
                        // Check generation before each chunk
                        int currentGen;
                        lock (_streamingLock) { currentGen = _streamingGeneration; }
                        if (currentGen != generation) break;
                        
                        int take = Math.Min(chunkSamples, arr.Length - pos);
                        int neededBytes = take * 2;
                        while (!ct.IsCancellationRequested && provider.BufferedBytes > provider.BufferLength - neededBytes)
                        {
                            // Check generation during backpressure wait
                            lock (_streamingLock) { currentGen = _streamingGeneration; }
                            if (currentGen != generation) return;
                            await Task.Delay(10).ConfigureAwait(false);
                        }
                        if (ct.IsCancellationRequested) break;
                        
                        // Final generation check before adding samples
                        lock (_streamingLock) { currentGen = _streamingGeneration; }
                        if (currentGen != generation) break;
                        
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
                        pos += take;
                    }
                }

                // Keep processing sentences until queue is empty AND no more are coming
                int consecutiveEmptyPolls = 0;
                const int maxEmptyPolls = 8;
                const int maxEmptyPollsWhileLlmStreaming = 40; // ~1 second when LLM is still generating

                while (!ct.IsCancellationRequested)
                {
                    // Check if generation changed (barge-in occurred)
                    int currentGen;
                    lock (_streamingLock) { currentGen = _streamingGeneration; }
                    if (currentGen != generation)
                    {
                        Console.WriteLine($"[TTS] Streaming generation changed ({generation} -> {currentGen}), exiting loop");
                        break;
                    }
                    
                    if (_streamingSentenceQueue.TryDequeue(out var sentence))
                    {
                        consecutiveEmptyPolls = 0;

                        if (!speakingStartedFired)
                        {
                            speakingStartedFired = true;
                            try { OnTtsSpeakingStarted?.Invoke(); } catch { }
                        }

                        Console.WriteLine($"[TTS] Processing streamed sentence {++totalSentencesProcessed} (gen={generation}): {sentence.Length} chars");

                        // Check cancellation before synthesis
                        if (ct.IsCancellationRequested) break;

                        // Check generation again before expensive synthesis
                        lock (_streamingLock) { currentGen = _streamingGeneration; }
                        if (currentGen != generation)
                        {
                            Console.WriteLine($"[TTS] Generation changed before synthesis, aborting");
                            break;
                        }

                        // Synthesize the sentence
                        float[] audio = null;
                        try
                        {
                            audio = await Task.Run(() => SynthesizeOne(sentence, voicePath, snap)).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[TTS] Synthesis error: {ex.Message}");
                        }

                        // Check cancellation after synthesis
                        if (ct.IsCancellationRequested) break;
                        
                        // Check generation again after synthesis
                        lock (_streamingLock) { currentGen = _streamingGeneration; }
                        if (currentGen != generation)
                        {
                            Console.WriteLine($"[TTS] Generation changed during synthesis, discarding audio");
                            break;
                        }

                        if (audio != null && audio.Length > 0)
                        {
                            await QueueFloatAudioAsync(audio).ConfigureAwait(false);
                        }

                        // Add inter-sentence padding
                        lock (_streamingLock) { currentGen = _streamingGeneration; }
                        if (!ct.IsCancellationRequested && currentGen == generation && snap.MinClausePaddingMs > 0)
                        {
                            int padSamples = (int)Math.Round(SampleRate * (snap.MinClausePaddingMs / 1000.0));
                            if (padSamples > 0)
                            {
                                await QueueFloatAudioAsync(new float[padSamples]).ConfigureAwait(false);
                            }
                        }
                    }
                    else
                    {
                        consecutiveEmptyPolls++;
                        
                        // Check if LLM is still streaming - if so, wait much longer for more sentences
                        bool llmStillStreaming = false;
                        try { llmStillStreaming = OllamaService.IsLlmStreaming; } catch { }
                        
                        bool hasBufferedAudio = provider.BufferedBytes > 0;
                        
                        // Use longer timeout if LLM is still generating
                        int effectiveMaxPolls;
                        if (llmStillStreaming)
                        {
                            effectiveMaxPolls = maxEmptyPollsWhileLlmStreaming;
                        }
                        else if (hasBufferedAudio)
                        {
                            effectiveMaxPolls = maxEmptyPolls + 4;
                        }
                        else
                        {
                            effectiveMaxPolls = maxEmptyPolls;
                        }
                        
                        if (consecutiveEmptyPolls >= effectiveMaxPolls)
                        {
                            Console.WriteLine($"[TTS] Queue empty for {consecutiveEmptyPolls} polls (llmStreaming={llmStillStreaming}), stream complete");
                            break;
                        }
                        
                        try
                        {
                            await Task.Delay(25, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }

                // Determine if we were cancelled or generation changed
                bool generationChanged;
                lock (_streamingLock) { generationChanged = _streamingGeneration != generation; }

                // If cancelled or generation changed, audio was already stopped by CancelStreamingPlayback
                // Just exit without draining
                if (ct.IsCancellationRequested || generationChanged)
                {
                    Console.WriteLine($"[TTS] Streaming stopped - cancelled={ct.IsCancellationRequested}, genChanged={generationChanged}");
                    // Don't touch the shared audio output here - it may already be in use by a new generation
                }
                else
                {
                    // Wait for buffered audio to drain (normal completion)
                    while (provider.BufferedBytes > 0)
                    {
                        // Keep checking if we got pre-empted during drain
                        lock (_streamingLock)
                        {
                            if (_streamingGeneration != generation)
                            {
                                Console.WriteLine($"[TTS] Pre-empted during audio drain");
                                break;
                            }
                        }
                        await Task.Delay(40).ConfigureAwait(false);
                    }
                    
                    // Only stop if we're still the current generation
                    lock (_audioOutputLock)
                    {
                        lock (_streamingLock)
                        {
                            if (_streamingGeneration == generation && _sharedWaveOut == waveOut)
                            {
                                try { waveOut.Stop(); } catch { }
                            }
                        }
                    }
                    try { await Task.WhenAny(playbackStoppedTcs.Task, Task.Delay(200)).ConfigureAwait(false); } catch { }
                }

                Console.WriteLine($"[TTS] Streaming playback complete (gen={generation}): {totalSentencesProcessed} sentences, cancelled={ct.IsCancellationRequested}, genChanged={generationChanged}");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[TTS] Streaming playback cancelled (exception)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TTS] Streaming playback error: {ex.Message}");
            }
            finally
            {
                // Always fire speaking finished if we started
                if (speakingStartedFired)
                {
                    try { OnTtsSpeakingFinished?.Invoke(); } catch { }
                }
                
                // Only clean up if we're still the owner of the shared audio
                lock (_audioOutputLock)
                {
                    if (_sharedWaveOut == waveOut)
                    {
                        // Check if another generation has started
                        bool stillOwner;
                        lock (_streamingLock) { stillOwner = _streamingGeneration == generation; }
                        
                        if (stillOwner)
                        {
                            try { waveOut.Stop(); } catch { }
                            try { waveOut.Dispose(); } catch { }
                            _sharedWaveOut = null;
                            _sharedProvider = null;
                        }
                        // If not still owner, leave the audio device for the new generation
                    }
                }
                
                // Mark playback as inactive
                lock (_streamingLock)
                {
                    _isStreamingPlaybackActive = false;
                }
                
                Console.WriteLine($"[TTS] Streaming playback loop ended (gen={generation})");
            }
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
        private static float[] GenerateAudioInternal(String text, String speaker)
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
                var audio = SynthesizeOne(text, voicePath, snap);
                // Apply peak normalization to prevent clipping
                ApplyPeakNormalize(audio, 0.90f);
                return audio;
            }

            // Cross-fade parameters: 15ms overlap @ 24kHz = 360 samples
            // Increased from 10ms for smoother transitions
            const int crossFadeSamples = 360;
            
            var final = new List<float>(allSegments.Count * 24000); // rough reserve
            float[] prevAudio = null;
            
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
                    prevAudio = null; // reset cross-fade state after pause
                    continue;
                }
                Log("SEG", $"Synth {i+1}/{allSegments.Count} chars={seg.Length}");
                var audio = SynthesizeOne(seg, voicePath, snap);
                Log("SEG", $"Done {i+1}/{allSegments.Count} samples={audio.Length}");
                
                if (audio.Length > 0)
                {
                    // Apply cross-fade with previous segment if both are long enough
                    bool canCrossFade = prevAudio != null && 
                                        prevAudio.Length >= crossFadeSamples * 2 && 
                                        audio.Length >= crossFadeSamples * 2;
                    
                    if (canCrossFade)
                    {
                        // Cross-fade: blend end of prevAudio with start of current audio
                        // The last crossFadeSamples of prevAudio are already in 'final', 
                        // so we need to modify them in-place
                        int startIdx = final.Count - crossFadeSamples;
                        for (int j = 0; j < crossFadeSamples; j++)
                        {
                            // Use cosine fade for smoother transition
                            float t_fade = (float)j / crossFadeSamples;
                            float fadeOut = 0.5f * (1.0f + (float)Math.Cos(Math.PI * t_fade)); // 1.0 -> 0.0 (cosine)
                            float fadeIn = 0.5f * (1.0f - (float)Math.Cos(Math.PI * t_fade));  // 0.0 -> 1.0 (cosine)
                            final[startIdx + j] = final[startIdx + j] * fadeOut + audio[j] * fadeIn;
                        }
                        // Add the rest of current audio (skip the cross-faded portion)
                        for (int j = crossFadeSamples; j < audio.Length; j++)
                        {
                            final.Add(audio[j]);
                        }
                        Log("SEG", $"CrossFade applied: {crossFadeSamples} samples ({crossFadeSamples * 1000.0 / SampleRate:F1}ms)");
                    }
                    else
                    {
                        // No cross-fade (first segment or segments too short)
                        // Add a tiny overlap blend if possible to avoid hard cuts
                        if (prevAudio != null && final.Count > 0 && audio.Length > 0)
                        {
                            // Micro-blend: just blend the last/first sample to avoid click
                            int blendSamples = Math.Min(24, Math.Min(final.Count, audio.Length)); // ~1ms
                            int startIdx = final.Count - blendSamples;
                            for (int j = 0; j < blendSamples; j++)
                            {
                                float t_fade = (float)j / blendSamples;
                                final[startIdx + j] = final[startIdx + j] * (1.0f - t_fade) + audio[j] * t_fade;
                            }
                            // Add rest of audio after blend region
                            for (int j = blendSamples; j < audio.Length; j++)
                            {
                                final.Add(audio[j]);
                            }
                        }
                        else
                        {
                            final.AddRange(audio);
                        }
                    }
                    prevAudio = audio;
                }
                
                // No inter-segment padding needed since we use cross-fade
                // Only add padding if cross-fade wasn't applied AND MinClausePaddingMs is set
                // (This handles cases where segments are too short for cross-fade)
            }
            
            var total = final.ToArray();
            
            // === CRITICAL: Apply peak normalization AFTER all cross-fading ===
            // Cross-fading can push peaks above 1.0, causing clipping artifacts
            ApplyPeakNormalize(total, 0.90f);
            
            Log("GEN", $"Done hash={hash} samples={total.Length} ms={start.ElapsedMilliseconds}");
            return total;
        }
        
        /// <summary>
        /// Normalize audio to prevent clipping. If peak > targetPeak, scale down.
        /// This should be called AFTER cross-fading to catch any peaks created by blending.
        /// </summary>
        private static void ApplyPeakNormalize(float[] samples, float targetPeak = 0.90f)
        {
            if (samples == null || samples.Length == 0) return;
            
            float peak = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float abs = Math.Abs(samples[i]);
                if (abs > peak) peak = abs;
            }
            
            if (peak > targetPeak && peak > 1e-6f)
            {
                float gain = targetPeak / peak;
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] *= gain;
                }
                Log("NORM", $"Peak {peak:F3} > {targetPeak:F3}, applied gain={gain:F3}");
            }
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
        // --- Helper: split text into clause-delimited segments for faster TTS start ---
        // Splits on sentence-ending punctuation AND commas (for clause-level streaming)
        private static List<string> SplitIntoSegments(string text)
        {
            var segments = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return segments;
            var sb = new StringBuilder();
            foreach (var ch in text)
            {
                sb.Append(ch);
                // Split on sentence endings and commas (clause boundaries)
                if (ch == '.' || ch == '!' || ch == '?' || ch == '…' || ch == ',' || ch == ';' || ch == ':')
                {
                    var seg = sb.ToString().Trim();
                    // Only add if segment has meaningful content (avoid empty/tiny segments)
                    if (seg.Length > 1) segments.Add(seg);
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
