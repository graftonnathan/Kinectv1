// AudioUtils.cs
// AudioUtils.cs
using System;
using System.Buffers;
using System.Collections.Concurrent;
using NAudio.Wave;
using NAudio.Dsp;


public static class AudioUtils
{
    private static float? _cachedVoiceAmpThreshold = null; // derived linear PCM threshold (0..32768)

    // Derive threshold from settings Audio.VoiceThreshold (0..1) once and cache
    private static float GetVoiceAmpThreshold()
    {
        if (!_cachedVoiceAmpThreshold.HasValue)
        {
            try
            {
                var vt = Kinectv1.App.SettingsProvider?.Current?.Audio?.VoiceThreshold ?? 0.2; // default from defaults.json
                if (vt < 0) vt = 0; if (vt > 1) vt = 1;
                _cachedVoiceAmpThreshold = (float)(vt * 32768.0); // convert normalized amplitude -> PCM short range
            }
            catch { _cachedVoiceAmpThreshold = (float)(0.2 * 32768.0); }
        }
        return _cachedVoiceAmpThreshold.Value;
    }

    public static void RefreshVoiceThreshold() => _cachedVoiceAmpThreshold = null;

    public static bool IsVoiceActive(byte[] buffer, int bytesRecorded, out float rms)
    {
        long sum = 0; int samples = bytesRecorded / 2; if (samples <= 0) { rms = 0; return false; }
        for (int i = 0; i < bytesRecorded; i += 2)
        {
            if (i + 1 >= bytesRecorded) break;
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            sum += sample * sample;
        }
        double rmsDouble = Math.Sqrt(sum / (double)samples); // raw PCM amplitude (0..32768)
        rms = (float)rmsDouble;
        float thresh = GetVoiceAmpThreshold();
        return rmsDouble >= thresh;
    }

    public static bool IsVoiceActive(byte[] buffer, int bytesRecorded) => IsVoiceActive(buffer, bytesRecorded, out _);

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

    public static float[] ConvertToFloatPcm(byte[] buffer, int bytesRecorded)
    {
        int samples = bytesRecorded / 2;
        var pooled = ArrayPool<float>.Shared.Rent(samples);
        try
        {
            for (int i = 0; i < samples; i++)
            {
                int bi = i * 2; if (bi + 1 >= bytesRecorded) break;
                short sample = (short)(buffer[bi] | (buffer[bi + 1] << 8));
                pooled[i] = sample / 32768.0f;
            }
            var result = new float[samples];
            Array.Copy(pooled, result, samples);
            return result;
        }
        finally { ArrayPool<float>.Shared.Return(pooled); }
    }

    // Per-source filter state to maintain continuity across frames
    private static readonly ConcurrentDictionary<string, BiQuadFilter> _resamplerFilters = new ConcurrentDictionary<string, BiQuadFilter>();
    
    /// <summary>
    /// Get or create a persistent low-pass filter for the given source.
    /// This maintains filter state across frames for smooth audio.
    /// </summary>
    private static BiQuadFilter GetOrCreateFilter(string sourceId)
    {
        return _resamplerFilters.GetOrAdd(sourceId ?? "default", _ => BiQuadFilter.LowPassFilter(48000, 7000, 0.707f));
    }
    
    /// <summary>
    /// Reset the filter state for a source (call when audio stream restarts)
    /// </summary>
    public static void ResetResamplerFilter(string sourceId)
    {
        _resamplerFilters.TryRemove(sourceId ?? "default", out _);
    }

    /// <summary>
    /// Resample MONO 48kHz float audio to 16kHz.
    /// Uses BiQuad low-pass filter at 7kHz (Nyquist for 16kHz) and linear interpolation.
    /// IMPORTANT: Uses per-source persistent filter state for continuity across frames.
    /// </summary>
    public static float[] ResampleMono48kTo16k(float[] input, int inputLength, string sourceId = null)
    {
        if (input == null || inputLength <= 0) return Array.Empty<float>();
        var lpfBuffer = ArrayPool<float>.Shared.Rent(inputLength);
        try
        {
            // Use persistent filter to maintain state across frames
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
    /// Legacy overload without sourceId - creates new filter each time (may cause discontinuities)
    /// </summary>
    public static float[] ResampleMono48kTo16k(float[] input, int inputLength)
    {
        // For backward compatibility, use a shared "legacy" filter
        return ResampleMono48kTo16k(input, inputLength, "legacy");
    }

    /// <summary>
    /// Resample STEREO 48kHz float audio to MONO 16kHz.
    /// First converts stereo to mono by averaging channels, then resamples.
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
    /// Legacy overload without sourceId
    /// </summary>
    public static float[] ResampleStereo48kTo16kMono(float[] input, int inputLength)
    {
        return ResampleStereo48kTo16kMono(input, inputLength, null);
    }

    /// <summary>
    /// Resample STEREO 48kHz PCM16 byte audio to MONO 16kHz float.
    /// For Discord audio which is 48kHz stereo.
    /// </summary>
    public static float[] Resample48kTo16kMono(float[] input, int inputLength)
    {
        // This is the stereo version - kept for backward compatibility with Discord
        return ResampleStereo48kTo16kMono(input, inputLength);
    }

    /// <summary>
    /// Resample STEREO 48kHz PCM16 byte audio to MONO 16kHz float.
    /// IMPORTANT: This expects STEREO input (Discord format: 48kHz stereo 16-bit).
    /// For MONO input (Mumble format), use ResampleMono48kTo16kFromBytes instead.
    /// </summary>
    public static float[] Resample48kTo16kMono(byte[] input, int inputLength, string sourceId = null)
    {
        if (input == null || inputLength <= 0) return Array.Empty<float>();
        if (inputLength % 2 != 0) inputLength--;
        int sampleCount = inputLength / 2; // Total samples (for stereo: L,R,L,R... so sampleCount/2 stereo pairs)
        var floatInput = ArrayPool<float>.Shared.Rent(sampleCount);
        try
        {
            for (int i = 0; i < sampleCount; i++)
            {
                int bi = i * 2; if (bi + 1 >= inputLength) break;
                short sample = (short)((input[bi + 1] << 8) | input[bi]);
                floatInput[i] = sample / 32768.0f;
            }
            // This treats the input as stereo and converts to mono
            return ResampleStereo48kTo16kMono(floatInput, sampleCount, sourceId);
        }
        finally { ArrayPool<float>.Shared.Return(floatInput); }
    }
    
    /// <summary>
    /// Legacy overload without sourceId
    /// </summary>
    public static float[] Resample48kTo16kMono(byte[] input, int inputLength)
    {
        return Resample48kTo16kMono(input, inputLength, null);
    }

    /// <summary>
    /// Resample MONO 48kHz PCM16 byte audio to MONO 16kHz float.
    /// For Mumble audio which is 48kHz mono 16-bit.
    /// Uses persistent filter state for the given source to maintain continuity.
    /// </summary>
    public static float[] ResampleMono48kTo16kFromBytes(byte[] input, int inputLength, string sourceId = null)
    {
        if (input == null || inputLength <= 0) return Array.Empty<float>();
        if (inputLength % 2 != 0) inputLength--;
        int sampleCount = inputLength / 2; // Mono samples
        var floatInput = ArrayPool<float>.Shared.Rent(sampleCount);
        try
        {
            for (int i = 0; i < sampleCount; i++)
            {
                int bi = i * 2; if (bi + 1 >= inputLength) break;
                short sample = (short)((input[bi + 1] << 8) | input[bi]);
                floatInput[i] = sample / 32768.0f;
            }
            // Input is already mono, just resample with persistent filter
            return ResampleMono48kTo16k(floatInput, sampleCount, sourceId);
        }
        finally { ArrayPool<float>.Shared.Return(floatInput); }
    }
    
    /// <summary>
    /// Legacy overload without sourceId
    /// </summary>
    public static float[] ResampleMono48kTo16kFromBytes(byte[] input, int inputLength)
    {
        return ResampleMono48kTo16kFromBytes(input, inputLength, null);
    }

    public static class FrameBuilder
    {
        private static readonly float[] _frameBuffer = new float[1024];
        private static int _frameBufferLength = 0;
        private static readonly object _frameLock = new object();
        private const int TARGET_FRAME_SIZE = 320;
        public static float[][] AccumulateFrames(float[] samples)
        {
            if (samples == null || samples.Length == 0) return Array.Empty<float[]>();
            lock (_frameLock)
            {
                var list = new System.Collections.Generic.List<float[]>();
                int offset = 0;
                while (offset < samples.Length)
                {
                    int space = TARGET_FRAME_SIZE - _frameBufferLength;
                    int toCopy = Math.Min(space, samples.Length - offset);
                    if (_frameBufferLength + toCopy > _frameBuffer.Length) break;
                    Array.Copy(samples, offset, _frameBuffer, _frameBufferLength, toCopy);
                    _frameBufferLength += toCopy; offset += toCopy;
                    if (_frameBufferLength == TARGET_FRAME_SIZE)
                    {
                        var frame = new float[TARGET_FRAME_SIZE];
                        Array.Copy(_frameBuffer, 0, frame, 0, TARGET_FRAME_SIZE);
                        list.Add(frame);
                        _frameBufferLength = 0;
                    }
                }
                return list.ToArray();
            }
        }
        public static void Reset() { lock (_frameLock) { _frameBufferLength = 0; Array.Clear(_frameBuffer, 0, _frameBuffer.Length); } }
        public static int GetBufferOccupancy() { lock (_frameLock) return _frameBufferLength; }
        public static string GetStatus() { lock (_frameLock) return $"FrameBuilder: {_frameBufferLength}/{TARGET_FRAME_SIZE} samples buffered ({(float)_frameBufferLength / TARGET_FRAME_SIZE * 100:F1}% of next frame)"; }
    }
}
