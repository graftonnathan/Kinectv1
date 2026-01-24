using System;
using System.Collections.Generic;
using System.Linq;

namespace Kinectv1.Services.Transcription
{
    /// <summary>
    /// Speaker diarization for transcription mode.
    /// Tracks and labels speakers (Speaker A, B, ...) based on voice embeddings.
    /// 
    /// This diarizer is designed to work with phrase-level embeddings from WordAlignedEmbedder,
    /// computed on actual speech segments aligned with Vosk word timestamps.
    /// 
    /// KEY DESIGN:
    /// - Store multiple embedding samples per speaker to handle natural voice variance
    /// - Use best-of-N matching for robust speaker identification
    /// - Require consistent evidence before creating new speakers or switching
    /// - No arbitrary cluster limits - let the data determine speaker count
    /// - Track score patterns to detect when multiple speakers are being mixed into one cluster
    /// </summary>
    public sealed class SpeakerDiarizer
    {
        private static readonly Lazy<SpeakerDiarizer> _instance = new(() => new SpeakerDiarizer());
        public static SpeakerDiarizer Instance => _instance.Value;

        private sealed class Cluster
        {
            public string Label;
            public List<float[]> Embeddings;    // Multiple samples for robust matching
            public int MatchCount;               // How many embeddings matched this cluster
            public DateTime LastSeenUtc;
            public DateTime CreatedUtc;
        }

        private readonly Dictionary<string, Cluster> _clusters = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        private readonly List<(long s, long e, string label, float score)> _timeline = new();
        private const int MaxTimeline = 2000;

        private int _nextLabelIndex = 0;

        // === CONFIGURATION ===
        // Max embeddings to keep per cluster (for memory and matching speed)
        private const int MaxEmbeddingsPerCluster = 50;
        
        // Number of top matches to average when computing cluster score
        private const int TopKForScoring = 5;
        
        // Minimum number of embeddings before a cluster is "established"
        private const int MinEmbeddingsForEstablished = 3;
        
        // How much better must another cluster be to trigger a switch consideration
        private const double SwitchMargin = 0.10;
        
        // Consecutive phrase assignments needed to confirm a switch
        private const int SwitchConfirmCount = 2;
        
        // Don't switch back immediately after a switch
        private const int SwitchCooldownMs = 1500;

        // === PENDING SWITCH BUFFER (prevents centroid contamination) ===
        // How long a pending switch can hang before we discard it
        private const int PendingExpireMs = 1200;
        
        // Only commit embeddings when confident (prevents drift on garbage/near-silence)
        private const double CommitMinScore = 0.18;
        
        // Stop committing when A/B are too close
        private const double CommitMinMargin = 0.04;

        // === NEW SPEAKER DETECTION ===
        // If best score is below this, it's likely a different speaker
        // ECAPA-TDNN: same speaker typically scores 0.5-0.9, different speaker 0.1-0.4
        private const double NewSpeakerThreshold = 0.35;
        
        // Track recent scores to detect multi-speaker mixing
        private readonly List<double> _recentBestScores = new();
        private const int RecentScoreWindowSize = 15;
        
        // If score variance is high AND mean is mediocre, we likely have mixed speakers
        private const double MixedSpeakerVarianceThreshold = 0.015;
        private const double MixedSpeakerMeanThreshold = 0.55;
        
        // Count consecutive low scores to trigger new speaker
        private int _consecutiveLowScores = 0;
        private const int ConsecutiveLowScoresForNewSpeaker = 3;

        private const string LabelPrefix = "Speaker ";

        // State tracking
        private string _currentSpeaker = null;
        private DateTime _lastSwitchUtc = DateTime.MinValue;
        private string _pendingSwitchTarget = null;
        private int _pendingSwitchCount = 0;
        private long _pendingStartMs = 0;
        private readonly List<float[]> _pendingEmbeddings = new();
        private int _totalPhrases = 0;
        
        private static DateTime _lastLogTime = DateTime.MinValue;

        private SpeakerDiarizer() { }

        public void Reset()
        {
            lock (_lock)
            {
                _clusters.Clear();
                _timeline.Clear();
                _nextLabelIndex = 0;
                _currentSpeaker = null;
                _lastSwitchUtc = DateTime.MinValue;
                _pendingSwitchTarget = null;
                _pendingSwitchCount = 0;
                _pendingStartMs = 0;
                _pendingEmbeddings.Clear();
                _totalPhrases = 0;
                _recentBestScores.Clear();
                _consecutiveLowScores = 0;
                Console.WriteLine("[Diarizer] Reset - all clusters cleared");
            }
        }

        public int SpeakerCount
        {
            get { lock (_lock) return _clusters.Count; }
        }

        public string[] GetSpeakerLabels()
        {
            lock (_lock)
            {
                return _clusters.Keys.OrderBy(k => k).ToArray();
            }
        }

        /// <summary>
        /// Main entry point: identify which speaker this embedding belongs to, or create a new one.
        /// Returns (speaker label, confidence score).
        /// 
        /// KEY FIX: Buffer embeddings during pending switch and only commit them if the switch confirms.
        /// This prevents contaminating clusters with the wrong speaker's audio during uncertain periods.
        /// </summary>
        public (string label, float confidence) IdentifyOrAssign(float[] embedding)
        {
            if (embedding == null || embedding.Length == 0) return ("Unknown", 0f);
            
            // Validate embedding
            for (int i = 0; i < embedding.Length; i++)
            {
                if (float.IsNaN(embedding[i]) || float.IsInfinity(embedding[i]))
                    return ("Unknown", 0f);
            }

            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _totalPhrases++;

                // === FIRST SPEAKER ===
                if (_clusters.Count == 0)
                {
                    ClearPending();
                    return CreateSpeaker(embedding, now, "first speaker");
                }

                // === SCORE ALL CLUSTERS ===
                var scores = new List<(string label, double score, Cluster cluster)>();
                
                foreach (var kv in _clusters)
                {
                    var score = ComputeClusterScore(embedding, kv.Value);
                    scores.Add((kv.Key, score, kv.Value));
                }
                
                // Sort by score descending
                scores.Sort((a, b) => b.score.CompareTo(a.score));
                
                var best = scores[0];
                var bestLabel = best.label;
                var bestScore = best.score;
                var bestCluster = best.cluster;
                
                // Track recent scores for pattern detection
                TrackRecentScore(bestScore);
                
                // Get second best for margin calculation
                double secondBestScore = scores.Count > 1 ? scores[1].score : double.NegativeInfinity;
                double margin = bestScore - secondBestScore;
                
                // Current speaker's score (if we have a current speaker)
                double currentScore = 0;
                Cluster currentCluster = null;
                if (!string.IsNullOrEmpty(_currentSpeaker))
                {
                    var currentEntry = scores.FirstOrDefault(s => 
                        string.Equals(s.label, _currentSpeaker, StringComparison.OrdinalIgnoreCase));
                    currentScore = currentEntry.score;
                    currentCluster = currentEntry.cluster;
                }

                // Periodic logging
                if ((now - _lastLogTime).TotalSeconds >= 3.0)
                {
                    var scoreStr = string.Join(", ", scores.Take(4).Select(s => $"{s.label}={s.score:F3}"));
                    var (mean, variance) = GetRecentScoreStats();
                    Console.WriteLine($"[Diarizer] current={_currentSpeaker}, best={bestLabel}({bestScore:F3}), scores=[{scoreStr}], clusters={_clusters.Count}, phrases={_totalPhrases}, recentMean={mean:F3}, var={variance:F4}, lowStreak={_consecutiveLowScores}");
                    _lastLogTime = now;
                }

                // === NEW SPEAKER DETECTION ===
                bool shouldCreateNewSpeaker = ShouldCreateNewSpeaker(bestScore, bestCluster);
                
                if (shouldCreateNewSpeaker)
                {
                    _consecutiveLowScores = 0;
                    ClearPending();
                    return CreateSpeaker(embedding, now, $"detected new voice (best={bestScore:F3})");
                }
                
                // Update low score streak
                if (bestScore < NewSpeakerThreshold)
                    _consecutiveLowScores++;
                else
                    _consecutiveLowScores = 0;
                
                // === DECISION LOGIC ===
                
                // Case 1: Best is current speaker (or current is not set)
                bool bestIsCurrent = string.Equals(bestLabel, _currentSpeaker, StringComparison.OrdinalIgnoreCase);
                if (bestIsCurrent || string.IsNullOrEmpty(_currentSpeaker))
                {
                    // Clear pending and commit normally
                    ClearPending();
                    
                    // Only commit if reasonably confident (prevents drift on garbage/near-silence)
                    if (bestScore >= CommitMinScore)
                    {
                        AddToCluster(bestCluster, embedding);
                    }
                    
                    _currentSpeaker = bestLabel;
                    return (bestLabel, (float)bestScore);
                }
                
                // Case 2: A different speaker has the best score - treat as potential switch
                double deltaVsCurrent = bestScore - currentScore;
                
                // Expire stale pending
                if (_pendingSwitchCount > 0 && (nowMs - _pendingStartMs) > PendingExpireMs)
                {
                    ClearPending();
                }
                
                // If margin is small, stick with current (avoid flip-flopping)
                // But DO NOT update current cluster during uncertainty
                if (deltaVsCurrent < SwitchMargin)
                {
                    ClearPending();
                    
                    // Only commit to current cluster if we're confident
                    if (currentScore >= CommitMinScore && deltaVsCurrent < -CommitMinMargin)
                    {
                        AddToCluster(currentCluster, embedding);
                    }
                    // else: don't commit during ambiguous period
                    
                    return (_currentSpeaker, (float)currentScore);
                }
                
                // Significant margin - consider switching
                // Check cooldown
                if ((now - _lastSwitchUtc).TotalMilliseconds < SwitchCooldownMs)
                {
                    // In cooldown, stick with current but track pending
                    // DO NOT add to current cluster - buffer instead
                    if (_pendingSwitchTarget != bestLabel)
                    {
                        _pendingSwitchTarget = bestLabel;
                        _pendingSwitchCount = 0;
                        _pendingEmbeddings.Clear();
                        _pendingStartMs = nowMs;
                    }
                    _pendingSwitchCount++;
                    _pendingEmbeddings.Add((float[])embedding.Clone());
                    
                    return (_currentSpeaker, (float)currentScore);
                }
                
                // Start or continue pending toward best.label
                if (_pendingSwitchTarget == null || 
                    !string.Equals(_pendingSwitchTarget, bestLabel, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingSwitchTarget = bestLabel;
                    _pendingSwitchCount = 0;
                    _pendingEmbeddings.Clear();
                    _pendingStartMs = nowMs;
                }
                
                _pendingSwitchCount++;
                _pendingEmbeddings.Add((float[])embedding.Clone());
                
                Console.WriteLine($"[Diarizer] Possible switch: {_currentSpeaker} -> {bestLabel} ({_pendingSwitchCount}/{SwitchConfirmCount}) " +
                    $"best={bestScore:F3} cur={currentScore:F3} dCur={deltaVsCurrent:F3} margin={margin:F3}");
                
                // Confirm switch ONLY if:
                // - enough consecutive frames
                // - minimum time since last switch (cooldown passed)
                // - and the candidate is actually meaningfully better than current
                var strongEnough = (bestScore >= CommitMinScore) && (deltaVsCurrent >= CommitMinMargin);
                
                if (_pendingSwitchCount >= SwitchConfirmCount && strongEnough)
                {
                    var target = _pendingSwitchTarget;
                    var targetCluster = _clusters[target];
                    
                    // Commit buffered embeddings to the target cluster ONLY (this is the critical fix)
                    foreach (var e in _pendingEmbeddings)
                    {
                        AddToCluster(targetCluster, e);
                    }
                    
                    Console.WriteLine($"[Diarizer] SWITCH CONFIRMED: {_currentSpeaker} -> {target} (score={bestScore:F3}, margin={deltaVsCurrent:F3}, buffered={_pendingEmbeddings.Count})");
                    
                    _currentSpeaker = target;
                    _lastSwitchUtc = now;
                    ClearPending();
                    
                    return (target, (float)bestScore);
                }
                
                // While pending: DO NOT update any cluster. Keep current speaker until confirmed.
                return (_currentSpeaker, (float)currentScore);
            }
        }

        /// <summary>
        /// Determine if we should create a new speaker based on score patterns.
        /// </summary>
        private bool ShouldCreateNewSpeaker(double bestScore, Cluster bestCluster)
        {
            // Need minimum history before creating additional speakers
            if (_totalPhrases < 8) return false;
            
            // Need established cluster before splitting
            if (bestCluster.MatchCount < MinEmbeddingsForEstablished) return false;
            
            // Method 1: Consecutive low scores indicate a different voice
            if (_consecutiveLowScores >= ConsecutiveLowScoresForNewSpeaker && bestScore < NewSpeakerThreshold)
            {
                Console.WriteLine($"[Diarizer] New speaker trigger: {_consecutiveLowScores} consecutive low scores (best={bestScore:F3})");
                return true;
            }
            
            // Method 2: High variance in recent scores suggests mixed speakers in one cluster
            var (mean, variance) = GetRecentScoreStats();
            if (_recentBestScores.Count >= RecentScoreWindowSize / 2)
            {
                // High variance + mediocre mean = likely two speakers being mixed
                if (variance > MixedSpeakerVarianceThreshold && mean < MixedSpeakerMeanThreshold && bestScore < NewSpeakerThreshold)
                {
                    Console.WriteLine($"[Diarizer] New speaker trigger: high variance ({variance:F4}) with mediocre mean ({mean:F3})");
                    return true;
                }
            }
            
            // Method 3: Current embedding is very different from cluster centroid
            // (This catches cases where the cluster has grown diverse due to mixed speakers)
            if (bestCluster.Embeddings.Count >= 10 && bestScore < 0.30)
            {
                // Check if this embedding is consistently low against multiple stored embeddings
                int lowCount = 0;
                foreach (var stored in bestCluster.Embeddings.TakeLast(10))
                {
                    var sim = CosineSimilarity(stored, bestCluster.Embeddings[0]); // Compare to first embedding
                    if (sim < 0.4) lowCount++;
                }
                
                // If many stored embeddings are also dissimilar, the cluster is polluted
                if (lowCount >= 4)
                {
                    Console.WriteLine($"[Diarizer] New speaker trigger: cluster appears polluted ({lowCount}/10 low internal similarity)");
                    return true;
                }
            }
            
            return false;
        }

        private void TrackRecentScore(double score)
        {
            _recentBestScores.Add(score);
            while (_recentBestScores.Count > RecentScoreWindowSize)
                _recentBestScores.RemoveAt(0);
        }

        private (double mean, double variance) GetRecentScoreStats()
        {
            if (_recentBestScores.Count == 0) return (0, 0);
            
            double sum = 0;
            foreach (var s in _recentBestScores) sum += s;
            double mean = sum / _recentBestScores.Count;
            
            double varSum = 0;
            foreach (var s in _recentBestScores)
            {
                var diff = s - mean;
                varSum += diff * diff;
            }
            double variance = varSum / _recentBestScores.Count;
            
            return (mean, variance);
        }

        /// <summary>
        /// Clear pending switch state.
        /// </summary>
        private void ClearPending()
        {
            _pendingSwitchTarget = null;
            _pendingSwitchCount = 0;
            _pendingStartMs = 0;
            _pendingEmbeddings.Clear();
        }

        /// <summary>
        /// Compute similarity score between an embedding and a cluster.
        /// Uses average of top-K similarities for robustness.
        /// </summary>
        private double ComputeClusterScore(float[] embedding, Cluster cluster)
        {
            if (cluster.Embeddings == null || cluster.Embeddings.Count == 0)
                return -1.0;

            var similarities = new List<double>();
            foreach (var stored in cluster.Embeddings)
            {
                var sim = CosineSimilarity(embedding, stored);
                similarities.Add(sim);
            }
            
            // Sort descending and take top-K
            similarities.Sort((a, b) => b.CompareTo(a));
            int k = Math.Min(TopKForScoring, similarities.Count);
            
            double sum = 0;
            for (int i = 0; i < k; i++)
                sum += similarities[i];
            
            return sum / k;
        }

        /// <summary>
        /// Add an embedding to a cluster (if it's not too similar to existing ones).
        /// </summary>
        private void AddToCluster(Cluster cluster, float[] embedding)
        {
            if (cluster == null) return;
            if (cluster.Embeddings == null)
                cluster.Embeddings = new List<float[]>();

            // Check if too similar to existing (avoid storing duplicates)
            foreach (var existing in cluster.Embeddings)
            {
                if (CosineSimilarity(embedding, existing) > 0.92)
                    return; // Skip duplicate
            }

            cluster.Embeddings.Add((float[])embedding.Clone());
            cluster.MatchCount++;
            cluster.LastSeenUtc = DateTime.UtcNow;

            // Trim if over limit (remove oldest)
            while (cluster.Embeddings.Count > MaxEmbeddingsPerCluster)
            {
                cluster.Embeddings.RemoveAt(0);
            }
        }

        /// <summary>
        /// Create a new speaker cluster.
        /// </summary>
        private (string, float) CreateSpeaker(float[] embedding, DateTime now, string reason)
        {
            var label = GetNextLabel();
            var cluster = new Cluster
            {
                Label = label,
                Embeddings = new List<float[]> { (float[])embedding.Clone() },
                MatchCount = 1,
                LastSeenUtc = now,
                CreatedUtc = now
            };
            _clusters[label] = cluster;
            _currentSpeaker = label;
            _lastSwitchUtc = now;
            _pendingSwitchTarget = null;
            _pendingSwitchCount = 0;
            _recentBestScores.Clear(); // Reset score tracking for new speaker
            
            Console.WriteLine($"[Diarizer] NEW SPEAKER: {label} ({reason}), total={_clusters.Count}");
            return (label, 1.0f);
        }

        private string GetNextLabel()
        {
            var label = IndexToLabel(_nextLabelIndex);
            _nextLabelIndex++;
            return label;
        }

        private static string IndexToLabel(int index)
        {
            var result = new List<char>();
            int remaining = index;

            do
            {
                result.Insert(0, (char)('A' + (remaining % 26)));
                remaining = remaining / 26 - 1;
            } while (remaining >= 0);

            return LabelPrefix + new string(result.ToArray());
        }

        private static double CosineSimilarity(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length == 0 || b.Length == 0) return -1.0;

            int len = Math.Min(a.Length, b.Length);
            double dot = 0, normA = 0, normB = 0;

            for (int i = 0; i < len; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }

            if (normA < 1e-9 || normB < 1e-9) return -1.0;
            return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
        }

        /// <summary>
        /// Identify without assigning (read-only lookup).
        /// </summary>
        public (string label, float confidence) Identify(float[] embedding)
        {
            if (embedding == null || embedding.Length == 0) return ("Unknown", 0f);

            lock (_lock)
            {
                if (_clusters.Count == 0) return ("Unknown", 0f);

                string best = null;
                double bestScore = double.NegativeInfinity;

                foreach (var kv in _clusters)
                {
                    var score = ComputeClusterScore(embedding, kv.Value);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = kv.Key;
                    }
                }

                return (best ?? "Unknown", (float)bestScore);
            }
        }

        public bool MergeSpeakers(string label1, string label2)
        {
            lock (_lock)
            {
                if (!_clusters.TryGetValue(label1, out var c1) || !_clusters.TryGetValue(label2, out var c2))
                    return false;

                // Merge embeddings
                if (c1.Embeddings != null && c2.Embeddings != null)
                {
                    foreach (var emb in c2.Embeddings)
                        c1.Embeddings.Add(emb);
                    
                    // Trim to max
                    while (c1.Embeddings.Count > MaxEmbeddingsPerCluster)
                        c1.Embeddings.RemoveAt(0);
                }

                c1.MatchCount += c2.MatchCount;
                c1.LastSeenUtc = DateTime.UtcNow;

                _clusters.Remove(label2);
                
                if (string.Equals(_currentSpeaker, label2, StringComparison.OrdinalIgnoreCase))
                    _currentSpeaker = label1;
                
                Console.WriteLine($"[Diarizer] Merged {label2} into {label1}");
                return true;
            }
        }

        public Dictionary<string, int> GetClusterStats()
        {
            lock (_lock)
            {
                return _clusters.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.MatchCount ?? 0);
            }
        }

        public void ObserveFrame(long startSample, long endSample, string label, float score)
        {
            if (endSample <= startSample) return;
            if (string.IsNullOrWhiteSpace(label)) return;

            lock (_lock)
            {
                _timeline.Add((startSample, endSample, label, score));
                if (_timeline.Count > MaxTimeline)
                    _timeline.RemoveRange(0, _timeline.Count - MaxTimeline);
            }
        }

        public string GetDominantSpeaker(long startSample, long endSample, string fallback = "Unknown")
        {
            if (endSample <= startSample) return fallback;

            lock (_lock)
            {
                if (_timeline.Count == 0) return fallback;

                var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                foreach (var (s, e, label, score) in _timeline)
                {
                    if (e <= startSample || s >= endSample) continue;

                    var os = Math.Max(s, startSample);
                    var oe = Math.Min(e, endSample);
                    var dur = Math.Max(0, oe - os);
                    if (dur <= 0) continue;

                    totals.TryGetValue(label, out var cur);
                    totals[label] = cur + dur;
                }

                if (totals.Count == 0) return fallback;
                return totals.OrderByDescending(kv => kv.Value).First().Key;
            }
        }
    }
}
