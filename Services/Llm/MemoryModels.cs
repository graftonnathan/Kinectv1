using System;

namespace Kinectv1.Llm
{
    /// <summary>
    /// A topic centroid that merges similar conversation chunks.
    /// </summary>
    public sealed class TopicCentroid
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        
        /// <summary>Base64-encoded quantized embedding (int8).</summary>
        public string EmbeddingBase64 { get; set; } = string.Empty;
        
        /// <summary>Canonical summary (LLM-rewritten on merge).</summary>
        public string Summary { get; set; } = string.Empty;
        
        /// <summary>Number of chunks merged into this centroid.</summary>
        public int MergeCount { get; set; } = 1;
        
        /// <summary>When created.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        /// <summary>When last updated.</summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        
        /// <summary>Speakers associated with this centroid.</summary>
        public string[] Speakers { get; set; } = Array.Empty<string>();
        
        /// <summary>Importance score (higher = more relevant).</summary>
        public double Importance { get; set; } = 0.5;

        public float[] GetEmbedding()
        {
            if (string.IsNullOrEmpty(EmbeddingBase64))
                return Array.Empty<float>();
            
            try
            {
                var bytes = Convert.FromBase64String(EmbeddingBase64);
                var result = new float[bytes.Length];
                for (int i = 0; i < bytes.Length; i++)
                {
                    sbyte signed = (sbyte)bytes[i];
                    result[i] = signed / 127f;
                }
                return result;
            }
            catch
            {
                return Array.Empty<float>();
            }
        }

        public void SetEmbedding(float[] embedding)
        {
            if (embedding == null || embedding.Length == 0)
            {
                EmbeddingBase64 = string.Empty;
                return;
            }
            
            var bytes = new byte[embedding.Length];
            for (int i = 0; i < embedding.Length; i++)
            {
                float clamped = Math.Max(-1f, Math.Min(1f, embedding[i]));
                sbyte quantized = (sbyte)(clamped * 127f);
                bytes[i] = (byte)quantized;
            }
            EmbeddingBase64 = Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// Merge embedding using weighted average. Summary is set externally after LLM rewrite.
        /// </summary>
        public void MergeEmbeddingOnly(float[] newEmbedding)
        {
            if (newEmbedding == null || newEmbedding.Length == 0) return;
            
            var current = GetEmbedding();
            if (current.Length == 0)
            {
                SetEmbedding(newEmbedding);
                MergeCount = 1;
                return;
            }
            
            if (current.Length != newEmbedding.Length) return;
            
            float existingWeight = MergeCount;
            float newWeight = 1f;
            float totalWeight = existingWeight + newWeight;
            
            var merged = new float[current.Length];
            for (int i = 0; i < current.Length; i++)
                merged[i] = (current[i] * existingWeight + newEmbedding[i] * newWeight) / totalWeight;
            
            // Normalize
            float norm = 0f;
            for (int i = 0; i < merged.Length; i++)
                norm += merged[i] * merged[i];
            norm = (float)Math.Sqrt(norm);
            if (norm > 0)
            {
                for (int i = 0; i < merged.Length; i++)
                    merged[i] /= norm;
            }
            
            SetEmbedding(merged);
            MergeCount++;
            UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Set the canonical summary (replaces, does not append).
        /// </summary>
        public void SetCanonicalSummary(string summary)
        {
            Summary = summary ?? string.Empty;
            UpdatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Represents a chunk of archived conversation for embedding.
    /// </summary>
    public sealed class ArchivedChunk
    {
        /// <summary>Formatted text for embedding and summarization.</summary>
        public string Text { get; set; } = string.Empty;
        
        /// <summary>start timestamp of first message in chunk.</summary>
        public DateTime Start { get; set; }
        
        /// <summary>end timestamp of last message in chunk.</summary>
        public DateTime End { get; set; }
        
        /// <summary>Speakers involved in this chunk.</summary>
        public string[] Speakers { get; set; } = Array.Empty<string>();
        
        /// <summary>Approximate token count.</summary>
        public int ApproxTokens { get; set; }
    }

    /// <summary>
    /// Pinned facts that should never be forgotten.
    /// </summary>
    public sealed class PinnedFact
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Text { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string[] Tags { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Root container for the vector memory JSON file.
    /// </summary>
    public sealed class VectorMemoryStore
    {
        public int SchemaVersion { get; set; } = 4;
        public TopicCentroid[] Centroids { get; set; } = Array.Empty<TopicCentroid>();
        public PinnedFact[] PinnedFacts { get; set; } = Array.Empty<PinnedFact>();
    }
}
