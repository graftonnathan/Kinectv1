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
    /// WebRTC signaling server with chat relay for WebRTC-sourced conversations only.
    /// Supports both HTTP and HTTPS for iOS Safari microphone access.
    /// </summary>
    public sealed class WebRtcSignalingServer
    {
        public event Action<string> OnLog;
        public event Action<WebSocket, string> OnWebSocketMessage;

        private readonly int _httpPort;
        private readonly int _httpsPort;
        private readonly bool _httpsEnabled;
        private HttpListener _httpListener;
        private HttpListener _httpsListener;
        private CancellationTokenSource _cts;
        private Task _httpAcceptTask;
        private Task _httpsAcceptTask;
        
        // Track mode change requests to trigger MainWindow
        public static event Action<AudioInMode> OnModeChangeRequested;
        
        // Allow MainWindow to receive text input from web UI
        public static event Action<string, string> OnWebTextInput; // (speaker, text)
        
        // Allow WebRTC transport to receive audio from web client
        public static event Action<AudioFrame> OnWebAudioReceived;
        
        // Allow MainWindow to broadcast mode changes to web clients
        private static WebRtcSignalingServer _instance;
        public static void BroadcastModeChange(int mode)
        {
            if (_instance == null)
            {
                Console.WriteLine($"[WebRTC] BroadcastModeChange skipped - server not started yet (mode={mode})");
                return;
            }
            Console.WriteLine($"[WebRTC] Broadcasting mode change: {mode} to {_instance._clients.Count} clients");
            _instance?.Broadcast(new { type = "status", mode });
        }

        private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _clientSendLocks = new();
        private int _clientId = 0;
        
        private static readonly string _wwwrootPath;

        // Track if we're receiving WebRTC audio (to filter messages)
        private static volatile bool _webRtcActive = false;
        private static DateTime _webRtcActiveUntil = DateTime.MinValue;
        public static bool IsWebRtcActive => _webRtcActive || DateTime.UtcNow < _webRtcActiveUntil;
        public static void SetWebRtcActive(bool active) => _webRtcActive = active;
        
        // Extend the active window (called when TTS starts)
        private static void ExtendWebRtcActive(int seconds = 30)
        {
            _webRtcActiveUntil = DateTime.UtcNow.AddSeconds(seconds);
        }

        // TTS playback state tracking for self-hearing suppression
        private static volatile bool _ttsSpeaking = false;
        private static DateTime _ttsEndTime = DateTime.MinValue;
        private static DateTime _lastTtsAudioTime = DateTime.MinValue;
        private const int TTS_SUPPRESSION_MS = 300;
        private const int TTS_SPEAKING_TIMEOUT_MS = 5000;
        
        // Track frames for debugging
        private static int _audioFramesReceived = 0;
        private static int _audioFramesSuppressed = 0;
        private static DateTime _lastAudioLogTime = DateTime.MinValue;
        
        public static bool ShouldSuppressAudio()
        {
            try
            {
                var bargeInEnabled = App.SettingsProvider?.Current?.Asr?.BargeInEnabled ?? true;
                if (bargeInEnabled) return false;
            }
            catch { }
            
            if (_ttsSpeaking && _lastTtsAudioTime != DateTime.MinValue)
            {
                var timeSinceLastAudio = (DateTime.UtcNow - _lastTtsAudioTime).TotalMilliseconds;
                if (timeSinceLastAudio > TTS_SPEAKING_TIMEOUT_MS)
                {
                    _ttsSpeaking = false;
                    _ttsEndTime = DateTime.UtcNow;
                    Console.WriteLine($"[WebRTC] TTS speaking timeout - resetting flag after {timeSinceLastAudio:F0}ms");
                }
            }
            
            if (_ttsSpeaking) return true;
            if (_ttsEndTime != DateTime.MinValue && (DateTime.UtcNow - _ttsEndTime).TotalMilliseconds < TTS_SUPPRESSION_MS)
                return true;
            return false;
        }

        // Track if HTTPS actually started successfully (for URL fallback)
        private bool _httpsActuallyStarted = false;
        public bool HttpsActuallyStarted => _httpsActuallyStarted;

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

        public WebRtcSignalingServer(int port) : this(port, false, port + 1) { }

        public static string GetWebContentPath() => _wwwrootPath;

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

            return filename switch
            {
                "index.html" => GetEmbeddedIndexHtml(),
                "client.js" => GetEmbeddedClientJs(),
                _ => null
            };
        }

        public static int GetCurrentMode()
        {
            try { return (int)(App.SettingsProvider?.Current?.App?.InputMode ?? AudioInMode.LocalMic); }
            catch { return 0; }
        }

        public async Task StartAsync(CancellationToken ct)
        {
            _instance = this;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            EnsureFirewallRules();
            await StartHttpServerAsync();
            if (_httpsEnabled) await StartHttpsServerAsync();
            SubscribeToEvents();
        }

        private void EnsureFirewallRules()
        {
            try
            {
                var checkHttp = RunNetsh($"advfirewall firewall show rule name=\"Kinectv1 WebRTC HTTP\"");
                if (!checkHttp.Contains("Kinectv1 WebRTC HTTP"))
                {
                    Log($"[WebRTC] Adding firewall rule for HTTP port {_httpPort}...");
                    var result = RunNetsh($"advfirewall firewall add rule name=\"Kinectv1 WebRTC HTTP\" dir=in action=allow protocol=tcp localport={_httpPort}");
                    Log(result.Contains("Ok") ? $"[WebRTC] Firewall rule added for HTTP" : $"[WebRTC] Firewall rule result: {result.Trim()}");
                }

                if (_httpsEnabled)
                {
                    var checkHttps = RunNetsh($"advfirewall firewall show rule name=\"Kinectv1 WebRTC HTTPS\"");
                    if (!checkHttps.Contains("Kinectv1 WebRTC HTTPS"))
                    {
                        Log($"[WebRTC] Adding firewall rule for HTTPS port {_httpsPort}...");
                        var result = RunNetsh($"advfirewall firewall add rule name=\"Kinectv1 WebRTC HTTPS\" dir=in action=allow protocol=tcp localport={_httpsPort}");
                        Log(result.Contains("Ok") ? $"[WebRTC] Firewall rule added for HTTPS" : $"[WebRTC] Firewall rule result: {result.Trim()}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[WebRTC] Firewall setup warning: {ex.Message}");
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
                Log($"[WebRTC] HTTP URL: http://{ip}:{_httpPort}/");
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                Log($"[WebRTC] WARNING: Cannot bind HTTP to all interfaces (Access Denied)");
                var addAclResult = RunNetsh($"http add urlacl url=http://+:{_httpPort}/ user=Everyone");
                Log($"[WebRTC] URL ACL result: {addAclResult.Trim()}");
                
                try
                {
                    _httpListener = new HttpListener();
                    _httpListener.Prefixes.Add($"http://+:{_httpPort}/");
                    _httpListener.Start();
                    var ip = GetLocalIP();
                    Log($"[WebRTC] HTTP server started on port {_httpPort} (after ACL)");
                }
                catch
                {
                    _httpListener = new HttpListener();
                    _httpListener.Prefixes.Add($"http://localhost:{_httpPort}/");
                    _httpListener.Prefixes.Add($"http://127.0.0.1:{_httpPort}/");
                    _httpListener.Start();
                    Log($"[WebRTC] HTTP server started on localhost:{_httpPort} ONLY (LAN access disabled)");
                }
            }

            _httpAcceptTask = Task.Run(() => AcceptLoop(_httpListener, "HTTP", _cts.Token), _cts.Token);
            await Task.CompletedTask;
        }

        private async Task StartHttpsServerAsync()
        {
            Log($"[WebRTC] HTTPS is enabled, attempting to start on port {_httpsPort}...");
            
            try
            {
                Log("[WebRTC] Getting/creating self-signed certificate...");
                var cert = HttpsHelper.GetOrCreateCertificate();
                var thumbprint = cert.Thumbprint;
                Log($"[WebRTC] Certificate thumbprint: {thumbprint}");
                
                Log($"[WebRTC] Binding certificate to port {_httpsPort}...");
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

                _httpsAcceptTask = Task.Run(() => AcceptLoop(_httpsListener, "HTTPS", _cts.Token), _cts.Token);
            }
            catch (Exception ex)
            {
                Log($"[WebRTC] *** HTTPS SETUP FAILED *** {ex.Message}");
                _httpsActuallyStarted = false;
            }

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

        private bool BindCertificateToPort(int port, string thumbprint)
        {
            try
            {
                RunNetsh($"http delete sslcert ipport=0.0.0.0:{port}");
                var result = RunNetsh($"http add sslcert ipport=0.0.0.0:{port} certhash={thumbprint} appid={{00000000-0000-0000-0000-000000000000}}");
                
                if (result.Contains("successfully") || result.Contains("already exists"))
                    return true;
                    
                Log($"[WebRTC] Certificate binding failed: {result.Trim()}");
                return false;
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
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using var process = System.Diagnostics.Process.Start(psi);
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);
                
                return output + error;
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        public async Task StopAsync()
        {
            UnsubscribeFromEvents();
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

        private void SubscribeToEvents()
        {
            try
            {
                VoiceRecognizer.OnPartialTranscription += OnPartial;
                VoiceRecognizer.OnTranscription += OnTranscription;
                OllamaService.OnResponseChunk += OnChunk;
                OllamaService.OnResponseReceived += OnResponse;
                Tts.TtsService.OnTtsAudioChunk += OnTtsAudioChunk;
                Tts.TtsService.OnTtsCancelled += OnTtsCancelled;
                Tts.TtsService.OnTtsSpeakingStarted += OnTtsSpeakingStarted;
                Tts.TtsService.OnTtsSpeakingFinished += OnTtsSpeakingFinished;
                Services.Transcription.TranscriptionService.OnSpeakerIdentified += OnSpeakerIdentified;
            }
            catch { }
        }

        private void UnsubscribeFromEvents()
        {
            try
            {
                VoiceRecognizer.OnPartialTranscription -= OnPartial;
                VoiceRecognizer.OnTranscription -= OnTranscription;
                OllamaService.OnResponseChunk -= OnChunk;
                OllamaService.OnResponseReceived -= OnResponse;
                Tts.TtsService.OnTtsAudioChunk -= OnTtsAudioChunk;
                Tts.TtsService.OnTtsCancelled -= OnTtsCancelled;
                Tts.TtsService.OnTtsSpeakingStarted -= OnTtsSpeakingStarted;
                Tts.TtsService.OnTtsSpeakingFinished -= OnTtsSpeakingFinished;
                Services.Transcription.TranscriptionService.OnSpeakerIdentified -= OnSpeakerIdentified;
            }
            catch { }
        }

        private void OnTtsSpeakingStarted()
        {
            _ttsSpeaking = true;
            _ttsEndTime = DateTime.MinValue;
        }

        private void OnTtsSpeakingFinished()
        {
            _ttsSpeaking = false;
            _ttsEndTime = DateTime.UtcNow;
        }

        private void OnTtsCancelled()
        {
            if (GetCurrentMode() != 3 && !IsWebRtcActive) return;
            _ttsSpeaking = false;
            _ttsEndTime = DateTime.UtcNow;
            Broadcast(new { type = "tts_stop" });
        }

        private void OnPartial(string text)
        {
            if (GetCurrentMode() != 3) return;
            Broadcast(new { type = "partial", text });
        }

        private void OnTranscription(string text)
        {
            // If transcription-only mode is enabled, let the diarized pipeline handle UI; otherwise broadcast to chat.
            var transcriptionEnabled = false;
            try { transcriptionEnabled = App.SettingsProvider?.Current?.Transcription?.Enabled ?? false; } catch { }
            if (transcriptionEnabled) return;

            // Treat speech-driven messages as active WebRTC sessions so responses are relayed to the web UI.
            _webRtcActive = true;
            ExtendWebRtcActive(30);

            Broadcast(new { type = "transcription", text });
        }

        private string _buffer = "";
        private void OnChunk(string chunk)
        {
            if (GetCurrentMode() != 3 && !IsWebRtcActive) return;
            _buffer += chunk;
            Broadcast(new { type = "response_chunk", text = _buffer });
        }

        private void OnResponse(string response)
        {
            if (GetCurrentMode() != 3 && !IsWebRtcActive) return;
            _buffer = "";
            Broadcast(new { type = "response", text = response });
        }

        private void OnSpeakerIdentified(string speaker, string text)
        {
            if (GetCurrentMode() != 3 && !IsWebRtcActive) return;
            Broadcast(new { type = "transcription_diarized", speaker, text });
        }

        private void OnTtsAudioChunk(byte[] pcmData, int sampleRate)
        {
            _ttsSpeaking = true;
            _lastTtsAudioTime = DateTime.UtcNow;
            
            var currentMode = GetCurrentMode();
            if (currentMode != 3 && !IsWebRtcActive) return;
            if (pcmData == null || pcmData.Length == 0) return;
            if (_clients.Count == 0) return;
            
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

        private void Broadcast(object data)
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

        private async void AcceptLoop(HttpListener listener, string protocol, CancellationToken ct)
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

                if (req.IsWebSocketRequest && path == "/")
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
                        var debug = App.SettingsProvider?.Current?.Debug;
                        Serve(res, JsonConvert.SerializeObject(new { 
                            mode = GetCurrentMode(), 
                            bargeInEnabled, 
                            transcriptionEnabled,
                            webRtcEchoCancellation = debug?.WebRtcEchoCancellation ?? false,
                            webRtcNoiseSuppression = debug?.WebRtcNoiseSuppression ?? false,
                            webRtcAutoGainControl = debug?.WebRtcAutoGainControl ?? false
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
                        var file = Path.Combine(_wwwrootPath, path.TrimStart('/'));
                        if (File.Exists(file))
                            Serve(res, File.ReadAllText(file, Encoding.UTF8), GetMime(file));
                        else
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
                var audioMode = (AudioInMode)modeInt;

                OnModeChangeRequested?.Invoke(audioMode);
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

                var svc = App.SettingsProvider;
                var cur = svc?.Current;
                if (svc != null && cur != null)
                {
                    var next = cur with { Transcription = cur.Transcription with { Enabled = enabled } };
                    svc.Save(next);
                }

                // If transcription is being turned off, embed any pending chunk immediately
                if (!enabled)
                {
                    try { await Services.Transcription.TranscriptionService.Instance.ForceEmbedCurrentChunkAsync().ConfigureAwait(false); }
                    catch { }
                }

                Broadcast(new { type = "transcription_mode", enabled });
                Serve(res, JsonConvert.SerializeObject(new { success = true, enabled }), "application/json");
            }
            catch (Exception ex)
            {
                ServeError(res, 500, ex.Message);
            }
        }

        private async Task HandleWebSocket(HttpListenerContext ctx, CancellationToken ct)
        {
            WebSocketContext wsCtx;
            try { wsCtx = await ctx.AcceptWebSocketAsync(null); }
            catch { return; }

            var ws = wsCtx.WebSocket;
            var id = $"c{Interlocked.Increment(ref _clientId)}";
            _clients[id] = ws;
            _clientSendLocks[id] = new SemaphoreSlim(1, 1);

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
                        // Respond to keepalive ping with pong
                        try
                        {
                            long ts = 0;
                            try { ts = (long?)msg.ts ?? 0; } catch { }
                            var pongJson = JsonConvert.SerializeObject(new { type = "pong", ts });
                            var pongBytes = Encoding.UTF8.GetBytes(pongJson);
                            if (ws.State == WebSocketState.Open)
                            {
                                await ws.SendAsync(new ArraySegment<byte>(pongBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                            }
                        }
                        catch { }
                        break;

                    case "text":
                        string text = (string)msg.text;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            if (!OllamaService.IsEnabled())
                            {
                                Broadcast(new { type = "response", text = "[LLM is disabled - enable Ollama in Settings]" });
                                break;
                            }
                            
                            _webRtcActive = true;
                            ExtendWebRtcActive(60);
                            
                            var speaker = App.SettingsProvider?.Current?.Ollama?.ForcedSpeakerId ?? "User";
                            try { OnWebTextInput?.Invoke(speaker, text); } catch { }

                            // Broadcast once so other web clients see the user prompt; sender dedupes via pendingText.
                            try
                            {
                                var transcriptionEnabled = App.SettingsProvider?.Current?.Transcription?.Enabled ?? false;
                                if (!transcriptionEnabled)
                                {
                                    Broadcast(new { type = "transcription", text, speaker });
                                }
                            }
                            catch { }
                            
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await OllamaService.DispatchAsync(speaker, text);
                                }
                                catch (Exception ex)
                                {
                                    Broadcast(new { type = "response", text = $"[Error: {ex.Message}]" });
                                }
                                finally 
                                { 
                                    _webRtcActive = false; 
                                }
                            });
                        }
                        break;

                    case "audio":
                        string audioData = (string)msg.data;
                        int sampleRate = (int?)msg.sampleRate ?? 16000;
                        int prerollMs = 0;
                        try { prerollMs = (int?)msg.prerollMs ?? 0; } catch { }
                        
                        if (!string.IsNullOrEmpty(audioData))
                        {
                            _audioFramesReceived++;
                            _webRtcActive = true;
                            ExtendWebRtcActive(30);

                            if (ShouldSuppressAudio())
                            {
                                _audioFramesSuppressed++;
                                break;
                            }

                            try
                            {
                                var pcmBytes = Convert.FromBase64String(audioData);
                                
                                // Log incoming audio details for debugging (every 50 frames)
                                if (_audioFramesReceived % 50 == 1)
                                {
                                    int inSamples = pcmBytes.Length / 2;
                                    Console.WriteLine($"[WebRTC][Audio] Incoming: {pcmBytes.Length} bytes, {inSamples} samples @ {sampleRate}Hz");
                                }

                                if (prerollMs > 0 && sampleRate > 0)
                                {
                                    var keepMs = Math.Min(40, prerollMs);
                                    var dropMs = Math.Max(0, prerollMs - keepMs);
                                    var dropBytes = (int)Math.Round(sampleRate * (dropMs / 1000.0) * 2.0);
                                    if (dropBytes > 0 && dropBytes < pcmBytes.Length)
                                    {
                                        var trimmed = new byte[pcmBytes.Length - dropBytes];
                                        Buffer.BlockCopy(pcmBytes, dropBytes, trimmed, 0, trimmed.Length);
                                        pcmBytes = trimmed;
                                    }
                                }

                                // CRITICAL: Resample from browser's native rate to 16kHz for Vosk/DebugAudioCapture
                                // Browsers typically capture at 44100Hz or 48000Hz
                                byte[] pcm16kBytes;
                                if (sampleRate != 16000)
                                {
                                    pcm16kBytes = AudioUtils.ResampleMonoTo16k(pcmBytes, pcmBytes.Length, sampleRate, "webrtc-client");
                                    
                                    // Log resampling result (every 50 frames)
                                    if (_audioFramesReceived % 50 == 1)
                                    {
                                        int outSamples = pcm16kBytes.Length / 2;
                                        double expectedRatio = sampleRate / 16000.0;
                                        Console.WriteLine($"[WebRTC][Audio] Resampled: {pcm16kBytes.Length} bytes, {outSamples} samples @ 16kHz (ratio={expectedRatio:F2})");
                                    }
                                }
                                else
                                {
                                    pcm16kBytes = pcmBytes;
                                }

                                // Convert to short[] for the AudioFrame (normalized to 16kHz)
                                var pcm16 = new short[pcm16kBytes.Length / 2];
                                Buffer.BlockCopy(pcm16kBytes, 0, pcm16, 0, pcm16kBytes.Length);

                                var frame = new AudioFrame(
                                    Pcm16: pcm16,
                                    SampleRate: 16000, // Always 16kHz after resampling
                                    Channels: 1,
                                    TimestampTicks: DateTime.UtcNow.Ticks,
                                    SourceId: "webrtc-client"
                                );

                                OnWebAudioReceived?.Invoke(frame);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[WebRTC][Audio] Error processing: {ex.Message}");
                            }
                        }
                        break;

                    case "diag":
                        // Diagnostic messages from client - just log them
                        break;

                    case "offer":
                    case "ice":
                        OnWebSocketMessage?.Invoke(ws, message);
                        break;
                }
            }
            catch { }
            
            await Task.CompletedTask;
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

        private static string GetMime(string path) => Path.GetExtension(path).ToLower() switch
        {
            ".html" or ".htm" => "text/html",
            ".js" => "application/javascript",
            ".css" => "text/css",
            ".json" => "application/json",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };

        private void Log(string msg) { try { OnLog?.Invoke(msg); } catch { } }

        #region Embedded Fallback
        private static string GetEmbeddedIndexHtml() => "<!DOCTYPE html><html><head><title>Voice AI</title></head><body><h1>Voice AI</h1><p>External index.html not found. Place files in wwwroot/webrtc/</p></body></html>";
        private static string GetEmbeddedClientJs() => "console.log('External client.js not found');";
        #endregion
    }
}
