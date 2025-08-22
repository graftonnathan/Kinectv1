using System;
using System.Buffers;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;

namespace Kinectv1
{
    /// <summary>
    /// Professional Discord audio processing pipeline for high-quality speech recognition
    /// Implements the battle-tested signal chain: 48kHz stereo ? conditioning ? 16kHz mono s16
    /// </summary>
    public static class DiscordAudioProcessor
    {
        // Pipeline configuration
        private const int DISCORD_SAMPLE_RATE = 48000;
        private const int TARGET_SAMPLE_RATE = 16000;
        private const int DISCORD_CHANNELS = 2; // Stereo
        private const int TARGET_CHANNELS = 1;  // Mono
        private const int CHUNK_SIZE_MS = 20;   // 20ms chunks for Vosk
        private const int TARGET_CHUNK_SAMPLES = TARGET_SAMPLE_RATE * CHUNK_SIZE_MS / 1000; // 320 samples at 16kHz

        // Audio processing parameters
        private const float HIGH_PASS_CUTOFF = 80f;      // Remove rumble/DC
        private const float TARGET_RMS_DBFS = -21.5f;    // Target loudness (~-23 to -20 dBFS)
        private const float LIMITER_THRESHOLD_DBFS = -3f; // Hard limit peaks
        private const float NOISE_GATE_THRESHOLD = 0.01f; // Basic noise gate

        // Resampling and filtering state
        private static WaveFormat _discordFormat;
        private static WaveFormat _targetFormat;
        private static BiQuadFilter _highPassFilter;
        
        // Audio processing buffers
        private static float[] _processingBuffer;
        private static float[] _monoBuffer;
        private static float[] _filteredBuffer;
        private static float[] _normalizedBuffer;
        private static byte[] _outputBuffer;
        
        // RMS calculation and normalization
        private static float _currentRms = 0f;
        private static float _targetRms = DbfsToLinear(TARGET_RMS_DBFS);
        private static float _limiterThreshold = DbfsToLinear(LIMITER_THRESHOLD_DBFS);
        
        // Smoothing for gain control
        private static float _smoothedGain = 1.0f;
        private static readonly float GAIN_SMOOTHING_ALPHA = 0.99f; // Slower gain changes
        
        // Statistics and monitoring
        private static int _processedChunks = 0;
        private static float _peakLevel = 0f;
        private static float _averageRms = 0f;
        private static readonly object _statsLock = new object();

        static DiscordAudioProcessor()
        {
            Initialize();
        }

        /// <summary>
        /// Initialize the audio processing pipeline
        /// </summary>
        private static void Initialize()
        {
            try
            {
                Console.WriteLine("?? Initializing professional Discord audio processing pipeline...");
                
                // Define audio formats
                _discordFormat = new WaveFormat(DISCORD_SAMPLE_RATE, 16, DISCORD_CHANNELS); // 48kHz, 16-bit, stereo
                _targetFormat = new WaveFormat(TARGET_SAMPLE_RATE, 16, TARGET_CHANNELS);    // 16kHz, 16-bit, mono
                
                // Initialize high-pass filter (Butterworth, 2nd order)
                _highPassFilter = BiQuadFilter.HighPassFilter(DISCORD_SAMPLE_RATE, HIGH_PASS_CUTOFF, 0.707f);
                
                // Initialize processing buffers
                int maxDiscordSamples = DISCORD_SAMPLE_RATE * CHUNK_SIZE_MS / 1000 * DISCORD_CHANNELS; // 1920 samples for 20ms stereo
                int maxTargetSamples = TARGET_SAMPLE_RATE * CHUNK_SIZE_MS / 1000; // 320 samples for 20ms mono
                
                _processingBuffer = new float[maxDiscordSamples];
                _monoBuffer = new float[maxDiscordSamples / 2];
                _filteredBuffer = new float[maxDiscordSamples / 2];
                _normalizedBuffer = new float[maxDiscordSamples / 2];
                _outputBuffer = new byte[maxTargetSamples * 2]; // 16-bit output
                
                Console.WriteLine("? Discord audio processing pipeline initialized");
                Console.WriteLine($"   Input: {DISCORD_SAMPLE_RATE}Hz, {DISCORD_CHANNELS}ch, 16-bit");
                Console.WriteLine($"   Output: {TARGET_SAMPLE_RATE}Hz, {TARGET_CHANNELS}ch, 16-bit");
                Console.WriteLine($"   Chunk size: {CHUNK_SIZE_MS}ms ({TARGET_CHUNK_SAMPLES} samples)");
                Console.WriteLine($"   High-pass: {HIGH_PASS_CUTOFF}Hz");
                Console.WriteLine($"   Target RMS: {TARGET_RMS_DBFS:F1} dBFS");
                Console.WriteLine($"   Limiter: {LIMITER_THRESHOLD_DBFS:F1} dBFS");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Failed to initialize Discord audio processor: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Process Discord audio through the professional signal chain
        /// Input: 48kHz, s16, stereo from Discord (~20ms bursts)
        /// Output: 16kHz, s16, mono for Vosk (320 samples per chunk)
        /// </summary>
        public static (byte[] processedAudio, int processedLength, float rmsLevel) ProcessDiscordAudio(
            byte[] discordAudio, 
            int audioLength, 
            string username = "Discord")
        {
            using (var scope = Telemetry.LatencyScope("discord_audio_process"))
            {
                try
                {
                    if (discordAudio == null || audioLength <= 0)
                    {
                        Telemetry.Counter("discord_audio.null_input");
                        return (null, 0, 0f);
                    }

                    // Count processed chunks
                    Telemetry.Counter("discord_audio.chunks_processed");

                    // Step 1: Convert s16 to float for processing
                    int sampleCount = audioLength / 2; // 16-bit = 2 bytes per sample
                    if (sampleCount > _processingBuffer.Length)
                    {
                        Console.WriteLine($"?? Discord audio chunk too large: {sampleCount} samples (max: {_processingBuffer.Length})");
                        Telemetry.Counter("discord_audio.oversized_chunks");
                        sampleCount = _processingBuffer.Length;
                        audioLength = sampleCount * 2;
                    }

                    // Convert bytes to float samples
                    for (int i = 0; i < sampleCount; i++)
                    {
                        short sample = (short)((discordAudio[i * 2 + 1] << 8) | discordAudio[i * 2]);
                        _processingBuffer[i] = sample / 32768.0f; // Normalize to [-1.0, 1.0]
                    }

                    // Step 2: Convert stereo to mono (L+R)/2
                    int monoSampleCount = sampleCount / 2;
                    for (int i = 0; i < monoSampleCount; i++)
                    {
                        _monoBuffer[i] = (_processingBuffer[i * 2] + _processingBuffer[i * 2 + 1]) * 0.5f;
                    }

                    // Step 3: High-pass filter to remove rumble/DC
                    using (var hpScope = Telemetry.LatencyScope("discord_audio.hp_filter"))
                    {
                        for (int i = 0; i < monoSampleCount; i++)
                        {
                            _filteredBuffer[i] = _highPassFilter.Transform(_monoBuffer[i]);
                        }
                    }

                    // Step 4: Calculate RMS and normalize to target loudness
                    float rms = CalculateRms(_filteredBuffer, monoSampleCount);
                    _currentRms = rms;

                    // Track RMS levels for monitoring
                    Telemetry.Accumulator("discord_audio.rms_total", rms);
                    if (rms > 0.001f)
                    {
                        Telemetry.Accumulator("discord_audio.rms_sum", rms);
                        Telemetry.Counter("discord_audio.rms_count");
                    }

                    // Apply adaptive gain control
                    float targetGain = (rms > 0.001f) ? (_targetRms / rms) : 1.0f;
                    
                    // Clamp gain to reasonable limits (prevent over-amplification)
                    targetGain = Math.Max(0.1f, Math.Min(10.0f, targetGain));
                    
                    // Smooth gain changes to prevent artifacts
                    _smoothedGain = GAIN_SMOOTHING_ALPHA * _smoothedGain + (1.0f - GAIN_SMOOTHING_ALPHA) * targetGain;

                    // Step 5: Apply gain and soft limiter
                    using (var normalizeScope = Telemetry.LatencyScope("discord_audio.normalize"))
                    {
                        int clippedSamples = 0;
                        for (int i = 0; i < monoSampleCount; i++)
                        {
                            float sample = _filteredBuffer[i] * _smoothedGain;
                            
                            // Soft limiter to prevent clipping
                            if (Math.Abs(sample) > _limiterThreshold)
                            {
                                sample = Math.Sign(sample) * SoftLimit(Math.Abs(sample), _limiterThreshold);
                                clippedSamples++;
                            }
                            
                            _normalizedBuffer[i] = sample;
                        }
                        
                        // Track clipping
                        if (clippedSamples > 0)
                        {
                            Telemetry.Counter("discord_audio.clip_count");
                            Telemetry.Accumulator("discord_audio.clipped_samples", clippedSamples);
                        }
                    }

                    // Step 6: High-quality resample from 48kHz to 16kHz with anti-aliasing LPF
                    float[] resampledSamples;
                    using (var resampleScope = Telemetry.LatencyScope("discord_audio.resample"))
                    {
                        // Use quality resampler with anti-aliasing instead of naive decimation
                        resampledSamples = AudioUtils.Resample48kTo16kMono(_normalizedBuffer, monoSampleCount);
                    }

                    // Step 7: Frame building to ensure consistent 320-sample chunks
                    float[][] completeFrames;
                    using (var frameScope = Telemetry.LatencyScope("discord_audio.frame_build"))
                    {
                        completeFrames = AudioUtils.FrameBuilder.AccumulateFrames(resampledSamples);
                    }

                    // Process each complete frame (should be 320 samples each)
                    if (completeFrames.Length > 0)
                    {
                        // For now, process the first complete frame (could batch process multiple frames)
                        var frame = completeFrames[0];
                        int outputByteCount = frame.Length * 2;
                        
                        if (outputByteCount > _outputBuffer.Length)
                        {
                            Telemetry.Counter("discord_audio.output_buffer_overflow");
                            outputByteCount = _outputBuffer.Length;
                            frame = frame.Take(outputByteCount / 2).ToArray();
                        }

                        // Convert frame to s16 for Vosk
                        for (int i = 0; i < frame.Length; i++)
                        {
                            float sample = Math.Max(-1.0f, Math.Min(1.0f, frame[i])); // Clamp
                            short intSample = (short)(sample * 32767.0f);
                            
                            _outputBuffer[i * 2] = (byte)(intSample & 0xFF);
                            _outputBuffer[i * 2 + 1] = (byte)((intSample >> 8) & 0xFF);
                        }

                        // Track frame metrics
                        Telemetry.Accumulator("discord_audio.frame_size", frame.Length);
                        if (completeFrames.Length > 1)
                        {
                            Telemetry.Accumulator("discord_audio.frames_pending", completeFrames.Length - 1);
                        }

                        // Update statistics
                        UpdateStatistics(rms, _smoothedGain);

                        // Calculate final RMS for UI display (scale to expected range)
                        float displayRms = rms * 3000f; // Scale for UI compatibility

                        // Minimal logging - only show every 100 chunks or significant events
                        if (_processedChunks % 100 == 0)
                        {
                            Console.WriteLine($"?? Discord Audio: {_processedChunks} chunks processed, RMS: {LinearToDbfs(rms):+0.1f}dB, Frame size: {frame.Length}");
                        }

                        return (_outputBuffer, outputByteCount, displayRms);
                    }
                    else
                    {
                        // No complete frames available yet, return empty
                        Telemetry.Counter("discord_audio.incomplete_frames");
                        return (null, 0, 0f);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"? Discord audio processing failed: {ex.Message}");
                    Telemetry.Counter("discord_audio.processing_errors");
                    Telemetry.Event("discord_audio.error", new { error = ex.Message }, TelemetryLevel.Error);
                    return (null, 0, 0f);
                }
            }
        }

        /// <summary>
        /// Calculate RMS level of audio buffer
        /// </summary>
        private static float CalculateRms(float[] buffer, int length)
        {
            if (buffer == null || length <= 0) return 0f;
            
            double sum = 0.0;
            for (int i = 0; i < length; i++)
            {
                sum += buffer[i] * buffer[i];
            }
            
            return (float)Math.Sqrt(sum / length);
        }

        /// <summary>
        /// Soft limiter function
        /// </summary>
        private static float SoftLimit(float input, float threshold)
        {
            if (input <= threshold) return input;
            
            // Soft knee compression above threshold
            float ratio = 0.3f; // Compression ratio
            float excess = input - threshold;
            return threshold + excess * ratio;
        }

        /// <summary>
        /// Convert dBFS to linear scale
        /// </summary>
        private static float DbfsToLinear(float dbfs)
        {
            return (float)Math.Pow(10.0, dbfs / 20.0);
        }

        /// <summary>
        /// Convert linear scale to dBFS
        /// </summary>
        private static float LinearToDbfs(float linear)
        {
            if (linear <= 0f) return -100f; // Very quiet
            return 20.0f * (float)Math.Log10(linear);
        }

        /// <summary>
        /// Update processing statistics
        /// </summary>
        private static void UpdateStatistics(float rms, float gain)
        {
            lock (_statsLock)
            {
                _processedChunks++;
                _peakLevel = Math.Max(_peakLevel, rms);
                _averageRms = (_averageRms * (_processedChunks - 1) + rms) / _processedChunks;
            }
        }

        /// <summary>
        /// Get processing statistics for monitoring
        /// </summary>
        public static (int processedChunks, float currentRms, float averageRms, float peakLevel, float currentGain) GetStatistics()
        {
            lock (_statsLock)
            {
                return (_processedChunks, _currentRms, _averageRms, _peakLevel, _smoothedGain);
            }
        }

        /// <summary>
        /// Reset processing statistics
        /// </summary>
        public static void ResetStatistics()
        {
            lock (_statsLock)
            {
                _processedChunks = 0;
                _peakLevel = 0f;
                _averageRms = 0f;
                _currentRms = 0f;
                _smoothedGain = 1.0f;
            }
            
            Console.WriteLine("?? Discord audio processing statistics reset");
        }

        /// <summary>
        /// Check if the processor is properly initialized
        /// </summary>
        public static bool IsInitialized()
        {
            return _outputBuffer != null && _highPassFilter != null;
        }

        /// <summary>
        /// Get detailed status for debugging
        /// </summary>
        public static string GetProcessorStatus()
        {
            var (chunks, currentRms, avgRms, peak, gain) = GetStatistics();
            
            return $"?? Discord Audio Processor Status:\n" +
                   $"   Initialized: {IsInitialized()}\n" +
                   $"   Processed chunks: {chunks}\n" +
                   $"   Current RMS: {LinearToDbfs(currentRms):+0.1f} dBFS\n" +
                   $"   Average RMS: {LinearToDbfs(avgRms):+0.1f} dBFS\n" +
                   $"   Peak level: {LinearToDbfs(peak):+0.1f} dBFS\n" +
                   $"   Current gain: {gain:F2}x ({LinearToDbfs(gain):+0.1f} dB)\n" +
                   $"   Target RMS: {TARGET_RMS_DBFS:+0.1f} dBFS\n" +
                   $"   Limiter threshold: {LIMITER_THRESHOLD_DBFS:+0.1f} dBFS";
        }

        /// <summary>
        /// Cleanup resources
        /// </summary>
        public static void Dispose()
        {
            try
            {
                // Reset filter state
                if (_highPassFilter != null)
                {
                    _highPassFilter = null;
                }
                
                Console.WriteLine("?? Discord audio processor disposed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error disposing Discord audio processor: {ex.Message}");
            }
        }
    }
}