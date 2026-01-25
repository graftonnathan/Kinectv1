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

        // NEW: token-budgeted segment packer
        private static IReadOnlyList<TranscriptSegment> PackSegmentsByTokenBudget(
            IReadOnlyList<TranscriptSegment> orderedSegments,
            int tokenBudget)
        {
            if (orderedSegments == null || orderedSegments.Count == 0)
                return Array.Empty<TranscriptSegment>();

            tokenBudget = Math.Max(64, tokenBudget);

            var packed = new List<TranscriptSegment>(Math.Min(orderedSegments.Count, 64));
            var used = 0;

            foreach (var seg in orderedSegments)
            {
                if (seg == null) continue;

                var speaker = !string.IsNullOrWhiteSpace(seg.SpeakerName) ? seg.SpeakerName : (seg.SpeakerLabel ?? string.Empty);
                var line = $"- {seg.Title} [{seg.TStartSec:0.0}-{seg.TEndSec:0.0}] {speaker}: {(seg.Text ?? string.Empty).Trim()}";
                var cost = TokenBudget.EstimateTokens(line);

                if (packed.Count > 0 && used + cost > tokenBudget)
                    break;

                if (packed.Count == 0 && cost > tokenBudget)
                {
                    // Ensure we always return something deterministic.
                    // Truncate text to roughly fit.
                    var maxChars = Math.Max(64, (tokenBudget - 16) * 4);
                    var txt = (seg.Text ?? string.Empty).Trim();
                    if (txt.Length > maxChars)
                        txt = txt.Substring(0, maxChars) + "...";
                }

                packed.Add(seg);
                used += cost;
            }

            return packed;
        }

        // Expand around multiple hits by including N segments before/after each match (stable, deterministic)
        private static IReadOnlyList<TranscriptSegment> ExpandAroundMatchesByIndex(
            TranscriptSegmentIndexStore store,
            IReadOnlyList<(TranscriptSegment Segment, double Score)> matches,
            int beforeCount,
            int afterCount)
        {
            if (store == null || matches == null || matches.Count == 0)
                return Array.Empty<TranscriptSegment>();

            beforeCount = Math.Max(0, beforeCount);
            afterCount = Math.Max(0, afterCount);

            var segMap = new Dictionary<string, TranscriptSegment>(StringComparer.OrdinalIgnoreCase);

            foreach (var (seg, _) in matches)
            {
                if (seg == null || string.IsNullOrWhiteSpace(seg.SessionId)) continue;

                var window = store.GetSessionRangeByIndex(seg.SessionId, seg.TStartSec, beforeCount, afterCount);
                foreach (var s in window)
                {
                    if (s == null || string.IsNullOrWhiteSpace(s.Id)) continue;
                    segMap[s.Id] = s;
                }
            }

            // Fallback: if windows somehow yielded nothing, keep the raw matches.
            if (segMap.Count == 0)
            {
                foreach (var (seg, _) in matches)
                {
                    if (seg == null || string.IsNullOrWhiteSpace(seg.Id)) continue;
                    segMap[seg.Id] = seg;
                }
            }

            return segMap.Values
                .OrderBy(s => string.IsNullOrWhiteSpace(s.SessionId) ? 1 : 0)
                .ThenBy(s => s.SessionId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.TStartSec)
                .ToList();
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

            // Hard token budgets for deterministic packing.
            // If user asks for "more", allow a larger budget.
            var tokenBudget = WantsMore(userQuery) ? 1600 : 900;

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

            // 1) semantic topK
            var matches = store.Search(queryEmbedding, topK);
            if (matches == null || matches.Count == 0)
            {
                Console.WriteLine("[TranscriptRecall] Segment store loaded but no matches found.");
                return new TranscriptContextPack
                {
                    Header = "No transcript matches found.",
                    Snippets = Array.Empty<TranscriptSnippet>()
                };
            }

            // 2) expand around matches in adjacency space (N segments before/after)
            // If user wants more, expand more around each hit.
            int before = WantsMore(userQuery) ? 10 : 5;
            int after = WantsMore(userQuery) ? 12 : 6;
            var expandedOrdered = ExpandAroundMatchesByIndex(store, matches, before, after);

            // 3) pack deterministically into token budget
            var packed = PackSegmentsByTokenBudget(expandedOrdered, tokenBudget);

            static string ToTime(double sec)
            {
                if (sec < 0) sec = 0;
                var ts = TimeSpan.FromSeconds(sec);
                if (ts.TotalHours >= 1)
                    return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
                return $"{ts.Minutes:00}:{ts.Seconds:00}";
            }

            var snippets = new List<TranscriptSnippet>();
            foreach (var seg in packed)
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
                    IsContextWindow = true
                });
            }

            var header = "Relevant transcript excerpts (token-budgeted):";

            return new TranscriptContextPack
            {
                Header = header,
                Snippets = snippets,
                IsExpandedContext = true
            };
        }
    }
}
