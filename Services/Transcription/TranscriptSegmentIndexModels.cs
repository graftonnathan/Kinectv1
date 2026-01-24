using System;

namespace Kinectv1.Services.Transcription
{
    public sealed class TranscriptSegment
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string SessionId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string SpeakerLabel { get; set; } = string.Empty;
        public string SpeakerName { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public double TStartSec { get; set; }
        public double TEndSec { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public string EmbeddingBase64 { get; set; } = string.Empty;

        public float[] GetEmbedding()
        {
            if (string.IsNullOrWhiteSpace(EmbeddingBase64))
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

            // Normalize and quantize to int8 for compact storage
            embedding = NormalizeVector(embedding);

            var bytes = new byte[embedding.Length];
            for (int i = 0; i < embedding.Length; i++)
            {
                float clamped = Math.Max(-1f, Math.Min(1f, embedding[i]));
                sbyte q = (sbyte)(clamped * 127f);
                bytes[i] = (byte)q;
            }
            EmbeddingBase64 = Convert.ToBase64String(bytes);
        }

        private static float[] NormalizeVector(float[] vec)
        {
            double norm = 0;
            for (int i = 0; i < vec.Length; i++)
                norm += vec[i] * vec[i];
            norm = Math.Sqrt(norm);
            if (norm < 1e-10) return vec;

            var result = new float[vec.Length];
            for (int i = 0; i < vec.Length; i++)
                result[i] = (float)(vec[i] / norm);
            return result;
        }
    }

    public sealed class TranscriptSegmentStoreFile
    {
        public int SchemaVersion { get; set; } = 1;
        public TranscriptSegment[] Segments { get; set; } = Array.Empty<TranscriptSegment>();
    }
}
