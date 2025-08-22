// AudioUtils.cs
using System;
using System.Buffers;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;

public static class AudioUtils
{
    private static float? _cachedVadThreshold = null;
    
    /// <summary>
    /// Get the current VAD threshold from settings (cached for performance)
    /// </summary>
    private static float GetVadThreshold()
    {
        if (!_cachedVadThreshold.HasValue)
        {
            try
            {
                _cachedVadThreshold = Kinectv1.AppSettings.LoadVoiceActivityThreshold();
                Console.WriteLine($"🎙️ VAD threshold loaded: {_cachedVadThreshold.Value:F0}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to load VAD threshold, using default 300: {ex.Message}");
                _cachedVadThreshold = 300f; // Fallback default
            }
        }
        return _cachedVadThreshold.Value;
    }
    
    /// <summary>
    /// Update the cached VAD threshold (call this when settings change)
    /// </summary>
    public static void RefreshVadThreshold()
    {
        _cachedVadThreshold = null;
        GetVadThreshold(); // This will reload and cache the new value
    }

    public static bool IsVoiceActive(byte[] buffer, int bytesRecorded, out float rms)
    {
        // Simple RMS-based voice activity detection
        long sum = 0;
        for (int i = 0; i < bytesRecorded; i += 2)
        {
            if (i + 1 < bytesRecorded)
            {
                short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                sum += sample * sample;
            }
        }
        
        double rmsDouble = Math.Sqrt((double)sum / (bytesRecorded / 2));
        rms = (float)rmsDouble;
        
        // Use configurable threshold instead of hardcoded 500
        float vadThreshold = GetVadThreshold();
        return rms > vadThreshold;
    }

    public static bool IsVoiceActive(byte[] buffer, int bytesRecorded)
    {
        // Overload for backward compatibility
        return IsVoiceActive(buffer, bytesRecorded, out _);
    }

    public static float CalculateRms(byte[] buffer, int bytesRecorded)
    {
        // Calculate RMS level for audio visualization
        long sum = 0;
        for (int i = 0; i < bytesRecorded; i += 2)
        {
            if (i + 1 < bytesRecorded)
            {
                short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                sum += sample * sample;
            }
        }
        
        double rms = Math.Sqrt((double)sum / (bytesRecorded / 2));
        return (float)rms;
    }

    public static float[] ConvertToFloatPcm(byte[] buffer, int bytesRecorded)
    {
        int samples = bytesRecorded / 2;
        float[] floatPcm = new float[samples];
        
        for (int i = 0; i < samples; i++)
        {
            int byteIndex = i * 2;
            if (byteIndex + 1 < bytesRecorded)
            {
                short sample = (short)(buffer[byteIndex] | (buffer[byteIndex + 1] << 8));
                floatPcm[i] = sample / 32768.0f;
            }
        }
        
        return floatPcm;
    }

    /// <summary>
    /// High-quality resampling from 48kHz mono to 16kHz mono with anti-aliasing LPF
    /// For use when audio is already converted to mono
    /// </summary>
    /// <param name="input">48kHz mono float samples</param>
    /// <param name="inputLength">Number of input samples</param>
    /// <returns>16kHz mono samples (approximately inputLength/3)</returns>
    public static float[] ResampleMono48kTo16k(float[] input, int inputLength)
    {
        if (input == null || inputLength <= 0) return new float[0];

        // Pool temporary buffers to reduce allocations
        var lpfBuffer = ArrayPool<float>.Shared.Rent(inputLength);
        try
        {
            // Step 1: Apply anti-aliasing LPF before decimation (cutoff ~7kHz for 16kHz output)
            // Create fresh filter for each call to avoid state contamination
            var lpFilter = BiQuadFilter.LowPassFilter(48000, 7000, 0.707f);
            for (int i = 0; i < inputLength; i++)
            {
                lpfBuffer[i] = lpFilter.Transform(input[i]);
            }

            // Step 2: High-quality resampling using linear interpolation (3:1 ratio)
            int outputLength = inputLength / 3;
            if (outputLength <= 0) return new float[0];
            
            var output = new float[outputLength];
            
            for (int n = 0; n < outputLength; n++)
            {
                double sourceIndex = (double)n * 3.0;
                int i0 = (int)sourceIndex;
                int i1 = Math.Min(i0 + 1, inputLength - 1);
                double frac = sourceIndex - i0;
                
                // Bounds check for safety
                if (i0 < inputLength && i1 < inputLength)
                {
                    // Linear interpolation for better quality than simple decimation
                    output[n] = (float)((1.0 - frac) * lpfBuffer[i0] + frac * lpfBuffer[i1]);
                }
            }

            return output;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(lpfBuffer);
        }
    }

    /// <summary>
    /// High-quality resampling from 48kHz stereo to 16kHz mono with anti-aliasing LPF
    /// Replaces naive decimation to prevent aliasing and improve ASR quality
    /// </summary>
    /// <param name="input">48kHz stereo float samples</param>
    /// <param name="inputLength">Number of input samples</param>
    /// <returns>16kHz mono samples (approximately inputLength/6)</returns>
    public static float[] Resample48kTo16kMono(float[] input, int inputLength)
    {
        if (input == null || inputLength <= 0) return new float[0];

        // Pool temporary buffers to reduce allocations
        var monoBuffer = ArrayPool<float>.Shared.Rent(inputLength / 2);
        try
        {
            // Step 1: Convert stereo to mono (L+R)/2
            int monoLength = inputLength / 2;
            for (int i = 0; i < monoLength; i++)
            {
                monoBuffer[i] = (input[i * 2] + input[i * 2 + 1]) * 0.5f;
            }

            // Step 2: Use the mono resampler
            return ResampleMono48kTo16k(monoBuffer, monoLength);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(monoBuffer);
        }
    }

    /// <summary>
    /// High-quality resampling from 48kHz stereo to 16kHz mono with anti-aliasing LPF (byte array input)
    /// </summary>
    /// <param name="input">48kHz stereo s16 byte samples</param>
    /// <param name="inputLength">Number of input bytes</param>
    /// <returns>16kHz mono samples</returns>
    public static float[] Resample48kTo16kMono(byte[] input, int inputLength)
    {
        if (input == null || inputLength <= 0) return new float[0];
        
        // Ensure we have even number of bytes for 16-bit samples
        if (inputLength % 2 != 0) inputLength--;
        if (inputLength <= 0) return new float[0];

        // Convert s16 bytes to float samples first
        int sampleCount = inputLength / 2;
        var floatInput = ArrayPool<float>.Shared.Rent(sampleCount);
        try
        {
            for (int i = 0; i < sampleCount; i++)
            {
                int byteIndex = i * 2;
                if (byteIndex + 1 < inputLength)
                {
                    // Little-endian s16 conversion
                    short sample = (short)((input[byteIndex + 1] << 8) | input[byteIndex]);
                    floatInput[i] = sample / 32768.0f;
                }
            }

            return Resample48kTo16kMono(floatInput, sampleCount);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floatInput);
        }
    }

    /// <summary>
    /// Frame builder to accumulate samples into consistent 320-sample (20ms at 16kHz) frames
    /// Prevents "chunk too large" issues by ensuring proper frame sizes
    /// Thread-safe implementation for multi-threaded audio processing
    /// </summary>
    public static class FrameBuilder
    {
        private static readonly float[] _frameBuffer = new float[1024]; // Accumulation buffer (allows for 3+ frames buffering)
        private static int _frameBufferLength = 0;
        private static readonly object _frameLock = new object();
        private const int TARGET_FRAME_SIZE = 320; // 20ms at 16kHz
        
        /// <summary>
        /// Add samples to frame buffer and return complete 320-sample frames
        /// </summary>
        /// <param name="samples">Input samples to add</param>
        /// <returns>Array of complete 320-sample frames, or empty if no complete frames available</returns>
        public static float[][] AccumulateFrames(float[] samples)
        {
            if (samples == null || samples.Length == 0) return new float[0][];

            lock (_frameLock)
            {
                var completeFrames = new List<float[]>();
                int inputOffset = 0;

                while (inputOffset < samples.Length)
                {
                    // How much space is left in the current frame?
                    int spaceLeft = TARGET_FRAME_SIZE - _frameBufferLength;
                    int samplesToCopy = Math.Min(spaceLeft, samples.Length - inputOffset);

                    // Bounds check to prevent buffer overflow
                    if (_frameBufferLength + samplesToCopy > _frameBuffer.Length)
                    {
                        Console.WriteLine($"⚠️ FrameBuilder buffer overflow prevented: {_frameBufferLength} + {samplesToCopy} > {_frameBuffer.Length}");
                        break;
                    }

                    // Copy samples into frame buffer
                    Array.Copy(samples, inputOffset, _frameBuffer, _frameBufferLength, samplesToCopy);
                    _frameBufferLength += samplesToCopy;
                    inputOffset += samplesToCopy;

                    // If frame is complete (320 samples), extract it
                    if (_frameBufferLength == TARGET_FRAME_SIZE)
                    {
                        var frame = new float[TARGET_FRAME_SIZE];
                        Array.Copy(_frameBuffer, 0, frame, 0, TARGET_FRAME_SIZE);
                        completeFrames.Add(frame);
                        _frameBufferLength = 0; // Reset for next frame
                    }
                }

                return completeFrames.ToArray();
            }
        }

        /// <summary>
        /// Reset frame builder state (call when switching audio sources or on error)
        /// </summary>
        public static void Reset()
        {
            lock (_frameLock)
            {
                _frameBufferLength = 0;
                Array.Clear(_frameBuffer, 0, _frameBuffer.Length);
            }
        }

        /// <summary>
        /// Get current buffer occupancy for monitoring
        /// </summary>
        public static int GetBufferOccupancy()
        {
            lock (_frameLock)
            {
                return _frameBufferLength;
            }
        }

        /// <summary>
        /// Get frame builder status for debugging
        /// </summary>
        public static string GetStatus()
        {
            lock (_frameLock)
            {
                return $"FrameBuilder: {_frameBufferLength}/{TARGET_FRAME_SIZE} samples buffered " +
                       $"({(float)_frameBufferLength / TARGET_FRAME_SIZE * 100:F1}% of next frame)";
            }
        }
    }
}