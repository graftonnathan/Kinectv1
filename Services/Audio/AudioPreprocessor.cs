using System;
using System.Collections.Concurrent;
using NAudio.Dsp;

namespace Kinectv1
{
    /// <summary>
    /// Lightweight speech-friendly preprocessing for PCM16 mono audio intended for ASR (Vosk).
    /// Goals:
    /// - Remove DC/rumble (high-pass)
    /// - Reduce hiss (low-pass)
    /// - Gentle, slow AGC to improve far-mic intelligibility without pumping
    /// - Hard limiter to prevent clipping
    ///
    /// This is intentionally conservative to avoid distorting consonants.
    /// </summary>
    public static class AudioPreprocessor
    {
        private sealed class State
        {
            public BiQuadFilter Hp;
            public BiQuadFilter Lp;
            public double SmoothedRms;
            public double Gain; // linear
        }

        private static readonly ConcurrentDictionary<string, State> _states = new();

        public static void Reset(string sourceId)
        {
            if (string.IsNullOrWhiteSpace(sourceId)) sourceId = "default";
            _states.TryRemove(sourceId, out _);
        }

        /// <summary>
        /// Process PCM16 mono bytes in-place.
        /// Returns the same buffer for convenience.
        /// </summary>
        public static byte[] ProcessPcm16MonoInPlace(byte[] pcm16, int bytes, int sampleRate, string sourceId,
            bool enableAgc = true,
            bool enableBandpass = true)
        {
            if (pcm16 == null || bytes <= 0) return pcm16;
            if (bytes % 2 != 0) bytes--; // safety
            if (bytes <= 0) return pcm16;
            if (sampleRate <= 0) sampleRate = 16000;

            if (string.IsNullOrWhiteSpace(sourceId)) sourceId = "default";

            var st = _states.GetOrAdd(sourceId, _ => new State
            {
                // Conservative cutoffs for speech
                Hp = BiQuadFilter.HighPassFilter(sampleRate, 65f, 0.707f),
                Lp = BiQuadFilter.LowPassFilter(sampleRate, 6500f, 0.707f),
                SmoothedRms = 0,
                Gain = 1.0
            });

            // If sample rate changes mid-stream, rebuild filters.
            // (BiQuadFilter instances are stateful.)
            // Not locking: minor race here is acceptable for best-effort audio processing.
            try
            {
                // There is no way to query filter SR, so recreate if likely mismatch.
                // We keep it simple: recreate when sampleRate not 16k (common) or after reset.
                // Call Reset(sourceId) when stream restarts for best results.
                if (sampleRate != 16000)
                {
                    st.Hp = BiQuadFilter.HighPassFilter(sampleRate, 65f, 0.707f);
                    st.Lp = BiQuadFilter.LowPassFilter(sampleRate, 6500f, 0.707f);
                }
            }
            catch { }

            // RMS (float domain) for AGC control
            // Target is intentionally low to avoid lifting noise too much.
            const double targetRms = 0.10;       // ~-20 dBFS
            const double maxGain = 8.0;          // +18.1 dB
            const double minGain = 0.35;         // -9.1 dB
            const double attack = 0.22;          // slightly slower to avoid over-boosting transient noise
            const double release = 0.04;         // slightly slower decay

            // Limiter
            const double limiter = 0.98;

            double sumSq = 0;
            int samples = bytes / 2;

            // First pass: compute RMS quickly (still cheap) from int16
            for (int i = 0; i < bytes; i += 2)
            {
                short s = (short)(pcm16[i] | (pcm16[i + 1] << 8));
                double f = s / 32768.0;
                sumSq += f * f;
            }

            var rms = Math.Sqrt(sumSq / Math.Max(1, samples));
            if (double.IsNaN(rms) || double.IsInfinity(rms)) rms = 0;

            // Smooth RMS so gain changes slowly
            var alpha = rms > st.SmoothedRms ? attack : release;
            st.SmoothedRms = (1.0 - alpha) * st.SmoothedRms + alpha * rms;

            if (enableAgc)
            {
                // Compute desired gain from smoothed RMS
                double desired = st.SmoothedRms > 1e-6 ? (targetRms / st.SmoothedRms) : maxGain;
                if (desired > maxGain) desired = maxGain;
                if (desired < minGain) desired = minGain;

                // Smooth gain changes further
                // (use smaller alpha than RMS smoothing)
                const double gainAlpha = 0.05;
                st.Gain = (1.0 - gainAlpha) * st.Gain + gainAlpha * desired;
            }
            else
            {
                st.Gain = 1.0;
            }

            // Second pass: filter + apply gain + limiter, write back to PCM16
            for (int i = 0; i < bytes; i += 2)
            {
                short s = (short)(pcm16[i] | (pcm16[i + 1] << 8));
                float x = s / 32768f;

                if (enableBandpass)
                {
                    try
                    {
                        x = st.Hp.Transform(x);
                        x = st.Lp.Transform(x);
                    }
                    catch { }
                }

                double y = x * st.Gain;

                // Hard limiter
                if (y > limiter) y = limiter;
                else if (y < -limiter) y = -limiter;

                short o = (short)Math.Round(y * 32767.0);
                pcm16[i] = (byte)(o & 0xFF);
                pcm16[i + 1] = (byte)((o >> 8) & 0xFF);
            }

            return pcm16;
        }
    }
}
