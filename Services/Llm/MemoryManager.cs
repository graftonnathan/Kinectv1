using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kinectv1.Settings;
using Newtonsoft.Json.Linq;

namespace Kinectv1.Llm
{
    /// <summary>
    /// Orchestrates vector memory isolated per system prompt (persona).
    /// Supports chunked archiving, query rewrite, and LLM-based summary rewriting.
    /// </summary>
    public sealed class MemoryManager : IDisposable
    {
        private readonly Func<OllamaSettings> _getSettings;
        private readonly Func<string> _getMemoryKey;
        private readonly Dictionary<string, VectorStore> _vectorStores = new Dictionary<string, VectorStore>();
        private LmStudioEmbeddingClient _embeddingClient;
        private HttpClient _llmHttpClient;
        private bool _initialized;
        private bool _disposed;
        private readonly object _initLock = new object();

        // Chunking configuration (larger chunks for better context)
        private const int TARGET_CHUNK_TOKENS = 800;
        private const int CHUNK_OVERLAP_TOKENS = 100;

        // Search configuration (retrieve more, inject based on budget)
        private const int SEARCH_TOP_K = 20;       // Retrieve many candidates
        private const int INJECT_TOP_K = 8;        // Inject up to this many
        private const double MIN_SCORE_FLOOR = 0.40; // Low floor, rely on ranking

        // Structured summary prompt with entity anchors
        private const string SUMMARIZE_SYSTEM_PROMPT = @"You are a memory summarization assistant. Create a structured summary with:

Topic: <one-line topic>
Entities: <comma-separated key names, APIs, products, settings, values>
Summary: <2-3 sentences capturing the main discussion and conclusions>
Decisions: <any decisions made, or 'None'>

Be factual. Include specific values, names, and technical terms. Output only the structured format above.";

        // Rewrite prompt for merging summaries
        private const string REWRITE_SYSTEM_PROMPT = @"You maintain a compact, accurate long-term memory summary for a topic centroid.";

        private const string REWRITE_USER_TEMPLATE = @"Rewrite the centroid summary by integrating the new information.

Rules:
- Output in the same structured format (Topic/Entities/Summary/Decisions)
- Preserve ALL key entities (product names, APIs, people, device models, settings, values)
- Resolve contradictions by preferring the newest info
- Keep total length <= 1200 characters
- No filler, no speculation

Existing summary:
<<<OLD>>>
{0}
<<<END>>>

New info to integrate:
<<<NEW>>>
{1}
<<<END>>>";

        // Query rewrite prompt for better retrieval
        private const string QUERY_REWRITE_PROMPT = "Rewrite this query into key entities and nouns for search. Query: {0}. Search terms:";

        public MemoryManager(Func<OllamaSettings> getSettings, Func<string> getMemoryKey = null)
        {
            _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
            _getMemoryKey = getMemoryKey ?? (() => "default");
        }

        public string CurrentMemoryKey => _getMemoryKey?.Invoke() ?? "default";

        private VectorStore GetCurrentVectorStore()
        {
            var key = CurrentMemoryKey;
            lock (_initLock)
            {
                if (_vectorStores.TryGetValue(key, out var store))
                    return store;
                return null;
            }
        }

        public async Task InitializeAsync(CancellationToken ct = default)
        {
            if (_disposed) return;

            var settings = _getSettings();
            if (settings == null || !settings.VectorMemoryEnabled)
                return;

            var memoryKey = CurrentMemoryKey;
            
            lock (_initLock)
            {
                if (_vectorStores.ContainsKey(memoryKey))
                {
                    _initialized = true;
                    return;
                }

                if (_embeddingClient == null)
                {
                    _embeddingClient = new LmStudioEmbeddingClient(
                        settings.LmStudioBaseUrl,
                        settings.ApiKey,
                        () => _getSettings()?.EmbeddingsModel
                    );
                }

                if (_llmHttpClient == null)
                {
                    _llmHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                    if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                        _llmHttpClient.DefaultRequestHeaders.Authorization = 
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);
                }

                var basePath = ResolveBasePath(settings.VectorDbPath);
                var storePath = System.IO.Path.Combine(basePath, memoryKey, "vectors.json");
                
                var vectorStore = new VectorStore(storePath);
                _vectorStores[memoryKey] = vectorStore;
                _initialized = true;
            }

            var store = GetCurrentVectorStore();
            if (store != null)
                await store.LoadAsync(ct).ConfigureAwait(false);
        }

        public bool IsEnabled => _initialized && _getSettings()?.VectorMemoryEnabled == true;

        /// <summary>
        /// Enable/disable debug mode for search diagnostics.
        /// </summary>
        public void SetDebugMode(bool enabled)
        {
            VectorStore.DebugMode = enabled;
        }

        public async Task<List<ConversationMessage>> ProcessOverflowAsync(
            List<ConversationMessage> messages,
            string speaker,
            CancellationToken ct = default,
            bool forceArchiveAll = false)
        {
            if (!IsEnabled || messages == null || messages.Count == 0)
                return messages;

            var settings = _getSettings();
            int hotLimit = settings.HotContextTokenLimit;
            int totalTokens = messages.Sum(m => TokenEstimator.EstimateTokens(m.Content));

            if (!forceArchiveAll && totalTokens <= hotLimit)
                return messages;

            List<ConversationMessage> toKeep, toArchive;

            if (forceArchiveAll)
            {
                toKeep = new List<ConversationMessage>();
                toArchive = messages;
            }
            else
            {
                int targetTokens = (int)(hotLimit * 0.7);
                toKeep = new List<ConversationMessage>();
                toArchive = new List<ConversationMessage>();

                int runningTokens = 0;
                for (int i = messages.Count - 1; i >= 0; i--)
                {
                    int msgTokens = TokenEstimator.EstimateTokens(messages[i].Content);
                    if (runningTokens + msgTokens <= targetTokens)
                    {
                        toKeep.Insert(0, messages[i]);
                        runningTokens += msgTokens;
                    }
                    else
                    {
                        toArchive.Insert(0, messages[i]);
                    }
                }
            }

            if (toArchive.Count == 0)
                return messages;

            await ArchiveMessagesAsync(toArchive, speaker, ct).ConfigureAwait(false);
            return toKeep;
        }

        public async Task<MemoryContext> BuildContextAsync(
            string userMessage,
            string speaker,
            CancellationToken ct = default)
        {
            var context = new MemoryContext();

            if (!IsEnabled || string.IsNullOrWhiteSpace(userMessage))
                return context;

            await InitializeAsync(ct).ConfigureAwait(false);

            var vectorStore = GetCurrentVectorStore();
            if (vectorStore == null)
                return context;

            var settings = _getSettings();
            int budget = settings.MemoryContextBudget;
            double minScore = Math.Min(settings.MinRetrievalScore, MIN_SCORE_FLOOR);
            int usedTokens = 0;

            try
            {
                // Pinned facts first (always included)
                var pinnedFacts = vectorStore.GetPinnedFacts();
                if (pinnedFacts != null && pinnedFacts.Length > 0)
                {
                    context.PinnedFacts = new List<string>();
                    foreach (var fact in pinnedFacts)
                    {
                        int factTokens = TokenEstimator.EstimateTokens(fact.Text);
                        if (usedTokens + factTokens <= budget)
                        {
                            context.PinnedFacts.Add(fact.Text);
                            usedTokens += factTokens;
                        }
                    }
                }

                // Vector search with query rewrite
                int remainingBudget = budget - usedTokens;
                if (remainingBudget > 50 && vectorStore.CentroidCount > 0 && _embeddingClient != null)
                {
                    // Try query rewrite for better retrieval
                    string searchQuery = userMessage;
                    try
                    {
                        var rewritten = await RewriteQueryForSearchAsync(userMessage, ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(rewritten))
                        {
                            searchQuery = rewritten;
                            Console.WriteLine($"?? Query rewritten: {searchQuery}");
                        }
                    }
                    catch 
                    {
                        // Use original query on failure
                    }

                    // Get embedding for search query
                    float[] queryEmbedding = null;
                    try
                    {
                        queryEmbedding = await _embeddingClient.GetEmbeddingAsync(searchQuery, ct).ConfigureAwait(false);
                    }
                    catch { }

                    if (queryEmbedding != null && queryEmbedding.Length > 0)
                    {
                        // Search with high topK, filter by budget
                        var results = vectorStore.Search(
                            queryEmbedding,
                            topK: SEARCH_TOP_K,
                            recencyBoostFactor: settings.RecencyBoostFactor,
                            minSimilarity: minScore
                        );

                        context.RetrievedChunks = new List<RetrievedChunk>();
                        int injected = 0;
                        
                        foreach (var r in results)
                        {
                            if (injected >= INJECT_TOP_K) break;

                            int chunkTokens = TokenEstimator.EstimateTokens(r.Centroid.Summary);
                            if (usedTokens + chunkTokens <= budget)
                            {
                                context.RetrievedChunks.Add(new RetrievedChunk
                                {
                                    Text = r.Centroid.Summary,
                                    Score = r.Score,
                                    CreatedAt = r.Centroid.CreatedAt
                                });
                                usedTokens += chunkTokens;
                                injected++;
                            }
                        }
                        
                        Console.WriteLine($"?? Retrieved {results.Count} candidates, injected {injected} chunks ({usedTokens} tokens)");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"?? BuildContextAsync error: {ex.Message}");
            }

            return context;
        }

        public string FormatContextForPrompt(MemoryContext context)
        {
            if (context == null || context.IsEmpty)
                return string.Empty;

            var sb = new StringBuilder();

            if (context.PinnedFacts?.Count > 0)
            {
                sb.AppendLine("IMPORTANT FACTS:");
                foreach (var fact in context.PinnedFacts)
                    sb.AppendLine($"• {fact}");
                sb.AppendLine();
            }

            if (context.RetrievedChunks?.Count > 0)
            {
                sb.AppendLine("RELEVANT MEMORY:");
                foreach (var chunk in context.RetrievedChunks)
                {
                    sb.AppendLine($"• {chunk.Text}");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        public async Task PinFactAsync(string text, string[] tags = null, CancellationToken ct = default)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(text)) return;

            await InitializeAsync(ct).ConfigureAwait(false);
            var vectorStore = GetCurrentVectorStore();
            if (vectorStore == null) return;

            vectorStore.AddPinnedFact(new PinnedFact
            {
                Text = text,
                Tags = tags ?? Array.Empty<string>(),
                CreatedAt = DateTime.UtcNow
            });
            await SaveAsync(ct).ConfigureAwait(false);
        }

        public async Task SaveAsync(CancellationToken ct = default)
        {
            var vectorStore = GetCurrentVectorStore();
            if (vectorStore != null)
                await vectorStore.SaveAsync(ct).ConfigureAwait(false);
        }

        public void ClearAll()
        {
            var vectorStore = GetCurrentVectorStore();
            vectorStore?.Clear();
        }

        public (int centroids, int totalMerged, long estimatedBytes, string memoryKey) GetCurrentStats()
        {
            var vectorStore = GetCurrentVectorStore();
            if (vectorStore == null)
                return (0, 0, 0, CurrentMemoryKey);
            var stats = vectorStore.GetStats();
            return (stats.centroids, stats.totalMerged, stats.estimatedBytes, CurrentMemoryKey);
        }

        #region Chunking

        /// <summary>
        /// Build archive chunks from messages, respecting message boundaries.
        /// Uses larger chunks (800 tokens) for better context preservation.
        /// </summary>
        public static IEnumerable<ArchivedChunk> BuildArchiveChunks(
            List<ConversationMessage> messages,
            int targetTokens = TARGET_CHUNK_TOKENS,
            int overlapTokens = CHUNK_OVERLAP_TOKENS)
        {
            if (messages == null || messages.Count == 0)
                yield break;

            var currentChunk = new List<ConversationMessage>();
            int currentTokens = 0;
            int overlapStartIndex = 0;

            for (int i = 0; i < messages.Count; i++)
            {
                var msg = messages[i];
                int msgTokens = TokenEstimator.EstimateTokens(msg.Content);

                // If adding this message exceeds target and we have content, emit chunk
                if (currentTokens + msgTokens > targetTokens && currentChunk.Count > 0)
                {
                    yield return CreateChunk(currentChunk);

                    // Calculate overlap
                    currentChunk = new List<ConversationMessage>();
                    currentTokens = 0;
                    
                    for (int j = i - 1; j >= overlapStartIndex && currentTokens < overlapTokens; j--)
                    {
                        var overlapMsg = messages[j];
                        int overlapMsgTokens = TokenEstimator.EstimateTokens(overlapMsg.Content);
                        if (currentTokens + overlapMsgTokens <= overlapTokens)
                        {
                            currentChunk.Insert(0, overlapMsg);
                            currentTokens += overlapMsgTokens;
                        }
                        else break;
                    }
                    
                    overlapStartIndex = i;
                }

                currentChunk.Add(msg);
                currentTokens += msgTokens;
            }

            if (currentChunk.Count > 0)
            {
                yield return CreateChunk(currentChunk);
            }
        }

        private static ArchivedChunk CreateChunk(List<ConversationMessage> messages)
        {
            var sb = new StringBuilder();
            var speakers = new HashSet<string>();
            DateTime start = DateTime.MaxValue;
            DateTime end = DateTime.MinValue;

            foreach (var msg in messages)
            {
                var role = msg.Role?.ToUpperInvariant() ?? "USER";
                sb.AppendLine($"{role}: {msg.Content}");
                
                if (!string.IsNullOrWhiteSpace(msg.Speaker))
                    speakers.Add(msg.Speaker);
                
                if (msg.Timestamp < start) start = msg.Timestamp;
                if (msg.Timestamp > end) end = msg.Timestamp;
            }

            var text = sb.ToString().TrimEnd();
            return new ArchivedChunk
            {
                Text = text,
                Start = start == DateTime.MaxValue ? DateTime.UtcNow : start,
                End = end == DateTime.MinValue ? DateTime.UtcNow : end,
                Speakers = speakers.ToArray(),
                ApproxTokens = TokenEstimator.EstimateTokens(text)
            };
        }

        #endregion

        #region Archiving

        private async Task ArchiveMessagesAsync(List<ConversationMessage> messages, string speaker, CancellationToken ct)
        {
            await InitializeAsync(ct).ConfigureAwait(false);
            var vectorStore = GetCurrentVectorStore();
            if (_embeddingClient == null || vectorStore == null) return;

            var chunks = BuildArchiveChunks(messages, TARGET_CHUNK_TOKENS, CHUNK_OVERLAP_TOKENS).ToList();
            Console.WriteLine($"?? Archiving {messages.Count} messages as {chunks.Count} chunks ({TARGET_CHUNK_TOKENS} tokens/chunk)");

            int successCount = 0;
            foreach (var chunk in chunks)
            {
                try
                {
                    await ArchiveChunkAsync(vectorStore, chunk, speaker, ct).ConfigureAwait(false);
                    successCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"?? Failed to archive chunk: {ex.Message}");
                }
            }

            Console.WriteLine($"?? Successfully archived {successCount}/{chunks.Count} chunks");
            await vectorStore.SaveAsync(ct).ConfigureAwait(false);
        }

        private async Task ArchiveChunkAsync(VectorStore vectorStore, ArchivedChunk chunk, string speaker, CancellationToken ct)
        {
            var embedding = await _embeddingClient.GetEmbeddingAsync(chunk.Text, ct).ConfigureAwait(false);
            if (embedding == null || embedding.Length == 0)
            {
                Console.WriteLine($"?? Empty embedding for chunk, skipping");
                return;
            }

            string summary;
            try
            {
                summary = await GenerateStructuredSummaryAsync(chunk.Text, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"?? Summary generation failed, using fallback: {ex.Message}");
                summary = CreateFallbackSummary(chunk.Text);
            }

            var speakers = chunk.Speakers?.Length > 0 
                ? chunk.Speakers 
                : new[] { speaker ?? "Unknown" };

            await vectorStore.AddOrMergeCentroidAsync(
                embedding,
                summary,
                speakers,
                0.6,
                RewriteCentroidSummaryAsync
            ).ConfigureAwait(false);
        }

        #endregion

        #region LLM Calls

        /// <summary>
        /// Rewrite user query into search-optimized form with key entities.
        /// </summary>
        private async Task<string> RewriteQueryForSearchAsync(string userQuery, CancellationToken ct)
        {
            var settings = _getSettings();
            if (settings == null || _llmHttpClient == null || string.IsNullOrWhiteSpace(settings.Model))
                return null;

            var baseUrl = settings.LmStudioBaseUrl?.TrimEnd('/') ?? "http://127.0.0.1:1234";

            var requestBody = new
            {
                model = settings.Model,
                messages = new object[]
                {
                    new { role = "user", content = string.Format(QUERY_REWRITE_PROMPT, userQuery) }
                },
                stream = false,
                temperature = 0.1,
                max_tokens = 100
            };

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(requestBody);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
            using var response = await _llmHttpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var responseObj = JObject.Parse(responseJson);
            
            var content = responseObj["choices"]?[0]?["message"]?["content"]?.ToString()?.Trim();
            
            // Validate rewritten query isn't too short or too long
            if (!string.IsNullOrWhiteSpace(content) && content.Length >= 5 && content.Length <= 200)
                return content;
            
            return null;
        }

        /// <summary>
        /// Generate structured summary with entity anchors.
        /// </summary>
        private async Task<string> GenerateStructuredSummaryAsync(string conversationText, CancellationToken ct)
        {
            var settings = _getSettings();
            if (settings == null || _llmHttpClient == null)
                throw new InvalidOperationException("LLM client not initialized");

            var baseUrl = settings.LmStudioBaseUrl?.TrimEnd('/') ?? "http://127.0.0.1:1234";
            var model = settings.Model;

            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("No LLM model configured");

            var requestBody = new
            {
                model = model,
                messages = new object[]
                {
                    new { role = "system", content = SUMMARIZE_SYSTEM_PROMPT },
                    new { role = "user", content = $"Summarize this conversation:\n\n{conversationText}" }
                },
                stream = false,
                temperature = 0.3,
                max_tokens = 400
            };

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(requestBody);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            using var response = await _llmHttpClient.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var responseObj = JObject.Parse(responseJson);
            
            var content = responseObj["choices"]?[0]?["message"]?["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("Empty response from LLM");

            return content.Trim();
        }

        /// <summary>
        /// Rewrite centroid summary by integrating new information.
        /// </summary>
        private async Task<string> RewriteCentroidSummaryAsync(string oldSummary, string newSummary)
        {
            var settings = _getSettings();
            if (settings == null || _llmHttpClient == null)
                throw new InvalidOperationException("LLM client not initialized");

            var baseUrl = settings.LmStudioBaseUrl?.TrimEnd('/') ?? "http://127.0.0.1:1234";
            var model = settings.Model;

            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("No LLM model configured");

            var userContent = string.Format(REWRITE_USER_TEMPLATE, oldSummary, newSummary);

            var requestBody = new
            {
                model = model,
                messages = new object[]
                {
                    new { role = "system", content = REWRITE_SYSTEM_PROMPT },
                    new { role = "user", content = userContent }
                },
                stream = false,
                temperature = 0.2,
                max_tokens = 600
            };

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(requestBody);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await _llmHttpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var responseObj = JObject.Parse(responseJson);
            
            var content = responseObj["choices"]?[0]?["message"]?["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("Empty rewrite response from LLM");

            var result = content.Trim();
            if (result.Length > 1200)
                result = result.Substring(0, 1197) + "...";

            return result;
        }

        /// <summary>
        /// Fallback summary when LLM is unavailable.
        /// </summary>
        private string CreateFallbackSummary(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            var userMatch = Regex.Match(text, @"USER:\s*(.+?)(?=\n|$)", RegexOptions.IgnoreCase);
            var assistantMatch = Regex.Match(text, @"ASSISTANT:\s*(.+?)(?:\.|!|\?)", RegexOptions.IgnoreCase);

            var sb = new StringBuilder();
            sb.Append("Topic: User query | Entities: ");
            
            // Extract potential entities (capitalized words, quoted strings)
            var entities = Regex.Matches(text, @"\b[A-Z][a-zA-Z]+\b|""[^""]+""")
                .Cast<Match>()
                .Select(m => m.Value)
                .Distinct()
                .Take(5);
            sb.Append(string.Join(", ", entities));
            
            sb.Append(" | Summary: ");
            
            if (userMatch.Success)
            {
                var question = userMatch.Groups[1].Value.Trim();
                if (question.Length > 100) question = question.Substring(0, 97) + "...";
                sb.Append($"Q: {question}");
            }

            if (assistantMatch.Success)
            {
                var answer = assistantMatch.Groups[1].Value.Trim();
                if (answer.Length > 150) answer = answer.Substring(0, 147) + "...";
                if (userMatch.Success) sb.Append(" ");
                sb.Append($"A: {answer}");
            }

            return sb.ToString();
        }

        #endregion

        #region Helpers

        // Resolve relative paths against the app base dir and its ancestors (so 'history/memory' works
        // whether running from repo root, bin folder, or packaged output).
        private static string ResolvePathUpwards(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                if (System.IO.Path.IsPathRooted(raw)) return System.IO.Path.GetFullPath(raw);

                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var di = new System.IO.DirectoryInfo(baseDir);
                for (int i = 0; i < 6 && di != null; i++)
                {
                    var candidate = System.IO.Path.Combine(di.FullName, raw);
                    // If it already exists as a directory/file, prefer it.
                    if (System.IO.Directory.Exists(candidate) || System.IO.File.Exists(candidate))
                        return System.IO.Path.GetFullPath(candidate);
                    di = di.Parent;
                }

                return System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, raw));
            }
            catch { return raw; }
        }

        private string ResolveBasePath(string basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath))
                return AppDomain.CurrentDomain.BaseDirectory;

            var resolved = ResolvePathUpwards(basePath);
            return string.IsNullOrWhiteSpace(resolved) ? AppDomain.CurrentDomain.BaseDirectory : resolved;
        }

        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                _embeddingClient?.Dispose();
                _llmHttpClient?.Dispose();
                _disposed = true;
            }
        }
    }

    public class ConversationMessage
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public string Speaker { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class MemoryContext
    {
        public List<string> PinnedFacts { get; set; } = new List<string>();
        public List<RetrievedChunk> RetrievedChunks { get; set; } = new List<RetrievedChunk>();

        public bool IsEmpty =>
            (PinnedFacts == null || PinnedFacts.Count == 0) &&
            (RetrievedChunks == null || RetrievedChunks.Count == 0);
    }

    public class RetrievedChunk
    {
        public string Text { get; set; }
        public double Score { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
