// OllamaService.cs — single, canonical implementation (no hosted service)
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
    // Keep this if UI references it
    public enum PromptTone { Neutral = 0, Friendly = 1, Formal = 2 }

    public static class OllamaService
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static string _baseUrl = "http://localhost:11434";
        private static string _defaultModel = "gemma3:4b";       // match your App.config default
        private static bool _isEnabled = false;                  // start disabled per your note
        private static string _systemPrompt = "";                // cached prompt text
        private static DateTime _lastSystemPromptLoad = DateTime.MinValue;

        private static bool _initialized = false;
        private static readonly object _initLock = new object();

        // Events for UI integration
        public static event Action<string> OnPromptSent;
        public static event Action<string> OnResponseReceived;
        public static event Action<string> OnError;

        // Static ctor: very light—don’t do I/O here
        static OllamaService()
        {
            // If you later add AppSettings keys for these, read them here.
            // Keep side-effects minimal to avoid type loader issues.
        }

        // ---------------- Public API (used by MainWindow / VoiceProcessor) ----------------

        public static bool IsEnabled() => _isEnabled;

        public static void SetEnabled(bool enabled)
        {
            _isEnabled = enabled;
            SafeExec(() => AppSettings.SaveOllamaEnabled(enabled)); // ignore if AppSettings is not wired
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

                // Expected: { "models": [ { "name": "model:tag", ... }, ... ] }
                var root = JObject.Parse(json);
                if (root["models"] is JArray arr)
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

        /// <summary>Main async entry. Raises OnPromptSent/OnResponseReceived/OnError.</summary>
        public static async Task SendPromptAsync(string speakerName, string transcription, CancellationToken ct = default)
        {
            try
            {
                if (!_isEnabled)
                {
                    OnError?.Invoke("Ollama is disabled.");
                    return;
                }

                InitializeIfNeeded();
                ReloadSettings(); // pick up live changes (model, prompt path) if present

                var system = LoadSystemPrompt();
                var userPrompt = BuildUserPrompt(speakerName, transcription, system);

                OnPromptSent?.Invoke(userPrompt);

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

                // Typical response: { "response":"...", "done":true, ... }
                var text = ExtractTextFromGenerate(body);
                if (string.IsNullOrWhiteSpace(text)) text = ExtractTextFromChat(body); // fallback for alt routes
                if (string.IsNullOrWhiteSpace(text)) text = "(no response)";

                OnResponseReceived?.Invoke(text);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"SendPromptAsync error: {ex.Message}");
            }
        }

        /// <summary>Sync helper used by UI buttons; actual text arrives via OnResponseReceived.</summary>
        public static string SendPrompt(string prompt, PromptTone tone = PromptTone.Neutral)
        {
            try
            {
                var t = SendPromptAsync("User", prompt);
                t.Wait();
                return "(sent)";
            }
            catch (Exception ex)
            {
                var err = $"SendPrompt error: {ex.Message}";
                OnError?.Invoke(err);
                return err;
            }
        }

        // ---------------- Internals ----------------

        private static void InitializeIfNeeded()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;

                // Load persisted enabled state / model if AppSettings exists
                _isEnabled = SafeCall(() => AppSettings.LoadOllamaEnabled(), _isEnabled);
                var model = SafeCall(() => AppSettings.LoadOllamaModel(), _defaultModel);
                if (!string.IsNullOrWhiteSpace(model)) _defaultModel = model;

                // Optional: base URL if you later add it to AppSettings
                // _baseUrl = SafeCall(() => AppSettings.LoadOllamaBaseUrl(), _baseUrl);

                _initialized = true;
            }
        }

        private static void ReloadSettings()
        {
            // Re-read model each call so UI changes are respected
            var model = SafeCall(() => AppSettings.LoadOllamaModel(), _defaultModel);
            if (!string.IsNullOrWhiteSpace(model)) _defaultModel = model;
        }

        private static string LoadSystemPrompt()
        {
            try
            {
                // Throttle disk reads
                if ((DateTime.UtcNow - _lastSystemPromptLoad).TotalSeconds < 2 && !string.IsNullOrEmpty(_systemPrompt))
                    return _systemPrompt;

                var path = SafeCall(() => AppSettings.LoadSystemPromptPath(), "");
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
            var who = string.IsNullOrWhiteSpace(speakerName) ? "UnknownSpeaker" : speakerName.Trim();
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
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
            if (!string.IsNullOrWhiteSpace(who)) sb.Append($" ({who})");
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
                var content = obj["message"]?["content"]?.ToString();
                if (!string.IsNullOrWhiteSpace(content)) return content;
                return obj["text"]?.ToString() ?? "";
            }
            catch { return ""; }
        }

        // Helpers to make integration robust if AppSettings isn’t fully wired yet
        private static T SafeCall<T>(Func<T> f, T fallback)
        {
            try { return f(); } catch { return fallback; }
        }
        private static void SafeExec(Action a)
        {
            try { a(); } catch { /* swallow */ }
        }
    }
}
