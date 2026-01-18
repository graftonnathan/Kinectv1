using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Kinectv1.Llm
{
    /// <summary>
    /// JSON-backed vector store with cosine similarity search.
    /// Supports chunked archiving and LLM-based summary rewriting on merge.
    /// </summary>
    public sealed class VectorStore
    {
        private readonly string _filePath;
        private readonly object _lock = new object();
        private VectorMemoryStore _store;

        private const double MERGE_THRESHOLD = 0.85;
        private const int MAX_CENTROIDS = 100;

        // Debug mode for recall diagnostics
        public static bool DebugMode { get; set; } = false;

        public VectorStore(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            _store = new VectorMemoryStore();
        }

        public async Task LoadAsync(CancellationToken ct = default)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    _store = new VectorMemoryStore();
                    return;
                }

                var json = await ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    _store = new VectorMemoryStore();
                    return;
                }

                lock (_lock)
                {
                    _store = JsonConvert.DeserializeObject<VectorMemoryStore>(json) ?? new VectorMemoryStore();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VectorStore load error: {ex.Message}");
                _store = new VectorMemoryStore();
            }
        }

        public async Task SaveAsync(CancellationToken ct = default)
        {
            try
            {
                string json;
                lock (_lock)
                {
                    json = JsonConvert.SerializeObject(_store, Formatting.Indented);
                }

                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                await WriteAllTextAsync(_filePath, json, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VectorStore save error: {ex.Message}");
            }
        }

        /// <summary>
        /// Add or merge a centroid with LLM-based summary rewriting on merge.
        /// </summary>
        public async Task AddOrMergeCentroidAsync(
            float[] embedding, 
            string summary, 
            string[] speakers = null, 
            double importance = 0.5,
            Func<string, string, Task<string>> rewriteSummaryFunc = null)
        {
            if (embedding == null || embedding.Length == 0) return;

            // Normalize input embedding
            embedding = NormalizeVector(embedding);

            TopicCentroid bestMatch = null;
            double bestSimilarity = 0;
            string existingSummary = null;

            lock (_lock)
            {
                var centroids = _store.Centroids?.ToList() ?? new List<TopicCentroid>();

                foreach (var c in centroids)
                {
                    var existingEmb = c.GetEmbedding();
                    if (existingEmb.Length == 0) continue;
                    if (existingEmb.Length != embedding.Length) continue; // Dimension mismatch protection

                    // Normalize stored embedding for comparison
                    existingEmb = NormalizeVector(existingEmb);
                    double sim = CosineSimilarity(embedding, existingEmb);
                    
                    if (sim > bestSimilarity)
                    {
                        bestSimilarity = sim;
                        bestMatch = c;
                    }
                }

                if (bestMatch != null && bestSimilarity >= MERGE_THRESHOLD)
                {
                    existingSummary = bestMatch.Summary;
                }
            }

            // Handle merge with summary rewrite
            if (bestMatch != null && bestSimilarity >= MERGE_THRESHOLD)
            {
                string rewrittenSummary;
                
                if (rewriteSummaryFunc != null && !string.IsNullOrWhiteSpace(existingSummary))
                {
                    try
                    {
                        rewrittenSummary = await rewriteSummaryFunc(existingSummary, summary).ConfigureAwait(false);
                        Console.WriteLine($"?? Merged centroid (sim={bestSimilarity:F3}) with rewritten summary ({rewrittenSummary.Length} chars)");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"?? Summary rewrite failed, using fallback: {ex.Message}");
                        rewrittenSummary = FallbackMergeSummary(existingSummary, summary);
                    }
                }
                else
                {
                    rewrittenSummary = string.IsNullOrWhiteSpace(existingSummary) ? summary : FallbackMergeSummary(existingSummary, summary);
                }

                lock (_lock)
                {
                    bestMatch.MergeEmbeddingOnly(embedding);
                    bestMatch.SetCanonicalSummary(TruncateText(rewrittenSummary, 1800));
                    bestMatch.Importance = Math.Max(bestMatch.Importance, importance);
                    
                    if (speakers != null && speakers.Length > 0)
                    {
                        bestMatch.Speakers = (bestMatch.Speakers ?? Array.Empty<string>())
                            .Concat(speakers).Distinct().ToArray();
                    }
                }
            }
            else
            {
                // New centroid
                lock (_lock)
                {
                    var centroids = _store.Centroids?.ToList() ?? new List<TopicCentroid>();
                    
                    var newCentroid = new TopicCentroid
                    {
                        Summary = TruncateText(summary, 1800),
                        Speakers = speakers ?? Array.Empty<string>(),
                        Importance = importance
                    };
                    newCentroid.SetEmbedding(embedding);
                    centroids.Add(newCentroid);

                    Console.WriteLine($"?? Created new centroid ({summary.Length} chars summary)");

                    if (centroids.Count > MAX_CENTROIDS)
                    {
                        var toRemove = centroids
                            .OrderBy(c => c.Importance)
                            .ThenBy(c => c.UpdatedAt)
                            .First();
                        centroids.Remove(toRemove);
                        Console.WriteLine($"?? Evicted oldest low-importance centroid");
                    }

                    _store.Centroids = centroids.ToArray();
                }
            }
        }

        /// <summary>
        /// Synchronous version for backward compatibility.
        /// </summary>
        public void AddOrMergeCentroid(float[] embedding, string summary, 
            string[] speakers = null, double importance = 0.5)
        {
            AddOrMergeCentroidAsync(embedding, summary, speakers, importance, null)
                .ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Search with detailed scoring and debug logging.
        /// </summary>
        public List<(TopicCentroid Centroid, double Score)> Search(
            float[] queryEmbedding,
            int topK = 10,
            double recencyBoostFactor = 0.1,
            double minSimilarity = 0.0)
        {
            if (queryEmbedding == null || queryEmbedding.Length == 0)
                return new List<(TopicCentroid, double)>();

            // Normalize query embedding
            queryEmbedding = NormalizeVector(queryEmbedding);

            lock (_lock)
            {
                if (_store.Centroids == null || _store.Centroids.Length == 0)
                    return new List<(TopicCentroid, double)>();

                var now = DateTime.UtcNow;
                var maxAge = TimeSpan.FromDays(30);

                // Score all centroids with detailed breakdown
                var scored = _store.Centroids
                    .Select(c =>
                    {
                        var emb = c.GetEmbedding();
                        if (emb.Length == 0 || emb.Length != queryEmbedding.Length)
                            return (Centroid: c, Cosine: 0.0, Recency: 0.0, Importance: 0.0, Final: 0.0);

                        // Normalize stored embedding
                        emb = NormalizeVector(emb);
                        
                        double cosine = CosineSimilarity(queryEmbedding, emb);
                        var age = now - c.UpdatedAt;
                        double recency = Math.Max(0, 1.0 - (age.TotalDays / maxAge.TotalDays));
                        double importance = c.Importance;

                        // Weighted scoring: semantic similarity dominates
                        // cosine: 0-1, recency: 0-1, importance: 0-1
                        double finalScore = (cosine * 0.85) + (recency * recencyBoostFactor) + (importance * 0.05);

                        return (Centroid: c, Cosine: cosine, Recency: recency, Importance: importance, Final: finalScore);
                    })
                    .OrderByDescending(x => x.Final)
                    .ToList();

                // Debug logging
                if (DebugMode && scored.Count > 0)
                {
                    Console.WriteLine($"?? SEARCH DEBUG: {scored.Count} centroids, query dim={queryEmbedding.Length}");
                    var top20 = scored.Take(20).ToList();
                    for (int i = 0; i < top20.Count; i++)
                    {
                        var s = top20[i];
                        var preview = s.Centroid.Summary?.Length > 80 
                            ? s.Centroid.Summary.Substring(0, 80) + "..." 
                            : s.Centroid.Summary ?? "(empty)";
                        Console.WriteLine($"  #{i+1}: cos={s.Cosine:F3} rec={s.Recency:F2} imp={s.Importance:F2} final={s.Final:F3} merge={s.Centroid.MergeCount} | {preview}");
                    }
                }

                return scored
                    .Where(x => x.Final >= minSimilarity)
                    .Take(topK)
                    .Select(x => (x.Centroid, x.Final))
                    .ToList();
            }
        }

        /// <summary>
        /// Search returning raw cosine scores (no boosts) for debugging.
        /// </summary>
        public List<(TopicCentroid Centroid, double RawCosine)> SearchRawCosine(float[] queryEmbedding, int topK = 20)
        {
            if (queryEmbedding == null || queryEmbedding.Length == 0)
                return new List<(TopicCentroid, double)>();

            queryEmbedding = NormalizeVector(queryEmbedding);

            lock (_lock)
            {
                if (_store.Centroids == null || _store.Centroids.Length == 0)
                    return new List<(TopicCentroid, double)>();

                return _store.Centroids
                    .Select(c =>
                    {
                        var emb = c.GetEmbedding();
                        if (emb.Length == 0 || emb.Length != queryEmbedding.Length)
                            return (Centroid: c, Cosine: 0.0);
                        
                        emb = NormalizeVector(emb);
                        return (Centroid: c, Cosine: CosineSimilarity(queryEmbedding, emb));
                    })
                    .OrderByDescending(x => x.Cosine)
                    .Take(topK)
                    .ToList();
            }
        }

        public void AddPinnedFact(PinnedFact fact)
        {
            if (fact == null) return;
            lock (_lock)
            {
                var list = _store.PinnedFacts?.ToList() ?? new List<PinnedFact>();
                list.Add(fact);
                _store.PinnedFacts = list.ToArray();
            }
        }

        public PinnedFact[] GetPinnedFacts()
        {
            lock (_lock)
            {
                return _store.PinnedFacts?.ToArray() ?? Array.Empty<PinnedFact>();
            }
        }

        public bool RemovePinnedFact(string id)
        {
            lock (_lock)
            {
                var list = _store.PinnedFacts?.ToList();
                if (list == null) return false;
                var removed = list.RemoveAll(f => f.Id == id);
                _store.PinnedFacts = list.ToArray();
                return removed > 0;
            }
        }

        public int CentroidCount
        {
            get { lock (_lock) { return _store.Centroids?.Length ?? 0; } }
        }

        public void Clear()
        {
            lock (_lock) { _store = new VectorMemoryStore(); }
        }

        public (int centroids, int totalMerged, long estimatedBytes) GetStats()
        {
            lock (_lock)
            {
                int centroids = _store.Centroids?.Length ?? 0;
                int totalMerged = _store.Centroids?.Sum(c => c.MergeCount) ?? 0;
                long bytes = 0;
                int totalSummaryChars = 0;
                if (_store.Centroids != null)
                {
                    foreach (var c in _store.Centroids)
                    {
                        bytes += (long)((c.EmbeddingBase64?.Length ?? 0) * 0.75);
                        bytes += (c.Summary?.Length ?? 0) * 2;
                        bytes += 100;
                        totalSummaryChars += c.Summary?.Length ?? 0;
                    }
                }
                Console.WriteLine($"?? VectorStore stats: {centroids} centroids, {totalMerged} merged, ~{totalSummaryChars} chars stored");
                return (centroids, totalMerged, bytes);
            }
        }

        #region Helpers

        private static string TruncateText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (text.Length <= maxLength) return text;
            return text.Substring(0, maxLength - 3) + "...";
        }

        private static string FallbackMergeSummary(string oldSummary, string newSummary)
        {
            if (string.IsNullOrWhiteSpace(oldSummary)) return newSummary ?? string.Empty;
            if (string.IsNullOrWhiteSpace(newSummary)) return oldSummary;
            
            var combined = oldSummary + " | " + newSummary;
            if (combined.Length > 1500)
            {
                var excess = combined.Length - 1400;
                var breakPoint = combined.IndexOf(" | ", excess);
                if (breakPoint > 0)
                    combined = "..." + combined.Substring(breakPoint + 3);
                else
                    combined = "..." + combined.Substring(excess);
            }
            return combined;
        }

        /// <summary>
        /// Normalize vector to unit length for accurate cosine similarity.
        /// </summary>
        private static float[] NormalizeVector(float[] vec)
        {
            if (vec == null || vec.Length == 0) return vec;

            double norm = 0;
            for (int i = 0; i < vec.Length; i++)
                norm += vec[i] * vec[i];
            
            norm = Math.Sqrt(norm);
            if (norm < 1e-10) return vec; // Avoid division by zero

            var result = new float[vec.Length];
            for (int i = 0; i < vec.Length; i++)
                result[i] = (float)(vec[i] / norm);
            
            return result;
        }

        /// <summary>
        /// Cosine similarity between two (ideally unit-normalized) vectors.
        /// </summary>
        private static double CosineSimilarity(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length || a.Length == 0)
                return 0.0;

            double dot = 0;
            for (int i = 0; i < a.Length; i++)
                dot += a[i] * b[i];

            // If vectors are normalized, dot product = cosine
            // But we still compute norms for safety
            double normA = 0, normB = 0;
            for (int i = 0; i < a.Length; i++)
            {
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }

            if (normA < 1e-10 || normB < 1e-10) return 0.0;
            return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
        }

        #endregion

        #region File I/O

        private static readonly object _fileLock = new object();

        private static Task<string> ReadAllTextAsync(string path, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                const int maxRetries = 5;
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    try
                    {
                        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs, Encoding.UTF8))
                            return sr.ReadToEnd();
                    }
                    catch (IOException) when (attempt < maxRetries - 1)
                    {
                        Thread.Sleep(50 * (attempt + 1));
                    }
                }
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    return sr.ReadToEnd();
            }, ct);
        }

        private static Task WriteAllTextAsync(string path, string content, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                const int maxRetries = 5;
                lock (_fileLock)
                {
                    for (int attempt = 0; attempt < maxRetries; attempt++)
                    {
                        try
                        {
                            var tempPath = path + ".tmp";
                            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            using (var sw = new StreamWriter(fs, Encoding.UTF8))
                                sw.Write(content);

                            if (File.Exists(path))
                            {
                                var bakPath = path + ".bak";
                                if (File.Exists(bakPath)) File.Delete(bakPath);
                                File.Replace(tempPath, path, bakPath);
                            }
                            else
                            {
                                File.Move(tempPath, path);
                            }
                            return;
                        }
                        catch (IOException) when (attempt < maxRetries - 1)
                        {
                            Thread.Sleep(100 * (attempt + 1));
                        }
                    }
                    File.WriteAllText(path, content, Encoding.UTF8);
                }
            }, ct);
        }

        #endregion
    }
}
