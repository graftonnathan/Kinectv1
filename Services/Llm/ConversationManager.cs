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

        private static void LogErr(string msg)
        {
            try { OnError?.Invoke(msg); } catch { }
        }

        private static readonly object _lock = new();
        private static LlmRouter _router;
        private static string _systemPromptCache = string.Empty;
        private static DateTime _lastSystemPromptLoadUtc = DateTime.MinValue;

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
                var system = LoadSystemPrompt();
                var history = BuildHistoryBlock(normalizedSpeaker);
                var userPrompt = BuildUserPrompt(normalizedSpeaker, transcription, null, history);
                try { OnPromptSent?.Invoke(userPrompt); } catch { }
                AppendConversation(normalizedSpeaker, "user", transcription);
                string raw;
                try { raw = await _router.ChatOnceAsync(system, userPrompt).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (string.IsNullOrWhiteSpace(raw)) raw = "(no response)";
                var cleaned = SanitizeAssistantText(raw);
                AppendConversation(normalizedSpeaker, "assistant", cleaned);
                try { OnResponseReceived?.Invoke(cleaned); } catch { }
            }
            catch (Exception ex) { LogErr($"LLM dispatch failed: {ex.Message}"); }
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
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                var sys = systemPrompt.Trim();
                if (!sys.StartsWith("SYSTEM:", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("SYSTEM: You are a concise assistant for a Kinect-based multimodal app.");
                sb.AppendLine(sys).AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(historyBlock))
            {
                sb.AppendLine("CONVERSATION:").AppendLine(historyBlock.Trim()).AppendLine();
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
