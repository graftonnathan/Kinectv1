using System;
using System.Collections.Generic;
using System.Linq;

namespace Kinectv1.Services.Transcription
{
    /// <summary>
    /// Speaker diarization for transcription mode.
    /// Tracks and labels speakers (Speaker A, B, C...) based on voice embeddings
    /// when speakers are not enrolled in the main SpeakerIdentifier.
    /// </summary>
    public sealed class SpeakerDiarizer
    {
        private static readonly Lazy<SpeakerDiarizer> _instance = new(() => new SpeakerDiarizer());
        public static SpeakerDiarizer Instance => _instance.Value;

        // Speaker clusters: label -> list of embeddings
        private readonly Dictionary<string, List<float[]>> _clusters = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        // Track last assigned label index
        private int _nextLabelIndex = 0;

        // Default similarity threshold - calibrated for pyannote embeddings
        // Same-speaker similarity is often 0.1-0.4, different-speaker is -0.1 to 0.15
        private const double DefaultSimilarityThreshold = 0.10;

        // Maximum embeddings to keep per cluster (for memory management)
        private const int MaxEmbeddingsPerCluster = 20;

        // Label prefix
        private const string LabelPrefix = "Speaker ";

        private SpeakerDiarizer() { }

        /// <summary>
        /// Reset all clusters and start fresh (call at session start).
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _clusters.Clear();
                _nextLabelIndex = 0;
                Console.WriteLine("[Diarizer] Reset - all clusters cleared");
            }
        }

        /// <summary>
        /// Get the current number of identified speakers.
        /// </summary>
        public int SpeakerCount
        {
            get { lock (_lock) return _clusters.Count; }
        }

        /// <summary>
        /// Get all current speaker labels.
        /// </summary>
        public string[] GetSpeakerLabels()
        {
            lock (_lock)
            {
                return _clusters.Keys.OrderBy(k => k).ToArray();
            }
        }

        /// <summary>
        /// Identify or assign a speaker label based on voice embedding.
        /// Returns the speaker label (e.g., "Speaker A") and confidence score.
        /// </summary>
        public (string label, float confidence) IdentifyOrAssign(float[] embedding)
        {
            if (embedding == null || embedding.Length == 0)
            {
                return ("Unknown", 0f);
            }

            lock (_lock)
            {
                // Get threshold from settings
                double threshold = GetSimilarityThreshold();

                // First, try to match against existing clusters
                string bestMatch = null;
                double bestScore = -1.0;
                double secondBestScore = -1.0;
                string secondBestMatch = null;

                foreach (var kvp in _clusters)
                {
                    var clusterCenter = ComputeCentroid(kvp.Value);
                    var similarity = CosineSimilarity(embedding, clusterCenter);

                    // Debug: log detailed comparison for first cluster
                    if (_clusters.Count == 1 && kvp.Value.Count <= 3)
                    {
                        // Show embedding comparison details
                        var embFirst4 = string.Join(",", embedding.Take(4).Select(v => v.ToString("F3")));
                        var centFirst4 = string.Join(",", clusterCenter.Take(4).Select(v => v.ToString("F3")));
                        Console.WriteLine($"[Diarizer] Compare: new=[{embFirst4}...] vs {kvp.Key}=[{centFirst4}...] = {similarity:F4}");
                    }

                    if (similarity > bestScore)
                    {
                        secondBestScore = bestScore;
                        secondBestMatch = bestMatch;
                        bestScore = similarity;
                        bestMatch = kvp.Key;
                    }
                    else if (similarity > secondBestScore)
                    {
                        secondBestScore = similarity;
                        secondBestMatch = kvp.Key;
                    }
                }

                // Calculate margin between best and second best
                double margin = bestScore - secondBestScore;
                
                // Log similarity scores periodically for debugging (every 10 seconds, or on significant events)
                var now = DateTime.UtcNow;
                bool isSignificantEvent = (bestMatch != null && bestScore < threshold) || 
                                          (_clusters.Count > 1 && margin < 0.1);
                bool shouldLog = _clusters.Count > 0 && 
                                ((now - _lastLogTime).TotalSeconds >= 10.0 || isSignificantEvent);
                if (shouldLog)
                {
                    Console.WriteLine($"[Diarizer] Scores: best={bestMatch}({bestScore:F3}), second={secondBestMatch}({secondBestScore:F3}), margin={margin:F3}, thr={threshold:F3}, clusters={_clusters.Count}");
                    _lastLogTime = now;
                }

                // Require a margin to switch speakers (helps prevent chattering)
                const double MinSwitchMargin = 0.02;

                if (bestMatch != null && bestScore >= threshold)
                {
                    // If we have multiple clusters and the margin is too small, it's ambiguous
                    // but still assign to best match
                    if (_clusters.Count > 1 && secondBestScore >= 0 && margin < MinSwitchMargin)
                    {
                        // Ambiguous - could be either speaker, still add to best
                    }
                    
                    // Match found - add embedding to cluster and return
                    AddEmbeddingToCluster(bestMatch, embedding);
                    return (bestMatch, (float)bestScore);
                }

                // No match above threshold - create new cluster
                var newLabel = GetNextLabel();
                _clusters[newLabel] = new List<float[]> { (float[])embedding.Clone() };
                Console.WriteLine($"[Diarizer] NEW SPEAKER: {newLabel} (best={bestMatch}:{bestScore:F3} < thr={threshold:F3}, clusters={_clusters.Count})");

                return (newLabel, 1.0f);
            }
        }

        private static DateTime _lastLogTime = DateTime.MinValue;

        /// <summary>
        /// Identify speaker without adding to cluster (read-only lookup).
        /// </summary>
        public (string label, float confidence) Identify(float[] embedding)
        {
            if (embedding == null || embedding.Length == 0)
            {
                return ("Unknown", 0f);
            }

            lock (_lock)
            {
                if (_clusters.Count == 0)
                {
                    return ("Unknown", 0f);
                }

                string bestMatch = null;
                double bestScore = -1.0;

                foreach (var kvp in _clusters)
                {
                    var clusterCenter = ComputeCentroid(kvp.Value);
                    var similarity = CosineSimilarity(embedding, clusterCenter);

                    if (similarity > bestScore)
                    {
                        bestScore = similarity;
                        bestMatch = kvp.Key;
                    }
                }

                double threshold = GetSimilarityThreshold();
                if (bestMatch != null && bestScore >= threshold)
                {
                    return (bestMatch, (float)bestScore);
                }

                return ("Unknown", (float)Math.Max(0, bestScore));
            }
        }

        /// <summary>
        /// Manually merge two speakers (useful for corrections).
        /// </summary>
        public bool MergeSpeakers(string label1, string label2)
        {
            lock (_lock)
            {
                if (!_clusters.ContainsKey(label1) || !_clusters.ContainsKey(label2))
                {
                    return false;
                }

                // Merge embeddings from label2 into label1
                var embeddings2 = _clusters[label2];
                foreach (var emb in embeddings2)
                {
                    AddEmbeddingToCluster(label1, emb);
                }

                _clusters.Remove(label2);
                Console.WriteLine($"[Diarizer] Merged {label2} into {label1}");
                return true;
            }
        }

        /// <summary>
        /// Get cluster statistics for debugging/display.
        /// </summary>
        public Dictionary<string, int> GetClusterStats()
        {
            lock (_lock)
            {
                return _clusters.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count);
            }
        }

        private string GetNextLabel()
        {
            // Generate labels: Speaker A, Speaker B, ..., Speaker Z, Speaker AA, etc.
            var label = IndexToLabel(_nextLabelIndex);
            _nextLabelIndex++;
            return label;
        }

        private static string IndexToLabel(int index)
        {
            // Convert index to letter(s): 0=A, 1=B, ..., 25=Z, 26=AA, 27=AB, etc.
            var result = new List<char>();
            int remaining = index;

            do
            {
                result.Insert(0, (char)('A' + (remaining % 26)));
                remaining = remaining / 26 - 1;
            } while (remaining >= 0);

            return LabelPrefix + new string(result.ToArray());
        }

        private void AddEmbeddingToCluster(string label, float[] embedding)
        {
            if (!_clusters.TryGetValue(label, out var list))
            {
                list = new List<float[]>();
                _clusters[label] = list;
            }

            list.Add((float[])embedding.Clone());

            // Limit embeddings per cluster
            while (list.Count > MaxEmbeddingsPerCluster)
            {
                list.RemoveAt(0); // Remove oldest
            }
        }

        private static float[] ComputeCentroid(List<float[]> embeddings)
        {
            if (embeddings == null || embeddings.Count == 0)
            {
                return Array.Empty<float>();
            }

            int dim = embeddings[0].Length;
            var centroid = new double[dim];

            foreach (var emb in embeddings)
            {
                for (int i = 0; i < dim && i < emb.Length; i++)
                {
                    centroid[i] += emb[i];
                }
            }

            var result = new float[dim];
            double inv = 1.0 / embeddings.Count;
            for (int i = 0; i < dim; i++)
            {
                result[i] = (float)(centroid[i] * inv);
            }

            // L2 normalize
            double norm = 0;
            for (int i = 0; i < dim; i++)
            {
                norm += result[i] * result[i];
            }
            norm = Math.Sqrt(norm) + 1e-9;
            for (int i = 0; i < dim; i++)
            {
                result[i] = (float)(result[i] / norm);
            }

            return result;
        }

        private static double CosineSimilarity(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length == 0 || b.Length == 0)
            {
                return -1.0;
            }

            int len = Math.Min(a.Length, b.Length);
            double dot = 0, normA = 0, normB = 0;

            for (int i = 0; i < len; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }

            if (normA < 1e-9 || normB < 1e-9)
            {
                return -1.0;
            }

            return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
        }

        /// <summary>
        /// Get the similarity threshold from transcription settings.
        /// 0 = very lenient (fewer speakers detected), 1 = very strict (more speakers detected)
        /// 
        /// Note: The pyannote embedding model produces embeddings with lower similarity scores
        /// than expected. Same-speaker similarity can be as low as 0.1-0.4 depending on audio quality.
        /// Different-speaker similarity is typically -0.1 to 0.15.
        /// </summary>
        private static double GetSimilarityThreshold()
        {
            try
            {
                var threshold = App.SettingsProvider?.Current?.Transcription?.DiarizationSimilarityThreshold;
                if (threshold.HasValue && threshold.Value >= 0 && threshold.Value <= 1)
                {
                    // Map 0-1 setting to actual threshold:
                    // 0 (less strict) -> 0.05 (very low threshold, matches easily, fewer speakers)
                    // 0.5 (moderate) -> 0.125
                    // 1 (more strict) -> 0.20 (higher threshold, harder to match, more speakers)
                    // These values are calibrated for pyannote embeddings which have lower similarity scores
                    var mapped = 0.05 + (threshold.Value * 0.15);
                    
                    // Only log occasionally to reduce spam
                    var now = DateTime.UtcNow;
                    if ((now - _lastThresholdLog).TotalSeconds >= 10.0)
                    {
                        Console.WriteLine($"[Diarizer] Threshold: setting={threshold.Value:F2} -> actual={mapped:F3}");
                        _lastThresholdLog = now;
                    }
                    return mapped;
                }
            }
            catch { }

            return DefaultSimilarityThreshold;
        }

        private static DateTime _lastThresholdLog = DateTime.MinValue;
    }
}
