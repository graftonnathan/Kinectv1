// OllamaService.cs (drop-in replacement)
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using Kinectv1.Llm;

namespace Kinectv1
{
    /// <summary>Simple prompt tone enum (kept for API compatibility).</summary>
    public enum PromptTone
    {
        Neutral = 0,
        Friendly = 1,
        Formal = 2
    }

    /// <summary>
    /// Minimal, stable Ollama client used by UI and VoiceProcessor.
    /// Provides the public surface expected elsewhere in the app.
    /// </summary>
    public static class OllamaService
    {
        // --- Events used by UI for telemetry/status ---
        public static event Action<string> OnPromptSent;
        public static event Action<string> OnResponseReceived;
        public static event Action<string> OnError;

        // --- Backing fields ---
        private static readonly object _lock = new object();
        private static bool _initialized;
        private static HttpClient _httpClient;
        private static string _baseUrl = "http://127.0.0.1:11434"; // default Ollama endpoint
        private static string _lmStudioBaseUrl = "http://127.0.0.1:1234"; // default LM Studio endpoint
        private static string _defaultModel = "llama3.1:8b";
        private static string _systemPrompt = "";
        private static DateTime _lastSystemPromptLoad = DateTime.MinValue;

        // Router & clients
        private static LlmRouter _router;

        // --- Conversation persistence (minimal) ---
        private class ConversationMessage
        {
            public string Role { get; set; }
            public string Content { get; set; }
            public DateTime Timestamp { get; set; }
            public string Speaker { get; set; }
        }
        private static readonly object _convLock = new object();

        private static string GetConversationFilePath()
        {
            var dir = AppSettings.LoadConversationHistoryPath();
            if (string.IsNullOrWhiteSpace(dir)) dir = "history"; // AppSettings already validates; keep simple here
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var fullDir = Path.IsPathRooted(dir) ? dir : Path.Combine(baseDir, dir);
            try { Directory.CreateDirectory(fullDir); } catch { }
            return Path.Combine(fullDir, "conversation.json");
        }

        private static Dictionary<string, List<ConversationMessage>> LoadConversations()
        {
            try
            {
                var path = GetConversationFilePath();
                if (!File.Exists(path)) return new Dictionary<string, List<ConversationMessage>>(StringComparer.OrdinalIgnoreCase);
                var json = File.ReadAllText(path, Encoding.UTF8);
                var data = JsonConvert.DeserializeObject<Dictionary<string, List<ConversationMessage>>>(json);
                return data ?? new Dictionary<string, List<ConversationMessage>>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, List<ConversationMessage>>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void SaveConversations(Dictionary<string, List<ConversationMessage>> conv)
        {
            try
            {
                var path = GetConversationFilePath();
                var json = JsonConvert.SerializeObject(conv, Formatting.Indented);
                File.WriteAllText(path, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Conversation save failed: {ex.Message}");
            }
        }

        private static void AppendConversation(string speaker, string role, string content)
        {
            if (!AppSettings.LoadOllamaMemoryEnabled()) return;
            if (string.IsNullOrWhiteSpace(speaker)) speaker = "UnknownSpeaker";
            try
            {
                lock (_convLock)
                {
                    var conv = LoadConversations();
                    if (!conv.TryGetValue(speaker, out var list))
                    {
                        list = new List<ConversationMessage>();
                        conv[speaker] = list;
                    }
                    list.Add(new ConversationMessage
                    {
                        Role = role,
                        Content = content ?? string.Empty,
                        Timestamp = DateTime.Now,
                        Speaker = speaker
                    });

                    // Trim per-speaker list to max
                    var maxPerSpeaker = AppSettings.LoadOllamaMaxMessagesPerSpeaker();
                    if (maxPerSpeaker > 0 && list.Count > maxPerSpeaker)
                    {
                        list.RemoveRange(0, list.Count - maxPerSpeaker);
                    }
                    SaveConversations(conv);
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Conversation append failed: {ex.Message}");
            }
        }

        private static string BuildHistoryBlock(string speaker)
        {
            if (!AppSettings.LoadOllamaMemoryEnabled()) return string.Empty;
            try
            {
                lock (_convLock)
                {
                    var conv = LoadConversations();
                    if (!conv.TryGetValue(speaker, out var list) || list.Count == 0) return string.Empty;

                    // Use most recent N items
                    var maxPerSpeaker = AppSettings.LoadOllamaMaxMessagesPerSpeaker();
                    var start = Math.Max(0, list.Count - Max(1, maxPerSpeaker));
                    var sb = new StringBuilder();
                    for (int i = start; i < list.Count; i++)
                    {
                        var m = list[i];
                        if (string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                            sb.AppendLine($"USER ({speaker}): {m.Content}");
                        else
                            sb.AppendLine($"ASSISTANT: {m.Content}");
                    }
                    return sb.ToString();
                }
            }
            catch { return string.Empty; }
        }

        private static string CleanAssistantPrefix(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            var t = text.TrimStart();
            // Strip common assistant labels at the start
            t = Regex.Replace(t, @"^(assistant|ai|bot|system|maggie)\s*[:\-]\s*", string.Empty, RegexOptions.IgnoreCase);
            // Strip accidental speaker echo like "Nathan says:" at the start
            t = Regex.Replace(t, "^(?:[A-Za-z][\\w .'\"]{0,40})\\s+says\\s*[:\\-]\\s*", string.Empty, RegexOptions.IgnoreCase);
            return t.Trim();
        }

        // Remove metadata like "Timestamp: 2025-..." from model responses
        private static string SanitizeAssistantText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            try
            {
                var t = CleanAssistantPrefix(text);

                // Normalize fancy quotes to ASCII so regex can match
                t = t.Replace('\u201C', '"').Replace('\u201D', '"')
                     .Replace('\u2018', '\'').Replace('\u2019', '\'');

                // Remove whole lines starting with Timestamp:/Time Stamp:
                t = Regex.Replace(t, @"(?im)^\s*(time\s*stamp|timestamp)\s*:\s*.*$", string.Empty);

                // Remove inline labels like "..., Timestamp: \"2025-...\"" or without quotes
                t = Regex.Replace(
                    t,
                    @"(?i)\b(time\s*stamp|timestamp)\s*:\s*[\""]?\d{4}-\d{2}-\d{2}T[^\s\""]+[\""]?",
                    string.Empty);

                // Optionally strip <think> blocks based on setting
                t = ApplyThinkPolicy(t);

                // Collapse whitespace
                t = Regex.Replace(t, @"\s+", " ").Trim();
                return t;
            }
            catch { return CleanAssistantPrefix(text); }
        }

        private static string ApplyThinkPolicy(string text)
        {
            try
            {
                // If OutputThink is true, preserve content; otherwise, remove <think> blocks entirely.
                var show = false;
                try
                {
                    // Prefer JSON snapshot if available
                    var snap = App.SettingsProvider?.Current;
                    show = snap?.Ollama?.OutputThink ?? false;
                }
                catch { }
                if (!show)
                {
                    // Remove any <think>...</think> (multi-line) completely
                    text = Regex.Replace(text, @"(?is)<\s*think\s*>.*?<\s*/\s*think\s*>", string.Empty);
                }
                return text;
            }
            catch { return text; }
        }

        // --------------- Public API expected by the app ---------------

        public static bool IsEnabled() => AppSettings.LoadOllamaEnabled();

        public static void SetEnabled(bool enabled)
        {
            try
            {
                // Persist through JSON settings pipeline
                var svc = App.SettingsProvider;
                if (svc == null) { AppSettings.SaveOllamaEnabled(enabled); return; }
                svc.Save(curr => curr with { Ollama = curr.Ollama with { Enabled = enabled } });
            }
            catch { AppSettings.SaveOllamaEnabled(enabled); }
        }

        public static void SetProvider(string provider)
        {
            try
            {
                EnsureRouterInitialized();
                var p = string.Equals(provider, "LMStudio", StringComparison.OrdinalIgnoreCase) ? LlmProvider.LMStudio : LlmProvider.Ollama;
                _router.SwitchTo(p);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"SetProvider failed: {ex.Message}");
            }
        }

        public static async Task<bool> TestConnectionAsync(CancellationToken ct = default)
        {
            try
            {
                InitializeIfNeeded();
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/tags");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                var resp = await _httpClient.SendAsync(req, cts.Token).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Ollama connection failed: {ex.Message}");
                return false;
            }
        }

        public static async Task<List<string>> GetAvailableModelsAsync(CancellationToken ct = default)
        {
            var list = new List<string>();
            try
            {
                InitializeIfNeeded();

                // Determine provider from settings
                var provider = "Ollama";
                try { provider = App.SettingsProvider?.Current?.Ollama?.Provider ?? "Ollama"; } catch { }

                if (string.Equals(provider, "LMStudio", StringComparison.OrdinalIgnoreCase))
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"{_lmStudioBaseUrl}/v1/models");
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(6));
                    var resp = await _httpClient.SendAsync(req, cts.Token).ConfigureAwait(false);
                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    // Expected OpenAI schema: { "data": [ {"id": "model-id"}, ... ] }
                    var root = JObject.Parse(json);
                    var arr = root["data"] as JArray;
                    if (arr != null)
                    {
                        foreach (var m in arr)
                        {
                            var id = m?["id"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(id)) list.Add(id);
                        }
                    }
                }
                else
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/tags");
                    using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts2.CancelAfter(TimeSpan.FromSeconds(6));
                    var resp2 = await _httpClient.SendAsync(req, cts2.Token).ConfigureAwait(false);
                    var json2 = await resp2.Content.ReadAsStringAsync().ConfigureAwait(false);

                    // Expected format: { "models": [ { "name": "model:tag", ... }, ... ] }
                    var root2 = JObject.Parse(json2);
                    var arr2 = root2["models"] as JArray;
                    if (arr2 != null)
                    {
                        foreach (var m in arr2)
                        {
                            var name = m?["name"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(name)) list.Add(name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"GetAvailableModels failed: {ex.Message}");
            }
            return list;
        }

        /// <summary>
        /// Immediately switch the active Ollama model and persist it.
        /// </summary>
        public static void SetDefaultModel(string model)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(model)) return;
                lock (_lock)
                {
                    _defaultModel = model.Trim();
                }
                AppSettings.SaveOllamaModel(model.Trim());
                Console.WriteLine($"Saved Ollama model: {model.Trim()}");
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"SetDefaultModel failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Primary async entry used by VoiceProcessor. Sends a prompt and raises events.
        /// </summary>
        public static async Task SendPromptAsync(string speakerName, string transcription, CancellationToken ct = default)
        {
            try
            {
                if (!IsEnabled())
                {
                    OnError?.Invoke("Ollama is disabled.");
                    return;
                }

                InitializeIfNeeded();
                EnsureRouterInitialized();

                // Load model from settings each call in case user changed it in UI
                var model = AppSettings.LoadOllamaModel();
                if (!string.IsNullOrWhiteSpace(model))
                {
                    lock (_lock) { _defaultModel = model.Trim(); }
                }
                // Resolve model via JSON settings pipeline; fall back to legacy only if pipeline not available
                string effectiveModel = null;
                try
                {
                    var svc = App.SettingsProvider;
                    var snap = svc?.Current;
                    effectiveModel = snap?.Ollama?.Model;
                }
                catch { }
                if (string.IsNullOrWhiteSpace(effectiveModel))
                {
                    var legacy = AppSettings.LoadOllamaModel();
                    if (!string.IsNullOrWhiteSpace(legacy)) effectiveModel = legacy.Trim();
                }
                if (string.IsNullOrWhiteSpace(effectiveModel)) effectiveModel = _defaultModel;

                var system = LoadSystemPrompt();
                var normalizedSpeaker = string.IsNullOrWhiteSpace(speakerName) ? "UnknownSpeaker" : speakerName.Trim();

                // Build history block if memory enabled
                var historyBlock = BuildHistoryBlock(normalizedSpeaker);
                // Build user prompt WITHOUT embedding the system prompt; system is passed separately to router
                var userPrompt = BuildUserPrompt(normalizedSpeaker, transcription, null, historyBlock);

                OnPromptSent?.Invoke(userPrompt);

                // Persist user message
                AppendConversation(normalizedSpeaker, "user", transcription);

                string fullText;
                try
                {
                    fullText = await _router.ChatOnceAsync(system, userPrompt).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Provider switched or cancelled; do not report error
                    return;
                }

                if (string.IsNullOrWhiteSpace(fullText))
                    fullText = "(no response)";

                var cleaned = SanitizeAssistantText(fullText);

                // Persist assistant response
                AppendConversation(normalizedSpeaker, "assistant", cleaned);

                OnResponseReceived?.Invoke(cleaned);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"SendPromptAsync error: {ex.Message}");
            }
        }

        /// <summary>
        /// Synchronous helper used by MainWindow for quick tests.
        /// </summary>
        public static string SendPrompt(string prompt, PromptTone tone = PromptTone.Neutral)
        {
            try
            {
                // Keep API parity with UI code that ignores the tone today
                var task = SendPromptAsync("TestSpeaker", prompt);
                task.Wait();
                return "(sent)"; // UI reacts via OnResponseReceived
            }
            catch (Exception ex)
            {
                var err = $"SendPrompt error: {ex.Message}";
                OnError?.Invoke(err);
                return err;
            }
        }

        // --------------- Helpers ---------------

        private static void InitializeIfNeeded()
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;

                _httpClient = new HttpClient();

                // Model default from settings (safe if missing)
                var model = AppSettings.LoadOllamaModel();
                if (!string.IsNullOrWhiteSpace(model))
                    _defaultModel = model;

                // Base URL: not present in AppSettings; keep default unless you later add it.
                // _baseUrl = AppSettings.LoadOllamaBaseUrl(); // (doesn't exist in your repo)

                _initialized = true;
            }
        }

        private static void EnsureRouterInitialized()
        {
            if (_router != null) return;
            lock (_lock)
            {
                if (_router != null) return;

                // Create clients with lazy model getter (reads latest from JSON pipeline)
                string GetModel() {
                    try { return App.SettingsProvider?.Current?.Ollama?.Model ?? AppSettings.LoadOllamaModel() ?? _defaultModel; }
                    catch { return _defaultModel; }
                }

                var ollamaClient = new Kinectv1.Llm.OllamaClient("http://127.0.0.1:11434", apiKey: null, getModel: GetModel);
                var lmClient = new Kinectv1.Llm.LmStudioClient("http://127.0.0.1:1234", apiKey: "lm-studio", getModel: GetModel);

                var start = LlmProvider.Ollama;
                try
                {
                    var prov = App.SettingsProvider?.Current?.Ollama?.Provider;
                    if (string.Equals(prov, "LMStudio", StringComparison.OrdinalIgnoreCase)) start = LlmProvider.LMStudio;
                }
                catch { }

                _router = new LlmRouter(ollamaClient, lmClient, start);
            }
        }

        private static string LoadSystemPrompt()
        {
            try
            {
                // basic throttling on disk reads
                if ((DateTime.UtcNow - _lastSystemPromptLoad).TotalSeconds < 2 && !string.IsNullOrEmpty(_systemPrompt))
                    return _systemPrompt;

                var path = AppSettings.LoadSystemPromptPath();
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    _systemPrompt = File.ReadAllText(path);
                    _lastSystemPromptLoad = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"System prompt load failed: {ex.Message}");
            }
            return _systemPrompt ?? string.Empty;
        }

        private static string BuildUserPrompt(string speakerName, string transcription, string systemPrompt, string historyBlock = null)
        {
            speakerName = string.IsNullOrWhiteSpace(speakerName) ? "Unknown" : speakerName.Trim();
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                // If the prompt already has "SYSTEM:" prefix, keep it. Otherwise add our own.
                var sys = systemPrompt.Trim();
                if (sys.StartsWith("SYSTEM:", StringComparison.OrdinalIgnoreCase) ||
                    sys.StartsWith("System:", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine(sys);
                }
                else
                {
                    sb.AppendLine("SYSTEM: You are a concise assistant for a Kinect-based multimodal app.");
                    sb.AppendLine(sys);
                }
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(historyBlock))
            {
                sb.AppendLine("CONVERSATION:");
                sb.AppendLine(historyBlock.Trim());
                sb.AppendLine();
            }

            // Align incoming message format with system prompt guidance:
            // "Name says: <their words>"
            var userLine = $"{speakerName} says: {transcription?.Trim()}";
            sb.Append(userLine);
            return sb.ToString();
        }

        private static string ExtractTextFromGenerate(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return "";
                var obj = JObject.Parse(json);
                // /api/generate returns "response"
                return obj["response"]?.ToString() ?? "";
            }
            catch { return ""; }
        }

        private static string ExtractTextFromChat(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return "";
                var obj = JObject.Parse(json);
                // /api/chat streams "message":{"content": "..."} pieces; consolidated non-stream may have it too
                var content = obj["message"]?["content"]?.ToString();
                if (!string.IsNullOrWhiteSpace(content)) return content;

                // Sometimes "text" is used by wrappers
                return obj["text"]?.ToString() ?? "";
            }
            catch { return ""; }
        }

        // local helper to avoid System.Math call in tight loop above
        private static int Max(int a, int b) => a > b ? a : b;
    }
}
