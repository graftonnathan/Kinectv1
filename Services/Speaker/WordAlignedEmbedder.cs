using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Kinectv1.Services.Transcription;
using Kinectv1.Settings;

namespace Kinectv1.Services.Speaker
{
    /// <summary>
    /// Word-aligned speaker embedding for accurate diarization.
    /// Instead of fixed sliding windows, this computes embeddings for specific word/phrase segments
    /// using Vosk's word-level timing output.
    /// </summary>
    public sealed class WordAlignedEmbedder
    {
        private static readonly Lazy<WordAlignedEmbedder> _instance = new(() => new WordAlignedEmbedder());
        public static WordAlignedEmbedder Instance => _instance.Value;

        private const int SampleRate = 16000;
        private const int MinWordSamples = SampleRate / 4;  // 250ms minimum for meaningful embedding
        private const int MaxWordSamples = SampleRate * 3;  // 3s maximum to avoid memory issues
        private const int RingBufferSeconds = 30;           // Keep 30s of audio history
        private const int RingBufferSamples = SampleRate * RingBufferSeconds;
        
        // Minimum phrase duration in milliseconds for confident speaker assignment
        // Phrases shorter than this are treated as "low confidence" and won't trigger speaker switches
        private const int MinConfidentPhraseMs = 800;

        // Ring buffer for raw PCM (sample-indexed)
        private readonly float[] _ring = new float[RingBufferSamples];
        private long _writePos = 0;  // Absolute sample position of next write
        private readonly object _ringLock = new();

        // ONNX session (shared with SpeakerEmbedder or separate)
        private InferenceSession _session;
        private string _modelPath;
        private readonly object _onnxLock = new();
        private bool _initialized = false;

        private DateTime _lastLog = DateTime.MinValue;

        private WordAlignedEmbedder() { }

        /// <summary>
        /// Initialize the embedder. Call after settings are available.
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            TryLoadModel();

            try
            {
                var svc = App.SettingsProvider;
                if (svc != null)
                {
                    svc.Changed += (s, snap) =>
                    {
                        try { TryLoadModel(); } catch { }
                    };
                }
            }
            catch { }
        }

        /// <summary>
        /// Add PCM16 samples to the ring buffer with absolute sample position tracking.
        /// </summary>
        public void AddPcm16(short[] pcm16, int count, long startSample)
        {
            if (pcm16 == null || count <= 0) return;
            if (count > pcm16.Length) count = pcm16.Length;

            lock (_ringLock)
            {
                // If startSample jumps forward significantly, we may have gaps - just continue
                _writePos = startSample;

                for (int i = 0; i < count; i++)
                {
                    int idx = (int)((_writePos + i) % RingBufferSamples);
                    _ring[idx] = pcm16[i] / 32768f;
                }

                _writePos = startSample + count;
            }
        }

        /// <summary>
        /// Add PCM16 bytes (for compatibility with existing byte-based calls).
        /// </summary>
        public void AddPcm16Bytes(byte[] buffer, int bytes, long startSample)
        {
            if (buffer == null || bytes <= 0) return;
            int samples = bytes / 2;
            var pcm = new short[samples];
            Buffer.BlockCopy(buffer, 0, pcm, 0, samples * 2);
            AddPcm16(pcm, samples, startSample);
        }

        /// <summary>
        /// Extract audio for a specific sample range from the ring buffer.
        /// Returns null if the range is not available (too old or invalid).
        /// </summary>
        public float[] ExtractAudio(long startSample, long endSample)
        {
            if (endSample <= startSample) return null;

            long len = endSample - startSample;
            if (len > MaxWordSamples) len = MaxWordSamples;
            if (len < MinWordSamples) return null;

            lock (_ringLock)
            {
                // Check if range is still in buffer
                long oldestAvailable = _writePos - RingBufferSamples;
                if (startSample < oldestAvailable)
                {
                    // Requested range is too old
                    return null;
                }

                var audio = new float[len];
                for (long i = 0; i < len; i++)
                {
                    int idx = (int)((startSample + i) % RingBufferSamples);
                    if (idx < 0) idx += RingBufferSamples;
                    audio[i] = _ring[idx];
                }

                return audio;
            }
        }

        /// <summary>
        /// Compute speaker embedding for a specific word/phrase segment.
        /// </summary>
        public float[] ComputeEmbedding(long startSample, long endSample)
        {
            var audio = ExtractAudio(startSample, endSample);
            if (audio == null || audio.Length < MinWordSamples) return null;

            // Check if audio is mostly silence
            double rms = 0;
            for (int i = 0; i < audio.Length; i++) rms += audio[i] * audio[i];
            rms = Math.Sqrt(rms / audio.Length);
            double db = 20.0 * Math.Log10(rms + 1e-9);

            var silenceDb = App.SettingsProvider?.Current?.Transcription?.SpeakerEmbeddingSilenceDb ?? -70.0;
            if (db < silenceDb)
            {
                // Too quiet - skip
                return null;
            }

            lock (_onnxLock)
            {
                if (_session == null)
                {
                    // Fallback to envelope (not reliable for diarization)
                    return ComputeEnvelopeFallback(audio);
                }

                return ComputeOnnxEmbedding(audio);
            }
        }

        /// <summary>
        /// Given a list of word timings (from Vosk JSON), compute embeddings for each word
        /// and assign speakers. Returns word-level speaker assignments.
        /// 
        /// Short phrases (< 800ms voiced audio) are treated as "low confidence" and use
        /// read-only identification instead of updating clusters - this prevents
        /// spurious speaker switches on brief utterances.
        /// </summary>
        public List<WordSpeakerAssignment> AssignSpeakersToWords(List<WordTiming> words, long sampleOffset = 0)
        {
            if (words == null || words.Count == 0) return new List<WordSpeakerAssignment>();

            var results = new List<WordSpeakerAssignment>();

            // Group consecutive words into phrases for more stable embeddings
            // Minimum phrase duration: 500ms, maximum: 2.5s
            var phrases = GroupWordsIntoPhrases(words, minMs: 500, maxMs: 2500);
            
            // Log phrase grouping
            var now = DateTime.UtcNow;
            if ((now - _lastLog).TotalSeconds >= 5.0)
            {
                Console.WriteLine($"[WordAligned] {words.Count} words -> {phrases.Count} phrases (offset={sampleOffset})");
                _lastLog = now;
            }

            foreach (var phrase in phrases)
            {
                long startSample = sampleOffset + (long)(phrase.StartSec * SampleRate);
                long endSample = sampleOffset + (long)(phrase.EndSec * SampleRate);
                
                // Calculate phrase duration
                double phraseDurMs = (phrase.EndSec - phrase.StartSec) * 1000;
                bool isLowConfidence = phraseDurMs < MinConfidentPhraseMs;
                
                // Ensure minimum duration for embedding computation
                if (endSample - startSample < MinWordSamples)
                {
                    // Extend to minimum
                    endSample = startSample + MinWordSamples;
                }

                var emb = ComputeEmbedding(startSample, endSample);
                string speaker = "Unknown";
                float confidence = 0f;

                if (emb != null && emb.Length > 0)
                {
                    if (isLowConfidence)
                    {
                        // Short phrase: use read-only identification (no cluster updates, no switching)
                        // This prevents spurious switches on brief utterances
                        (speaker, confidence) = SpeakerDiarizer.Instance.Identify(emb);
                        
                        var phraseText = string.Join(" ", phrase.Words.Select(w => w.Word));
                        if (phraseText.Length > 40) phraseText = phraseText.Substring(0, 40) + "...";
                        Console.WriteLine($"[WordAligned] Short phrase [{phraseDurMs:F0}ms < {MinConfidentPhraseMs}ms]: \"{phraseText}\" -> {speaker} (read-only, {confidence:F3})");
                    }
                    else
                    {
                        // Normal phrase: full identification with cluster updates
                        (speaker, confidence) = SpeakerDiarizer.Instance.IdentifyOrAssign(emb);
                        SpeakerDiarizer.Instance.ObserveFrame(startSample, endSample, speaker, confidence);
                        
                        // Log embedding details for diagnostics
                        var phraseText = string.Join(" ", phrase.Words.Select(w => w.Word));
                        if (phraseText.Length > 40) phraseText = phraseText.Substring(0, 40) + "...";
                        Console.WriteLine($"[WordAligned] Phrase [{phraseDurMs:F0}ms]: \"{phraseText}\" -> {speaker} ({confidence:F3})");
                    }
                }
                else
                {
                    // Log why we couldn't get embedding
                    var phraseText = string.Join(" ", phrase.Words.Select(w => w.Word));
                    if (phraseText.Length > 30) phraseText = phraseText.Substring(0, 30) + "...";
                    Console.WriteLine($"[WordAligned] No embedding for phrase: \"{phraseText}\" (samples {startSample}-{endSample})");
                }

                // Assign the phrase's speaker to all its constituent words
                foreach (var word in phrase.Words)
                {
                    results.Add(new WordSpeakerAssignment
                    {
                        Word = word.Word,
                        StartSec = word.StartSec,
                        EndSec = word.EndSec,
                        Speaker = speaker,
                        Confidence = confidence
                    });
                }
            }

            return results;
        }

        /// <summary>
        /// Given word assignments, determine the dominant speaker for an utterance.
        /// Uses weighted voting based on word duration.
        /// </summary>
        public static (string speaker, float confidence) GetDominantSpeaker(List<WordSpeakerAssignment> words)
        {
            if (words == null || words.Count == 0) return ("Unknown", 0f);

            var totals = new Dictionary<string, (double duration, double confSum, int count)>(StringComparer.OrdinalIgnoreCase);

            foreach (var w in words)
            {
                if (string.IsNullOrWhiteSpace(w.Speaker) || w.Speaker == "Unknown") continue;

                double dur = Math.Max(0, w.EndSec - w.StartSec);
                totals.TryGetValue(w.Speaker, out var cur);
                totals[w.Speaker] = (cur.duration + dur, cur.confSum + w.Confidence * dur, cur.count + 1);
            }

            if (totals.Count == 0) return ("Unknown", 0f);

            var best = totals.OrderByDescending(kv => kv.Value.duration).First();
            float avgConf = best.Value.duration > 0 ? (float)(best.Value.confSum / best.Value.duration) : 0f;

            return (best.Key, avgConf);
        }

        #region Private Helpers

        private List<PhraseGroup> GroupWordsIntoPhrases(List<WordTiming> words, int minMs, int maxMs)
        {
            var phrases = new List<PhraseGroup>();
            if (words.Count == 0) return phrases;

            var current = new PhraseGroup { Words = new List<WordTiming>() };
            current.Words.Add(words[0]);
            current.StartSec = words[0].StartSec;
            current.EndSec = words[0].EndSec;

            for (int i = 1; i < words.Count; i++)
            {
                var word = words[i];
                double currentDurMs = (current.EndSec - current.StartSec) * 1000;
                double gapMs = (word.StartSec - current.EndSec) * 1000;

                // Start new phrase if:
                // - Current phrase exceeds max duration
                // - Gap between words is large (>300ms suggests speaker change point)
                bool exceedsMax = (word.EndSec - current.StartSec) * 1000 > maxMs;
                bool hasGap = gapMs > 300;

                if (exceedsMax || (hasGap && currentDurMs >= minMs))
                {
                    if (current.Words.Count > 0)
                        phrases.Add(current);

                    current = new PhraseGroup { Words = new List<WordTiming>() };
                    current.Words.Add(word);
                    current.StartSec = word.StartSec;
                    current.EndSec = word.EndSec;
                }
                else
                {
                    current.Words.Add(word);
                    current.EndSec = word.EndSec;
                }
            }

            // Add final phrase
            if (current.Words.Count > 0)
                phrases.Add(current);

            // Handle phrases that are too short: merge with neighbors or expand
            var merged = new List<PhraseGroup>();
            foreach (var p in phrases)
            {
                double durMs = (p.EndSec - p.StartSec) * 1000;
                if (durMs < minMs && merged.Count > 0)
                {
                    // Merge with previous
                    var prev = merged[merged.Count - 1];
                    prev.Words.AddRange(p.Words);
                    prev.EndSec = p.EndSec;
                }
                else
                {
                    merged.Add(p);
                }
            }

            return merged;
        }

        private float[] ComputeEnvelopeFallback(float[] audio)
        {
            // Simple envelope-based pseudo-embedding (not reliable for real diarization)
            const int dims = 32;
            int seg = audio.Length / dims;
            if (seg <= 0) seg = 1;

            var v = new float[dims];
            for (int d = 0; d < dims; d++)
            {
                double sum = 0;
                int start = d * seg;
                for (int i = 0; i < seg && (start + i) < audio.Length; i++)
                    sum += Math.Abs(audio[start + i]);
                v[d] = (float)(sum / seg);
            }

            // L2 normalize
            double n2 = 0;
            for (int d = 0; d < dims; d++) n2 += v[d] * v[d];
            n2 = Math.Sqrt(n2) + 1e-9;
            for (int d = 0; d < dims; d++) v[d] = (float)(v[d] / n2);

            return v;
        }

        private float[] ComputeOnnxEmbedding(float[] audio)
        {
            try
            {
                // Convert to PCM16 for feature extraction
                var pcm16 = new short[audio.Length];
                for (int i = 0; i < audio.Length; i++)
                {
                    var x = audio[i];
                    if (x > 1f) x = 1f;
                    else if (x < -1f) x = -1f;
                    pcm16[i] = (short)(x * 32767f);
                }

                // Compute log-mel fbank features
                var feats = Fbank80.ComputeLogMelFbank80(pcm16, sampleRate: SampleRate);
                int tLen = feats.GetLength(0);
                if (tLen <= 0) return null;

                // Build input tensor [1,T,80]
                var inputName = _session.InputMetadata.Keys.First();
                var inputTensor = new DenseTensor<float>(new[] { 1, tLen, 80 });
                for (int t = 0; t < tLen; t++)
                    for (int f = 0; f < 80; f++)
                        inputTensor[0, t, f] = feats[t, f];

                var input = NamedOnnxValue.CreateFromTensor(inputName, inputTensor);

                using var results = _session.Run(new[] { input });
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
            catch (Exception ex)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastLog).TotalSeconds >= 10)
                {
                    Console.WriteLine($"[WordAlignedEmbedder] ONNX error: {ex.Message}");
                    _lastLog = now;
                }
                return null;
            }
        }

        private void TryLoadModel()
        {
            lock (_onnxLock)
            {
                try
                {
                    var path = App.SettingsProvider?.Current?.Transcription?.SpeakerEmbeddingModelPath;
                    if (string.IsNullOrWhiteSpace(path)) return;

                    var resolved = ResolvePath(path);
                    if (!File.Exists(resolved))
                    {
                        Console.WriteLine($"[WordAlignedEmbedder] Model not found: {resolved}");
                        return;
                    }

                    if (string.Equals(_modelPath, resolved, StringComparison.OrdinalIgnoreCase)) return;

                    try { _session?.Dispose(); } catch { }
                    _session = null;

                    var opts = new SessionOptions
                    {
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
                        InterOpNumThreads = 1,
                        IntraOpNumThreads = 1
                    };

                    _session = new InferenceSession(resolved, opts);
                    _modelPath = resolved;
                    Console.WriteLine($"[WordAlignedEmbedder] Loaded model: {_modelPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WordAlignedEmbedder] Model load failed: {ex.Message}");
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
                if (!Path.IsPathRooted(expanded))
                    expanded = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, expanded);
                return Path.GetFullPath(expanded);
            }
            catch { return p; }
        }

        #endregion

        #region Helper Classes

        private sealed class PhraseGroup
        {
            public List<WordTiming> Words;
            public double StartSec;
            public double EndSec;
        }

        #endregion
    }

    /// <summary>
    /// Represents a word with its timing from Vosk output.
    /// </summary>
    public sealed class WordTiming
    {
        public string Word { get; set; } = "";
        public double StartSec { get; set; }
        public double EndSec { get; set; }
    }

    /// <summary>
    /// Result of word-level speaker assignment.
    /// </summary>
    public sealed class WordSpeakerAssignment
    {
        public string Word { get; set; } = "";
        public double StartSec { get; set; }
        public double EndSec { get; set; }
        public string Speaker { get; set; } = "Unknown";
        public float Confidence { get; set; }
    }
}
