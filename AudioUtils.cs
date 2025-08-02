// AudioUtils.cs
using System;

public static class AudioUtils
{
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
        return rms > 500; // Threshold for voice activity
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
}