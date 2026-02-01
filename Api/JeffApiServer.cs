using System;
using System.IO;
using System.Net;
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

        public static bool IsRunning => _listener?.IsListening ?? false;

        public static void Start(bool lanAccess = true)
        {
            if (_listener != null) return;

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            
            // Enable LAN access
            if (lanAccess)
            {
                _listener.Prefixes.Add($"http://*:{Port}/");
                Console.WriteLine($"🌐 Web interface available on LAN at http://<this-ip>:{Port}");
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

                // Serve web frontend for root and web paths
                if (path == "/" || path == "/index.html")
                {
                    await ServeWebFile(resp, "index.html", "text/html");
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

                    case "/api/status":
                        await WriteJson(resp, 200, new { 
                            running = true, 
                            provider = Kinectv1.App.SettingsProvider?.Current?.Ollama?.Provider ?? "Unknown",
                            lmStudioUrl = Kinectv1.App.SettingsProvider?.Current?.Ollama?.LmStudioBaseUrl,
                            memoryEnabled = Kinectv1.App.SettingsProvider?.Current?.Ollama?.MemoryEnabled
                        });
                        break;

                    default:
                        // Try to serve static web files
                        if (path.StartsWith("/"))
                        {
                            var fileName = path.TrimStart('/');
                            var webDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web");
                            var filePath = Path.Combine(webDir, fileName);
                            
                            if (File.Exists(filePath))
                            {
                                var mimeType = GetMimeType(fileName);
                                await ServeWebFile(resp, fileName, mimeType);
                                return;
                            }
                        }
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

        private static async Task WriteJson(HttpListenerResponse resp, int status, object data)
        {
            resp.StatusCode = status;
            resp.ContentType = "application/json";
            var json = JsonConvert.SerializeObject(data);
            var bytes = Encoding.UTF8.GetBytes(json);
            await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            resp.Close();
        }

        private static async Task ServeWebFile(HttpListenerResponse resp, string fileName, string mimeType)
        {
            try
            {
                var webDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web");
                var filePath = Path.Combine(webDir, fileName);
                
                if (!File.Exists(filePath))
                {
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
                Console.WriteLine($"[JeffApi] Failed to serve {fileName}: {ex.Message}");
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
