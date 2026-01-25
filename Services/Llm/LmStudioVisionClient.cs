using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace Kinectv1.Llm
{
    internal static class LmStudioVisionClient
    {
        private static readonly HttpClient _http = new HttpClient();

        public static async Task<string> ChatAsync(string baseUrl, string model, string userText, byte[] jpegBytes)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = "http://127.0.0.1:1234/v1";
            if (!baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) && !baseUrl.EndsWith("/v1/", StringComparison.OrdinalIgnoreCase))
                baseUrl = baseUrl.TrimEnd('/') + "/v1";

            var content = new List<object>
            {
                new { type = "text", text = userText ?? string.Empty }
            };

            if (jpegBytes != null && jpegBytes.Length > 0)
            {
                var b64 = Convert.ToBase64String(jpegBytes);
                content.Add(new
                {
                    type = "image_url",
                    image_url = new { url = $"data:image/jpeg;base64,{b64}" }
                });
            }

            var payload = new
            {
                model = model,
                messages = new object[]
                {
                    new { role = "user", content }
                },
                temperature = 0.2
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions")
            {
                Content = JsonContent.Create(payload, options: new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                })
            };

            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            var respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"LM Studio error {(int)resp.StatusCode}: {respText}");

            using var doc = JsonDocument.Parse(respText);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;
        }
    }
}
