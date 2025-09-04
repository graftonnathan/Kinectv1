using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kinectv1.Llm
{
    public sealed class LmStudioClient : ILlmClient
    {
        private readonly HttpClient _http;
        private readonly string _base;
        private readonly Func<string> _getModel; // delegate to read latest model lazily

        public LmStudioClient(string baseUrl, string apiKey, Func<string> getModel)
        {
            _base = (baseUrl ?? "http://127.0.0.1:1234").TrimEnd('/');
            _http = new HttpClient();
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", string.IsNullOrWhiteSpace(apiKey) ? "lm-studio" : apiKey);
            _getModel = getModel ?? (() => "Meta-Llama-3.1-8B-Instruct-Q4_K_M");
        }

        public async IAsyncEnumerable<string> ChatStreamAsync(string system, string user, CancellationToken ct = default)
        {
            var reqObj = new
            {
                model = _getModel(),
                messages = new object[] {
                    new { role = "system", content = system ?? string.Empty },
                    new { role = "user", content = user ?? string.Empty }
                },
                stream = true,
                temperature = 0.7,
                top_p = 0.9,
                max_tokens = 1024
            };

            var json = JsonConvert.SerializeObject(reqObj);
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{_base}/v1/chat/completions")
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };

            using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            using (var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var r = new StreamReader(s, Encoding.UTF8, true))
            {
                while (!r.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await r.ReadLineAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!line.StartsWith("data:")) continue;
                    var payload = line.Substring("data:".Length).Trim();
                    if (payload == "[DONE]") yield break;

                    string piece = null;
                    try
                    {
                        var obj = JObject.Parse(payload);
                        var delta = obj["choices"][0]["delta"] as JObject;
                        if (delta != null && delta.TryGetValue("content", out var t))
                        {
                            piece = t?.ToString();
                        }
                    }
                    catch { /* ignore keepalives/chunks */ }

                    if (!string.IsNullOrEmpty(piece)) yield return piece;
                }
            }
        }

        public async Task<string> ChatOnceAsync(string system, string user, CancellationToken ct = default)
        {
            var sb = new StringBuilder();
            await foreach (var tok in ChatStreamAsync(system, user, ct))
                sb.Append(tok);
            return sb.ToString();
        }
    }
}
