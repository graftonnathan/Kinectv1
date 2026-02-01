using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Kinectv1.Settings;
using Newtonsoft.Json;

namespace Kinectv1.Voice
{
    /// <summary>
    /// Simple audio frame record for WebRTC audio streaming.
    /// </summary>
    public readonly record struct AudioFrame(
        short[] Pcm16,
        int SampleRate,
        int Channels,
        long TimestampTicks,
        string SourceId
    );

    /// <summary>
    /// Simplified WebRTC signaling server for headless mode.
    /// Provides WebSocket-based audio streaming from browser to Maggie.
    /// </summary>
    public sealed class WebRtcSignalingServer
    {
        public event Action<string> OnLog;
        public event Action<AudioFrame> OnWebAudioReceived;
        public event Action<string, string> OnWebTextInput; // (speaker, text)

        private readonly int _httpPort;
        private readonly int _httpsPort;
        private readonly bool _httpsEnabled;
        private HttpListener _httpListener;
        private HttpListener _httpsListener;
        private CancellationTokenSource _cts;
        private Task _httpAcceptTask;
        private Task _httpsAcceptTask;
        private bool _httpsActuallyStarted = false;
        
        private static WebRtcSignalingServer _instance;
        
        // Track connected clients
        private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
        
        // Track if WebRTC is active (has connected clients)
        private static volatile bool _webRtcModeActive = false;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _clientSendLocks = new();
        private int _clientId = 0;
 
        private static readonly string _wwwrootPath;

        static WebRtcSignalingServer()
        {
            _wwwrootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "webrtc");
        }

        public WebRtcSignalingServer(int httpPort, bool httpsEnabled = false, int httpsPort = 8788)
        {
            _httpPort = httpPort;
            _httpsEnabled = httpsEnabled;
            _httpsPort = httpsPort;
        }

        public static void BroadcastModeChange(int mode)
        {
            if (_instance == null) return;
            _instance?.Broadcast(new { type = "status", mode });
        }

        private string ReadWebFile(string filename)
        {
            var path = Path.Combine(_wwwrootPath, filename);
            try
            {
                if (File.Exists(path))
                    return File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log($"[WebRTC] Failed to read {filename}: {ex.Message}");
            }
            return GetEmbeddedFallback(filename);
        }

        private static string GetEmbeddedFallback(string filename)
        {
            if (filename == "index.html")
                return "<!DOCTYPE html><html><head><title>Maggie WebRTC</title></head><body><h1>Maggie WebRTC</h1><p>Web interface files not found. Place files in wwwroot/webrtc/</p></body></html>";
            if (filename == "client.js")
                return "console.log('client.js not found in wwwroot/webrtc/');";
            return null;
        }

        public static int GetCurrentMode()
        {
            // When WebRTC has active clients, report WebRTC mode regardless of settings
            if (_webRtcModeActive) return (int)AudioInMode.WebRtcVoice;
            try { return (int)(App.SettingsProvider?.Current?.App?.InputMode ?? AudioInMode.LocalMic); }
            catch { return 0; }
        }

        public async Task StartAsync(CancellationToken ct)
        {
            _instance = this;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await StartHttpServerAsync();
            if (_httpsEnabled) await StartHttpsServerAsync();
            SubscribeToEvents();
        }
        
        private void SubscribeToEvents()
        {
            // Hook up transcription events for WebRTC clients
            try 
            { 
                Headless.HeadlessVoiceRecognizer.Instance.OnTranscription += OnTranscription;
                Log("[WebRTC] Subscribed to transcription events");
            }
            catch (Exception ex) 
            { 
                Log($"[WebRTC] Failed to subscribe to transcription: {ex.Message}"); 
            }
        }
        
        private void OnTranscription(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // When WebRTC is active and has clients, broadcast transcription to web clients
            if (_webRtcModeActive && _clients.Count > 0)
            {
                Broadcast(new { type = "transcription", text, speaker = "You" });
            }
        }

        private async Task StartHttpsServerAsync()
        {
            Log($"[WebRTC] HTTPS is enabled, attempting to start on port {_httpsPort}...");
            
            try
            {
                var cert = HttpsHelper.GetOrCreateCertificate();
                var thumbprint = cert.Thumbprint;
                Log($"[WebRTC] Certificate thumbprint: {thumbprint}");
                
                // Try to bind certificate to port using netsh
                var bindResult = BindCertificateToPort(_httpsPort, thumbprint);
                if (!bindResult)
                {
                    Log($"[WebRTC] *** HTTPS DISABLED: Could not bind certificate to port {_httpsPort} ***");
                    _httpsActuallyStarted = false;
                    return;
                }

                _httpsListener = new HttpListener();
                _httpsListener.Prefixes.Add($"https://+:{_httpsPort}/");

                try
                {
                    _httpsListener.Start();
                    _httpsActuallyStarted = true;
                    Log($"[WebRTC] *** HTTPS SERVER STARTED SUCCESSFULLY ***");
                    var ip = GetLocalIP();
                    Log($"[WebRTC] HTTPS URL: https://{ip}:{_httpsPort}/");
                }
                catch (HttpListenerException ex)
                {
                    Log($"[WebRTC] *** HTTPS FAILED TO START *** Error: {ex.Message}");
                    _httpsActuallyStarted = false;
                    return;
                }

                _httpsAcceptTask = Task.Run(() => AcceptLoop(_httpsListener, _cts.Token), _cts.Token);
            }
            catch (Exception ex)
            {
                Log($"[WebRTC] *** HTTPS SETUP FAILED *** {ex.Message}");
                _httpsActuallyStarted = false;
            }

            await Task.CompletedTask;
        }

        private bool BindCertificateToPort(int port, string thumbprint)
        {
            try
            {
                // Check if already bound
                var checkResult = RunNetsh($"http show sslcert ipport=0.0.0.0:{port}");
                if (checkResult.Contains(thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"[WebRTC] Certificate already bound to port {port}");
                    return true;
                }

                // Add SSL cert binding
                var addResult = RunNetsh($"http add sslcert ipport=0.0.0.0:{port} certhash={thumbprint} appid={{12345678-1234-1234-1234-123456789012}}");
                if (addResult.Contains("Error", StringComparison.OrdinalIgnoreCase) && !addResult.Contains("0x183"))
                {
                    Log($"[WebRTC] Failed to bind certificate: {addResult}");
                    return false;
                }
                
                Log($"[WebRTC] Certificate bound to port {port}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[WebRTC] Certificate binding error: {ex.Message}");
                return false;
            }
        }

        private string RunNetsh(string args)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netsh", args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc.WaitForExit();
                return proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private async Task StartHttpServerAsync()
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://+:{_httpPort}/");

            try
            {
                _httpListener.Start();
                var ip = GetLocalIP();
                Log($"[WebRTC] HTTP server started on port {_httpPort}");
                Log($"[WebRTC] WebRTC URL: http://{ip}:{_httpPort}/");
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                Log($"[WebRTC] WARNING: Cannot bind HTTP to all interfaces (Access Denied)");
                try
                {
                    _httpListener = new HttpListener();
                    _httpListener.Prefixes.Add($"http://localhost:{_httpPort}/");
                    _httpListener.Prefixes.Add($"http://127.0.0.1:{_httpPort}/");
                    _httpListener.Start();
                    Log($"[WebRTC] HTTP server started on localhost:{_httpPort} ONLY (LAN access disabled)");
                }
                catch (Exception inner)
                {
                    Log($"[WebRTC] FATAL: HTTP listener failed to start: {inner.Message}");
                    throw;
                }
            }
            catch (Exception ex)
            {
                Log($"[WebRTC] HTTP start error: {ex.Message}. Trying localhost...");
                try
                {
                    _httpListener = new HttpListener();
                    _httpListener.Prefixes.Add($"http://localhost:{_httpPort}/");
                    _httpListener.Prefixes.Add($"http://127.0.0.1:{_httpPort}/");
                    _httpListener.Start();
                    Log($"[WebRTC] HTTP server started on localhost:{_httpPort} ONLY (fallback)");
                }
                catch
                {
                    Log($"[WebRTC] FATAL: HTTP listener failed to start after fallback");
                    throw;
                }
            }

            _httpAcceptTask = Task.Run(() => AcceptLoop(_httpListener, _cts.Token), _cts.Token);
            await Task.CompletedTask;
        }

        private static string GetLocalIP()
        {
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            var ip = addr.Address.ToString();
                            if (!ip.StartsWith("127.") && !ip.StartsWith("169.254."))
                                return ip;
                        }
                    }
                }
            }
            catch { }
            return "localhost";
        }

        public async Task StopAsync()
        {
            // Unsubscribe from events
            try 
            { 
                Headless.HeadlessVoiceRecognizer.Instance.OnTranscription -= OnTranscription; 
                Log("[WebRTC] Unsubscribed from transcription events");
            }
            catch { }
            
            try { _cts?.Cancel(); } catch { }
            try { _httpListener?.Stop(); } catch { }
            try { _httpsListener?.Stop(); } catch { }
            
            foreach (var client in _clients.Values)
                try { client.Dispose(); } catch { }
            _clients.Clear();
            
            try { if (_httpAcceptTask != null) await Task.WhenAny(_httpAcceptTask, Task.Delay(1000)); } catch { }
            try { if (_httpsAcceptTask != null) await Task.WhenAny(_httpsAcceptTask, Task.Delay(1000)); } catch { }
            try { _cts?.Dispose(); } catch { }
        }

        private async void AcceptLoop(HttpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && listener?.IsListening == true)
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    _ = HandleRequest(ctx, ct);
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        private async Task HandleRequest(HttpListenerContext ctx, CancellationToken ct)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            try
            {
                var path = req.Url?.AbsolutePath ?? "/";

                if (path.Equals("/ws", StringComparison.OrdinalIgnoreCase) && req.IsWebSocketRequest)
                {
                    await HandleWebSocket(ctx, ct);
                    return;
                }

                switch (path)
                {
                    case "/":
                    case "/index.html":
                        Serve(res, ReadWebFile("index.html"), "text/html");
                        break;
                    case "/client.js":
                        Serve(res, ReadWebFile("client.js"), "application/javascript");
                        break;
                    case "/api/status":
                        var bargeInEnabled = App.SettingsProvider?.Current?.Asr?.BargeInEnabled ?? false;
                        var transcriptionEnabled = App.SettingsProvider?.Current?.Transcription?.Enabled ?? false;
                        Serve(res, JsonConvert.SerializeObject(new { 
                            mode = GetCurrentMode(), 
                            bargeInEnabled, 
                            transcriptionEnabled
                        }), "application/json");
                        break;
                    case "/api/mode":
                        if (req.HttpMethod == "POST")
                            await HandleModeChange(req, res);
                        else
                            ServeError(res, 405, "Method not allowed");
                        break;
                    case "/api/transcription":
                        if (req.HttpMethod == "POST")
                            await HandleTranscriptionToggle(req, res);
                        else
                            ServeError(res, 405, "Method not allowed");
                        break;
                    default:
                        ServeError(res, 404, "Not found");
                        break;
                }
            }
            catch { try { res.StatusCode = 500; res.Close(); } catch { } }
        }

        private async Task HandleModeChange(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                dynamic data = JsonConvert.DeserializeObject(body);
                int modeInt = (int)data.mode;

                Broadcast(new { type = "status", mode = modeInt });
                Serve(res, JsonConvert.SerializeObject(new { success = true, mode = modeInt }), "application/json");
            }
            catch (Exception ex)
            {
                ServeError(res, 500, ex.Message);
            }
        }

        private async Task HandleTranscriptionToggle(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                dynamic data = JsonConvert.DeserializeObject(body);
                bool enabled = (bool)data.enabled;

                // Broadcast to all clients (settings persistence handled by caller)
                Broadcast(new { type = "transcription_mode", enabled });
                Serve(res, JsonConvert.SerializeObject(new { success = true, enabled }), "application/json");
                Log($"[WebRTC] Transcription mode toggled: {enabled}");
            }
            catch (Exception ex)
            {
                ServeError(res, 500, ex.Message);
            }
        }

        private void Serve(HttpListenerResponse res, string content, string mime)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(content ?? "");
                res.ContentType = mime + "; charset=utf-8";
                res.ContentLength64 = bytes.Length;
                res.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");
                res.AddHeader("Access-Control-Allow-Origin", "*");
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.Close();
            }
            catch { }
        }

        private void ServeError(HttpListenerResponse res, int code, string msg)
        {
            try
            {
                res.StatusCode = code;
                Serve(res, JsonConvert.SerializeObject(new { error = msg }), "application/json");
            }
            catch { }
        }

        private void Log(string msg) { try { OnLog?.Invoke(msg); } catch { } }

        private async Task HandleWebSocket(HttpListenerContext ctx, CancellationToken ct)
        {
            WebSocketContext wsCtx;
            try { wsCtx = await ctx.AcceptWebSocketAsync(null); }
            catch { return; }

            var ws = wsCtx.WebSocket;
            var id = $"c{Interlocked.Increment(ref _clientId)}";
            _clients[id] = ws;
            _clientSendLocks[id] = new SemaphoreSlim(1, 1);
            
            // Activate WebRTC mode when first client connects
            var wasActive = _webRtcModeActive;
            _webRtcModeActive = true;
            if (!wasActive)
            {
                Log("[WebRTC] First client connected - activating WebRTC mode");
                BroadcastModeChange((int)AudioInMode.WebRtcVoice);
            }

            try
            {
                var bargeInEnabled = App.SettingsProvider?.Current?.Asr?.BargeInEnabled ?? false;
                var status = JsonConvert.SerializeObject(new { type = "status", mode = GetCurrentMode(), bargeInEnabled });
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(status)), WebSocketMessageType.Text, true, ct);
            }
            catch { }

            var buf = new byte[8192];
            var sb = new StringBuilder();

            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    sb.Clear();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.MessageType != WebSocketMessageType.Text) continue;
                        sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text) continue;

                    var msg = sb.ToString();
                    if (!string.IsNullOrWhiteSpace(msg))
                        await ProcessMessage(ws, msg);
                }
            }
            catch { }
            finally
            {
                _clients.TryRemove(id, out _);
                if (_clientSendLocks.TryRemove(id, out var sendLock))
                    try { sendLock.Dispose(); } catch { }
                try { ws.Dispose(); } catch { }
                
                // Deactivate WebRTC mode when last client disconnects
                if (_clients.IsEmpty && _webRtcModeActive)
                {
                    _webRtcModeActive = false;
                    Log("[WebRTC] Last client disconnected - deactivating WebRTC mode");
                }
            }
        }

        private async Task ProcessMessage(WebSocket ws, string message)
        {
            try
            {
                dynamic msg = JsonConvert.DeserializeObject(message);
                string type = (string)msg.type;

                switch (type)
                {
                    case "ping":
                        try
                        {
                            var pong = JsonConvert.SerializeObject(new { type = "pong", ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                            var pongBytes = Encoding.UTF8.GetBytes(pong);
                            if (ws.State == WebSocketState.Open)
                                await ws.SendAsync(new ArraySegment<byte>(pongBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                        catch { }
                        break;

                    case "text":
                        string text = (string)msg.text;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            var speaker = App.SettingsProvider?.Current?.Ollama?.ForcedSpeakerId ?? "User";
                            try { OnWebTextInput?.Invoke(speaker, text); } catch { }
                            Broadcast(new { type = "transcription", text, speaker });
                        }
                        break;

                    case "audio":
                        string audioData = (string)msg.data;
                        int sampleRate = (int?)msg.sampleRate ?? 16000;
                        
                        if (!string.IsNullOrEmpty(audioData))
                        {
                            try
                            {
                                var pcmBytes = Convert.FromBase64String(audioData);
                                byte[] pcm16kBytes;
                                
                                if (sampleRate != 16000)
                                {
                                    pcm16kBytes = AudioUtils.ResampleMonoTo16k(pcmBytes, pcmBytes.Length, sampleRate, "webrtc-client");
                                }
                                else
                                {
                                    pcm16kBytes = pcmBytes;
                                }

                                var pcm16 = new short[pcm16kBytes.Length / 2];
                                Buffer.BlockCopy(pcm16kBytes, 0, pcm16, 0, pcm16kBytes.Length);

                                var frame = new AudioFrame(
                                    Pcm16: pcm16,
                                    SampleRate: 16000,
                                    Channels: 1,
                                    TimestampTicks: DateTime.UtcNow.Ticks,
                                    SourceId: "webrtc-client"
                                );

                                OnWebAudioReceived?.Invoke(frame);
                            }
                            catch (Exception ex)
                            {
                                Log($"[WebRTC][Audio] Error processing: {ex.Message}");
                            }
                        }
                        break;

                    case "image":
                        // Image support - broadcast to all clients
                        string img = (string)msg.data;
                        if (!string.IsNullOrWhiteSpace(img) && img.Length <= 8_000_000)
                        {
                            var imageSpeaker = App.SettingsProvider?.Current?.Ollama?.ForcedSpeakerId ?? "User";
                            Broadcast(new { type = "image", data = img, role = "user", speaker = imageSpeaker });
                        }
                        break;
                }
            }
            catch { }
            
            await Task.CompletedTask;
        }

        public void Broadcast(object data)
        {
            var json = JsonConvert.SerializeObject(data);
            var bytes = Encoding.UTF8.GetBytes(json);
            
            foreach (var kvp in _clients.ToArray())
            {
                var ws = kvp.Value;
                var clientId = kvp.Key;
                if (ws.State == WebSocketState.Open)
                {
                    var sendLock = _clientSendLocks.GetOrAdd(clientId, _ => new SemaphoreSlim(1, 1));
                    
                    _ = Task.Run(async () =>
                    {
                        bool acquired = false;
                        try 
                        { 
                            acquired = await sendLock.WaitAsync(100);
                            if (!acquired) return;
                            if (ws.State != WebSocketState.Open) return;
                            
                            using var cts = new CancellationTokenSource(500);
                            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
                        }
                        catch { }
                        finally
                        {
                            if (acquired) try { sendLock.Release(); } catch { }
                        }
                    });
                }
            }
        }

        public void BroadcastTranscription(string text, string speaker)
        {
            Broadcast(new { type = "transcription", text, speaker });
        }

        public void BroadcastResponse(string text)
        {
            Broadcast(new { type = "response", text });
        }

        public void BroadcastResponseChunk(string text)
        {
            Broadcast(new { type = "response_chunk", text });
        }

        public void BroadcastTtsAudio(byte[] pcmData, int sampleRate)
        {
            if (pcmData == null || pcmData.Length == 0 || _clients.Count == 0) return;
            try
            {
                var base64 = Convert.ToBase64String(pcmData);
                Broadcast(new { type = "tts_audio", data = base64, sampleRate });
            }
            catch (Exception ex)
            {
                Log($"[WebRTC] TTS audio broadcast error: {ex.Message}");
            }
        }

        public void BroadcastTtsStop()
        {
            Broadcast(new { type = "tts_stop" });
        }
    }
}
