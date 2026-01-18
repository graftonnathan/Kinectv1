using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kinectv1.Llm
{
    /// <summary>
    /// Client for LM Studio's OpenAI-compatible embeddings endpoint.
    /// Converts text into vector representations for semantic search.
    /// </summary>
    public sealed class LmStudioEmbeddingClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly Func<string> _getModel;
        private bool _disposed;

        /// <summary>
        /// Create a new LM Studio embedding client.
        /// </summary>
        /// <param name="baseUrl">LM Studio base URL (e.g., http://127.0.0.1:1234)</param>
        /// <param name="apiKey">Optional API key</param>
        /// <param name="getModel">Function to get the current embedding model name</param>
        public LmStudioEmbeddingClient(string baseUrl, string apiKey, Func<string> getModel)
        {
            _baseUrl = (baseUrl ?? "http://127.0.0.1:1234").TrimEnd('/');
            _getModel = getModel ?? (() => "text-embedding-nomic-embed-text-v1.5");
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                _http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }
        }

        /// <summary>
        /// Get embedding vector for a single text.
        /// </summary>
        public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<float>();

            var model = _getModel();
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("Embeddings model not configured");

            var requestBody = new
            {
                model = model,
                input = text
            };

            var json = JsonConvert.SerializeObject(requestBody);
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var response = await _http.PostAsync($"{_baseUrl}/v1/embeddings", content, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var result = JObject.Parse(responseJson);

                // Extract embedding from response: { "data": [{ "embedding": [...] }] }
                var embedding = result["data"]?[0]?["embedding"];
                if (embedding == null)
                    throw new InvalidOperationException("No embedding returned from LM Studio");

                return embedding.ToObject<float[]>();
            }
        }

        /// <summary>
        /// Get embeddings for multiple texts in a single request (batch).
        /// </summary>
        public async Task<float[][]> GetEmbeddingsAsync(string[] texts, CancellationToken ct = default)
        {
            if (texts == null || texts.Length == 0)
                return Array.Empty<float[]>();

            var model = _getModel();
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("Embeddings model not configured");

            var requestBody = new
            {
                model = model,
                input = texts
            };

            var json = JsonConvert.SerializeObject(requestBody);
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var response = await _http.PostAsync($"{_baseUrl}/v1/embeddings", content, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var result = JObject.Parse(responseJson);

                var dataArray = result["data"] as JArray;
                if (dataArray == null)
                    throw new InvalidOperationException("No embeddings returned from LM Studio");

                var embeddings = new float[dataArray.Count][];
                for (int i = 0; i < dataArray.Count; i++)
                {
                    var embedding = dataArray[i]?["embedding"];
                    embeddings[i] = embedding?.ToObject<float[]>() ?? Array.Empty<float>();
                }

                return embeddings;
            }
        }

        /// <summary>
        /// Test if the embeddings endpoint is available.
        /// </summary>
        public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
        {
            try
            {
                var embedding = await GetEmbeddingAsync("test", ct).ConfigureAwait(false);
                return embedding != null && embedding.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Get the dimension of embeddings from the current model.
        /// </summary>
        public async Task<int> GetEmbeddingDimensionAsync(CancellationToken ct = default)
        {
            var embedding = await GetEmbeddingAsync("dimension test", ct).ConfigureAwait(false);
            return embedding?.Length ?? 0;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _http?.Dispose();
                _disposed = true;
            }
        }
    }
}
