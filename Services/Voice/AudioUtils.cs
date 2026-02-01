using System;

namespace Kinectv1.Voice
{
    /// <summary>
    /// Audio utility functions for resampling and format conversion.
    /// </summary>
    public static class AudioUtils
    {
        /// <summary>
        /// Resample mono PCM audio to 16kHz using simple linear interpolation.
        /// </summary>
        /// <param name="input">Input PCM bytes (16-bit little-endian)</param>
        /// <param name="inputLength">Number of valid bytes in input</param>
        /// <param name="inputSampleRate">Sample rate of input audio</param>
        /// <param name="sourceId">Source identifier for logging</param>
        /// <returns>Resampled PCM bytes at 16kHz</returns>
        public static byte[] ResampleMonoTo16k(byte[] input, int inputLength, int inputSampleRate, string sourceId)
        {
            if (inputSampleRate == 16000)
                return input;

            // Convert bytes to shorts
            var inputSamples = inputLength / 2;
            var inputShorts = new short[inputSamples];
            Buffer.BlockCopy(input, 0, inputShorts, 0, inputLength);

            // Calculate output size
            var outputSamples = (int)(inputSamples * 16000L / inputSampleRate);
            var outputShorts = new short[outputSamples];

            // Linear interpolation resampling
            for (int i = 0; i < outputSamples; i++)
            {
                var srcIndex = i * (double)inputSampleRate / 16000.0;
                var index = (int)srcIndex;
                var frac = srcIndex - index;

                if (index >= inputSamples - 1)
                {
                    outputShorts[i] = inputShorts[inputSamples - 1];
                }
                else
                {
                    var s1 = inputShorts[index];
                    var s2 = inputShorts[index + 1];
                    outputShorts[i] = (short)(s1 + (s2 - s1) * frac);
                }
            }

            // Convert back to bytes
            var outputBytes = new byte[outputShorts.Length * 2];
            Buffer.BlockCopy(outputShorts, 0, outputBytes, 0, outputBytes.Length);

            return outputBytes;
        }

        /// <summary>
        /// Calculate RMS (Root Mean Square) of PCM16 audio data.
        /// Returns value in 0-10000 scale for UI compatibility.
        /// </summary>
        public static float CalculateRms(byte[] pcm16, int length)
        {
            if (pcm16 == null || length <= 0) return 0f;
            
            int samples = length / 2;
            if (samples == 0) return 0f;

            double sumSquares = 0.0;
            for (int i = 0; i < samples; i++)
            {
                int bi = i * 2;
                if (bi + 1 >= length) break;
                short sample = (short)(pcm16[bi] | (pcm16[bi + 1] << 8));
                double norm = sample / 32768.0;
                sumSquares += norm * norm;
            }

            return (float)(Math.Sqrt(sumSquares / samples) * 10000.0);
        }

        /// <summary>
        /// Calculate RMS from short array.
        /// Returns value in 0-10000 scale.
        /// </summary>
        public static float CalculateRms(short[] pcm)
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

        /// <summary>
        /// Convert PCM16 byte array to short array.
        /// </summary>
        public static short[] BytesToShorts(byte[] bytes, int length)
        {
            var samples = length / 2;
            var shorts = new short[samples];
            Buffer.BlockCopy(bytes, 0, shorts, 0, length);
            return shorts;
        }

        /// <summary>
        /// Convert short array to PCM16 byte array.
        /// </summary>
        public static byte[] ShortsToBytes(short[] shorts)
        {
            var bytes = new byte[shorts.Length * 2];
            Buffer.BlockCopy(shorts, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        /// <summary>
        /// Resample 48kHz stereo PCM to 16kHz mono float array.
        /// Discord sends 48kHz stereo, we need 16kHz mono for STT.
        /// </summary>
        /// <param name="input">48kHz stereo PCM16 bytes (interleaved left/right)</param>
        /// <param name="length">Number of valid bytes</param>
        /// <returns>16kHz mono float array (values -1.0 to 1.0)</returns>
        public static float[] Resample48kTo16kMono(byte[] input, int length)
        {
            if (input == null || length < 4) return Array.Empty<float>();

            // 48kHz stereo to 16kHz mono:
            // - Downsample by 3x (48000 -> 16000)
            // - Convert stereo to mono (average left + right)
            // - Convert to float [-1, 1]

            int inputSamples = length / 2; // 16-bit samples
            int outputSamples = inputSamples / 6; // stereo / 2 channels * 1/3 sample rate
            var output = new float[outputSamples];

            for (int i = 0; i < outputSamples; i++)
            {
                // Source index in stereo samples (each frame is 2 samples: L, R)
                // We take every 3rd frame (for 3x downsampling)
                int srcFrame = i * 3;
                int srcIndex = srcFrame * 2; // *2 for stereo

                if (srcIndex + 3 >= inputSamples) break;

                int bi = srcIndex * 2; // byte index
                if (bi + 3 >= length) break;

                // Read left and right channels
                short left = (short)(input[bi] | (input[bi + 1] << 8));
                short right = (short)(input[bi + 2] | (input[bi + 3] << 8));

                // Mono mix and convert to float
                float mono = ((left + right) / 2.0f) / 32768.0f;
                output[i] = mono;
            }

            return output;
        }
    }
}
