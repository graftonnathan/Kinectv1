using System;
using NAudio.Dsp;

namespace Kinectv1.Services.Speaker
{
    public static class Fbank80
    {
        private const int DefaultSampleRate = 16000;
        private const int FrameLengthSamples = 400; // 25ms @ 16k
        private const int HopLengthSamples = 160;   // 10ms @ 16k
        private const int FftSize = 512;
        private const int MelBins = 80;

        public static float[,] ComputeLogMelFbank80(short[] pcm16, int sampleRate = DefaultSampleRate)
        {
            if (pcm16 == null || pcm16.Length == 0) return new float[0, MelBins];

            if (sampleRate != DefaultSampleRate)
                throw new ArgumentOutOfRangeException(nameof(sampleRate), "Fbank80 currently expects 16kHz PCM.");

            int n = pcm16.Length;
            if (n < FrameLengthSamples) return new float[0, MelBins];

            int frames = 1 + (n - FrameLengthSamples) / HopLengthSamples;
            if (frames <= 0) return new float[0, MelBins];

            var window = GetHammingWindow(FrameLengthSamples);
            var melFilters = GetMelFilterbank(sampleRate, FftSize, MelBins, fMin: 20.0, fMax: sampleRate / 2.0);

            var feats = new float[frames, MelBins];

            // Pre-alloc buffers to avoid per-frame allocations
            var fftBuf = new Complex[FftSize];
            var power = new double[(FftSize / 2) + 1];

            // NAudio FFT requires m such that n = 2^m.
            int fftM = Log2(FftSize);

            for (int t = 0; t < frames; t++)
            {
                int start = t * HopLengthSamples;

                // Window + zero-pad into Complex buffer
                for (int i = 0; i < FftSize; i++)
                {
                    float x = 0f;
                    if (i < FrameLengthSamples)
                    {
                        x = (pcm16[start + i] / 32768f) * window[i];
                    }
                    fftBuf[i].X = x;
                    fftBuf[i].Y = 0f;
                }

                FastFourierTransform.FFT(true, fftM, fftBuf);

                // Power spectrum (only non-redundant bins)
                for (int k = 0; k < power.Length; k++)
                {
                    double re = fftBuf[k].X;
                    double im = fftBuf[k].Y;
                    power[k] = (re * re) + (im * im);
                }

                // Apply mel filterbank + log
                for (int m = 0; m < MelBins; m++)
                {
                    double e = 0;
                    var filt = melFilters[m];
                    for (int k = filt.StartBin; k <= filt.EndBin; k++)
                    {
                        e += power[k] * filt.Weights[k - filt.StartBin];
                    }

                    // log(max(mel, 1e-10))
                    feats[t, m] = (float)Math.Log(Math.Max(e, 1e-10));
                }
            }

            ApplyCmvnInPlace(feats);
            return feats;
        }

        public static void ApplyCmvnInPlace(float[,] feats)
        {
            int tLen = feats.GetLength(0);
            int fLen = feats.GetLength(1);
            for (int f = 0; f < fLen; f++)
            {
                double mean = 0;
                for (int t = 0; t < tLen; t++) mean += feats[t, f];
                mean /= Math.Max(1, tLen);
                for (int t = 0; t < tLen; t++) feats[t, f] = (float)(feats[t, f] - mean);
            }
        }

        private readonly struct MelFilter
        {
            public MelFilter(int startBin, int endBin, float[] weights)
            {
                StartBin = startBin;
                EndBin = endBin;
                Weights = weights;
            }

            public int StartBin { get; }
            public int EndBin { get; }
            public float[] Weights { get; }
        }

        private static float[] GetHammingWindow(int length)
        {
            var w = new float[length];
            if (length <= 1)
            {
                for (int i = 0; i < length; i++) w[i] = 1f;
                return w;
            }

            for (int i = 0; i < length; i++)
            {
                // 0.54 - 0.46*cos(2*pi*n/(N-1))
                w[i] = (float)(0.54 - 0.46 * Math.Cos((2.0 * Math.PI * i) / (length - 1)));
            }
            return w;
        }

        private static MelFilter[] GetMelFilterbank(int sampleRate, int fftSize, int melBins, double fMin, double fMax)
        {
            int nFftBins = (fftSize / 2) + 1;

            // mel points
            double melMin = HzToMel(fMin);
            double melMax = HzToMel(fMax);

            var melPoints = new double[melBins + 2];
            for (int i = 0; i < melPoints.Length; i++)
            {
                melPoints[i] = melMin + (i * (melMax - melMin) / (melBins + 1));
            }

            var hzPoints = new double[melPoints.Length];
            for (int i = 0; i < hzPoints.Length; i++)
            {
                hzPoints[i] = MelToHz(melPoints[i]);
            }

            var bin = new int[hzPoints.Length];
            for (int i = 0; i < hzPoints.Length; i++)
            {
                var b = (int)Math.Floor((fftSize + 1) * hzPoints[i] / sampleRate);
                if (b < 0) b = 0;
                if (b >= nFftBins) b = nFftBins - 1;
                bin[i] = b;
            }

            var filters = new MelFilter[melBins];
            for (int m = 0; m < melBins; m++)
            {
                int left = bin[m];
                int center = bin[m + 1];
                int right = bin[m + 2];

                if (right <= left)
                {
                    // Degenerate; create zero filter
                    filters[m] = new MelFilter(0, 0, new float[] { 0f });
                    continue;
                }

                int start = left;
                int end = right;
                int len = Math.Max(1, end - start + 1);
                var w = new float[len];

                for (int k = start; k <= end; k++)
                {
                    double weight;
                    if (k < center)
                    {
                        weight = (center == left) ? 0.0 : (k - left) / (double)(center - left);
                    }
                    else
                    {
                        weight = (right == center) ? 0.0 : (right - k) / (double)(right - center);
                    }

                    if (weight < 0) weight = 0;
                    w[k - start] = (float)weight;
                }

                filters[m] = new MelFilter(start, end, w);
            }

            return filters;
        }

        private static int Log2(int n)
        {
            int p = 0;
            int v = n;
            while ((v >>= 1) != 0) p++;
            if ((1 << p) != n) throw new ArgumentException("FFT size must be a power of two.");
            return p;
        }

        private static double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);

        private static double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);
    }
}
