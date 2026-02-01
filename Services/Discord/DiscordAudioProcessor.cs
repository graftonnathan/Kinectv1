using System;
using Kinectv1;
using Kinectv1.Voice;

namespace Kinectv1.Discord
{
    /// <summary>
    /// Processes incoming Discord PCM audio (48kHz stereo s16) into 16kHz mono s16 for ASR and returns an RMS level for UI.
    /// </summary>
    internal static class DiscordAudioProcessor
    {
        /// <summary>
        /// Convert incoming Discord PCM to 16kHz mono s16 and compute RMS for level meters.
        /// </summary>
        /// <param name="discordAudio">Raw PCM from Discord.Net AudioInStream (typically 48kHz stereo, 16-bit)</param>
        /// <param name="audioLength">Number of valid bytes in discordAudio</param>
        /// <param name="username">Speaker username (for future per-user processing)</param>
        /// <returns>Tuple: (processed bytes, processed length, rms)</returns>
        public static (byte[] processedAudio, int processedLength, float rmsLevel) ProcessDiscordAudio(byte[] discordAudio, int audioLength, string username = "Discord")
        {
            try
            {
                if (discordAudio == null || audioLength <= 0)
                    return (Array.Empty<byte>(), 0, 0f);

                // Resample to 16kHz mono float[] using AudioUtils
                var mono16k = AudioUtils.Resample48kTo16kMono(discordAudio, audioLength);
                if (mono16k == null || mono16k.Length == 0)
                    return (Array.Empty<byte>(), 0, 0f);

                // Convert float [-1,1] to PCM16 bytes
                var outBytes = new byte[mono16k.Length * 2];
                int b = 0;
                float peak = 0f;
                for (int i = 0; i < mono16k.Length; i++)
                {
                    var f = mono16k[i];
                    if (f > peak) peak = f; else if (-f > peak) peak = -f;
                    if (f > 0.98f) f = 0.98f; else if (f < -0.98f) f = -0.98f;
                    short s = (short)Math.Round(f * 32767f);
                    outBytes[b++] = (byte)(s & 0xFF);
                    outBytes[b++] = (byte)((s >> 8) & 0xFF);
                }

                // Compute RMS on resulting 16k mono PCM
                float rms = AudioUtils.CalculateRms(outBytes, outBytes.Length);
                return (outBytes, outBytes.Length, rms);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DiscordAudioProcessor error: {ex.Message}");
                return (Array.Empty<byte>(), 0, 0f);
            }
        }
    }
}
