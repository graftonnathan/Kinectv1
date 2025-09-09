using System;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Kinectv1
{
    // Rolling-window speaker embedder with optional ONNX model
    public static class SpeakerEmbedder
    {
        public static event Action<float[]> OnEmbedding; // 1s window embedding

        private const int SampleRate = 16000;
        private const int WindowSize = SampleRate * 1; // 1 second
        private const int HopSize = SampleRate / 2;    // 0.5 second

        private static readonly float[] _ring = new float[WindowSize];
        private static int _write;     // next write index
        private static int _filled;    // how many valid samples present
        private static int _sinceLast; // samples since last emit
        private static readonly object _gate = new object();

        // ONNX session state
        private static readonly object _sessGate = new object();
        private static InferenceSession _session;
        private static string _modelPath;

        static SpeakerEmbedder()
        {
            TryLoadFromSettings();
            try
            {
                var svc = App.SettingsProvider;
                if (svc != null)
                {
                    svc.Changed += (s, snap) => { try { TryLoadFromSettings(); } catch { } };
                }
            }
            catch { }
        }

        public static void AddPcm16(byte[] buffer, int bytes)
        {
            if (buffer == null || bytes <= 0) return;
            int samples = bytes / 2;
            lock (_gate)
            {
                for (int i = 0; i < samples; i++)
                {
                    short s = BitConverter.ToInt16(buffer, i * 2);
                    float f = s / 32768f; // -1..1
                    _ring[_write] = f;
                    _write = (_write + 1) % WindowSize;
                    if (_filled < WindowSize) _filled++;
                    _sinceLast++;
                }
                TryEmit();
            }
        }

        private static void TryEmit()
        {
            if (_filled < WindowSize) return;             // need full second
            if (_sinceLast < HopSize) return;             // hop control
            _sinceLast = 0;

            // Compute RMS dBFS for silence gating
            double sum = 0;
            for (int i = 0; i < WindowSize; i++) { var v = _ring[(_write + i) % WindowSize]; sum += v * v; }
            double rms = Math.Sqrt(sum / WindowSize);
            double db = 20.0 * Math.Log10(rms + 1e-9);
            if (db < -45.0) return;

            // Prefer ONNX embedding when session is available
            var emb = ComputeEmbeddingOnnx();
            if (emb == null)
            {
                emb = ComputeEmbeddingEnvelope();
            }

            try { OnEmbedding?.Invoke(emb); } catch { }
        }

        // Simple fallback envelope-based embedding (32-dim)
        private static float[] ComputeEmbeddingEnvelope()
        {
            const int dims = 32;
            int seg = WindowSize / dims; if (seg <= 0) seg = 1;
            var v = new float[dims];
            int idx = _write; // ring start
            for (int d = 0; d < dims; d++)
            {
                double acc = 0;
                for (int i = 0; i < seg; i++) { var x = _ring[(idx + i) % WindowSize]; acc += Math.Abs(x); }
                v[d] = (float)(acc / seg);
                idx = (idx + seg) % WindowSize;
            }
            // L2 normalize
            double n2 = 0; for (int d = 0; d < dims; d++) n2 += v[d] * v[d];
            n2 = Math.Sqrt(n2) + 1e-9; for (int d = 0; d < dims; d++) v[d] = (float)(v[d] / n2);
            return v;
        }

        // Compute embedding via ONNX model if loaded
        private static float[] ComputeEmbeddingOnnx()
        {
            InferenceSession sess; lock (_sessGate) sess = _session;
            if (sess == null) return null;

            // Build 1-second window in chronological order
            var mono = new float[WindowSize];
            for (int i = 0; i < WindowSize; i++) mono[i] = _ring[(_write + i) % WindowSize];

            // Normalize to target RMS 0.1 and clamp [-1,1]
            double sum = 0; for (int i = 0; i < WindowSize; i++) sum += mono[i] * mono[i];
            double rms = Math.Sqrt(sum / WindowSize);
            double target = 0.1; double gain = rms > 1e-9 ? (target / rms) : 1.0;
            for (int i = 0; i < WindowSize; i++)
            {
                double x = mono[i] * gain; if (x > 1.0) x = 1.0; else if (x < -1.0) x = -1.0; mono[i] = (float)x;
            }

            // Determine expected input shape and create tensor
            var inputName = sess.InputMetadata.Keys.First();
            var dims = sess.InputMetadata[inputName].Dimensions ?? new int[0];

            DenseTensor<float> tensor;
            if (dims.Length == 2)
            {
                // [1, 16000]
                tensor = new DenseTensor<float>(new[] { 1, WindowSize });
                for (int i = 0; i < WindowSize; i++) tensor[0, i] = mono[i];
            }
            else if (dims.Length == 3)
            {
                // [1, 1, 16000]
                tensor = new DenseTensor<float>(new[] { 1, 1, WindowSize });
                for (int i = 0; i < WindowSize; i++) tensor[0, 0, i] = mono[i];
            }
            else
            {
                // Fallback to [16000]
                tensor = new DenseTensor<float>(mono, new[] { WindowSize });
            }

            var input = NamedOnnxValue.CreateFromTensor(inputName, tensor);
            var results = sess.Run(new[] { input });
            try
            {
                var first = results.FirstOrDefault(); if (first == null) return null;
                var outTensor = first.AsTensor<float>();
                var arr = outTensor.ToArray();

                // L2 normalize output
                double n2 = 0; for (int i = 0; i < arr.Length; i++) n2 += arr[i] * arr[i];
                n2 = Math.Sqrt(n2) + 1e-9; for (int i = 0; i < arr.Length; i++) arr[i] = (float)(arr[i] / n2);
                return arr;
            }
            finally
            {
                // Dispose results items if disposable
                try
                {
                    foreach (var r in results)
                    {
                        (r as IDisposable)?.Dispose();
                    }
                }
                catch { }
                try { (input as IDisposable)?.Dispose(); } catch { }
            }
        }

        private static void TryLoadFromSettings()
        {
            try
            {
                var path = App.SettingsProvider?.Current?.Face?.SpeakerEmbeddingModelPath;
                if (string.IsNullOrWhiteSpace(path)) return;
                var resolved = ResolvePath(path);
                if (!File.Exists(resolved)) return;
                lock (_sessGate)
                {
                    if (string.Equals(_modelPath, resolved, StringComparison.OrdinalIgnoreCase)) return;
                    try { _session?.Dispose(); } catch { }
                    _session = new InferenceSession(resolved);
                    _modelPath = resolved;
                    Console.WriteLine($"[SpeakerEmbedder] Loaded speaker model: {_modelPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpeakerEmbedder] Model load failed: {ex.Message}");
                lock (_sessGate) { try { _session?.Dispose(); } catch { } _session = null; _modelPath = null; }
            }
        }

        private static string ResolvePath(string p)
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(p);
                if (!Path.IsPathRooted(expanded)) expanded = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, expanded);
                return Path.GetFullPath(expanded);
            }
            catch { return p; }
        }
    }
}
