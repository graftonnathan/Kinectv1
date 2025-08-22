// AudioUtils.cs
using System;
using System.Buffers;

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
        // Use ArrayPool to avoid per-call allocation in hot audio processing paths
        var pooledArray = ArrayPool<float>.Shared.Rent(samples);
        try
        {
            for (int i = 0; i < samples; i++)
            {
                int byteIndex = i * 2;
                if (byteIndex + 1 < bytesRecorded)
                {
                    short sample = (short)(buffer[byteIndex] | (buffer[byteIndex + 1] << 8));
                    pooledArray[i] = sample / 32768.0f;
                }
            }
            
            // Copy to final result array
            var result = new float[samples];
            Array.Copy(pooledArray, result, samples);
            return result;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(pooledArray);
        }
    }
}