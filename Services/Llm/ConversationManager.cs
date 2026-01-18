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

namespace Kinectv1
{
    // Central LLM + conversation history service
    public static class OllamaService
    {
        public static event Action<string> OnPromptSent;
        public static event Action<string> OnResponseReceived;
        public static event Action<string> OnError;
        
        // New: Events for memory archival status
        public static event Action OnMemoryArchiveStarted;
        public static event Action<int, int> OnMemoryArchiveCompleted; // (messagesArchived, chunksCreated)
        public static event Action<string> OnMemoryArchiveFailed;

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

        /// <summary>
        /// Get the current memory key based on system prompt path.
        /// Each system prompt gets its own isolated memory store.
        /// </summary>
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

        private static IEnumerable<string> EnumerateConversationFilesAscending()
        {
            var info = GetConversationStorageInfo();
            if (string.IsNullOrWhiteSpace(info.explicitFile)) yield break;
            if (info.singleFile) { yield return info.explicitFile; yield break; }
            if (!Directory.Exists(info.directory)) yield break;
            // Base + numbered variants conversation.json, conversation_#.json
            var files = Directory.GetFiles(info.directory, "conversation*.json");
            int IndexOf(string f)
            {
                var name = Path.GetFileNameWithoutExtension(f) ?? string.Empty;
                if (name.Equals("conversation", StringComparison.OrdinalIgnoreCase)) return 0;
                var us = name.LastIndexOf('_');
                if (us >= 0)
                {
                    var tail = us + 1 < name.Length ? name.Substring(us + 1) : string.Empty;
                    int n; if (int.TryParse(tail, out n)) return n;
                }
                return 0;
            }
            foreach (var f in files.OrderBy(f => IndexOf(f))) yield return f;
        }
        private static IEnumerable<string> EnumerateConversationFilesDescending() => EnumerateConversationFilesAscending().Reverse();

        private static string GetActiveConversationFileForAppend(string speaker)
        {
            var info = GetConversationStorageInfo();
            if (string.IsNullOrWhiteSpace(info.explicitFile)) return null;
            if (info.singleFile) return info.explicitFile;
            var files = EnumerateConversationFilesAscending().ToList();
            if (files.Count == 0) return info.explicitFile;
            var latest = files[files.Count - 1];
            try
            {
                var max = MaxMessagesPerSpeaker();
                if (max > 0 && CountSpeakerMessages(latest, speaker) >= max)
                {
                    int lastIndex = 0;
                    var fname = Path.GetFileNameWithoutExtension(latest) ?? "conversation";
                    if (!fname.Equals("conversation", StringComparison.OrdinalIgnoreCase))
                    {
                        var us = fname.LastIndexOf('_');
                        if (us >= 0)
                        {
                            var tail = us + 1 < fname.Length ? fname.Substring(us + 1) : string.Empty;
                            int n; if (int.TryParse(tail, out n)) lastIndex = n;
                        }
                    }
                    return Path.Combine(info.directory, lastIndex == 0 && Path.GetFileName(latest).Equals("conversation.json", StringComparison.OrdinalIgnoreCase)
                        ? "conversation_1.json" : "conversation_" + (lastIndex + 1) + ".json");
                }
            }
            catch { }
            return latest;
        }

        private static int CountSpeakerMessages(string file, string speaker)
        {
            try
            {
                if (!File.Exists(file)) return 0;
                var txt = File.ReadAllText(file, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(txt)) return 0;
                var jo = JObject.Parse(txt);
                var arr = jo[speaker] as JArray;
                return arr?.Count ?? 0;
            }
            catch { return 0; }
        }
        #endregion

        #region Append Logic
        private static bool MemoryEnabled()
        {
            bool enabled = Snap?.Ollama?.MemoryEnabled == true && !string.IsNullOrWhiteSpace(Snap?.Ollama?.ConversationHistoryPath);
            if (!enabled) LogErr("Conversation memory disabled or path not set.");
            return enabled;
        }
        private static int MaxMessagesPerSpeaker() => Snap?.Ollama?.MaxMessagesPerSpeaker ?? 0;

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
                    var ok = TryAppendSurgical(targetFile, speaker, role, content);
                    if (!ok && !FallbackAppendFull(targetFile, speaker, role, content))
                        LogErr("Conversation append failed (both surgical + fallback).");
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
        private static string BuildHistoryBlock(string speaker)
        {
            if (!MemoryEnabled()) return string.Empty;
            try
            {
                lock (_convLock)
                {
                    int max = MaxMessagesPerSpeaker();
                    var collected = new List<ConversationMessage>();
                    foreach (var file in EnumerateConversationFilesDescending())
                    {
                        try
                        {
                            if (!File.Exists(file)) continue;
                            var txt = File.ReadAllText(file, Encoding.UTF8);
                            if (string.IsNullOrWhiteSpace(txt)) continue;
                            var jo = JObject.Parse(txt);
                            var arr = jo[speaker] as JArray; if (arr == null || arr.Count == 0) continue;
                            for (int i = arr.Count - 1; i >= 0; i--)
                            {
                                if (max > 0 && collected.Count >= max) break;
                                var jm = arr[i] as JObject; if (jm == null) continue;
                                collected.Add(new ConversationMessage
                                {
                                    Role = jm["Role"]?.Value<string>(),
                                    Content = jm["Content"]?.Value<string>(),
                                    Speaker = jm["Speaker"]?.Value<string>(),
                                    Timestamp = jm["Timestamp"]?.Value<DateTime?>() ?? DateTime.MinValue
                                });
                            }
                            if (max > 0 && collected.Count >= max) break;
                        }
                        catch { }
                    }
                    if (collected.Count == 0) return string.Empty;
                    collected.Reverse();
                    if (max > 0 && collected.Count > max)
                        collected = collected.Skip(collected.Count - max).ToList();
                    var sb = new StringBuilder();
                    foreach (var m in collected)
                        sb.AppendLine(string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) ? $"USER ({speaker}): {m.Content}" : $"ASSISTANT: {m.Content}");
                    return sb.ToString();
                }
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Get conversation history messages for a speaker from the JSON files.
        /// Used by MemoryManager for token counting and overflow processing.
        /// </summary>
        public static List<Llm.ConversationMessage> GetConversationMessagesPublic(string speaker, int? maxMessages = null)
        {
            var result = new List<Llm.ConversationMessage>();
            if (!MemoryEnabled()) return result;

            speaker = string.IsNullOrWhiteSpace(speaker) ? "UnknownSpeaker" : speaker.Trim();

            try
            {
                lock (_convLock)
                {
                    int max = maxMessages ?? MaxMessagesPerSpeaker();
                    foreach (var file in EnumerateConversationFilesDescending())
                    {
                        try
                        {
                            if (!File.Exists(file)) continue;
                            var txt = File.ReadAllText(file, Encoding.UTF8);
                            if (string.IsNullOrWhiteSpace(txt)) continue;
                            var jo = JObject.Parse(txt);
                            var arr = jo[speaker] as JArray;
                            if (arr == null || arr.Count == 0) continue;

                            for (int i = arr.Count - 1; i >= 0; i--)
                            {
                                if (max > 0 && result.Count >= max) break;
                                var jm = arr[i] as JObject;
                                if (jm == null) continue;

                                result.Add(new Llm.ConversationMessage
                                {
                                    Role = jm["Role"]?.Value<string>() ?? "user",
                                    Content = jm["Content"]?.Value<string>() ?? string.Empty,
                                    Speaker = jm["Speaker"]?.Value<string>() ?? speaker,
                                    Timestamp = jm["Timestamp"]?.Value<DateTime?>() ?? DateTime.MinValue
                                });
                            }
                            if (max > 0 && result.Count >= max) break;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // Reverse to chronological order (oldest first)
            result.Reverse();
            return result;
        }

        /// <summary>
        /// Calculate total token count from conversation history JSON for a speaker.
        /// Uses the same formatting as BuildHistoryBlock for accurate estimation.
        /// </summary>
        public static int GetConversationTokenCount(string speaker)
        {
            var messages = GetConversationMessagesPublic(speaker);
            if (messages.Count == 0) return 0;

            int totalTokens = 0;
            foreach (var msg in messages)
            {
                // Estimate tokens for role prefix + content (matches how history is formatted)
                var formatted = string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase)
                    ? $"USER ({msg.Speaker}): {msg.Content}"
                    : $"ASSISTANT: {msg.Content}";
                totalTokens += TokenEstimator.EstimateTokens(formatted);
            }

            return totalTokens;
        }

        /// <summary>
        /// Get the current hot context token count for display in UI.
        /// </summary>
        public static (int tokenCount, int messageCount) GetHotContextStats(string speaker)
        {
            var messages = GetConversationMessagesPublic(speaker);
            int tokens = GetConversationTokenCount(speaker);
            return (tokens, messages.Count);
        }
        #endregion

        #region LLM Interaction
        public static bool IsEnabled() => Snap?.Ollama?.Enabled == true;
        public static Task DispatchAsync(string speaker, string text) => SendPromptAsync(speaker, text);

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
                var history = BuildHistoryBlock(normalizedSpeaker);
                
                // Build context from vector memory (warm summary + retrieved chunks)
                string memoryContext = string.Empty;
                var mm = GetMemoryManager();
                if (mm.IsEnabled)
                {
                    try
                    {
                        var context = await mm.BuildContextAsync(transcription, normalizedSpeaker, ct).ConfigureAwait(false);
                        memoryContext = mm.FormatContextForPrompt(context);
                        if (!string.IsNullOrWhiteSpace(memoryContext))
                        {
                            Console.WriteLine($"?? Retrieved memory context: {TokenEstimator.EstimateTokens(memoryContext)} tokens");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"?? Memory context retrieval failed: {ex.Message}");
                    }
                }
                
                var userPrompt = BuildUserPromptWithMemory(normalizedSpeaker, transcription, null, history, memoryContext);
                try { OnPromptSent?.Invoke(userPrompt); } catch { }
                AppendConversation(normalizedSpeaker, "user", transcription);
                
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

        /// <summary>
        /// Check if conversation history exceeds token limit and archive to vector DB if needed.
        /// </summary>
        private static async Task CheckAndProcessMemoryOverflowAsync(string speaker, CancellationToken ct = default)
        {
            try
            {
                var settings = Snap?.Ollama;
                if (settings == null || !settings.VectorMemoryEnabled) return;

                int hotLimit = settings.HotContextTokenLimit;
                int currentTokens = GetConversationTokenCount(speaker);

                if (currentTokens <= hotLimit) return;

                Console.WriteLine($"?? Memory overflow detected: {currentTokens} tokens > {hotLimit} limit. Archiving...");
                try { OnMemoryArchiveStarted?.Invoke(); } catch { }

                var messages = GetConversationMessagesPublic(speaker);
                if (messages.Count == 0) return;

                var mm = GetMemoryManager();
                if (!mm.IsEnabled)
                {
                    try { OnMemoryArchiveFailed?.Invoke("Vector memory not enabled or initialized"); } catch { }
                    return;
                }

                // Process overflow - archive old messages to vector DB
                var remaining = await mm.ProcessOverflowAsync(messages, speaker, ct).ConfigureAwait(false);

                int archivedCount = messages.Count - remaining.Count;
                if (archivedCount > 0)
                {
                    // Clear the old conversation history and keep only remaining messages
                    await ReplaceConversationHistoryAsync(speaker, remaining, ct).ConfigureAwait(false);
                    
                    // Save vector store
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

        /// <summary>
        /// Replace conversation history with the given messages (after archival).
        /// </summary>
        private static Task ReplaceConversationHistoryAsync(string speaker, List<Llm.ConversationMessage> remaining, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                try
                {
                    lock (_convLock)
                    {
                        var info = GetConversationStorageInfo();
                        if (string.IsNullOrWhiteSpace(info.explicitFile)) return;

                        // Read existing file
                        var file = info.explicitFile;
                        var root = new JObject();
                        if (File.Exists(file))
                        {
                            try { root = JObject.Parse(File.ReadAllText(file, Encoding.UTF8)); }
                            catch { root = new JObject(); }
                        }

                        // Replace speaker's messages with remaining ones
                        var newArr = new JArray();
                        foreach (var msg in remaining)
                        {
                            newArr.Add(new JObject
                            {
                                ["Role"] = msg.Role,
                                ["Content"] = msg.Content ?? string.Empty,
                                ["Timestamp"] = msg.Timestamp.ToString("o"),
                                ["Speaker"] = msg.Speaker ?? speaker
                            });
                        }
                        root[speaker] = newArr;

                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        File.WriteAllText(file, root.ToString(Formatting.Indented), Encoding.UTF8);
                    }
                }
                catch (Exception ex)
                {
                    LogErr($"Failed to replace conversation history: {ex.Message}");
                }
            }, ct);
        }

        /// <summary>
        /// Manually trigger memory archival for a speaker (for testing/UI).
        /// Returns (messagesArchived, tokensBeforeArchive).
        /// </summary>
        public static async Task<(int archived, int tokensBefore)> ForceArchiveToMemoryAsync(string speaker, CancellationToken ct = default)
        {
            try
            {
                var normalizedSpeaker = string.IsNullOrWhiteSpace(speaker) ? "UnknownSpeaker" : speaker.Trim();
                
                await EnsureMemoryInitializedAsync(ct).ConfigureAwait(false);
                
                var mm = GetMemoryManager();
                if (!mm.IsEnabled)
                {
                    try { OnMemoryArchiveFailed?.Invoke("Vector memory not enabled"); } catch { }
                    return (0, 0);
                }

                var messages = GetConversationMessagesPublic(normalizedSpeaker);
                if (messages.Count == 0)
                {
                    return (0, 0);
                }

                int tokensBefore = GetConversationTokenCount(normalizedSpeaker);
                
                try { OnMemoryArchiveStarted?.Invoke(); } catch { }

                // Archive ALL messages (force mode)
                var remaining = await mm.ProcessOverflowAsync(messages, normalizedSpeaker, ct, forceArchiveAll: true).ConfigureAwait(false);

                int archivedCount = messages.Count - remaining.Count;

                // Clear conversation history
                await ClearConversationHistoryAsync(normalizedSpeaker, ct).ConfigureAwait(false);

                // Save
                await mm.SaveAsync(ct).ConfigureAwait(false);

                Console.WriteLine($"?? Force archived {archivedCount} messages ({tokensBefore} tokens)");
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
        /// Archive all messages to vector store (bypasses overflow check).
        /// </summary>
        private static async Task<int> ArchiveAllMessagesAsync(MemoryManager mm, List<Llm.ConversationMessage> messages, string speaker, CancellationToken ct)
        {
            // Use reflection or make ProcessOverflowAsync accept force parameter
            // For now, use the existing method but with empty "keep" list
            var remaining = await mm.ProcessOverflowAsync(messages, speaker, ct).ConfigureAwait(false);
            return messages.Count - remaining.Count;
        }

        /// <summary>
        /// Clear all conversation history for a speaker.
        /// </summary>
        public static Task ClearConversationHistoryAsync(string speaker, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                try
                {
                    lock (_convLock)
                    {
                        var info = GetConversationStorageInfo();
                        if (string.IsNullOrWhiteSpace(info.explicitFile)) return;

                        var file = info.explicitFile;
                        if (!File.Exists(file)) return;

                        var root = JObject.Parse(File.ReadAllText(file, Encoding.UTF8));
                        root[speaker] = new JArray(); // Empty array
                        File.WriteAllText(file, root.ToString(Formatting.Indented), Encoding.UTF8);
                    }
                }
                catch (Exception ex)
                {
                    LogErr($"Failed to clear conversation history: {ex.Message}");
                }
            }, ct);
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

        private static string BuildUserPrompt(string speakerName, string transcription, string systemPrompt, string historyBlock)
        {
            return BuildUserPromptWithMemory(speakerName, transcription, systemPrompt, historyBlock, null);
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
        #endregion
    }
}
