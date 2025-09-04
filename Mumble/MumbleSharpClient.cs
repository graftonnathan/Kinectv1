using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Concentus.Structs;
using Concentus.Enums;
using Concentus.Common;
using NAudio.Wave;

namespace Kinectv1.Mumble
{
    // Phase 2: decoder/ingest scaffolding for 48k ? 16k PCM and RMS calculation
    // Note: Real Mumble protocol (MumbleSharp) plumbing will be added in Connect in Phase 3; here we expose helpers.
    internal static class MumbleSharpClient
    {
        private static readonly object _decoderGate = new object();
        private static OpusDecoder _opusDecoder; // 48k mono

        public static void EnsureDecoder()
        {
            lock (_decoderGate)
            {
                if (_opusDecoder == null)
                {
                    _opusDecoder = OpusDecoder.Create(48000, 1);
                }
            }
        }

        public static short[] DecodeOpusToPcm16(byte[] opusFrame, int length)
        {
            EnsureDecoder();
            var pcm = new short[960 * 2]; // up to 40ms safety
            int samples = _opusDecoder.Decode(opusFrame, 0, length, pcm, 0, 960, false);
            return pcm.Take(samples).ToArray();
        }

        public static byte[] Downsample48kTo16kPcm16(short[] pcm48k)
        {
            // Simple linear downsample 48000 -> 16000, mono
            if (pcm48k == null || pcm48k.Length == 0) return Array.Empty<byte>();
            int outSamples = pcm48k.Length / 3; // 48k -> 16k
            float ratio = 3f;
            var outPcm = new short[outSamples];
            for (int i = 0; i < outSamples; i++)
            {
                float srcIndex = i * ratio;
                int i0 = (int)srcIndex;
                int i1 = Math.Min(i0 + 1, pcm48k.Length - 1);
                float frac = srcIndex - i0;
                float v = pcm48k[i0] * (1 - frac) + pcm48k[i1] * frac;
                outPcm[i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, v));
            }
            // Convert to little endian bytes
            var bytes = new byte[outSamples * 2];
            Buffer.BlockCopy(outPcm, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        public static float CalculateRms(byte[] pcm16le, int bytes)
        {
            if (pcm16le == null || bytes <= 0) return 0f;
            int samples = bytes / 2;
            double sum = 0;
            for (int i = 0; i < samples; i++)
            {
                short s = BitConverter.ToInt16(pcm16le, i * 2);
                sum += (s * s);
            }
            double mean = sum / Math.Max(1, samples);
            double rms = Math.Sqrt(mean);
            return (float)rms;
        }
    }
}
