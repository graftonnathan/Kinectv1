using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Kinectv1.Services.Transcription
{
    public sealed class TranscriptSegmentIndexStore
    {
        private readonly string _filePath;
        private readonly object _lock = new();
        private TranscriptSegmentStoreFile _store = new();

        public TranscriptSegmentIndexStore(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        public async Task LoadAsync(CancellationToken ct = default)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    lock (_lock) _store = new TranscriptSegmentStoreFile();
                    return;
                }

                var json = await ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    lock (_lock) _store = new TranscriptSegmentStoreFile();
                    return;
                }

                var loaded = JsonConvert.DeserializeObject<TranscriptSegmentStoreFile>(json) ?? new TranscriptSegmentStoreFile();
                lock (_lock) _store = loaded;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TranscriptSegmentIndex] Load error: {ex.Message}");
                lock (_lock) _store = new TranscriptSegmentStoreFile();
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
                Console.WriteLine($"[TranscriptSegmentIndex] Save error: {ex.Message}");
            }
        }

        public TranscriptSegment UpsertSegment(TranscriptSegment segment)
        {
            if (segment == null) throw new ArgumentNullException(nameof(segment));

            lock (_lock)
            {
                var list = _store.Segments?.ToList() ?? new List<TranscriptSegment>();

                // Id is primary key when provided
                TranscriptSegment existing = null;
                if (!string.IsNullOrWhiteSpace(segment.Id))
                    existing = list.FirstOrDefault(s => string.Equals(s.Id, segment.Id, StringComparison.OrdinalIgnoreCase));

                // Otherwise dedupe on (sessionId, speakerLabel, tStart)
                if (existing == null)
                {
                    existing = list.FirstOrDefault(s =>
                        string.Equals(s.SessionId, segment.SessionId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(s.SpeakerLabel ?? string.Empty, segment.SpeakerLabel ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                        Math.Abs(s.TStartSec - segment.TStartSec) < 0.001);
                }

                segment.UpdatedAtUtc = DateTime.UtcNow;

                if (existing != null)
                {
                    existing.SessionId = segment.SessionId;
                    existing.Title = segment.Title;
                    existing.SpeakerLabel = segment.SpeakerLabel;
                    existing.SpeakerName = segment.SpeakerName;
                    existing.Text = segment.Text;
                    existing.TStartSec = segment.TStartSec;
                    existing.TEndSec = segment.TEndSec;
                    existing.StartedAtUtc = segment.StartedAtUtc;
                    existing.UpdatedAtUtc = segment.UpdatedAtUtc;
                    existing.EmbeddingBase64 = segment.EmbeddingBase64;
                    return existing;
                }

                if (string.IsNullOrWhiteSpace(segment.Id))
                    segment.Id = Guid.NewGuid().ToString("N");

                list.Add(segment);
                _store.Segments = list.ToArray();
                return segment;
            }
        }

        public IReadOnlyList<(TranscriptSegment Segment, double Score)> Search(float[] queryEmbedding, int topK = 6)
        {
            if (queryEmbedding == null || queryEmbedding.Length == 0)
                return Array.Empty<(TranscriptSegment, double)>();

            queryEmbedding = NormalizeVector(queryEmbedding);

            lock (_lock)
            {
                if (_store.Segments == null || _store.Segments.Length == 0)
                    return Array.Empty<(TranscriptSegment, double)>();

                var scored = new List<(TranscriptSegment seg, double score)>();

                foreach (var seg in _store.Segments)
                {
                    var emb = seg.GetEmbedding();
                    if (emb.Length == 0 || emb.Length != queryEmbedding.Length) continue;

                    emb = NormalizeVector(emb);
                    var cos = CosineSimilarity(queryEmbedding, emb);
                    scored.Add((seg, cos));
                }

                return scored
                    .OrderByDescending(x => x.score)
                    .Take(Math.Max(1, topK))
                    .Select(x => (x.seg, x.score))
                    .ToList();
            }
        }

        public TranscriptSegment? GetById(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            lock (_lock)
            {
                return _store.Segments?.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            }
        }

        public IReadOnlyList<TranscriptSegment> GetSessionWindow(string sessionId, double tStartSec, double tEndSec)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return Array.Empty<TranscriptSegment>();
            if (tEndSec < tStartSec) (tStartSec, tEndSec) = (tEndSec, tStartSec);

            lock (_lock)
            {
                return (_store.Segments ?? Array.Empty<TranscriptSegment>())
                    .Where(s => string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    .Where(s => s.TEndSec >= tStartSec && s.TStartSec <= tEndSec)
                    .OrderBy(s => s.TStartSec)
                    .ToList();
            }
        }

        public IReadOnlyList<TranscriptSegment> GetSessionRangeByIndex(string sessionId, double centerTStartSec, int beforeCount, int afterCount)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return Array.Empty<TranscriptSegment>();
            beforeCount = Math.Max(0, beforeCount);
            afterCount = Math.Max(0, afterCount);

            lock (_lock)
            {
                var list = (_store.Segments ?? Array.Empty<TranscriptSegment>())
                    .Where(s => string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(s => s.TStartSec)
                    .ToList();

                if (list.Count == 0) return Array.Empty<TranscriptSegment>();

                int idx = 0;
                double best = double.MaxValue;
                for (int i = 0; i < list.Count; i++)
                {
                    var d = Math.Abs(list[i].TStartSec - centerTStartSec);
                    if (d < best) { best = d; idx = i; }
                }

                int start = Math.Max(0, idx - beforeCount);
                int end = Math.Min(list.Count - 1, idx + afterCount);

                return list.Skip(start).Take(end - start + 1).ToList();
            }
        }

        private static float[] NormalizeVector(float[] vec)
        {
            if (vec == null || vec.Length == 0) return vec;
            double norm = 0;
            for (int i = 0; i < vec.Length; i++) norm += vec[i] * vec[i];
            norm = Math.Sqrt(norm);
            if (norm < 1e-10) return vec;
            var result = new float[vec.Length];
            for (int i = 0; i < vec.Length; i++) result[i] = (float)(vec[i] / norm);
            return result;
        }

        private static double CosineSimilarity(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length || a.Length == 0) return 0.0;
            double dot = 0;
            double na = 0;
            double nb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            if (na < 1e-10 || nb < 1e-10) return 0.0;
            return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        }

        private static readonly object _fileLock = new();

        private static Task<string> ReadAllTextAsync(string path, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                const int maxRetries = 5;
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    try
                    {
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var sr = new StreamReader(fs, Encoding.UTF8);
                        return sr.ReadToEnd();
                    }
                    catch (IOException) when (attempt < maxRetries - 1)
                    {
                        Thread.Sleep(50 * (attempt + 1));
                    }
                }

                using var fs2 = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr2 = new StreamReader(fs2, Encoding.UTF8);
                return sr2.ReadToEnd();
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
                            var tmp = path + ".tmp";
                            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                            using (var sw = new StreamWriter(fs, Encoding.UTF8))
                                sw.Write(content);

                            if (File.Exists(path))
                            {
                                var bak = path + ".bak";
                                if (File.Exists(bak)) File.Delete(bak);
                                File.Replace(tmp, path, bak);
                            }
                            else
                            {
                                File.Move(tmp, path);
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
    }
}
