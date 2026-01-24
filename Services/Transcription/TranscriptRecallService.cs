using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kinectv1.Llm;

namespace Kinectv1.Services.Transcription
{
    /// <summary>
    /// Transcript recall service.
    ///
    /// Queries the transcript segment index (persistent) for semantic matches.
    /// The injection is opt-in (only when TranscriptIntent detects a transcript request).
    /// </summary>
    public sealed class TranscriptRecallService
    {
        private readonly MemoryManager _memoryManager;
        private readonly LmStudioEmbeddingClient _embeddingClient;

        public TranscriptRecallService(MemoryManager memoryManager, LmStudioEmbeddingClient embeddingClient)
        {
            _memoryManager = memoryManager ?? throw new ArgumentNullException(nameof(memoryManager));
            _embeddingClient = embeddingClient ?? throw new ArgumentNullException(nameof(embeddingClient));
        }

        private static string ResolveTranscriptFolder()
        {
            try
            {
                var cfg = App.SettingsProvider?.Current?.Transcription;
                var folder = cfg?.OutputFolder ?? "transcriptions";
                if (!Path.IsPathRooted(folder))
                    folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, folder);
                return folder;
            }
            catch { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "transcriptions"); }
        }

        private static bool WantsMore(string userQuery)
        {
            if (string.IsNullOrWhiteSpace(userQuery)) return false;
            var q = userQuery.ToLowerInvariant();
            return q.Contains("more") || q.Contains("more context") || q.Contains("more details") || q.Contains("expand") || q.Contains("full") || q.Contains("everything");
        }

        public async Task<TranscriptContextPack?> TryBuildTranscriptContextAsync(
            string userQuery,
            int topK = 6,
            CancellationToken ct = default)
        {
            if (!TranscriptIntent.IsTranscriptAsk(userQuery))
                return null;

            // If user explicitly asks for more context/details, expand recall size.
            if (WantsMore(userQuery))
                topK = Math.Max(topK, 16);

            float[] queryEmbedding;
            try
            {
                queryEmbedding = await _embeddingClient.GetEmbeddingAsync(userQuery, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TranscriptRecall] Embedding failed: {ex.Message}");
                queryEmbedding = Array.Empty<float>();
            }

            if (queryEmbedding == null || queryEmbedding.Length == 0)
            {
                return new TranscriptContextPack
                {
                    Header = "No transcript matches found.",
                    Snippets = Array.Empty<TranscriptSnippet>()
                };
            }

            var folder = ResolveTranscriptFolder();
            var storePath = Path.Combine(folder, "transcript_segments.json");

            if (!File.Exists(storePath))
            {
                Console.WriteLine($"[TranscriptRecall] No segment store found at: {storePath}");
                return new TranscriptContextPack
                {
                    Header = "No transcript matches found.",
                    Snippets = Array.Empty<TranscriptSnippet>()
                };
            }

            var store = new TranscriptSegmentIndexStore(storePath);
            await store.LoadAsync(ct).ConfigureAwait(false);

            var results = store.Search(queryEmbedding, topK);
            if (results == null || results.Count == 0)
            {
                Console.WriteLine("[TranscriptRecall] Segment store loaded but no matches found.");
                return new TranscriptContextPack
                {
                    Header = "No transcript matches found.",
                    Snippets = Array.Empty<TranscriptSnippet>()
                };
            }

            static string ToTime(double sec)
            {
                if (sec < 0) sec = 0;
                var ts = TimeSpan.FromSeconds(sec);
                if (ts.TotalHours >= 1)
                    return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
                return $"{ts.Minutes:00}:{ts.Seconds:00}";
            }

            // Expand around the top match to provide continuity.
            // This keeps prompt size reasonable while giving the model more context.
            var topMatch = results.FirstOrDefault().Segment;
            var expanded = new List<TranscriptSegment>();
            if (topMatch != null && !string.IsNullOrWhiteSpace(topMatch.SessionId))
            {
                // If user wants more, expand window more aggressively.
                int before = WantsMore(userQuery) ? 10 : 5;
                int after = WantsMore(userQuery) ? 12 : 6;
                expanded.AddRange(store.GetSessionRangeByIndex(topMatch.SessionId, topMatch.TStartSec, before, after));
            }

            // Also include remaining semantic matches (to cover multiple parts of the call).
            // De-dup by segment Id.
            var segMap = new Dictionary<string, (TranscriptSegment seg, bool isWindow)>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in expanded)
            {
                if (s == null || string.IsNullOrWhiteSpace(s.Id)) continue;
                segMap[s.Id] = (s, true);
            }

            foreach (var (seg, _) in results)
            {
                if (seg == null || string.IsNullOrWhiteSpace(seg.Id)) continue;
                if (!segMap.ContainsKey(seg.Id))
                    segMap[seg.Id] = (seg, false);
            }

            // If we have a window, order chronologically for readability.
            // Otherwise keep semantic order (approx via score order) by sorting by time within session.
            var orderedSegments = segMap.Values
                .Select(v => v)
                .OrderBy(v => string.IsNullOrWhiteSpace(v.seg.SessionId) ? 1 : 0)
                .ThenBy(v => v.seg.SessionId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.seg.TStartSec)
                .ToList();

            var snippets = new List<TranscriptSnippet>();
            foreach (var (seg, isWindow) in orderedSegments)
            {
                if (seg == null) continue;

                var sp = !string.IsNullOrWhiteSpace(seg.SpeakerName) ? seg.SpeakerName : (seg.SpeakerLabel ?? string.Empty);

                snippets.Add(new TranscriptSnippet
                {
                    SessionId = seg.SessionId ?? string.Empty,
                    Title = seg.Title ?? string.Empty,
                    Speaker = sp,
                    TimeStart = ToTime(seg.TStartSec),
                    TimeEnd = ToTime(seg.TEndSec),
                    Text = (seg.Text ?? string.Empty).Trim(),
                    IsContextWindow = isWindow
                });
            }

            var header = expanded.Count > 0
                ? "Relevant transcript excerpts (semantic matches + surrounding context):"
                : "Relevant transcript excerpts (semantic matches):";

            return new TranscriptContextPack
            {
                Header = header,
                Snippets = snippets,
                IsExpandedContext = expanded.Count > 0
            };
        }
    }
}
