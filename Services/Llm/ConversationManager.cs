// ConversationManager.cs (conversation + context only)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Kinectv1.Llm;
using Kinectv1.Llm.Tools;
using Kinectv1.Services.Transcription;

namespace Kinectv1
{
    public static class OllamaService
    {
        public static event Action<string> OnPromptSent;
        public static event Action<string> OnResponseReceived;
        public static event Action<string> OnError;

        // Prevent duplicate console logs / abort fallbacks when overlapping dispatches occur
        private static long _chatLogGeneration = 0;
        private static long _lastAbortHandledGeneration = -1;
        
        // New: Streaming events for real-time TTS
        /// <summary>
        /// Fired for each complete sentence as it streams from the LLM.
        /// Enables TTS to start speaking while the LLM is still generating.
        /// </summary>
        public static event Action<string> OnResponseSentenceReady;
        
        /// <summary>
        /// Fired for each raw chunk from the LLM stream (for UI updates).
        /// </summary>
        public static event Action<string> OnResponseChunk;
        
        // New: Events for memory archival status
        public static event Action OnMemoryArchiveStarted;
        public static event Action<int, int> OnMemoryArchiveCompleted; // (messagesArchived, chunksCreated)
        public static event Action<string> OnMemoryArchiveFailed;
        
        // New: Tool execution events
        public static event Action<string, string> OnToolExecutionStarted; // (toolName, query)
        public static event Action<string, string> OnToolExecutionCompleted; // (toolName, resultSummary)

        // Streaming cancellation support
        private static CancellationTokenSource _currentStreamingCts;
        private static readonly object _streamingCtsLock = new object();
        
        // Track if LLM is currently streaming (used by TTS to know when to stop waiting)
        private static volatile bool _isLlmStreaming = false;
        
        /// <summary>
        /// Returns true if the LLM is currently streaming a response.
        /// Used by TTS streaming to know whether to wait longer for more sentences.
        /// </summary>
        public static bool IsLlmStreaming => _isLlmStreaming;

        /// <summary>
        /// Cancel any ongoing LLM streaming response.
        /// Called during barge-in to stop the current response generation.
        /// </summary>
        public static void CancelCurrentStreaming()
        {
            Console.WriteLine("[OllamaService] CancelCurrentStreaming");
            
            // Cancel the router's HTTP request - this aborts the connection to LM Studio/Ollama
            lock (_lock)
            {
                try { _router?.CancelCurrentRequest(); }
                catch (Exception ex) { Console.WriteLine($"[OllamaService] Router cancel error: {ex.Message}"); }
            }
            
            // Cancel the local streaming CTS (signals the await foreach loop to exit)
            lock (_streamingCtsLock)
            {
                try
                {
                    if (_currentStreamingCts != null && !_currentStreamingCts.IsCancellationRequested)
                        _currentStreamingCts.Cancel();
                }
                catch { }
            }
        }

        private static void LogErr(string msg)
        {
            try { OnError?.Invoke(msg); } catch { }
        }

        private static readonly object _lock = new();
        private static LlmRouter _router;
        private static string _systemPromptCache = string.Empty;
        private static DateTime _lastSystemPromptLoadUtc = DateTime.MinValue;

        // Vector memory manager (3-layer: hot/warm/cold)
        private static MemoryManager _memoryManager;
        private static readonly object _memoryLock = new();

        private static Kinectv1.Settings.AppSettings Snap => App.SettingsProvider?.Current;

        // Internal DTO for persisted conversation entries
        private class ConversationMessage
        {
            public string Role { get; set; }
            public string Content { get; set; }
            public DateTime Timestamp { get; set; }
            public string Speaker { get; set; }
        }
        private static readonly object _convLock = new();

        #region Memory Manager
        private static MemoryManager GetMemoryManager()
        {
            if (_memoryManager != null) return _memoryManager;
            lock (_memoryLock)
            {
                if (_memoryManager != null) return _memoryManager;
                _memoryManager = new MemoryManager(() => Snap?.Ollama, GetCurrentMemoryKey);
                return _memoryManager;
            }
        }

        private static string GetCurrentMemoryKey()
        {
            var systemPromptPath = Snap?.Ollama?.SystemPromptPath;
            if (string.IsNullOrWhiteSpace(systemPromptPath))
                return "default";

            try
            {
                // Use the filename without extension as the memory key
                var fileName = Path.GetFileNameWithoutExtension(systemPromptPath);
                if (string.IsNullOrWhiteSpace(fileName))
                    return "default";

                // Sanitize for use as folder name
                var invalidChars = Path.GetInvalidFileNameChars();
                foreach (var c in invalidChars)
                    fileName = fileName.Replace(c, '_');

                return fileName.ToLowerInvariant();
            }
            catch
            {
                return "default";
            }
        }

        private static async Task EnsureMemoryInitializedAsync(CancellationToken ct = default)
        {
            var mm = GetMemoryManager();
            await mm.InitializeAsync(ct).ConfigureAwait(false);
        }
        #endregion

        #region Path / Storage Helpers
        // Try to resolve relative path against base directory and its ancestors (up to 5 levels) to allow user placing 'history' beside project file.
        private static string ResolveHistoryPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                if (Path.IsPathRooted(raw)) return raw;
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var di = new DirectoryInfo(baseDir);
                for (int i = 0; i < 6 && di != null; i++)
                {
                    var candidate = Path.Combine(di.FullName, raw);
                    if (Directory.Exists(candidate) || File.Exists(candidate)) return candidate;
                    di = di.Parent;
                }
                // default to base directory combination
                return Path.GetFullPath(Path.Combine(baseDir, raw));
            }
            catch { return raw; }
        }

        private static (string directory, bool singleFile, string explicitFile) GetConversationStorageInfo()
        {
            var raw = Snap?.Ollama?.ConversationHistoryPath;
            if (string.IsNullOrWhiteSpace(raw)) return (null, false, null);
            raw = ResolveHistoryPath(raw);
            try
            {
                if (!Path.IsPathRooted(raw)) raw = Path.GetFullPath(raw);
                bool isFile = string.Equals(Path.GetExtension(raw), ".json", StringComparison.OrdinalIgnoreCase);
                if (isFile)
                {
                    var d = Path.GetDirectoryName(raw);
                    if (!string.IsNullOrWhiteSpace(d)) Directory.CreateDirectory(d);
                    return (d, true, raw);
                }
                Directory.CreateDirectory(raw);
                return (raw, false, Path.Combine(raw, "conversation.json"));
            }
            catch { return (null, false, null); }
        }

        private static string GetConversationHistoryPathForMemoryKey(string memoryKey)
        {
            if (string.IsNullOrWhiteSpace(memoryKey)) memoryKey = "default";
            var info = GetConversationStorageInfo();
            if (string.IsNullOrWhiteSpace(info.directory) && string.IsNullOrWhiteSpace(info.explicitFile)) return null;

            // If ConversationHistoryPath is a single explicit file, store per-memory under a sibling folder.
            // If it's a directory, store per-memory under a subfolder.
            try
            {
                if (info.singleFile)
                {
                    var dir = Path.GetDirectoryName(info.explicitFile);
                    if (string.IsNullOrWhiteSpace(dir)) return info.explicitFile;
                    return Path.Combine(dir, memoryKey, "conversation.json");
                }

                // directory mode
                return Path.Combine(info.directory, memoryKey, "conversation.json");
            }
            catch
            {
                return info.explicitFile;
            }
        }

        private static string GetCurrentMemoryKeySafe()
        {
            try { return GetCurrentMemoryKey(); } catch { return "default"; }
        }

        private static string ResolveConversationFileForMemoryKey(string memoryKey)
        {
            var p = GetConversationHistoryPathForMemoryKey(memoryKey);
            if (string.IsNullOrWhiteSpace(p)) return null;

            // Reuse the existing resolver behavior (allow relative paths)
            p = ResolveHistoryPath(p);
            try { return Path.GetFullPath(p); } catch { return p; }
        }

        private static string GetActiveConversationFileForAppend(string speaker)
        {
            // Store conversation history per system prompt/memory key.
            var key = GetCurrentMemoryKeySafe();
            var file = ResolveConversationFileForMemoryKey(key);
            if (string.IsNullOrWhiteSpace(file)) return null;
            try
            {
                var dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            }
            catch { }
            return file;
         }

        private static int GetConversationMaxTokens() => Snap?.Ollama?.ConversationMaxTokens ?? 0;

        private static int EstimateTokensAllSpeakersFromFile(string file)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return 0;
                var txt = File.ReadAllText(file, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(txt)) return 0;
                var jo = JObject.Parse(txt);
                int total = 0;
                foreach (var prop in jo.Properties())
                {
                    if (prop.Value is not JArray arr) continue;
                    foreach (var jmTok in arr)
                    {
                        var jm = jmTok as JObject;
                        if (jm == null) continue;
                        var role = jm["Role"]?.Value<string>() ?? "user";
                        var content = jm["Content"]?.Value<string>() ?? string.Empty;
                        var sp = jm["Speaker"]?.Value<string>() ?? prop.Name;
                        var formatted = string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)
                            ? $"USER ({sp}): {content}"
                            : $"ASSISTANT: {content}";
                        total += TokenEstimator.EstimateTokens(formatted);
                    }
                }
                return total;
            }
            catch { return 0; }
        }

        private static void EnsureConversationTokenBudget(string file)
        {
            try
            {
                var maxTokens = GetConversationMaxTokens();
                if (maxTokens <= 0) return; // disabled
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return;

                var current = EstimateTokensAllSpeakersFromFile(file);
                if (current <= maxTokens) return;

                var dir = Path.GetDirectoryName(file);
                if (string.IsNullOrWhiteSpace(dir)) return;

                // Rotate conversation.json -> conversation.<n>.json
                int next = 1;
                try
                {
                    var existing = Directory.GetFiles(dir, "conversation.*.json");
                    foreach (var f in existing)
                    {
                        var name = Path.GetFileName(f) ?? string.Empty;
                        // conversation.<n>.json
                        var parts = name.Split('.');
                        if (parts.Length >= 3 && int.TryParse(parts[1], out var n))
                            if (n >= next) next = n + 1;
                    }
                }
                catch { }

                var rotated = Path.Combine(dir, $"conversation.{next}.json");
                try { File.Copy(file, rotated, overwrite: false); }
                catch { return; }

                // Start a fresh conversation file.
                try { File.WriteAllText(file, "{}", Encoding.UTF8); } catch { }

                Console.WriteLine($"[OllamaService] Rotated conversation history: {current} tokens > {maxTokens}, archived to {Path.GetFileName(rotated)}");
            }
            catch { }
        }
        #endregion

        #region Append Logic
        private static bool MemoryEnabled()
        {
            bool enabled = Snap?.Ollama?.MemoryEnabled == true && !string.IsNullOrWhiteSpace(Snap?.Ollama?.ConversationHistoryPath);
            if (!enabled) LogErr("Conversation memory disabled or path not set.");
            return enabled;
        }

        private static void AppendConversation(string speaker, string role, string content)
        {
            if (!MemoryEnabled()) return;
            speaker = string.IsNullOrWhiteSpace(speaker) ? "UnknownSpeaker" : speaker.Trim();
            try
            {
                lock (_convLock)
                {
                    var targetFile = GetActiveConversationFileForAppend(speaker);
                    if (string.IsNullOrWhiteSpace(targetFile)) { LogErr("Conversation append aborted: target file unresolved"); return; }

                    // Enforce token budget (rotates whole conversation.json when exceeded)
                    EnsureConversationTokenBudget(targetFile);

                    var ok = TryAppendSurgical(targetFile, speaker, role, content);
                    if (!ok && !FallbackAppendFull(targetFile, speaker, role, content))
                        LogErr("Conversation append failed (both surgical + fallback).");

                    // Re-check after append; a single long message can push over the limit.
                    EnsureConversationTokenBudget(targetFile);
                }
            }
            catch (Exception ex) { LogErr($"Conversation append failed: {ex.Message}"); }
        }

        private static bool FallbackAppendFull(string file, string speaker, string role, string content)
        {
            try
            {
                var root = new JObject();
                if (File.Exists(file)) { try { root = JObject.Parse(File.ReadAllText(file, Encoding.UTF8)); } catch { root = new JObject(); } }
                var arr = root[speaker] as JArray ?? (JArray)(root[speaker] = new JArray());
                arr.Add(new JObject
                {
                    ["Role"] = role,
                    ["Content"] = content ?? string.Empty,
                    ["Timestamp"] = DateTime.UtcNow.ToString("o"),
                    ["Speaker"] = speaker
                });
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, root.ToString(Formatting.Indented), Encoding.UTF8);
                return true;
            }
            catch (Exception ex) { LogErr("Fallback append error: " + ex.Message); return false; }
        }

        private static bool TryAppendSurgical(string file, string speaker, string role, string content)
        {
            if (string.IsNullOrWhiteSpace(file)) return false;
            var msgObj = new ConversationMessage { Role = role, Content = content ?? string.Empty, Timestamp = DateTime.UtcNow, Speaker = speaker };
            static string Q(string s) => JsonConvert.SerializeObject(s ?? string.Empty);
            var sbMsg = new StringBuilder()
                .AppendLine("    {")
                .AppendLine("      \"Role\": " + Q(msgObj.Role) + ",")
                .AppendLine("      \"Content\": " + Q(msgObj.Content) + ",")
                .AppendLine("      \"Timestamp\": " + Q(msgObj.Timestamp.ToString("o")) + ",")
                .AppendLine("      \"Speaker\": " + Q(msgObj.Speaker))
                .Append("    }");
            var prettyMsg = sbMsg.ToString();
            try
            {
                if (!File.Exists(file))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    var root = new StringBuilder()
                        .AppendLine("{")
                        .AppendLine($"  \"{speaker}\": [")
                        .AppendLine(prettyMsg)
                        .AppendLine("  ]")
                        .Append("}")
                        .ToString();
                    File.WriteAllText(file, root, Encoding.UTF8);
                    return true;
                }
                var text = File.ReadAllText(file, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text) || !text.TrimEnd().EndsWith("}")) return false;
                var speakerKey = "\"" + speaker + "\"";
                int speakerIndex = text.IndexOf(speakerKey, StringComparison.OrdinalIgnoreCase);
                if (speakerIndex < 0)
                {
                    int insertPos = LastNonWsIndex(text);
                    if (insertPos <= 0 || text[insertPos] != '}') return false;
                    bool needComma = NeedsCommaBeforeProperty(text, insertPos);
                    var ins = new StringBuilder();
                    if (needComma) ins.Append(',');
                    ins.AppendLine()
                       .AppendLine($"  \"{speaker}\": [")
                       .AppendLine(prettyMsg)
                       .Append("  ]");
                    File.WriteAllText(file, text.Insert(insertPos, ins.ToString()), Encoding.UTF8);
                    return true;
                }
                int colon = text.IndexOf(':', speakerIndex); if (colon < 0) return false;
                int arrayStart = text.IndexOf('[', colon); if (arrayStart < 0) return false;
                int arrayEnd = FindMatchingBracket(text, arrayStart, '[', ']'); if (arrayEnd < 0) return false;
                var inner = text.Substring(arrayStart + 1, arrayEnd - arrayStart - 1).Trim();
                var ins2 = new StringBuilder();
                if (!string.IsNullOrEmpty(inner)) ins2.Append(',');

                ins2.AppendLine().Append(prettyMsg);
                File.WriteAllText(file, text.Insert(arrayEnd, ins2.ToString()), Encoding.UTF8);
                return true;
            }
            catch { return false; }
        }

        private static int LastNonWsIndex(string s)
        {
            for (int i = s.Length - 1; i >= 0; i--) if (!char.IsWhiteSpace(s[i])) return i; return -1;
        }
        private static bool NeedsCommaBeforeProperty(string text, int braceIndex)
        {
            for (int i = braceIndex - 1; i >= 0; i--) { var c = text[i]; if (char.IsWhiteSpace(c)) continue; return c != '{'; } return false;
        }
        private static int FindMatchingBracket(string s, int start, char open, char close)
        {
            int depth = 0; for (int i = start; i < s.Length; i++) { if (s[i] == open) depth++; else if (s[i] == close && --depth == 0) return i; } return -1;
        }
        #endregion

        #region History Assembly

        private static string BuildHistoryBlockAllSpeakersForCurrentPrompt(int? maxMessages = null)
        {
            if (!MemoryEnabled()) return string.Empty;

            try
            {
                var key = GetCurrentMemoryKeySafe();
                var messages = GetConversationMessagesAllSpeakers(key);
                if (messages.Count == 0) return string.Empty;

                // Keep the most recent messages if requested
                if (maxMessages.HasValue && maxMessages.Value > 0 && messages.Count > maxMessages.Value)
                    messages = messages.Skip(messages.Count - maxMessages.Value).ToList();

                var sb = new StringBuilder();
                foreach (var m in messages)
                {
                    if (string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine($"USER ({m.Speaker}): {m.Content}");
                    else
                        sb.AppendLine($"ASSISTANT: {m.Content}");
                }
                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static List<Llm.ConversationMessage> GetConversationMessagesAllSpeakers(string memoryKey)
         {
             var result = new List<Llm.ConversationMessage>();
             if (!MemoryEnabled()) return result;

             var conversationFile = ResolveConversationFileForMemoryKey(memoryKey);
             if (string.IsNullOrWhiteSpace(conversationFile) || !File.Exists(conversationFile))
                 return result;

             try
             {
                 lock (_convLock)
                 {
                     string txt;
                     try { txt = File.ReadAllText(conversationFile, Encoding.UTF8); }
                     catch { return result; }
                     if (string.IsNullOrWhiteSpace(txt)) return result;

                     JObject root;
                     try { root = JObject.Parse(txt); }
                     catch { return result; }

                     foreach (var prop in root.Properties())
                     {
                         if (prop.Value is not JArray arr) continue;
                         foreach (var jmTok in arr)
                         {
                             if (jmTok is not JObject jm) continue;
                             result.Add(new Llm.ConversationMessage
                             {
                                 Role = jm["Role"]?.Value<string>() ?? "user",
                                 Content = jm["Content"]?.Value<string>() ?? string.Empty,
                                 Speaker = jm["Speaker"]?.Value<string>() ?? prop.Name,
                                 Timestamp = jm["Timestamp"]?.Value<DateTime?>() ?? DateTime.MinValue
                             });
                         }
                     }
                 }
             }
             catch { }

             // Chronological order (oldest first)
             result.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
             return result;
         }

        private static int GetConversationTokenCountAllSpeakers(string memoryKey)
        {
            var messages = GetConversationMessagesAllSpeakers(memoryKey);
            if (messages.Count == 0) return 0;

            int totalTokens = 0;
            foreach (var msg in messages)
            {
                var formatted = string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase)
                    ? $"USER ({msg.Speaker}): {msg.Content}"
                    : $"ASSISTANT: {msg.Content}";
                totalTokens += TokenEstimator.EstimateTokens(formatted);
            }

            return totalTokens;
        }

        /// <summary>
        /// Get the current hot context stats across ALL speakers for the current memory key (system prompt).
        /// Used by the Memory UI to show totals without binding to a single speaker.
        /// </summary>
        public static (int tokenCount, int messageCount) GetHotContextStatsAllSpeakers()
        {
            var key = GetCurrentMemoryKeySafe();
            var messages = GetConversationMessagesAllSpeakers(key);
            int tokens = GetConversationTokenCountAllSpeakers(key);
            return (tokens, messages.Count);
        }
        #endregion

        #region LLM Interaction
        public static bool IsEnabled() => Snap?.Ollama?.Enabled == true;
        
        /// <summary>
        /// Dispatch a prompt to the LLM with optional history tracking.
        /// </summary>
        /// <param name="speaker">Speaker identifier</param>
        /// <param name="text">Text to send to LLM</param>
        /// <param name="skipHistory">If true, skip conversation history and don't save this exchange</param>
        public static Task DispatchAsync(string speaker, string text, bool skipHistory = false) 
            => SendPromptStreamingAsync(speaker, text, skipHistory: skipHistory);

        /// <summary>
        /// Streaming version of SendPromptAsync that fires OnResponseSentenceReady 
        /// for each complete sentence as it arrives from the LLM.
        /// This allows TTS to start speaking before the full response is complete.
        /// </summary>
        public static async Task SendPromptStreamingAsync(string speakerName, string transcription, CancellationToken ct = default, bool skipHistory = false)
        {
            CancellationTokenSource linkedCts;
            long myGen;
            lock (_streamingCtsLock)
            {
                try { _currentStreamingCts?.Cancel(); } catch { }
                try { _currentStreamingCts?.Dispose(); } catch { }

                _currentStreamingCts = ct == default
                    ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                    : new CancellationTokenSource();
                linkedCts = _currentStreamingCts;

                myGen = Interlocked.Increment(ref _chatLogGeneration);
            }

            var streamingCt = linkedCts.Token;
            _isLlmStreaming = true;

            try
            {
                if (!IsEnabled()) return;
                EnsureRouterInitialized();
                var normalizedSpeaker = string.IsNullOrWhiteSpace(speakerName) ? "UnknownSpeaker" : speakerName.Trim();

                // If this dispatch was already superseded/cancelled, don't log it.
                if (streamingCt.IsCancellationRequested) return;

                // Log full user message (complete, final)
                try { Console.WriteLine($"[CHAT][USER][{normalizedSpeaker}] {transcription}"); } catch { }

                await EnsureMemoryInitializedAsync(streamingCt).ConfigureAwait(false);
                
                var system = LoadSystemPrompt();
                
                // Add tool instructions to system prompt if tools are enabled
                var toolRegistry = ToolRegistry.Instance;
                if (toolRegistry.IsEnabled)
                {
                    system += toolRegistry.GenerateToolPrompt();
                }

                // Skip history if requested (for system operations like summary generation)
                string history = string.Empty;
                string memoryContext = string.Empty;
                var mm = GetMemoryManager();
                
                if (!skipHistory)
                {
                    // Conversational history is scoped to the selected system prompt, not a single speaker.
                    // Include all speakers from that prompt's conversation.json so the assistant has context.
                    history = BuildHistoryBlockAllSpeakersForCurrentPrompt();

                    // Build context from vector memory (warm summary + retrieved chunks)
                    if (mm.IsEnabled)
                    {
                        try
                        {
                            var context = await mm.BuildContextAsync(transcription, normalizedSpeaker, streamingCt).ConfigureAwait(false);
                            memoryContext = mm.FormatContextForPrompt(context);
                            if (!string.IsNullOrWhiteSpace(memoryContext))
                            {
                                Console.WriteLine($"?? Retrieved memory context: {TokenEstimator.EstimateTokens(memoryContext)} tokens");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"? Memory context retrieval failed: {ex.Message}");
                        }
                    }
                }
                
                // Transcript recall: only inject when asked
                string transcriptContext = null;
                bool transcriptRecallHit = false;
                if (TranscriptIntent.IsTranscriptAsk(transcription))
                {
                    try
                    {
                        Console.WriteLine($"[TranscriptRecall] Triggered for query: '{(transcription?.Length > 80 ? transcription.Substring(0, 80) + "..." : transcription)}'");

                        var recall = new TranscriptRecallService(mm, new Kinectv1.Llm.LmStudioEmbeddingClient(
                            Snap?.Ollama?.LmStudioBaseUrl ?? "http://127.0.0.1:1234",
                            Snap?.Ollama?.ApiKey,
                            () => Snap?.Ollama?.EmbeddingsModel));

                        var pack = await recall.TryBuildTranscriptContextAsync(transcription, topK: 6, ct).ConfigureAwait(false);
                        transcriptContext = pack?.ToPromptBlock();
                        transcriptRecallHit = pack?.Snippets != null && pack.Snippets.Count > 0;

                        Console.WriteLine($"[TranscriptRecall] Snippets={(pack?.Snippets?.Count ?? 0)} bytes={(transcriptContext?.Length ?? 0)}");

                        if (!transcriptRecallHit)
                        {
                            const string noHit = "I couldn't find anything relevant in the saved transcripts.";
                            try
                            {
                                var ttsEnabled = false;
                                try { ttsEnabled = Kinectv1.App.SettingsProvider?.Current?.Tts?.Enabled ?? false; } catch { }
                                if (ttsEnabled)
                                {
                                    var voice = Kinectv1.App.SettingsProvider?.Current?.Tts?.Speaker;
                                    Kinectv1.Tts.TtsService.QueueSentenceForStreaming(noHit, voice);
                                }
                            }
                            catch { }

                            try { OnResponseReceived?.Invoke(noHit); } catch { }
                            return;
                        }

                        if (!string.IsNullOrWhiteSpace(transcriptContext))
                        {
                            system += "\n\nWhen transcript excerpts are provided as context, treat them as reference material and cite sources in this format: Meeting Title [HH:MM:SS-HH:MM:SS], Speaker.";
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TranscriptRecall] Failed: {ex.Message}");
                    }
                }
                
                var userPrompt = BuildUserPromptWithMemory(normalizedSpeaker, transcription, null, history, memoryContext);

                if (!string.IsNullOrWhiteSpace(transcriptContext))
                {
                    userPrompt += "\n\nTRANSCRIPT CONTEXT:\n" + transcriptContext.Trim();
                    userPrompt += "\n\nINSTRUCTIONS:\nAnswer the user's question using the TRANSCRIPT CONTEXT above. If it doesn't contain enough information, say what's missing and ask a specific follow-up question.";
                }

                try { OnPromptSent?.Invoke(userPrompt); } catch { }
                
                if (!skipHistory)
                {
                    AppendConversation(normalizedSpeaker, "user", transcription);
                }
                
                // Stream the response with tool support
                var (finalResponse, toolsUsed) = await StreamResponseWithToolsAsync(system, userPrompt, normalizedSpeaker, streamingCt).ConfigureAwait(false);

                if (streamingCt.IsCancellationRequested)
                {
                    // Avoid duplicate abort fallback for multiple overlapping cancels.
                    if (Interlocked.CompareExchange(ref _lastAbortHandledGeneration, myGen, _lastAbortHandledGeneration) == myGen)
                        return;

                    Console.WriteLine("[OllamaService] Streaming aborted - not saving partial response");
                    var abortedMsg = "I couldn't complete that response. Please try again.";
                    try { OnResponseReceived?.Invoke(abortedMsg); } catch { }
                    try
                    {
                        var ttsEnabled = false;
                        try { ttsEnabled = Kinectv1.App.SettingsProvider?.Current?.Tts?.Enabled ?? false; } catch { }
                        if (ttsEnabled)
                        {
                            var voice = Kinectv1.App.SettingsProvider?.Current?.Tts?.Speaker;
                            Kinectv1.Tts.TtsService.QueueSentenceForStreaming(abortedMsg, voice);
                        }
                    }
                    catch { }

                    // Log final AI message (aborted)
                    try { Console.WriteLine($"[CHAT][AI][{normalizedSpeaker}] {abortedMsg}"); } catch { }
                    return;
                }
                
                if (string.IsNullOrWhiteSpace(finalResponse)) finalResponse = "(no response)";
                var cleanedFull = SanitizeAssistantText(finalResponse);

                if (!skipHistory)
                {
                    AppendConversation(normalizedSpeaker, "assistant", cleanedFull);
                
                    // Check for memory overflow after appending the response
                    await CheckAndProcessMemoryOverflowAsync(normalizedSpeaker, streamingCt).ConfigureAwait(false);
                
                    // Save memory manager state periodically
                    var mmSave = GetMemoryManager();
                    if (mmSave.IsEnabled)
                    {
                        try { await mmSave.SaveAsync(streamingCt).ConfigureAwait(false); }
                        catch { }
                    }
                }
                
                try { OnResponseReceived?.Invoke(cleanedFull); } catch { }

                // Log full AI response (complete, final)
                try { Console.WriteLine($"[CHAT][AI][{normalizedSpeaker}] {cleanedFull}"); } catch { }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[OllamaService] Streaming task cancelled");
            }
            catch (Exception ex)
            {
                LogErr($"LLM streaming dispatch failed: {ex.Message}");
            }
            finally
            {
                _isLlmStreaming = false;
                
                lock (_streamingCtsLock)
                {
                    if (_currentStreamingCts == linkedCts)
                    {
                        _currentStreamingCts = null;
                    }
                }
            }
        }

        /// <summary>
        /// Stream response from LLM with tool execution support.
        /// If the LLM requests a tool, execute it and continue the conversation.
        /// </summary>
        private static async Task<(string finalResponse, bool toolsUsed)> StreamResponseWithToolsAsync(
            string system, string userPrompt, string speaker, CancellationToken ct)
        {
            var toolRegistry = ToolRegistry.Instance;
            var fullResponse = new StringBuilder();
            var sentenceBuffer = new StringBuilder();
            bool toolsUsed = false;
            int toolIterations = 0;
            const int maxToolIterations = 3; // Prevent infinite tool loops

            string currentPrompt = userPrompt;

            while (toolIterations < maxToolIterations)
            {
                fullResponse.Clear();
                sentenceBuffer.Clear();

                try
                {
                    await foreach (var chunk in _router.ChatStreamAsync(system, currentPrompt, ct))
                    {
                        if (ct.IsCancellationRequested) break;
                        if (string.IsNullOrEmpty(chunk)) continue;
                        
                        fullResponse.Append(chunk);
                        sentenceBuffer.Append(chunk);
                        
                        // Only fire chunk events on the final iteration (when no more tools)
                        // For tool iterations, we buffer silently
                        if (toolIterations == 0 || !toolRegistry.HasToolCalls(fullResponse.ToString()))
                        {
                            try { OnResponseChunk?.Invoke(chunk); } catch { }
                        }
                        
                        // Extract and fire sentences for TTS (only if not a tool call response)
                        if (!toolRegistry.HasToolCalls(fullResponse.ToString()))
                        {
                            var bufferedText = sentenceBuffer.ToString();
                            var sentences = ExtractCompleteSentences(ref bufferedText);
                            sentenceBuffer.Clear();
                            sentenceBuffer.Append(bufferedText);
                            

                            foreach (var sentence in sentences)
                            {
                                if (ct.IsCancellationRequested) break;
                                var cleaned = SanitizeAssistantText(sentence);
                                if (!string.IsNullOrWhiteSpace(cleaned))
                                {
                                    try { OnResponseSentenceReady?.Invoke(cleaned); } catch { }
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return (fullResponse.ToString(), toolsUsed);
                }

                if (ct.IsCancellationRequested) break;

                var responseText = fullResponse.ToString();

                // Check for tool calls
                if (toolRegistry.IsEnabled && toolRegistry.HasToolCalls(responseText))
                {
                    var toolCalls = toolRegistry.ParseToolCalls(responseText);
                    if (toolCalls.Count > 0)
                    {
                        toolsUsed = true;
                        toolIterations++;

                        // Execute all tool calls
                        var toolResults = new StringBuilder();
                        foreach (var (toolName, parameters) in toolCalls)
                        {
                            try
                            {
                                // Extract query for display
                                string queryDisplay = parameters;
                                try
                                {
                                    var paramObj = JObject.Parse(parameters);
                                    queryDisplay = paramObj["query"]?.ToString() ?? parameters;
                                }
                                catch { }

                                Console.WriteLine($"[OllamaService] Tool call: {toolName}({queryDisplay})");
                                try { OnToolExecutionStarted?.Invoke(toolName, queryDisplay); } catch { }

                                var result = await toolRegistry.ExecuteToolAsync(toolName, parameters, ct).ConfigureAwait(false);
                                

                                // Summarize result for event
                                var resultSummary = result?.Length > 100 ? result.Substring(0, 100) + "..." : result;
                                try { OnToolExecutionCompleted?.Invoke(toolName, resultSummary); } catch { }

                                toolResults.AppendLine($"[Tool Result: {toolName}]");
                                toolResults.AppendLine(result);
                                toolResults.AppendLine();
                            }
                            catch (Exception ex)
                            {
                                toolResults.AppendLine($"[Tool Error: {toolName}]");
                                toolResults.AppendLine($"Error: {ex.Message}");
                                toolResults.AppendLine();
                            }
                        }

                        // Build continuation prompt with tool results
                        var cleanedResponse = toolRegistry.RemoveToolCalls(responseText);
                        currentPrompt = $"{userPrompt}\n\nASSISTANT: {cleanedResponse}\n\n{toolResults}\n\nPlease continue your response, incorporating the tool results naturally. Do not use any more tools.";
                        
                        Console.WriteLine($"[OllamaService] Continuing with tool results (iteration {toolIterations})");
                        continue;
                    }
                }

                // No tool calls, we're done
                // Flush any remaining buffer
                var remaining = sentenceBuffer.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(remaining) && remaining.Length > 1)
                {
                    var cleaned = SanitizeAssistantText(remaining);
                    if (!string.IsNullOrWhiteSpace(cleaned) && cleaned.Length > 1)
                    {
                        try { OnResponseSentenceReady?.Invoke(cleaned); } catch { }
                    }
                }

                return (responseText, toolsUsed);
            }

            // Max iterations reached
            Console.WriteLine($"[OllamaService] Max tool iterations ({maxToolIterations}) reached");
            return (fullResponse.ToString(), toolsUsed);
        }

        /// <summary>
        /// Original non-streaming version for backward compatibility.
        /// </summary>
        public static async Task SendPromptAsync(string speakerName, string transcription, CancellationToken ct = default)
        {
            try
            {
                if (!IsEnabled()) return;
                EnsureRouterInitialized();
                var normalizedSpeaker = string.IsNullOrWhiteSpace(speakerName) ? "UnknownSpeaker" : speakerName.Trim();

                // Initialize vector memory if enabled
                await EnsureMemoryInitializedAsync(ct).ConfigureAwait(false);

                var system = LoadSystemPrompt();
                var history = BuildHistoryBlockAllSpeakersForCurrentPrompt();

                // Build context from vector memory (warm summary + retrieved chunks)
                string memoryContext = string.Empty;
                var mm = GetMemoryManager();
                if (mm.IsEnabled)
                {
                    try
                    {
                        var context = await mm.BuildContextAsync(transcription, normalizedSpeaker, ct).ConfigureAwait(false);
                        memoryContext = mm.FormatContextForPrompt(context);
                    }
                    catch { }
                }
                
                // Transcript recall: only inject when asked
                string transcriptContext = null;
                if (TranscriptIntent.IsTranscriptAsk(transcription))
                {
                    try
                    {
                        Console.WriteLine($"[TranscriptRecall] Triggered for query: '{(transcription?.Length > 80 ? transcription.Substring(0, 80) + "..." : transcription)}'");

                        var recall = new TranscriptRecallService(mm, new Kinectv1.Llm.LmStudioEmbeddingClient(
                            Snap?.Ollama?.LmStudioBaseUrl ?? "http://127.0.0.1:1234",
                            Snap?.Ollama?.ApiKey,
                            () => Snap?.Ollama?.EmbeddingsModel));

                        var pack = await recall.TryBuildTranscriptContextAsync(transcription, topK: 6, ct).ConfigureAwait(false);
                        transcriptContext = pack?.ToPromptBlock();

                        Console.WriteLine($"[TranscriptRecall] Snippets={(pack?.Snippets?.Count ?? 0)} bytes={(transcriptContext?.Length ?? 0)}");

                        if (pack?.Snippets == null || pack.Snippets.Count == 0)
                        {
                            try
                            {
                                var ttsEnabled = false;
                                try { ttsEnabled = Kinectv1.App.SettingsProvider?.Current?.Tts?.Enabled ?? false; } catch { }
                                if (ttsEnabled)
                                {
                                    var voice = Kinectv1.App.SettingsProvider?.Current?.Tts?.Speaker;
                                    Kinectv1.Tts.TtsService.QueueSentenceForStreaming("I couldn't find anything relevant in the saved transcripts.", voice);
                                }
                            }
                            catch { }
                        }

                        var userPrompt = BuildUserPromptWithMemory(normalizedSpeaker, transcription, null, history, memoryContext);

                        if (!string.IsNullOrWhiteSpace(transcriptContext))
                        {
                            userPrompt += "\n\nTRANSCRIPT CONTEXT:\n" + transcriptContext.Trim();
                            userPrompt += "\n\nINSTRUCTIONS:\nAnswer the user's question using the TRANSCRIPT CONTEXT above. If it doesn't contain enough information, say what's missing and ask a specific follow-up question.";
                        }

                        string raw;
                        try { raw = await _router.ChatOnceAsync(system, userPrompt).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        if (string.IsNullOrWhiteSpace(raw)) raw = "(no response)";

                        var cleaned = SanitizeAssistantText(raw);
                        AppendConversation(normalizedSpeaker, "assistant", cleaned);

                        // Check for memory overflow after appending the response
                        await CheckAndProcessMemoryOverflowAsync(normalizedSpeaker, ct).ConfigureAwait(false);

                        // Save memory manager state periodically
                        if (mm.IsEnabled)
                        {
                            try { await mm.SaveAsync(ct).ConfigureAwait(false); }
                            catch { }
                        }

                        try { OnResponseReceived?.Invoke(cleaned); } catch { }
                    }
                    catch (Exception ex) { LogErr($"LLM dispatch failed: {ex.Message}"); }
                }
            }
            catch (Exception ex) { LogErr($"LLM dispatch failed: {ex.Message}"); }
        }

        /// <summary>
        /// Check if conversation history exceeds token limit and archive to vector DB if needed.
        /// Hot-context is scoped to the currently selected system prompt and includes ALL speakers.
        /// </summary>
        private static async Task CheckAndProcessMemoryOverflowAsync(string speaker, CancellationToken ct = default)
        {
            try
            {
                var settings = Snap?.Ollama;
                if (settings == null || !settings.VectorMemoryEnabled) return;

                int hotLimit = settings.HotContextTokenLimit;
                var key = GetCurrentMemoryKeySafe();
                int currentTokens = GetConversationTokenCountAllSpeakers(key);

                if (currentTokens <= hotLimit) return;

                Console.WriteLine($"?? Memory overflow detected: {currentTokens} tokens > {hotLimit} limit. Archiving...");
                try { OnMemoryArchiveStarted?.Invoke(); } catch { }

                // Archive all speakers for the current system prompt.
                // This ensures auto-vectorization triggers even when a single speaker doesn't exceed the limit.
                var messages = GetConversationMessagesAllSpeakers(key);
                if (messages.Count == 0) return;

                var mm = GetMemoryManager();
                if (!mm.IsEnabled)
                {
                    try { OnMemoryArchiveFailed?.Invoke("Vector memory not enabled or initialized"); } catch { }
                    return;
                }

                // Speaker parameter is informational; ProcessOverflowAsync archives using the messages list.
                var remaining = await mm.ProcessOverflowAsync(messages, speaker, ct).ConfigureAwait(false);

                int archivedCount = messages.Count - remaining.Count;
                if (archivedCount > 0)
                {
                    await ReplaceConversationHistoryAllSpeakersForCurrentPromptAsync(remaining, ct).ConfigureAwait(false);
                    await mm.SaveAsync(ct).ConfigureAwait(false);

                    Console.WriteLine($"?? Archived {archivedCount} messages, {remaining.Count} remaining in hot context");
                    try { OnMemoryArchiveCompleted?.Invoke(archivedCount, archivedCount); } catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"?? Memory overflow processing failed: {ex.Message}");
                try { OnMemoryArchiveFailed?.Invoke(ex.Message); } catch { }
            }
        }

        private static Task ReplaceConversationHistoryAllSpeakersForCurrentPromptAsync(List<Llm.ConversationMessage> remaining, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                try
                {
                    lock (_convLock)
                    {
                        var key = GetCurrentMemoryKeySafe();
                        var file = ResolveConversationFileForMemoryKey(key);
                        if (string.IsNullOrWhiteSpace(file)) return;

                        // Rebuild a full root object grouped by speaker, preserving order.
                        var root = new JObject();
                        foreach (var msg in remaining.OrderBy(m => m.Timestamp))
                        {
                            var sp = string.IsNullOrWhiteSpace(msg.Speaker) ? "UnknownSpeaker" : msg.Speaker.Trim();
                            var arr = root[sp] as JArray ?? (JArray)(root[sp] = new JArray());
                            arr.Add(new JObject
                            {
                                ["Role"] = msg.Role,
                                ["Content"] = msg.Content ?? string.Empty,
                                ["Timestamp"] = msg.Timestamp.ToString("o"),
                                ["Speaker"] = sp
                            });
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        File.WriteAllText(file, root.ToString(Formatting.Indented), Encoding.UTF8);
                    }
                }
                catch (Exception ex)
                {
                    LogErr($"Failed to replace conversation history (all speakers): {ex.Message}");
                }
            }, ct);
        }

        /// <summary>
        /// Manually trigger memory archival (for UI/testing).
        /// NEW STORAGE: archives ALL speakers for the current system prompt (memory key).
        /// Returns (messagesArchived, tokensBeforeArchive).
        /// </summary>
        public static async Task<(int archived, int tokensBefore)> ForceArchiveToMemoryAsync(string speaker, CancellationToken ct = default)
        {
            try
            {
                await EnsureMemoryInitializedAsync(ct).ConfigureAwait(false);

                var mm = GetMemoryManager();
                if (!mm.IsEnabled)
                {
                    try { OnMemoryArchiveFailed?.Invoke("Vector memory not enabled"); } catch { }
                    return (0, 0);
                }

                var key = GetCurrentMemoryKeySafe();
                var messages = GetConversationMessagesAllSpeakers(key);
                if (messages.Count == 0) return (0, 0);

                int tokensBefore = GetConversationTokenCountAllSpeakers(key);

                try { OnMemoryArchiveStarted?.Invoke(); } catch { }

                // Speaker parameter is informational only; archive is driven by messages list.
                var remaining = await mm.ProcessOverflowAsync(messages, "UnknownSpeaker", ct, forceArchiveAll: true).ConfigureAwait(false);

                int archivedCount = messages.Count - remaining.Count;

                // Replace hot context for this prompt with whatever remains (typically empty when forceArchiveAll=true).
                await ReplaceConversationHistoryAllSpeakersForCurrentPromptAsync(remaining, ct).ConfigureAwait(false);

                await mm.SaveAsync(ct).ConfigureAwait(false);

                Console.WriteLine($"?? Force archived {archivedCount} messages ({tokensBefore} tokens) for memoryKey '{key}'");
                try { OnMemoryArchiveCompleted?.Invoke(archivedCount, archivedCount); } catch { }

                return (archivedCount, tokensBefore);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"?? Force archive failed: {ex.Message}");
                try { OnMemoryArchiveFailed?.Invoke(ex.Message); } catch { }
                return (0, 0);
            }
        }

        /// <summary>
        /// Clear all vector memory (centroids, summaries, pinned facts).
        /// </summary>
        public static async Task ClearVectorMemoryAsync(CancellationToken ct = default)
        {
            try
            {
                await EnsureMemoryInitializedAsync(ct).ConfigureAwait(false);
                
                var mm = GetMemoryManager();
                if (mm == null)
                {
                    throw new InvalidOperationException("Memory manager not available");
                }

                // Clear the vector store
                mm.ClearAll();
                
                // Save the cleared state
                await mm.SaveAsync(ct).ConfigureAwait(false);
                
                Console.WriteLine("?? Vector memory cleared");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"?? Failed to clear vector memory: {ex.Message}");
                throw;
            }
        }

        private static void EnsureRouterInitialized()
        {
            if (_router != null) return;
            lock (_lock)
            {
                if (_router != null) return;
                string GetModel() { try { return Snap?.Ollama?.Model; } catch { return null; } }
                var ollamaClient = new OllamaClient(Snap?.Ollama?.BaseUrl ?? "http://127.0.0.1:11434", Snap?.Ollama?.ApiKey, GetModel);
                var lmClient = new LmStudioClient(Snap?.Ollama?.LmStudioBaseUrl ?? "http://127.0.0.1:1234", Snap?.Ollama?.ApiKey, GetModel);
                var start = (Snap?.Ollama?.Provider ?? "Ollama").Equals("LMStudio", StringComparison.OrdinalIgnoreCase) ? LlmProvider.LMStudio : LlmProvider.Ollama;
                _router = new LlmRouter(ollamaClient, lmClient, start);
            }
        }

        private static string LoadSystemPrompt()
        {
            try
            {
                if ((DateTime.UtcNow - _lastSystemPromptLoadUtc).TotalSeconds < 2 && !string.IsNullOrWhiteSpace(_systemPromptCache))
                    return _systemPromptCache;
                var path = Snap?.Ollama?.SystemPromptPath;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    if (!Path.IsPathRooted(path))
                    {
                        try { path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path)); } catch { path = null; }
                    }
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        _systemPromptCache = File.ReadAllText(path);
                        _lastSystemPromptLoadUtc = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception ex) { LogErr($"System prompt load failed: {ex.Message}"); }
            return _systemPromptCache ?? string.Empty;
        }

        private static string BuildUserPromptWithMemory(string speakerName, string transcription, string systemPrompt, string historyBlock, string memoryContext)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                var sys = systemPrompt.Trim();
                if (!sys.StartsWith("SYSTEM:", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("SYSTEM: You are a concise assistant for a Kinect-based multimodal app.");
                sb.AppendLine(sys).AppendLine();
            }
            
            // Include memory context (pinned facts, warm summary, retrieved chunks)
            if (!string.IsNullOrWhiteSpace(memoryContext))
            {
                sb.AppendLine("MEMORY CONTEXT:").AppendLine(memoryContext.Trim()).AppendLine();
            }
            
            if (!string.IsNullOrWhiteSpace(historyBlock))
            {
                sb.AppendLine("RECENT CONVERSATION:").AppendLine(historyBlock.Trim()).AppendLine();
            }
            sb.Append(speakerName + " says: " + (transcription ?? string.Empty).Trim());
            return sb.ToString();
        }

        private static string SanitizeAssistantText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            try
            {
                // Remove tool call blocks first
                text = ToolRegistry.Instance.RemoveToolCalls(text);
                
                var t = CleanAssistantPrefix(text)
                    .Replace('\u201C', '"').Replace('\u201D', '"')
                    .Replace('\u2018', '\'').Replace('\u2019', '\'');

                t = Regex.Replace(t, @"(?im)^\s*(time\s*stamp|timestamp)\s*:\s*.*$", string.Empty);
                t = Regex.Replace(t, @"(?i)\b(time\s*stamp|timestamp)\s*:\s*[\""]?\d{4}-\d{2}-\d{2}T[^\s\""]+[\""]?", string.Empty);
                t = ApplyThinkPolicy(t);
                t = Regex.Replace(t, @"\s+", " ").Trim();
                return t;
            }
            catch { return text.Trim(); }
        }

        private static string CleanAssistantPrefix(string text)
        {
            var t = text?.TrimStart();
            if (string.IsNullOrWhiteSpace(t)) return t;
            t = Regex.Replace(t, @"^(assistant|ai|bot|system|maggie)\s*[:\-]\s*", string.Empty, RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"^(?:[A-Za-z][\w .'\-]{0,40})\s+says\s*[:\-]\s*", string.Empty, RegexOptions.IgnoreCase);
            return t.Trim();
        }

        private static string ApplyThinkPolicy(string text)
        {
            bool show = false; try { show = Snap?.Ollama?.OutputThink ?? false; } catch { }
            if (!show)
                text = Regex.Replace(text, @"(?is)<\s*think\s*>.*?<\s*/\s*think\s*>", string.Empty);
            return text;
        }

        /// <summary>
        /// Extract complete sentences from the buffer, leaving incomplete text.
        /// Returns list of complete sentences and updates bufferText to contain only incomplete text.
        /// </summary>
        private static List<string> ExtractCompleteSentences(ref string bufferText)
        {
            var sentences = new List<string>();
            if (string.IsNullOrEmpty(bufferText)) return sentences;
            
            // Look for sentence-ending punctuation followed by space, newline, or end of string
            // Handle: . ! ? ... (ellipsis) followed by whitespace or at end
            int lastSentenceEnd = -1;
            int i = 0;
            
            while (i < bufferText.Length)
            {
                char c = bufferText[i];
                
                // Check for sentence-ending punctuation
                if (c == '.' || c == '!' || c == '?' || c == '…')
                {
                    // Handle ellipsis (...) as single unit
                    int punctEnd = i;
                    while (punctEnd + 1 < bufferText.Length && bufferText[punctEnd + 1] == '.')
                    {
                        punctEnd++;
                    }
                    
                    // Check if followed by whitespace (indicates sentence boundary)
                    int afterPunct = punctEnd + 1;
                    if (afterPunct < bufferText.Length)
                    {
                        char nextChar = bufferText[afterPunct];
                        if (char.IsWhiteSpace(nextChar) || nextChar == '\n' || nextChar == '\r')
                        {
                            // Found a sentence boundary
                            var sentence = bufferText.Substring(lastSentenceEnd + 1, punctEnd - lastSentenceEnd).Trim();
                            if (!string.IsNullOrWhiteSpace(sentence))
                            {
                                sentences.Add(sentence);
                            }
                            lastSentenceEnd = punctEnd;
                            i = afterPunct;
                            continue;
                        }
                    }
                    // Punctuation at end of buffer without trailing space - might be incomplete
                    i = punctEnd + 1;
                    continue;
                }
                
                // Check for newline as sentence boundary
                if (c == '\n')
                {
                    var sentence = bufferText.Substring(lastSentenceEnd + 1, i - lastSentenceEnd - 1).Trim();
                    if (!string.IsNullOrWhiteSpace(sentence) && sentence.Length > 1)
                    {
                        sentences.Add(sentence);
                        lastSentenceEnd = i;
                    }
                }
                
                i++;
            }
            
            // Keep the remaining incomplete part
            if (lastSentenceEnd >= 0 && lastSentenceEnd < bufferText.Length - 1)
            {
                bufferText = bufferText.Substring(lastSentenceEnd + 1).TrimStart();
            }
            else if (sentences.Count > 0)
            {
                bufferText = string.Empty;
            }
            // If no sentences extracted, keep the entire buffer
            
            return sentences;
        }
        #endregion

        /// <summary>
        /// Non-streaming single-shot chat. Optionally skip conversation history/logging.
        /// </summary>
        public static async Task<string> ChatOnceAsync(string systemPrompt, string userPrompt, CancellationToken ct = default, bool skipHistory = false)
        {
            try
            {
                if (!IsEnabled()) return string.Empty;
                EnsureRouterInitialized();

                await EnsureMemoryInitializedAsync(ct).ConfigureAwait(false);

                if (!skipHistory)
                {
                    // Append user to history
                    var speaker = "User";
                    AppendConversation(speaker, "user", userPrompt);
                }

                var response = await _router.ChatOnceAsync(systemPrompt ?? string.Empty, userPrompt ?? string.Empty, ct).ConfigureAwait(false);

                if (!skipHistory)
                {
                    var speaker = "User";
                    var cleaned = SanitizeAssistantText(response);
                    AppendConversation(speaker, "assistant", cleaned);
                }

                return response;
            }
            catch (OperationCanceledException)
            {
                return string.Empty;
            }
            catch (Exception ex)
            {
                LogErr($"ChatOnceAsync failed: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
