using System;
using System.IO;
using System.Linq;
using System.Threading;
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
        private static DateTime _lastAddLog = DateTime.MinValue;

        // ONNX session state - use a single lock for all ONNX operations
        private static readonly object _onnxLock = new object();
        private static InferenceSession _session;
        private static string _modelPath;
        private static volatile bool _initialized = false;
        private static volatile bool _settingsHooked = false;
        private static volatile bool _onnxBusy = false;

        /// <summary>
        /// Initialize the speaker embedder. Call this after App.SettingsProvider is ready.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            
            TryLoadFromSettings();
            
            if (!_settingsHooked)
            {
                try
                {
                    var svc = App.SettingsProvider;
                    if (svc != null)
                    {
                        svc.Changed += (s, snap) => 
                        { 
                            // Defer settings reload if ONNX is busy
                            if (!_onnxBusy)
                            {
                                try { TryLoadFromSettings(); } catch { } 
                            }
                        };
                        _settingsHooked = true;
                    }
                }
                catch { }
            }
        }

        public static void AddPcm16(byte[] buffer, int bytes)
        {
            if (buffer == null || bytes <= 0) return;
            
            // Lazy initialization on first use
            if (!_initialized)
            {
                try { Initialize(); } catch { }
            }
            
            int samples = bytes / 2;
            
            float[] audioSnapshot = null;
            double db = -100.0;
            bool shouldEmit = false;
            
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
                
                // Log buffer fill status periodically
                var now = DateTime.UtcNow;
                if ((now - _lastAddLog).TotalSeconds >= 2.0)
                {
                    Console.WriteLine($"[SpeakerEmbedder] AddPcm16: +{samples} samples, filled={_filled}/{WindowSize}, sinceLast={_sinceLast}/{HopSize}");
                    _lastAddLog = now;
                }
                
                // Check if we should emit (but don't do ONNX inference while holding lock)
                if (_filled >= WindowSize && _sinceLast >= HopSize && !_onnxBusy)
                {
                    _sinceLast = 0;
                    
                    // Compute RMS dBFS for silence gating
                    double sum = 0;
                    for (int i = 0; i < WindowSize; i++) { var v = _ring[(_write + i) % WindowSize]; sum += v * v; }
                    double rms = Math.Sqrt(sum / WindowSize);
                    db = 20.0 * Math.Log10(rms + 1e-9);
                    
                    // Log RMS level periodically
                    if ((now - _lastRmsLog).TotalSeconds >= 2.0)
                    {
                        Console.WriteLine($"[SpeakerEmbedder] RMS check: {db:F1}dB (threshold=-65dB, filled={_filled})");
                        _lastRmsLog = now;
                    }
                    
                    // Only emit if not silent
                    if (db >= -65.0)
                    {
                        shouldEmit = true;
                        // Take a snapshot of the audio for inference outside the lock
                        audioSnapshot = new float[WindowSize];
                        for (int i = 0; i < WindowSize; i++) 
                            audioSnapshot[i] = _ring[(_write + i) % WindowSize];
                    }
                }
            }
            
            // Perform ONNX inference outside the audio buffer lock
            if (shouldEmit && audioSnapshot != null)
            {
                EmitEmbedding(audioSnapshot, db);
            }
        }

        private static void EmitEmbedding(float[] audioSnapshot, double db)
        {
            // Use TryEnter to avoid blocking - just skip if ONNX is busy
            if (!Monitor.TryEnter(_onnxLock))
            {
                return; // Skip this frame if ONNX inference is already in progress
            }
            
            try
            {
                _onnxBusy = true;
                var now = DateTime.UtcNow;
                
                // Prefer ONNX embedding when session is available
                var emb = ComputeEmbeddingOnnxInternal(audioSnapshot);
                string method;
                int dim = 0;
                if (emb == null)
                {
                    emb = ComputeEmbeddingEnvelope(audioSnapshot);
                    method = "envelope";
                    dim = emb?.Length ?? 0;
                }
                else
                {
                    method = "ONNX";
                    dim = emb.Length;
                }

                // Log periodically with more embedding detail
                bool shouldLog = (now - _lastEmitLog).TotalSeconds >= 5.0 || method != _lastEmitMethod;
                if (shouldLog && emb != null && emb.Length > 0)
                {
                    float min = float.MaxValue, max = float.MinValue;
                    double embSum = 0, embSumSq = 0;
                    for (int i = 0; i < emb.Length; i++)
                    {
                        embSum += emb[i];
                        embSumSq += emb[i] * emb[i];
                        if (emb[i] < min) min = emb[i];
                        if (emb[i] > max) max = emb[i];
                    }
                    double mean = embSum / emb.Length;
                    double variance = (embSumSq / emb.Length) - (mean * mean);
                    
                    var first4 = string.Join(",", emb.Take(4).Select(v => v.ToString("F3")));
                    Console.WriteLine($"[SpeakerEmbedder] {method} dim={dim} RMS={db:F1}dB | emb[0..3]=[{first4}] range=[{min:F3},{max:F3}] var={variance:F6}");
                    _lastEmitLog = now;
                    _lastEmitMethod = method;
                }

                try { OnEmbedding?.Invoke(emb); } catch { }
            }
            finally
            {
                _onnxBusy = false;
                Monitor.Exit(_onnxLock);
            }
        }

        private static DateTime _lastEmitLog = DateTime.MinValue;
        private static DateTime _lastRmsLog = DateTime.MinValue;
        private static string _lastEmitMethod = "";

        // Simple fallback envelope-based embedding (32-dim)
        private static float[] ComputeEmbeddingEnvelope(float[] audio)
        {
            if (!_warnedAboutFallback)
            {
                Console.WriteLine("[SpeakerEmbedder] WARNING: Using envelope fallback - speaker diarization will NOT work!");
                Console.WriteLine("[SpeakerEmbedder] Configure 'Face.SpeakerEmbeddingModelPath' in settings for proper diarization.");
                _warnedAboutFallback = true;
            }

            const int dims = 32;
            int seg = audio.Length / dims; if (seg <= 0) seg = 1;
            var v = new float[dims];
            
            for (int d = 0; d < dims; d++)
            {
                double ampAcc = 0;
                int zeroCrossings = 0;
                float prevSample = 0;
                int start = d * seg;
                
                for (int i = 0; i < seg && (start + i) < audio.Length; i++)
                {
                    var x = audio[start + i];
                    ampAcc += Math.Abs(x);
                    
                    if (i > 0 && ((prevSample >= 0 && x < 0) || (prevSample < 0 && x >= 0)))
                        zeroCrossings++;
                    prevSample = x;
                }
                
                float amp = (float)(ampAcc / seg);
                float zcr = (float)zeroCrossings / seg;
                
                if (d % 2 == 0)
                    v[d] = amp;
                else
                    v[d] = zcr * 0.1f;
            }
            
            // L2 normalize
            double n2 = 0; for (int d = 0; d < dims; d++) n2 += v[d] * v[d];
            n2 = Math.Sqrt(n2) + 1e-9; for (int d = 0; d < dims; d++) v[d] = (float)(v[d] / n2);
            return v;
        }

        private static bool _warnedAboutFallback = false;

        // Internal ONNX computation - must be called with _onnxLock held
        private static float[] ComputeEmbeddingOnnxInternal(float[] audio)
        {
            var sess = _session;
            if (sess == null) return null;

            try
            {
                // Normalize to target RMS 0.1 and clamp [-1,1]
                var mono = new float[audio.Length];
                double sum = 0; 
                for (int i = 0; i < audio.Length; i++) sum += audio[i] * audio[i];
                double rms = Math.Sqrt(sum / audio.Length);
                double target = 0.1; 
                double gain = rms > 1e-9 ? (target / rms) : 1.0;
                for (int i = 0; i < audio.Length; i++)
                {
                    double x = audio[i] * gain; 
                    if (x > 1.0) x = 1.0; 
                    else if (x < -1.0) x = -1.0; 
                    mono[i] = (float)x;
                }

                var inputName = sess.InputMetadata.Keys.First();
                var dims = sess.InputMetadata[inputName].Dimensions ?? new int[0];

                if (!_loggedModelInfo)
                {
                    Console.WriteLine($"[SpeakerEmbedder] ONNX model input: name={inputName}, dims=[{string.Join(",", dims)}]");
                    var outputName = sess.OutputMetadata.Keys.FirstOrDefault();
                    var outputDims = outputName != null ? sess.OutputMetadata[outputName].Dimensions : new int[0];
                    Console.WriteLine($"[SpeakerEmbedder] ONNX model output: name={outputName}, dims=[{string.Join(",", outputDims ?? new int[0])}]");
                    _loggedModelInfo = true;
                }

                DenseTensor<float> tensor;
                int expectedSamples = WindowSize;
                
                if (dims.Length == 2)
                {
                    expectedSamples = dims[1] > 0 ? dims[1] : WindowSize;
                    tensor = new DenseTensor<float>(new[] { 1, expectedSamples });
                    for (int i = 0; i < Math.Min(mono.Length, expectedSamples); i++) tensor[0, i] = mono[i];
                }
                else if (dims.Length == 3)
                {
                    expectedSamples = dims[2] > 0 ? dims[2] : WindowSize;
                    tensor = new DenseTensor<float>(new[] { 1, 1, expectedSamples });
                    for (int i = 0; i < Math.Min(mono.Length, expectedSamples); i++) tensor[0, 0, i] = mono[i];
                }
                else
                {
                    tensor = new DenseTensor<float>(mono, new[] { mono.Length });
                }

                var input = NamedOnnxValue.CreateFromTensor(inputName, tensor);
                IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = null;
                try
                {
                    results = sess.Run(new[] { input });
                    
                    var first = results.FirstOrDefault(); 
                    if (first == null) return null;
                    
                    var outTensor = first.AsTensor<float>();
                    var arr = outTensor.ToArray();

                    // L2 normalize output
                    double n2 = 0; 
                    for (int i = 0; i < arr.Length; i++) n2 += arr[i] * arr[i];
                    n2 = Math.Sqrt(n2) + 1e-9; 
                    for (int i = 0; i < arr.Length; i++) arr[i] = (float)(arr[i] / n2);
                    return arr;
                }
                finally
                {
                    results?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpeakerEmbedder] ONNX inference error: {ex.Message}");
                return null;
            }
        }

        private static bool _loggedModelInfo = false;

        private static void TryLoadFromSettings()
        {
            // Must acquire lock to modify session
            lock (_onnxLock)
            {
                try
                {
                    var settingsProvider = App.SettingsProvider;
                    if (settingsProvider == null) return;
                    
                    var path = settingsProvider.Current?.Face?.SpeakerEmbeddingModelPath;
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        Console.WriteLine("[SpeakerEmbedder] No speaker embedding model configured - diarization will use fallback (limited accuracy)");
                        return;
                    }
                    var resolved = ResolvePath(path);
                    if (!File.Exists(resolved))
                    {
                        Console.WriteLine($"[SpeakerEmbedder] Speaker model not found: {resolved}");
                        return;
                    }
                    
                    if (string.Equals(_modelPath, resolved, StringComparison.OrdinalIgnoreCase)) return;
                    
                    bool wasUsingFallback = (_session == null);
                    
                    // Dispose old session
                    if (_session != null)
                    {
                        try { _session.Dispose(); } catch { }
                        _session = null;
                    }
                    _loggedModelInfo = false;
                    
                    // Create session with default options (CPU)
                    var sessionOptions = new SessionOptions();
                    sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC; // Use basic optimization
                    sessionOptions.InterOpNumThreads = 1; // Single thread for inter-op
                    sessionOptions.IntraOpNumThreads = 1; // Single thread for intra-op
                    
                    _session = new InferenceSession(resolved, sessionOptions);
                    _modelPath = resolved;
                    Console.WriteLine($"[SpeakerEmbedder] Loaded speaker model (CPU, single-threaded): {_modelPath}");
                    _warnedAboutFallback = false;
                    
                    // Reset diarizer when model changes
                    if (wasUsingFallback || _session != null)
                    {
                        try 
                        { 
                            Services.Transcription.SpeakerDiarizer.Instance.Reset();
                            Console.WriteLine("[SpeakerEmbedder] Diarizer reset due to model change");
                        } 
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SpeakerEmbedder] Model load failed: {ex.Message}");
                    try { _session?.Dispose(); } catch { } 
                    _session = null; 
                    _modelPath = null; 
                }
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
