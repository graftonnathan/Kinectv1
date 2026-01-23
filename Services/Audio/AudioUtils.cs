// AudioUtils.cs
// Audio utility methods for resampling and format conversion
using System;
using System.Buffers;
using System.Collections.Concurrent;
using NAudio.Dsp;

public static class AudioUtils
{
    /// <summary>
    /// Calculate RMS from PCM16 buffer (raw PCM amplitude 0-32768 scale).
    /// </summary>
    public static float CalculateRms(byte[] buffer, int bytesRecorded)
    {
        long sum = 0; int samples = bytesRecorded / 2; if (samples <= 0) return 0f;
        for (int i = 0; i < bytesRecorded; i += 2)
        {
            if (i + 1 >= bytesRecorded) break;
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            sum += sample * sample;
        }
        double rms = Math.Sqrt(sum / (double)samples);
        return (float)rms;
    }

    #region Audio Normalization

    // Per-source gain state for smooth AGC
    private static readonly ConcurrentDictionary<string, float> _sourceGains = new();
    private static readonly ConcurrentDictionary<string, float> _sourceRmsHistory = new();
    
    // Configurable normalization settings (loaded from DebugSettings)
    private static volatile bool _normalizationEnabled = true;
    private static volatile float _targetRms = 3000f;
    private static volatile float _maxGain = 8.0f;
    
    /// <summary>
    /// Minimum gain (never reduce below original level).
    /// </summary>
    public const float MIN_GAIN = 1.0f;
    
    /// <summary>
    /// Smoothing factor for gain changes (0-1, higher = faster response).
    /// </summary>
    public const float GAIN_SMOOTHING = 0.1f;

    /// <summary>
    /// Update normalization settings from DebugSettings.
    /// Called when settings are saved.
    /// </summary>
    public static void UpdateNormalizationSettings(bool enabled, float targetRms, float maxGain)
    {
        _normalizationEnabled = enabled;
        _targetRms = Math.Max(500f, Math.Min(10000f, targetRms));
        _maxGain = Math.Max(1f, Math.Min(20f, maxGain));
        
        Console.WriteLine($"[AudioUtils] Normalization settings updated: enabled={enabled}, targetRms={_targetRms:F0}, maxGain={_maxGain:F1}x");
        
        // Clear gain state so it starts fresh with new settings
        _sourceGains.Clear();
        _sourceRmsHistory.Clear();
    }

    /// <summary>
    /// Load normalization settings from DebugSettings on startup.
    /// </summary>
    public static void LoadNormalizationSettingsFromConfig()
    {
        try
        {
            var cfg = Kinectv1.App.SettingsProvider?.Current?.Debug;
            if (cfg != null)
            {
                _normalizationEnabled = cfg.WebRtcNormalizationEnabled;
                _targetRms = Math.Max(500f, Math.Min(10000f, cfg.WebRtcNormalizationTargetRms));
                _maxGain = Math.Max(1f, Math.Min(20f, cfg.WebRtcNormalizationMaxGain));
                Console.WriteLine($"[AudioUtils] Normalization loaded from settings: enabled={_normalizationEnabled}, targetRms={_targetRms:F0}, maxGain={_maxGain:F1}x");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AudioUtils] Failed to load normalization settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if normalization is currently enabled.
    /// </summary>
    public static bool IsNormalizationEnabled => _normalizationEnabled;

    /// <summary>
    /// Normalize PCM16 audio to a target RMS level with smooth gain adjustment.
    /// This is safe for STT as it applies consistent gain across frames.
    /// </summary>
    /// <param name="pcm16">PCM16 little-endian mono audio (modified in place)</param>
    /// <param name="length">Number of valid bytes</param>
    /// <param name="sourceId">Source ID for per-source gain tracking</param>
    /// <param name="targetRms">Target RMS level (0-32768 scale), uses configured default if not specified</param>
    /// <returns>The gain that was applied (1.0 if normalization is disabled)</returns>
    public static float NormalizePcm16InPlace(byte[] pcm16, int length, string sourceId = null, float? targetRms = null)
    {
        // Check if normalization is enabled
        if (!_normalizationEnabled)
        {
            return 1.0f;
        }
        
        if (pcm16 == null || length < 2) return 1.0f;
        
        var key = sourceId ?? "default";
        float effectiveTargetRms = targetRms ?? _targetRms;
        float effectiveMaxGain = _maxGain;
        
        // Calculate current RMS
        float currentRms = CalculateRms(pcm16, length);
        
        // Skip normalization for very quiet frames (likely silence)
        if (currentRms < 50f)
        {
            return 1.0f;
        }
        
        // Smooth the RMS measurement to avoid gain pumping
        float smoothedRms = _sourceRmsHistory.GetOrAdd(key, currentRms);
        smoothedRms = smoothedRms * 0.7f + currentRms * 0.3f;
        _sourceRmsHistory[key] = smoothedRms;
        
        // Calculate desired gain
        float desiredGain = effectiveTargetRms / Math.Max(smoothedRms, 1f);
        desiredGain = Math.Max(MIN_GAIN, Math.Min(effectiveMaxGain, desiredGain));
        
        // Smooth gain changes to prevent artifacts
        float currentGain = _sourceGains.GetOrAdd(key, 1.0f);
        float newGain = currentGain + (desiredGain - currentGain) * GAIN_SMOOTHING;
        _sourceGains[key] = newGain;
        
        // Apply gain if significant
        if (Math.Abs(newGain - 1.0f) < 0.05f)
        {
            return 1.0f; // Skip processing if gain is near unity
        }
        
        int samples = length / 2;
        for (int i = 0; i < samples; i++)
        {
            int bi = i * 2;
            if (bi + 1 >= length) break;
            
            short sample = (short)(pcm16[bi] | (pcm16[bi + 1] << 8));
            float amplified = sample * newGain;
            
            // Soft clipping to prevent harsh distortion
            if (amplified > 32000f) amplified = 32000f + (amplified - 32000f) * 0.1f;
            else if (amplified < -32000f) amplified = -32000f + (amplified + 32000f) * 0.1f;
            
            // Hard clip at limits
            if (amplified > 32767f) amplified = 32767f;
            else if (amplified < -32768f) amplified = -32768f;
            
            short output = (short)amplified;
            pcm16[bi] = (byte)(output & 0xFF);
            pcm16[bi + 1] = (byte)((output >> 8) & 0xFF);
        }
        
        return newGain;
    }
    
    /// <summary>
    /// Reset gain state for a source (call when audio stream restarts).
    /// </summary>
    public static void ResetGainState(string sourceId)
    {
        var key = sourceId ?? "default";
        _sourceGains.TryRemove(key, out _);
        _sourceRmsHistory.TryRemove(key, out _);
    }

    #endregion

    #region Resampling (Used by DiscordAudioProcessor and WebRTC)

    // Per-source filter state to maintain continuity across frames
    private static readonly ConcurrentDictionary<string, BiQuadFilter> _resamplerFilters = new ConcurrentDictionary<string, BiQuadFilter>();
    
    // Per-source filter for WebRTC (variable sample rates)
    private static readonly ConcurrentDictionary<string, BiQuadFilter> _webRtcFilters = new ConcurrentDictionary<string, BiQuadFilter>();
    
    private static BiQuadFilter GetOrCreateFilter(string sourceId)
    {
        return _resamplerFilters.GetOrAdd(sourceId ?? "default", _ => BiQuadFilter.LowPassFilter(48000, 7000, 0.707f));
    }
    
    private static BiQuadFilter GetOrCreateWebRtcFilter(string sourceId, int sampleRate)
    {
        var key = $"{sourceId ?? "default"}_{sampleRate}";
        // Nyquist frequency for target (16kHz) is 8kHz, so filter at 7kHz to avoid aliasing
        return _webRtcFilters.GetOrAdd(key, _ => BiQuadFilter.LowPassFilter(sampleRate, 7000, 0.707f));
    }
    
    /// <summary>
    /// Reset the filter state for a source (call when audio stream restarts)
    /// </summary>
    public static void ResetResamplerFilter(string sourceId)
    {
        _resamplerFilters.TryRemove(sourceId ?? "default", out _);
        
        // Also clear any WebRTC filters for this source
        var keysToRemove = new System.Collections.Generic.List<string>();
        foreach (var key in _webRtcFilters.Keys)
        {
            if (key.StartsWith((sourceId ?? "default") + "_"))
                keysToRemove.Add(key);
        }
        foreach (var key in keysToRemove)
            _webRtcFilters.TryRemove(key, out _);
    }

    /// <summary>
    /// Resample MONO 48kHz float audio to 16kHz.
    /// </summary>
    public static float[] ResampleMono48kTo16k(float[] input, int inputLength, string sourceId = null)
    {
        if (input == null || inputLength <= 0) return Array.Empty<float>();
        var lpfBuffer = ArrayPool<float>.Shared.Rent(inputLength);
        try
        {
            var lp = GetOrCreateFilter(sourceId);
            for (int i = 0; i < inputLength; i++) lpfBuffer[i] = lp.Transform(input[i]);
            int outputLength = inputLength / 3; if (outputLength <= 0) return Array.Empty<float>();
            var output = new float[outputLength];
            for (int n = 0; n < outputLength; n++)
            {
                double src = n * 3.0; int i0 = (int)src; int i1 = Math.Min(i0 + 1, inputLength - 1); double frac = src - i0;
                output[n] = (float)((1 - frac) * lpfBuffer[i0] + frac * lpfBuffer[i1]);
            }
            return output;
        }
        finally { ArrayPool<float>.Shared.Return(lpfBuffer); }
    }

    /// <summary>
    /// Resample STEREO 48kHz float audio to MONO 16kHz.
    /// </summary>
    public static float[] ResampleStereo48kTo16kMono(float[] input, int inputLength, string sourceId = null)
    {
        if (input == null || inputLength <= 0) return Array.Empty<float>();
        var monoBuf = ArrayPool<float>.Shared.Rent(inputLength / 2);
        try
        {
            int monoLen = inputLength / 2;
            for (int i = 0; i < monoLen; i++) monoBuf[i] = (input[i * 2] + input[i * 2 + 1]) * 0.5f;
            return ResampleMono48kTo16k(monoBuf, monoLen, sourceId);
        }
        finally { ArrayPool<float>.Shared.Return(monoBuf); }
    }

    /// <summary>
    /// Resample STEREO 48kHz PCM16 byte audio to MONO 16kHz float.
    /// For Discord audio which is 48kHz stereo 16-bit.
    /// </summary>
    public static float[] Resample48kTo16kMono(byte[] input, int inputLength, string sourceId = null)
    {
        if (input == null || inputLength <= 0) return Array.Empty<float>();
        if (inputLength % 2 != 0) inputLength--;
        int sampleCount = inputLength / 2;
        var floatInput = ArrayPool<float>.Shared.Rent(sampleCount);
        try
        {
            for (int i = 0; i < sampleCount; i++)
            {
                int bi = i * 2; if (bi + 1 >= inputLength) break;
                short sample = (short)((input[bi + 1] << 8) | input[bi]);
                floatInput[i] = sample / 32768.0f;
            }
            return ResampleStereo48kTo16kMono(floatInput, sampleCount, sourceId);
        }
        finally { ArrayPool<float>.Shared.Return(floatInput); }
    }

    /// <summary>
    /// Resample MONO PCM16 audio from any sample rate to 16kHz.
    /// Used for WebRTC audio which can be at various sample rates (44100, 48000, etc.).
    /// </summary>
    /// <param name="pcm16">PCM16 little-endian mono audio</param>
    /// <param name="length">Number of valid bytes</param>
    /// <param name="sourceSampleRate">Source sample rate (e.g., 44100, 48000)</param>
    /// <param name="sourceId">Optional source ID for filter state tracking</param>
    /// <returns>PCM16 bytes resampled to 16kHz, or original if already 16kHz</returns>
    public static byte[] ResampleMonoTo16k(byte[] pcm16, int length, int sourceSampleRate, string sourceId = null)
    {
        if (pcm16 == null || length <= 0) return Array.Empty<byte>();
        
        // Already at target rate - return copy
        if (sourceSampleRate == 16000)
        {
            var copy = new byte[length];
            Buffer.BlockCopy(pcm16, 0, copy, 0, length);
            return copy;
        }
        
        // Reject unreasonable sample rates
        if (sourceSampleRate < 8000 || sourceSampleRate > 192000)
        {
            Console.WriteLine($"[AudioUtils] Warning: Unusual sample rate {sourceSampleRate}, treating as 48kHz");
            sourceSampleRate = 48000;
        }
        
        int inputSamples = length / 2;
        if (inputSamples <= 0) return Array.Empty<byte>();
        
        // Calculate resampling ratio
        double ratio = (double)sourceSampleRate / 16000.0;
        int outputSamples = (int)(inputSamples / ratio);
        if (outputSamples <= 0) return Array.Empty<byte>();
        
        // Convert to float and apply low-pass filter (anti-aliasing)
        var floatInput = ArrayPool<float>.Shared.Rent(inputSamples);
        var lpfBuffer = ArrayPool<float>.Shared.Rent(inputSamples);
        try
        {
            // Convert PCM16 to float
            for (int i = 0; i < inputSamples; i++)
            {
                int bi = i * 2;
                if (bi + 1 >= length) break;
                short sample = (short)(pcm16[bi] | (pcm16[bi + 1] << 8));
                floatInput[i] = sample / 32768.0f;
            }
            
            // Apply low-pass filter to prevent aliasing
            var lp = GetOrCreateWebRtcFilter(sourceId, sourceSampleRate);
            for (int i = 0; i < inputSamples; i++)
            {
                lpfBuffer[i] = lp.Transform(floatInput[i]);
            }
            
            // Resample using linear interpolation
            var output = new byte[outputSamples * 2];
            for (int n = 0; n < outputSamples; n++)
            {
                double srcIdx = n * ratio;
                int i0 = (int)srcIdx;
                int i1 = Math.Min(i0 + 1, inputSamples - 1);
                double frac = srcIdx - i0;
                
                float sample = (float)((1 - frac) * lpfBuffer[i0] + frac * lpfBuffer[i1]);
                
                // Clamp to prevent overflow
                if (sample > 0.98f) sample = 0.98f;
                else if (sample < -0.98f) sample = -0.98f;
                
                short s16 = (short)(sample * 32767f);
                output[n * 2] = (byte)(s16 & 0xFF);
                output[n * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
            }
            
            return output;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floatInput);
            ArrayPool<float>.Shared.Return(lpfBuffer);
        }
    }

    /// <summary>
    /// Resample MONO short[] audio from any sample rate to 16kHz.
    /// Used for WebRTC audio which can be at various sample rates.
    /// </summary>
    /// <param name="pcm16">PCM16 mono samples</param>
    /// <param name="sourceSampleRate">Source sample rate</param>
    /// <param name="sourceId">Optional source ID for filter state tracking</param>
    /// <returns>PCM16 samples resampled to 16kHz</returns>
    public static short[] ResampleMonoTo16k(short[] pcm16, int sourceSampleRate, string sourceId = null)
    {
        if (pcm16 == null || pcm16.Length == 0) return Array.Empty<short>();
        
        // Already at target rate
        if (sourceSampleRate == 16000)
        {
            var copy = new short[pcm16.Length];
            Array.Copy(pcm16, copy, pcm16.Length);
            return copy;
        }
        
        // Reject unreasonable sample rates
        if (sourceSampleRate < 8000 || sourceSampleRate > 192000)
        {
            Console.WriteLine($"[AudioUtils] Warning: Unusual sample rate {sourceSampleRate}, treating as 48kHz");
            sourceSampleRate = 48000;
        }
        
        int inputSamples = pcm16.Length;
        double ratio = (double)sourceSampleRate / 16000.0;
        int outputSamples = (int)(inputSamples / ratio);
        if (outputSamples <= 0) return Array.Empty<short>();
        
        var floatInput = ArrayPool<float>.Shared.Rent(inputSamples);
        var lpfBuffer = ArrayPool<float>.Shared.Rent(inputSamples);
        try
        {
            // Convert to float
            for (int i = 0; i < inputSamples; i++)
            {
                floatInput[i] = pcm16[i] / 32768.0f;
            }
            
            // Apply low-pass filter
            var lp = GetOrCreateWebRtcFilter(sourceId, sourceSampleRate);
            for (int i = 0; i < inputSamples; i++)
            {
                lpfBuffer[i] = lp.Transform(floatInput[i]);
            }
            
            // Resample
            var output = new short[outputSamples];
            for (int n = 0; n < outputSamples; n++)
            {
                double srcIdx = n * ratio;
                int i0 = (int)srcIdx;
                int i1 = Math.Min(i0 + 1, inputSamples - 1);
                double frac = srcIdx - i0;
                
                float sample = (float)((1 - frac) * lpfBuffer[i0] + frac * lpfBuffer[i1]);
                
                if (sample > 0.98f) sample = 0.98f;
                else if (sample < -0.98f) sample = -0.98f;
                
                output[n] = (short)(sample * 32767f);
            }
            
            return output;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floatInput);
            ArrayPool<float>.Shared.Return(lpfBuffer);
        }
    }

    #endregion
}
