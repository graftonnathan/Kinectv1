using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Kinectv1.Services.Speaker;
using Kinectv1.Settings;

namespace Kinectv1
{
    // Rolling-window speaker embedder with optional ONNX model
    public static class SpeakerEmbedder
    {
        public static event Action<float[]> OnEmbedding; // 1s window embedding
        public static event Action<SpeakerEmbeddingFrame>? OnEmbeddingFrame;

        private const int SampleRate = 16000;
        private const int DefaultWindowMs = 1600;
        private const int DefaultHopMs = 800;
        private const double DefaultSilenceDb = -70.0;

        private static int _windowSamples = SampleRate; // default 1 second
        private static int _hopSamples = SampleRate / 2;    // 0.5 second
        private static double _silenceDb = DefaultSilenceDb;
        private static float[] _ring = new float[_windowSamples];
        private static int _write;
        private static int _filled;
        private static int _sinceLast;

        private static readonly object _gate = new object();
        private static DateTime _lastAddLog = DateTime.MinValue;

        // Absolute sample clock support (WebRTC only).
        // If not provided by caller, we still advance this cursor based on bytes fed.
        private static long _absSampleCursor = 0; // next sample index
        private static readonly object _recentFramesGate = new object();
        private static readonly System.Collections.Generic.List<SpeakerEmbeddingFrame> _recentFrames = new();
        private const int MaxFrames = 600; // ~5 min at 0.5s hop

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

        private static void ApplyEmbedderConfig(TranscriptionSettings transcription)
        {
            int windowMs = transcription?.SpeakerEmbeddingWindowMs ?? DefaultWindowMs;
            int hopMs = transcription?.SpeakerEmbeddingHopMs ?? DefaultHopMs;
            double silenceDb = transcription?.SpeakerEmbeddingSilenceDb ?? DefaultSilenceDb;

            windowMs = Math.Clamp(windowMs, 400, 4000);
            hopMs = Math.Clamp(hopMs, 200, Math.Max(200, windowMs - 100));

            int windowSamples = Math.Max(SampleRate / 2, windowMs * SampleRate / 1000);
            int hopSamples = Math.Max(SampleRate / 10, hopMs * SampleRate / 1000);

            lock (_gate)
            {
                if (_ring.Length != windowSamples)
                {
                    _ring = new float[windowSamples];
                    _write = 0;
                    _filled = 0;
                    _sinceLast = 0;
                }

                _windowSamples = windowSamples;
                _hopSamples = Math.Min(Math.Max(1, hopSamples), _windowSamples);
                _silenceDb = Math.Max(-120.0, Math.Min(-5.0, silenceDb));
            }

            Console.WriteLine($"[SpeakerEmbedder] Window={windowMs}ms hop={hopMs}ms silence={_silenceDb:F1}dB (samples={windowSamples}/{_hopSamples})");
        }

        // WebRTC can provide an explicit sample clock (16kHz domain).
        public static void AddPcm16(short[] pcm16, int count, long startSample)
        {
            if (pcm16 == null || count <= 0) return;
            if (count > pcm16.Length) count = pcm16.Length;

            lock (_gate)
            {
                _absSampleCursor = startSample;
            }

            // Convert to bytes and feed existing path.
            // NOTE: this allocates, but it's only intended for WebRTC mode and keeps behavior isolated.
            var bytes = new byte[count * 2];
            Buffer.BlockCopy(pcm16, 0, bytes, 0, bytes.Length);
            AddPcm16(bytes, bytes.Length);

            lock (_gate)
            {
                _absSampleCursor = startSample + count;
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
            long windowEndSample = 0;
            
            lock (_gate)
            {
                for (int i = 0; i < samples; i++)
                {
                    short s = BitConverter.ToInt16(buffer, i * 2);
                    float f = s / 32768f; // -1..1
                    _ring[_write] = f;
                    _write = (_write + 1) % _windowSamples;
                    if (_filled < _windowSamples) _filled++;
                    _sinceLast++;
                }

                // Advance internal sample clock for callers that don't provide it.
                _absSampleCursor += samples;
                windowEndSample = _absSampleCursor;
                
                // Log buffer fill status periodically
                var now = DateTime.UtcNow;
                if ((now - _lastAddLog).TotalSeconds >= 2.0)
                {
                    Console.WriteLine($"[SpeakerEmbedder] AddPcm16: +{samples} samples, filled={_filled}/{_windowSamples}, sinceLast={_sinceLast}/{_hopSamples}");
                    _lastAddLog = now;
                }
                
                // Check if we should emit (but don't do ONNX inference while holding lock)
                if (_filled >= _windowSamples && _sinceLast >= _hopSamples && !_onnxBusy)
                {
                    _sinceLast = 0;
                    
                    // Compute RMS dBFS for silence gating
                    double sum = 0;
                    for (int i = 0; i < _windowSamples; i++) { var v = _ring[(_write + i) % _windowSamples]; sum += v * v; }
                    double rms = Math.Sqrt(sum / _windowSamples);
                    db = 20.0 * Math.Log10(rms + 1e-9);
                    
                    // Log RMS level periodically
                    if ((now - _lastRmsLog).TotalSeconds >= 2.0)
                    {
                        Console.WriteLine($"[SpeakerEmbedder] RMS check: {db:F1}dB (threshold={_silenceDb:F1}dB, filled={_filled})");
                        _lastRmsLog = now;
                    }
                    
                    // Only emit if not silent
                    if (db >= _silenceDb)
                    {
                        shouldEmit = true;
                        // Take a snapshot of the audio for inference outside the lock
                        audioSnapshot = new float[_windowSamples];
                        for (int i = 0; i < _windowSamples; i++) 
                            audioSnapshot[i] = _ring[(_write + i) % _windowSamples];
                    }
                }
            }
            
            // Perform ONNX inference outside the audio buffer lock
            if (shouldEmit && audioSnapshot != null)
            {
                EmitEmbedding(audioSnapshot, db, windowEndSample);
            }
        }

        private static void EmitEmbedding(float[] audioSnapshot, double db, long windowEndSample)
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
                
                // Detailed frame for timeline correlation.
                try
                {
                    if (emb != null && emb.Length > 0)
                    {
                        var endSample = windowEndSample;
                        var startSample = endSample - _windowSamples;
                        if (startSample < 0) startSample = 0;

                        var frame = new SpeakerEmbeddingFrame
                        {
                            StartSample = startSample,
                            EndSample = endSample,
                            Embedding = emb,
                            RmsDb = (float)db,
                            IsSpeech = db >= _silenceDb
                        };

                        lock (_recentFramesGate)
                        {
                            _recentFrames.Add(frame);
                            if (_recentFrames.Count > MaxFrames)
                                _recentFrames.RemoveRange(0, _recentFrames.Count - MaxFrames);
                        }

                        try { OnEmbeddingFrame?.Invoke(frame); } catch { }
                    }
                }
                catch { }
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
                Console.WriteLine("[SpeakerEmbedder] Configure 'Transcription.SpeakerEmbeddingModelPath' in settings for proper diarization.");
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
                // Convert normalized float audio snapshot to PCM16 for feature extraction
                var pcm16 = new short[audio.Length];
                for (int i = 0; i < audio.Length; i++)
                {
                    var x = audio[i];
                    if (x > 1f) x = 1f;
                    else if (x < -1f) x = -1f;
                    pcm16[i] = (short)(x * 32767f);
                }

                // Compute [T,80] log-mel fbank features (+ mean CMVN)
                var feats = Fbank80.ComputeLogMelFbank80(pcm16, sampleRate: SampleRate);
                int tLen = feats.GetLength(0);
                if (tLen <= 0) return null;

                // Build input tensor [1,T,80]
                var inputName = sess.InputMetadata.Keys.First();
                var inputTensor = new DenseTensor<float>(new[] { 1, tLen, 80 });
                for (int t = 0; t < tLen; t++)
                    for (int f = 0; f < 80; f++)
                        inputTensor[0, t, f] = feats[t, f];

                if (!_loggedModelInfo)
                {
                    var dims = sess.InputMetadata[inputName].Dimensions ?? Array.Empty<int>();
                    Console.WriteLine($"[SpeakerEmbedder] ONNX model input: name={inputName}, dims=[{string.Join(",", dims)}]");
                    var outputName = sess.OutputMetadata.Keys.FirstOrDefault();
                    var outDims = outputName != null ? sess.OutputMetadata[outputName].Dimensions : Array.Empty<int>();
                    Console.WriteLine($"[SpeakerEmbedder] ONNX model output: name={outputName}, dims=[{string.Join(",", outDims ?? Array.Empty<int>())}]");
                    _loggedModelInfo = true;
                }

                var input = NamedOnnxValue.CreateFromTensor(inputName, inputTensor);
                IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = null;
                try
                {
                    results = sess.Run(new[] { input });

                    var first = results.FirstOrDefault();
                    if (first == null) return null;

                    var outTensor = first.AsTensor<float>();
                    var dims = outTensor.Dimensions;
                    int rank = dims.Length;
                    if (rank <= 0) return null;
                    int dim = dims[rank - 1];
                    if (dim <= 0) return null;

                    var emb = new float[dim];
                    if (rank == 2)
                    {
                        for (int i = 0; i < dim; i++) emb[i] = outTensor[0, i];
                    }
                    else
                    {
                        for (int i = 0; i < dim; i++) emb[i] = outTensor[i];
                    }

                    // NaN/Inf guard
                    for (int i = 0; i < emb.Length; i++)
                    {
                        if (float.IsNaN(emb[i]) || float.IsInfinity(emb[i]))
                            return null;
                    }

                    // L2 normalize
                    double n2 = 0;
                    for (int i = 0; i < emb.Length; i++) n2 += emb[i] * emb[i];
                    n2 = Math.Sqrt(n2);
                    if (n2 < 1e-12) return null;
                    double inv = 1.0 / n2;
                    for (int i = 0; i < emb.Length; i++) emb[i] = (float)(emb[i] * inv);

                    return emb;
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
                    if (settingsProvider == null)
                    {
                        ApplyEmbedderConfig(null);
                        return;
                    }
                    
                    var transcriptionSettings = settingsProvider.Current?.Transcription;
                    if (transcriptionSettings == null)
                    {
                        try { transcriptionSettings = settingsProvider.GetDefaultsEffective()?.Transcription; } catch { }
                    }
                    ApplyEmbedderConfig(transcriptionSettings);
 
                    var path = transcriptionSettings?.SpeakerEmbeddingModelPath;
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

        public static bool TryGetEmbeddingForRange(long startSample, long endSample, out float[] avg)
        {
            avg = Array.Empty<float>();
            if (endSample <= startSample) return false;

            System.Collections.Generic.List<SpeakerEmbeddingFrame> frames;
            lock (_recentFramesGate)
            {
                frames = _recentFrames
                    .Where(f => f.EndSample > startSample && f.StartSample < endSample && f.IsSpeech)
                    .ToList();
            }

            if (frames.Count == 0) return false;

            int dim = frames[0].Embedding?.Length ?? 0;
            if (dim <= 0) return false;

            var acc = new float[dim];
            double wsum = 0;

            foreach (var f in frames)
            {
                var overlapStart = Math.Max(startSample, f.StartSample);
                var overlapEnd = Math.Min(endSample, f.EndSample);
                var w = Math.Max(0, overlapEnd - overlapStart);
                if (w <= 0) continue;

                var emb = f.Embedding;
                if (emb == null || emb.Length != dim) continue;

                for (int i = 0; i < dim; i++)
                    acc[i] += emb[i] * (float)w;

                wsum += w;
            }

            if (wsum <= 0) return false;
            for (int i = 0; i < dim; i++)
                acc[i] /= (float)wsum;

            avg = acc;
            return true;
        }
    }
}
