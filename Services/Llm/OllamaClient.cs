using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kinectv1.Llm
{
    public sealed class OllamaClient : ILlmClient
    {
        private readonly string _base;
        private readonly string _apiKey;
        private readonly Func<string> _getModel;

        public OllamaClient(string baseUrl, string apiKey, Func<string> getModel)
        {
            _base = (baseUrl ?? "http://127.0.0.1:11434").TrimEnd('/');
            _apiKey = apiKey;
            _getModel = getModel ?? (() => "llama3.1:8b");
        }

        public async IAsyncEnumerable<string> ChatStreamAsync(string system, string user, [EnumeratorCancellation] CancellationToken ct = default, string[] images = null)
        {
            var reqObj = new
            {
                model = _getModel(),
                messages = new object[] {
                    new { role = "system", content = system ?? string.Empty },
                    new { role = "user", content = user ?? string.Empty }
                },
                images = images != null && images.Length > 0 ? images : null,
                stream = true,
                temperature = 0.7,
                top_p = 0.9,
                max_tokens = 1024
            };

            var json = JsonConvert.SerializeObject(reqObj);
            
            // New HttpClient per request for clean cancellation
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            if (!string.IsNullOrWhiteSpace(_apiKey))
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{_base}/v1/chat/completions")
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };

            HttpResponseMessage resp = null;
            Stream stream = null;
            StreamReader reader = null;
            CancellationTokenRegistration registration = default;
            
            try
            {
                // Forcefully abort connection when cancelled
                registration = ct.Register(() =>
                {
                    try { reader?.Dispose(); } catch { }
                    try { stream?.Dispose(); } catch { }
                    try { resp?.Dispose(); } catch { }
                    try { http.CancelPendingRequests(); } catch { }
                });
                
                resp = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                reader = new StreamReader(stream, Encoding.UTF8, true);
                
                while (!ct.IsCancellationRequested)
                {
                    string line;
                    try { line = await reader.ReadLineAsync().ConfigureAwait(false); }
                    catch (ObjectDisposedException) { yield break; }
                    catch (IOException) { yield break; }
                    
                    if (line == null || ct.IsCancellationRequested)
                        yield break;
                    
                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:")) 
                        continue;
                    
                    var payload = line.Substring(5).Trim();
                    if (payload == "[DONE]") 
                        yield break;

                    var piece = ExtractContent(payload);
                    if (!string.IsNullOrEmpty(piece)) 
                        yield return piece;
                }
            }
            finally
            {
                registration.Dispose();
                reader?.Dispose();
                stream?.Dispose();
                resp?.Dispose();
            }
        }

        private static string ExtractContent(string payload)
        {
            try
            {
                var obj = JObject.Parse(payload);
                var delta = obj["choices"]?[0]?["delta"] as JObject;
                if (delta != null && delta.TryGetValue("content", out var t))
                    return t?.ToString();
                return null;
            }
            catch { return null; }
        }

        public async Task<string> ChatOnceAsync(string system, string user, CancellationToken ct = default, string[] images = null)
        {
            var sb = new StringBuilder();
            await foreach (var tok in ChatStreamAsync(system, user, ct, images))
            {
                if (ct.IsCancellationRequested) break;
                sb.Append(tok);
            }
            return sb.ToString();
        }
    }
}
