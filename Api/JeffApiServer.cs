using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kinectv1.Api
{
    public static class JeffApiServer
    {
        private static HttpListener _listener;
        private static CancellationTokenSource _cts;
        private static Task _worker;
        private static readonly int Port = 18790;

        public static event Action<string, string> OnChatReceived;
        public static event Func<string, Task<string>> OnChatRequest;
        
        // Jeff message queue for web UI display
        private static readonly System.Collections.Concurrent.ConcurrentQueue<JeffMessage> _jeffMessages = new();
        private static readonly int MaxJeffMessages = 100;
        
        public class JeffMessage
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            public string Text { get; set; }
            public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public static bool IsRunning => _listener?.IsListening ?? false;

        public static void Start(bool lanAccess = true)
        {
            if (_listener != null) return;

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            
            // Always add localhost
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            
            // Try LAN access by binding to specific IP
            string lanIp = null;
            if (lanAccess)
            {
                try
                {
                    lanIp = GetLocalIpAddress();
                    if (!string.IsNullOrEmpty(lanIp) && lanIp != "127.0.0.1")
                    {
                        _listener.Prefixes.Add($"http://{lanIp}:{Port}/");
                        Console.WriteLine($"🌐 LAN access enabled at http://{lanIp}:{Port}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️  Could not enable LAN access: {ex.Message}");
                }
            }

            try
            {
                _listener.Start();
                _worker = Task.Run(() => RunLoop(_cts.Token));
                Console.WriteLine($"🌐 Jeff API server started on http://localhost:{Port}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to start Jeff API: {ex.Message}");
                Stop();
            }
        }

        private static string GetLocalIpAddress()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        var ipStr = ip.ToString();
                        // Skip loopback addresses
                        if (!ipStr.StartsWith("127."))
                            return ipStr;
                    }
                }
            }
            catch { }
            return null;
        }

        public static void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            Console.WriteLine("🌐 Jeff API server stopped");
        }

        private static async Task RunLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context), ct);
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[JeffApi] Error: {ex.Message}");
                }
            }
        }

        private static async Task HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            try
            {
                resp.Headers.Add("Access-Control-Allow-Origin", "*");
                resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (req.HttpMethod == "OPTIONS")
                {
                    resp.StatusCode = 200;
                    resp.Close();
                    return;
                }

                var path = req.Url.AbsolutePath.ToLowerInvariant();

                // Root path - redirect to WebRTC UI
                if (path == "/")
                {
                    resp.StatusCode = 302;
                    resp.Headers.Add("Location", "/webrtc/");
                    resp.Close();
                    return;
                }

                // WebRTC UI at /webrtc/
                if (path == "/webrtc" || path == "/webrtc/")
                {
                    await ServeWebFile(resp, "wwwroot/webrtc/index.html", "text/html");
                    return;
                }
                if (path.StartsWith("/webrtc/"))
                {
                    var fileName = path.Substring(8); // Remove /webrtc/
                    await ServeWebFile(resp, $"wwwroot/webrtc/{fileName}", GetMimeType(fileName));
                    return;
                }

                // Direct access to wwwroot files
                if (path.StartsWith("/wwwroot/"))
                {
                    var fileName = path.Substring(9);
                    await ServeWebFile(resp, $"wwwroot/{fileName}", GetMimeType(fileName));
                    return;
                }
                
                // API routes
                switch (path)
                {
                    case "/health":
                        await WriteJson(resp, 200, new { status = "ok", service = "maggie", version = "1.0" });
                        break;

                    case "/api/chat":
                        if (req.HttpMethod != "POST")
                        {
                            await WriteJson(resp, 405, new { error = "Method not allowed" });
                            return;
                        }
                        await HandleChat(req, resp);
                        break;

                    case "/api/jeff/message":
                        if (req.HttpMethod != "POST")
                        {
                            await WriteJson(resp, 405, new { error = "Method not allowed" });
                            return;
                        }
                        await HandleJeffMessage(req, resp);
                        break;
                    
                    case "/api/jeff/messages":
                        if (req.HttpMethod != "GET")
                        {
                            await WriteJson(resp, 405, new { error = "Method not allowed" });
                            return;
                        }
                        await HandleGetJeffMessages(resp);
                        break;

                    case "/api/status":
                        await WriteJson(resp, 200, new { 
                            running = true, 
                            provider = Kinectv1.App.SettingsProvider?.Current?.Ollama?.Provider ?? "Unknown",
                            lmStudioUrl = Kinectv1.App.SettingsProvider?.Current?.Ollama?.LmStudioBaseUrl,
                            memoryEnabled = Kinectv1.App.SettingsProvider?.Current?.Ollama?.MemoryEnabled
                        });
                        break;

                    // TTS proxy endpoints - forward to Qwen3-TTS service
                    case "/api/tts":
                        if (req.HttpMethod != "POST")
                        {
                            await WriteJson(resp, 405, new { error = "Method not allowed" });
                            return;
                        }
                        await HandleTtsProxy(req, resp);
                        break;

                    case "/api/tts/speak":
                        if (req.HttpMethod != "GET")
                        {
                            await WriteJson(resp, 405, new { error = "Method not allowed" });
                            return;
                        }
                        await HandleTtsSpeakProxy(req, resp);
                        break;

                    case "/api/voices":
                        await HandleVoicesProxy(resp);
                        break;

                    case "/api/tts/voice_description":
                        if (req.HttpMethod == "POST")
                            await HandleVoiceDescriptionProxy(req, resp);
                        else if (req.HttpMethod == "GET")
                            await HandleGetVoiceDescriptionProxy(resp);
                        break;

                    default:
                        await WriteJson(resp, 404, new { error = "Not found" });
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JeffApi] Request error: {ex.Message}");
                try { await WriteJson(resp, 500, new { error = ex.Message }); } catch { }
            }
        }

        private static async Task HandleChat(HttpListenerRequest req, HttpListenerResponse resp)
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, req.ContentEncoding))
                body = await sr.ReadToEndAsync();

            var json = JObject.Parse(body);
            var speaker = json["speaker"]?.ToString() ?? "Unknown";
            var message = json["message"]?.ToString();

            if (string.IsNullOrWhiteSpace(message))
            {
                await WriteJson(resp, 400, new { error = "Missing 'message' field" });
                return;
            }

            // If Jeff is speaking, queue his message for web UI display
            if (speaker.Equals("Jeff", StringComparison.OrdinalIgnoreCase))
            {
                var jeffMsg = new JeffMessage { Text = message };
                _jeffMessages.Enqueue(jeffMsg);
                while (_jeffMessages.Count > MaxJeffMessages)
                {
                    _jeffMessages.TryDequeue(out _);
                }
            }

            OnChatReceived?.Invoke(speaker, message);

            string response = null;
            if (OnChatRequest != null)
            {
                var handler = OnChatRequest;
                response = await handler(message);
            }

            await WriteJson(resp, 200, new { 
                success = true, 
                speaker,
                received = message,
                response 
            });
        }

        private static async Task HandleJeffMessage(HttpListenerRequest req, HttpListenerResponse resp)
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, req.ContentEncoding))
                body = await sr.ReadToEndAsync();

            var json = JObject.Parse(body);
            var message = json["message"]?.ToString();
            var text = json["text"]?.ToString();
            var msgText = message ?? text; // Accept either field

            if (string.IsNullOrWhiteSpace(msgText))
            {
                await WriteJson(resp, 400, new { error = "Missing 'message' or 'text' field" });
                return;
            }

            // Add to queue for web clients to poll
            var jeffMsg = new JeffMessage { Text = msgText };
            _jeffMessages.Enqueue(jeffMsg);
            
            // Trim old messages
            while (_jeffMessages.Count > MaxJeffMessages)
            {
                _jeffMessages.TryDequeue(out _);
            }

            Console.WriteLine($"[JeffApi] Jeff message queued: {msgText.Substring(0, Math.Min(50, msgText.Length))}...");

            await WriteJson(resp, 200, new { 
                success = true, 
                id = jeffMsg.Id,
                message = msgText,
                queued = true
            });
        }

        private static async Task HandleGetJeffMessages(HttpListenerResponse resp)
        {
            // Return all messages and clear the queue (simple polling approach)
            var messages = _jeffMessages.ToArray();
            _jeffMessages.Clear();
            
            await WriteJson(resp, 200, new { 
                messages,
                count = messages.Length
            });
        }

        private static readonly HttpClient _ttsClient = new HttpClient();
        private static string _ttsServiceUrl = Environment.GetEnvironmentVariable("MAGGIE_TTS_URL") ?? "http://localhost:7860";

        private static async Task HandleTtsProxy(HttpListenerRequest req, HttpListenerResponse resp)
        {
            try
            {
                // Read the incoming request body
                string body;
                using (var sr = new StreamReader(req.InputStream, req.ContentEncoding))
                    body = await sr.ReadToEndAsync();

                // Forward to TTS service
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var ttsResponse = await _ttsClient.PostAsync($"{_ttsServiceUrl}/tts", content);

                // Copy response status and headers
                resp.StatusCode = (int)ttsResponse.StatusCode;
                resp.ContentType = "audio/wav";
                
                // Copy TTS response headers
                if (ttsResponse.Headers.Contains("X-Speaker"))
                    resp.Headers.Add("X-Speaker", ttsResponse.Headers.GetValues("X-Speaker").FirstOrDefault());
                if (ttsResponse.Headers.Contains("X-Sample-Rate"))
                    resp.Headers.Add("X-Sample-Rate", ttsResponse.Headers.GetValues("X-Sample-Rate").FirstOrDefault());

                // Stream the audio data back
                var audioData = await ttsResponse.Content.ReadAsByteArrayAsync();
                await resp.OutputStream.WriteAsync(audioData, 0, audioData.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JeffApi] TTS proxy error: {ex.Message}");
                resp.StatusCode = 500;
                var errorBytes = Encoding.UTF8.GetBytes($"{{\"error\": \"{ex.Message}\"}}");
                resp.ContentType = "application/json";
                await resp.OutputStream.WriteAsync(errorBytes, 0, errorBytes.Length);
            }
            resp.Close();
        }

        private static async Task HandleTtsSpeakProxy(HttpListenerRequest req, HttpListenerResponse resp)
        {
            try
            {
                var text = req.QueryString["text"] ?? "";
                var speaker = req.QueryString["speaker"] ?? "Serena";

                if (string.IsNullOrWhiteSpace(text))
                {
                    await WriteJson(resp, 400, new { error = "Missing 'text' parameter" });
                    return;
                }

                // Build request to TTS service
                var requestObj = new { text, speaker, language = "English" };
                var json = JsonConvert.SerializeObject(requestObj);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var ttsResponse = await _ttsClient.PostAsync($"{_ttsServiceUrl}/tts", content);

                resp.StatusCode = (int)ttsResponse.StatusCode;
                resp.ContentType = "audio/wav";
                resp.Headers.Add("Content-Disposition", "attachment; filename=speech.wav");

                var audioData = await ttsResponse.Content.ReadAsByteArrayAsync();
                await resp.OutputStream.WriteAsync(audioData, 0, audioData.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JeffApi] TTS speak proxy error: {ex.Message}");
                await WriteJson(resp, 500, new { error = ex.Message });
                return;
            }
            resp.Close();
        }

        private static async Task HandleVoicesProxy(HttpListenerResponse resp)
        {
            try
            {
                var voicesResponse = await _ttsClient.GetAsync($"{_ttsServiceUrl}/voices");
                var voicesJson = await voicesResponse.Content.ReadAsStringAsync();
                
                resp.StatusCode = (int)voicesResponse.StatusCode;
                resp.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(voicesJson);
                await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JeffApi] Voices proxy error: {ex.Message}");
                // Return default voices if TTS service unavailable
                var defaultVoices = new[] {
                    new { name = "Serena", description = "Warm, gentle young female", language = "Chinese" },
                    new { name = "Vivian", description = "Bright, slightly edgy female", language = "Chinese" },
                    new { name = "Ryan", description = "Dynamic male voice", language = "English" }
                };
                await WriteJson(resp, 200, defaultVoices);
                return;
            }
            resp.Close();
        }

        private static async Task HandleVoiceDescriptionProxy(HttpListenerRequest req, HttpListenerResponse resp)
        {
            try
            {
                var description = req.QueryString["description"] ?? "";
                var proxyResponse = await _ttsClient.PostAsync($"{_ttsServiceUrl}/voice_description?description={Uri.EscapeDataString(description)}", null);
                var responseJson = await proxyResponse.Content.ReadAsStringAsync();
                
                resp.StatusCode = (int)proxyResponse.StatusCode;
                resp.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(responseJson);
                await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JeffApi] Voice description proxy error: {ex.Message}");
                await WriteJson(resp, 500, new { error = ex.Message });
                return;
            }
            resp.Close();
        }

        private static async Task HandleGetVoiceDescriptionProxy(HttpListenerResponse resp)
        {
            try
            {
                var proxyResponse = await _ttsClient.GetAsync($"{_ttsServiceUrl}/voice_description");
                var responseJson = await proxyResponse.Content.ReadAsStringAsync();
                
                resp.StatusCode = (int)proxyResponse.StatusCode;
                resp.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(responseJson);
                await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JeffApi] Get voice description proxy error: {ex.Message}");
                await WriteJson(resp, 200, new { 
                    current_description = "Speak in a cheery relaxing female voice",
                    default_description = "Speak in a cheery relaxing female voice",
                    using_custom = true
                });
                return;
            }
            resp.Close();
        }

        private static async Task WriteJson(HttpListenerResponse resp, int status, object data)
        {
            resp.StatusCode = status;
            resp.ContentType = "application/json";
            var json = JsonConvert.SerializeObject(data);
            var bytes = Encoding.UTF8.GetBytes(json);
            await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            resp.Close();
        }

        private static async Task ServeWebFile(HttpListenerResponse resp, string relativePath, string mimeType)
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var filePath = Path.Combine(baseDir, relativePath);
                
                if (!File.Exists(filePath))
                {
                    Console.WriteLine($"[JeffApi] File not found: {filePath}");
                    resp.StatusCode = 404;
                    resp.Close();
                    return;
                }

                var bytes = File.ReadAllBytes(filePath);
                resp.StatusCode = 200;
                resp.ContentType = mimeType;
                await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JeffApi] Failed to serve {relativePath}: {ex.Message}");
                resp.StatusCode = 500;
            }
            resp.Close();
        }

        private static string GetMimeType(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".html" => "text/html",
                ".css" => "text/css",
                ".js" => "application/javascript",
                ".json" => "application/json",
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream"
            };
        }
    }
}
