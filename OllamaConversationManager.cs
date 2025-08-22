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
        private static string _defaultModel = "llama3.1:8b";
        private static string _systemPrompt = "";
        private static DateTime _lastSystemPromptLoad = DateTime.MinValue;

        // --------------- Public API expected by the app ---------------

        public static bool IsEnabled() => AppSettings.LoadOllamaEnabled();

        public static void SetEnabled(bool enabled)
        {
            AppSettings.SaveOllamaEnabled(enabled);
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
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/tags");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                var resp = await _httpClient.SendAsync(req, cts.Token).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                // Expected format: { "models": [ { "name": "model:tag", ... }, ... ] }
                var root = JObject.Parse(json);
                var arr = root["models"] as JArray;
                if (arr != null)
                {
                    foreach (var m in arr)
                    {
                        var name = m?["name"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(name)) list.Add(name);
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

                // Load model from settings each call in case user changed it in UI
                var model = AppSettings.LoadOllamaModel();
                if (!string.IsNullOrWhiteSpace(model)) _defaultModel = model;

                var system = LoadSystemPrompt();
                var userPrompt = BuildUserPrompt(speakerName, transcription, system);

                OnPromptSent?.Invoke(userPrompt);

                // Use /api/generate (simpler) with stream=false
                var payload = new
                {
                    model = _defaultModel,
                    prompt = userPrompt,
                    stream = false
                };
                var json = JsonConvert.SerializeObject(payload);
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/generate")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(60));
                var resp = await _httpClient.SendAsync(req, cts.Token).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                // Typical response: { "model":"...", "created_at":"...", "response":"...", "done":true, ... }
                var text = ExtractTextFromGenerate(body);
                if (string.IsNullOrWhiteSpace(text))
                    text = ExtractTextFromChat(body); // fallback if server routed to chat

                if (string.IsNullOrWhiteSpace(text))
                    text = "(no response)";

                OnResponseReceived?.Invoke(text);
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

        private static string BuildUserPrompt(string speakerName, string transcription, string systemPrompt)
        {
            speakerName = string.IsNullOrWhiteSpace(speakerName) ? "UnknownSpeaker" : speakerName.Trim();
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

            sb.Append("USER");
            if (!string.IsNullOrWhiteSpace(speakerName))
                sb.Append($" ({speakerName})");
            sb.Append(": ");
            sb.Append(transcription?.Trim() ?? "");
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
    }
}
