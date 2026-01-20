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

    #region Resampling (Used by DiscordAudioProcessor and WebRTC)

    // Per-source filter state to maintain continuity across frames
    private static readonly ConcurrentDictionary<string, BiQuadFilter> _resamplerFilters = new ConcurrentDictionary<string, BiQuadFilter>();
    
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

    #endregion
}
