using System;

namespace Kinectv1.Voice
{
    /// <summary>
    /// Represents a normalized audio frame ready for STT processing.
    /// All audio sources should produce frames in this format.
    /// </summary>
    public readonly struct NormalizedAudioFrame
    {
        /// <summary>
        /// PCM16 little-endian mono samples at 16kHz.
        /// </summary>
        public byte[] Pcm16 { get; }

        /// <summary>
        /// Number of valid bytes in Pcm16 array.
        /// </summary>
        public int Length { get; }

        /// <summary>
        /// Pre-computed RMS value (0-10000 scale for UI compatibility).
        /// </summary>
        public float Rms { get; }

        /// <summary>
        /// Source of this audio frame.
        /// </summary>
        public AudioSourceType Source { get; }

        /// <summary>
        /// Optional identifier for the source (e.g., Discord user ID, WebRTC client ID).
        /// </summary>
        public string SourceId { get; }

        /// <summary>
        /// Timestamp when frame was captured (UTC ticks).
        /// </summary>
        public long TimestampTicks { get; }

        /// <summary>
        /// Duration of this frame in milliseconds.
        /// </summary>
        public int DurationMs => Length > 0 ? (Length / 2) * 1000 / 16000 : 0;

        public NormalizedAudioFrame(byte[] pcm16, int length, float rms, AudioSourceType source, string sourceId = null)
        {
            Pcm16 = pcm16 ?? throw new ArgumentNullException(nameof(pcm16));
            Length = length;
            Rms = rms;
            Source = source;
            SourceId = sourceId ?? string.Empty;
            TimestampTicks = DateTime.UtcNow.Ticks;
        }

        /// <summary>
        /// Create a frame from raw PCM16 data, computing RMS automatically.
        /// </summary>
        public static NormalizedAudioFrame Create(byte[] pcm16, int length, AudioSourceType source, string sourceId = null)
        {
            float rms = ComputeRms(pcm16, length);
            return new NormalizedAudioFrame(pcm16, length, rms, source, sourceId);
        }

        /// <summary>
        /// Compute RMS from PCM16 byte buffer (0-10000 scale).
        /// </summary>
        public static float ComputeRms(byte[] buffer, int length)
        {
            if (buffer == null || length <= 0) return 0f;
            int samples = length / 2;
            if (samples == 0) return 0f;

            double sumSquares = 0.0;
            for (int i = 0; i < samples; i++)
            {
                int bi = i * 2;
                if (bi + 1 >= length) break;
                short sample = (short)(buffer[bi] | (buffer[bi + 1] << 8));
                double norm = sample / 32768.0;
                sumSquares += norm * norm;
            }

            double mean = sumSquares / samples;
            return (float)(Math.Sqrt(mean) * 10000.0);
        }

        /// <summary>
        /// Compute RMS from PCM16 short array (0-10000 scale).
        /// </summary>
        public static float ComputeRms(short[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return 0f;
            double sumSq = 0;
            for (int i = 0; i < pcm.Length; i++)
            {
                double n = pcm[i] / 32768.0;
                sumSq += n * n;
            }
            return (float)(Math.Sqrt(sumSq / pcm.Length) * 10000.0);
        }
    }

    /// <summary>
    /// Enumeration of audio source types. Replaces string-based source detection.
    /// </summary>
    public enum AudioSourceType
    {
        /// <summary>Local microphone input.</summary>
        LocalMic,

        /// <summary>Discord voice channel audio.</summary>
        Discord,

        /// <summary>WebRTC browser-based audio.</summary>
        WebRtc,

        /// <summary>Preroll frames being replayed.</summary>
        Preroll,

        /// <summary>Unknown or unspecified source.</summary>
        Unknown
    }

    /// <summary>
    /// Extension methods for audio source handling.
    /// </summary>
    public static class AudioSourceExtensions
    {
        /// <summary>
        /// Check if source is external (not local mic).
        /// External sources skip VAD and feed directly to Vosk.
        /// </summary>
        public static bool IsExternal(this AudioSourceType source)
        {
            return source == AudioSourceType.Discord || source == AudioSourceType.WebRtc;
        }

        /// <summary>
        /// Convert legacy string prefix to AudioSourceType.
        /// </summary>
        public static AudioSourceType FromLegacyPrefix(string source)
        {
            if (string.IsNullOrEmpty(source)) return AudioSourceType.Unknown;

            if (source.StartsWith("webrtc:", StringComparison.OrdinalIgnoreCase))
                return AudioSourceType.WebRtc;
            if (source.StartsWith("Discord:", StringComparison.OrdinalIgnoreCase))
                return AudioSourceType.Discord;
            if (source.Equals("mic", StringComparison.OrdinalIgnoreCase))
                return AudioSourceType.LocalMic;
            if (source.Equals("preroll", StringComparison.OrdinalIgnoreCase))
                return AudioSourceType.Preroll;

            return AudioSourceType.Unknown;
        }

        /// <summary>
        /// Convert AudioSourceType to legacy string prefix for backward compatibility.
        /// </summary>
        public static string ToLegacyPrefix(this AudioSourceType source, string sourceId = null)
        {
            return source switch
            {
                AudioSourceType.WebRtc => $"webrtc:{sourceId ?? "client"}",
                AudioSourceType.Discord => $"Discord:{sourceId ?? "user"}",
                AudioSourceType.LocalMic => "mic",
                AudioSourceType.Preroll => "preroll",
                _ => "external"
            };
        }
    }
}
