using System;

namespace Kinectv1.Mumble
{
    /// <summary>
    /// Audio glue for Mumble: normalizes inbound/outbound audio to the app's STT/TTS contracts.
    /// Inbound: 48 kHz mono/stereo float -> downmix mono -> 16 kHz s16 -> VoiceRecognizer.ProcessExternalAudio
    /// Outbound: future hook for 16 kHz mono s16 -> resample 48 kHz mono float -> adapter send
    /// </summary>
    internal static class MumbleAudioBridge
    {
        // Basic mono downmix from interleaved stereo floats
        private static void DownmixToMono(float[] src, int channels, float[] dst)
        {
            if (channels <= 1)
            {
                Array.Copy(src, dst, Math.Min(src.Length, dst.Length));
                return;
            }

            int frames = src.Length / channels;
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                int baseIdx = i * channels;
                for (int c = 0; c < channels; c++) sum += src[baseIdx + c];
                dst[i] = sum / channels;
            }
        }

        // Simple linear resampler float mono
        private static float[] ResampleLinear(float[] source, int srcRate, int dstRate)
        {
            if (source == null || source.Length == 0 || srcRate == dstRate) return source ?? Array.Empty<float>();
            double ratio = (double)dstRate / srcRate;
            int dstLen = (int)Math.Round(source.Length * ratio);
            var dst = new float[dstLen];
            for (int n = 0; n < dstLen; n++)
            {
                double t = n / ratio;
                int i0 = (int)t;
                int i1 = Math.Min(i0 + 1, source.Length - 1);
                double frac = t - i0;
                dst[n] = (float)((1.0 - frac) * source[i0] + frac * source[i1]);
            }
            return dst;
        }

        private static byte[] FloatMonoToPcm16Le(float[] mono)
        {
            if (mono == null || mono.Length == 0) return Array.Empty<byte>();
            var dst = new byte[mono.Length * 2];
            int b = 0;
            for (int i = 0; i < mono.Length; i++)
            {
                float f = mono[i];
                if (f > 1f) f = 1f; else if (f < -1f) f = -1f;
                short s = (short)Math.Round(f * 32767f);
                dst[b++] = (byte)(s & 0xFF);
                dst[b++] = (byte)((s >> 8) & 0xFF);
            }
            return dst;
        }

        /// <summary>
        /// Entry point for inbound PCM from Mumble in float format at 48 kHz.
        /// </summary>
        public static void OnInboundPcmFloat(float[] pcm, int sampleRate, int channels, string username)
        {
            try
            {
                if (pcm == null || pcm.Length == 0) return;
                if (AppSettings.LoadAudioInMode() != AudioInMode.MumbleVoice) return;
                if (!VoiceRecognizer.IsReady()) return;

                // Downmix to mono if needed
                float[] mono;
                if (channels <= 1)
                    mono = pcm;
                else
                {
                    mono = new float[pcm.Length / channels];
                    DownmixToMono(pcm, channels, mono);
                }

                // Resample to 16 kHz
                int srcRate = sampleRate <= 0 ? 48000 : sampleRate;
                var mono16k = ResampleLinear(mono, srcRate, 16000);
                var bytes16k = FloatMonoToPcm16Le(mono16k);

                // Send to recognizer
                SpeakerIdentifier.SetDiscordSpeakerHint(username ?? "MumbleUser");
                VoiceRecognizer.ProcessExternalAudio(bytes16k, bytes16k.Length, $"Mumble:{username ?? "User"}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MumbleAudioBridge] Inbound error: {ex.Message}");
            }
        }
    }
}
