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
        private const int TTS_SUPPRESSION_MS = 300; // Suppress audio for 300ms after TTS ends (reduced)
        private const int TTS_SPEAKING_TIMEOUT_MS = 5000; // Reset _ttsSpeaking if no audio for 5 seconds
        
        // Track frames for debugging
        private static int _audioFramesReceived = 0;
        private static int _audioFramesSuppressed = 0;
        private static DateTime _lastAudioLogTime = DateTime.MinValue;
        
        /// <summary>
        /// Check if audio should be suppressed (TTS is playing or just finished).
        /// Respects the BargeInEnabled setting - when barge-in is enabled, audio is NEVER suppressed
        /// so VoiceRecognizer can detect speech and trigger cancellation.
        /// </summary>
        public static bool ShouldSuppressAudio()
        {
            // Check if barge-in is enabled - if so, NEVER suppress audio
            try
            {
                var bargeInEnabled = App.SettingsProvider?.Current?.Asr?.BargeInEnabled ?? true; // Default to true
                if (bargeInEnabled)
                {
                    // Barge-in enabled: always allow audio through so VoiceRecognizer can detect speech
                    return false;
                }
            }
            catch { }
            
            // Safety check: if _ttsSpeaking is true but no audio has been sent recently, reset it
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
            
            // Barge-in disabled: suppress audio during TTS playback
            if (_ttsSpeaking) return true;
            if (_ttsEndTime != DateTime.MinValue && (DateTime.UtcNow - _ttsEndTime).TotalMilliseconds < TTS_SUPPRESSION_MS)
                return true;
            return false;
        }

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

        // Legacy constructor for compatibility
        public WebRtcSignalingServer(int port) : this(port, false, port + 1) { }

        /// <summary>
        /// Get the path where external web files should be placed.
        /// </summary>
        public static string GetWebContentPath() => _wwwrootPath;

        /// <summary>
        /// Read file from disk or return embedded fallback.
        /// Files are read fresh each request (no caching) for development convenience.
        /// </summary>
        private string ReadWebFile(string filename)
        {
            var path = Path.Combine(_wwwrootPath, filename);
            try
            {
                if (File.Exists(path))
                {
                    return File.ReadAllText(path, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Log($"[WebRTC] Failed to read {filename}: {ex.Message}");
            }

            // Return embedded fallback
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

            // Ensure firewall rules exist
            EnsureFirewallRules();

            // Start HTTP server
            await StartHttpServerAsync();

            // Start HTTPS server if enabled
            if (_httpsEnabled)
            {
                await StartHttpsServerAsync();
            }

            SubscribeToEvents();
        }

        /// <summary>
        /// Ensure Windows Firewall allows incoming connections on our ports.
        /// </summary>
        private void EnsureFirewallRules()
        {
            try
            {
                // Check if HTTP rule exists
                var checkHttp = RunNetsh($"advfirewall firewall show rule name=\"Kinectv1 WebRTC HTTP\"");
                if (!checkHttp.Contains("Kinectv1 WebRTC HTTP"))
                {
                    Log($"[WebRTC] Adding firewall rule for HTTP port {_httpPort}...");
                    var result = RunNetsh($"advfirewall firewall add rule name=\"Kinectv1 WebRTC HTTP\" dir=in action=allow protocol=tcp localport={_httpPort}");
                    if (result.Contains("Ok"))
                        Log($"[WebRTC] Firewall rule added for HTTP");
                    else
                        Log($"[WebRTC] Firewall rule result: {result.Trim()}");
                }

                // Check if HTTPS rule exists
                if (_httpsEnabled)
                {
                    var checkHttps = RunNetsh($"advfirewall firewall show rule name=\"Kinectv1 WebRTC HTTPS\"");
                    if (!checkHttps.Contains("Kinectv1 WebRTC HTTPS"))
                    {
                        Log($"[WebRTC] Adding firewall rule for HTTPS port {_httpsPort}...");
                        var result = RunNetsh($"advfirewall firewall add rule name=\"Kinectv1 WebRTC HTTPS\" dir=in action=allow protocol=tcp localport={_httpsPort}");
                        if (result.Contains("Ok"))
                            Log($"[WebRTC] Firewall rule added for HTTPS");
                        else
                            Log($"[WebRTC] Firewall rule result: {result.Trim()}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[WebRTC] Firewall setup warning: {ex.Message}");
                Log($"[WebRTC] If iPhone can't connect, manually add firewall rules for ports {_httpPort} and {_httpsPort}");
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
                Log($"[WebRTC] Attempting to register URL reservation...");
                
                // Try to add URL reservation automatically
                var addAclResult = RunNetsh($"http add urlacl url=http://+:{_httpPort}/ user=Everyone");
                Log($"[WebRTC] URL ACL result: {addAclResult.Trim()}");
                
                // Retry with full binding
                try
                {
                    _httpListener = new HttpListener();
                    _httpListener.Prefixes.Add($"http://+:{_httpPort}/");
                    _httpListener.Start();
                    var ip = GetLocalIP();
                    Log($"[WebRTC] HTTP server started on port {_httpPort} (after ACL)");
                    Log($"[WebRTC] HTTP URL: http://{ip}:{_httpPort}/");
                }
                catch
                {
                    // Fall back to localhost only
                    _httpListener = new HttpListener();
                    _httpListener.Prefixes.Add($"http://localhost:{_httpPort}/");
                    _httpListener.Prefixes.Add($"http://127.0.0.1:{_httpPort}/");
                    _httpListener.Start();
                    Log($"[WebRTC] HTTP server started on localhost:{_httpPort} ONLY (LAN access disabled)");
                    Log($"[WebRTC] To enable LAN access, run as Admin once or run:");
                    Log($"[WebRTC]   netsh http add urlacl url=http://+:{_httpPort}/ user=Everyone");
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
                // Get or generate self-signed certificate
                Log("[WebRTC] Getting/creating self-signed certificate...");
                var cert = HttpsHelper.GetOrCreateCertificate();
                var thumbprint = cert.Thumbprint;
                Log($"[WebRTC] Certificate thumbprint: {thumbprint}");
                
                // Bind certificate to port using netsh (Windows)
                // This requires admin privileges the first time
                Log($"[WebRTC] Binding certificate to port {_httpsPort}...");
                var bindResult = BindCertificateToPort(_httpsPort, thumbprint);
                if (!bindResult)
                {
                    Log($"[WebRTC] *** HTTPS DISABLED: Could not bind certificate to port {_httpsPort} ***");
                    Log($"[WebRTC] To fix this, run the application as Administrator ONCE, then restart normally.");
                    Log($"[WebRTC] Or manually run in an Admin Command Prompt:");
                    Log($"[WebRTC]   netsh http add sslcert ipport=0.0.0.0:{_httpsPort} certhash={thumbprint} appid={{00000000-0000-0000-0000-000000000000}}");
                    return;
                }

                _httpsListener = new HttpListener();
                var prefix = $"https://+:{_httpsPort}/";
                Log($"[WebRTC] Adding HTTPS prefix: {prefix}");
                _httpsListener.Prefixes.Add(prefix);

                try
                {
                    _httpsListener.Start();
                    Log($"[WebRTC] *** HTTPS SERVER STARTED SUCCESSFULLY ***");
                    Log($"[WebRTC] HTTPS server listening on port {_httpsPort}");
                    
                    // Get local IP to display
                    var ip = GetLocalIP();
                    Log($"[WebRTC] HTTPS URL: https://{ip}:{_httpsPort}/");
                    Log($"[WebRTC] NOTE: On first visit, you must accept the self-signed certificate warning in your browser.");
                }
                catch (HttpListenerException ex)
                {
                    Log($"[WebRTC] *** HTTPS FAILED TO START ***");
                    Log($"[WebRTC] Error: {ex.Message}");
                    Log($"[WebRTC] Error code: {ex.ErrorCode}");
                    
                    if (ex.ErrorCode == 5) // Access Denied
                    {
                        Log($"[WebRTC] Access Denied - Run as Administrator once to register the URL reservation:");
                        Log($"[WebRTC]   netsh http add urlacl url=https://+:{_httpsPort}/ user=Everyone");
                    }
                    return;
                }

                _httpsAcceptTask = Task.Run(() => AcceptLoop(_httpsListener, "HTTPS", _cts.Token), _cts.Token);
            }
            catch (Exception ex)
            {
                Log($"[WebRTC] *** HTTPS SETUP FAILED ***");
                Log($"[WebRTC] Exception: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                    Log($"[WebRTC] Inner: {ex.InnerException.Message}");
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
                // First, try to delete any existing binding
                var deleteArgs = $"http delete sslcert ipport=0.0.0.0:{port}";
                var deleteResult = RunNetsh(deleteArgs);
                Log($"[WebRTC] Delete existing binding result: {deleteResult.Trim()}");
                
                // Now add the new binding
                // The certificate must be in the LocalMachine\MY or CurrentUser\MY store
                var addArgs = $"http add sslcert ipport=0.0.0.0:{port} certhash={thumbprint} appid={{00000000-0000-0000-0000-000000000000}}";
                var result = RunNetsh(addArgs);
                
                Log($"[WebRTC] Certificate binding result: {result.Trim()}");
                
                if (result.Contains("successfully") || result.Contains("SSL Certificate successfully added"))
                {
                    Log($"[WebRTC] Certificate bound to port {port}");
                    return true;
                }
                
                // Check if already bound
                if (result.Contains("Cannot create a file") || result.Contains("already exists"))
                {
                    Log($"[WebRTC] Certificate already bound to port {port}");
                    return true;
                }
                
                // Error 1312 means the certificate private key isn't accessible
                if (result.Contains("1312"))
                {
                    Log($"[WebRTC] Error 1312: Certificate private key not accessible.");
                    Log($"[WebRTC] Try running as Administrator, or regenerate the certificate.");
                    Log($"[WebRTC] You can also manually install the certificate:");
                    Log($"[WebRTC]   1. Open certmgr.msc");
                    Log($"[WebRTC]   2. Import the certificate from: {HttpsHelper.GetCertificatePath()}");
                    Log($"[WebRTC]   3. Place in 'Personal' store");
                    return false;
                }

                // Error 5 means access denied
                if (result.Contains("Error: 5") || result.Contains("Access is denied"))
                {
                    Log($"[WebRTC] Access denied. Run as Administrator once to bind the certificate.");
                    return false;
                }
                
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
                
                // Subscribe to TTS audio for web playback
                Tts.TtsService.OnTtsAudioChunk += OnTtsAudioChunk;
                Tts.TtsService.OnTtsCancelled += OnTtsCancelled;
                
                // Track TTS speaking state
                Tts.TtsService.OnTtsSpeakingStarted += OnTtsSpeakingStarted;
                Tts.TtsService.OnTtsSpeakingFinished += OnTtsSpeakingFinished;
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
            }
            catch { }
        }

        /// <summary>
        /// Called when TTS starts speaking - mark for audio suppression.
        /// </summary>
        private void OnTtsSpeakingStarted()
        {
            _ttsSpeaking = true;
            _ttsEndTime = DateTime.MinValue; // Clear end time
            Log("[WebRTC] TTS speaking started");
        }

        /// <summary>
        /// Called when TTS finishes speaking - start suppression cooldown.
        /// </summary>
        private void OnTtsSpeakingFinished()
        {
            _ttsSpeaking = false;
            _ttsEndTime = DateTime.UtcNow;
            Log("[WebRTC] TTS speaking finished");
        }

        /// <summary>
        /// Notify web clients to stop TTS playback (barge-in).
        /// </summary>
        private void OnTtsCancelled()
        {
            if (GetCurrentMode() != 3 && !IsWebRtcActive) return;
            _ttsSpeaking = false;
            _ttsEndTime = DateTime.UtcNow;
            Broadcast(new { type = "tts_stop" });
            Log("[WebRTC] TTS cancelled (barge-in)");
        }

        // Only relay if WebRTC mode is active
        private void OnPartial(string text)
        {
            if (GetCurrentMode() != 3) return; // WebRtcVoice = 3
            Broadcast(new { type = "partial", text });
        }

        private void OnTranscription(string text)
        {
            if (GetCurrentMode() != 3) return; // WebRtcVoice = 3
            Broadcast(new { type = "transcription", text });
        }

        private string _buffer = "";
        private void OnChunk(string chunk)
        {
            if (GetCurrentMode() != 3 && !IsWebRtcActive) return; // WebRtcVoice = 3
            _buffer += chunk;
            Broadcast(new { type = "response_chunk", text = _buffer });
        }

        private void OnResponse(string response)
        {
            if (GetCurrentMode() != 3 && !IsWebRtcActive) return; // WebRtcVoice = 3
            _buffer = "";
            Broadcast(new { type = "response", text = response });
        }

        /// <summary>
        /// Send TTS audio chunk to web clients for playback.
        /// Only sends when WebRTC mode is active or text was entered from web UI.
        /// </summary>
        private void OnTtsAudioChunk(byte[] pcmData, int sampleRate)
        {
            // Mark TTS as active when we receive audio
            _ttsSpeaking = true;
            _lastTtsAudioTime = DateTime.UtcNow;
            
            // Mode check - allow audio in WebRTC mode (3) OR when WebRTC active (web text input)
            var currentMode = GetCurrentMode();
            var isActive = IsWebRtcActive;
            
            // DEBUG: Log once per utterance
            var clientCount = _clients.Count;
            Log($"[WebRTC] OnTtsAudioChunk: mode={currentMode}, isActive={isActive}, clients={clientCount}, bytes={pcmData?.Length ?? 0}");
            
            if (currentMode != 3 && !isActive)
            {
                // Not in WebRTC mode and not WebRTC-sourced input - skip
                Log($"[WebRTC] TTS audio skipped: mode={currentMode} != 3, isActive={isActive}");
                return;
            }
            
            if (pcmData == null || pcmData.Length == 0) return;
            
            if (clientCount == 0)
            {
                Log("[WebRTC] TTS audio skipped: no connected clients");
                return;
            }
            
            try
            {
                // Convert to base64 for transmission
                var base64 = Convert.ToBase64String(pcmData);
                Log($"[WebRTC] Broadcasting TTS audio: {pcmData.Length} bytes ({base64.Length} b64) to {clientCount} clients");
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
            
            // Send to each client with proper locking to prevent concurrent SendAsync
            foreach (var kvp in _clients.ToArray())
            {
                var ws = kvp.Value;
                var clientId = kvp.Key;
                if (ws.State == WebSocketState.Open)
                {
                    // Get or create lock for this client
                    var sendLock = _clientSendLocks.GetOrAdd(clientId, _ => new SemaphoreSlim(1, 1));
                    
                    // Fire and forget with proper synchronization
                    _ = Task.Run(async () =>
                    {
                        bool acquired = false;
                        try 
                        { 
                            // Try to acquire lock with timeout - skip if busy
                            acquired = await sendLock.WaitAsync(100);
                            if (!acquired)
                            {
                                // Client is busy, skip this message
                                return;
                            }
                            
                            if (ws.State != WebSocketState.Open) return;
                            
                            using var cts = new CancellationTokenSource(500); // 500ms timeout
                            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            // Send timeout - client may be slow
                        }
                        catch (WebSocketException)
                        {
                            // Client disconnected, will be cleaned up on next receive
                        }
                        catch (ObjectDisposedException)
                        {
                            // WebSocket already disposed
                        }
                        catch (Exception ex)
                        {
                            Log($"[WebRTC] Send error to {clientId}: {ex.Message}");
                        }
                        finally
                        {
                            if (acquired)
                            {
                                try { sendLock.Release(); } catch { }
                            }
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

                if (req.IsWebSocketRequest)
                {
                    await HandleWebSocket(ctx, ct);
                    return;
                }

                switch (path)
                {
                    case "/":
                    case "/index.html":
                        var html = ReadWebFile("index.html");
                        Serve(res, html, "text/html");
                        break;
                    case "/client.js":
                        var js = ReadWebFile("client.js");
                        Serve(res, js, "application/javascript");
                        break;
                    case "/api/status":
                        Serve(res, JsonConvert.SerializeObject(new { mode = GetCurrentMode() }), "application/json");
                        break;
                    case "/api/mode":
                        if (req.HttpMethod == "POST")
                            await HandleModeChange(req, res);
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

                Log($"[WebRTC] Mode change requested: {audioMode}");

                // Trigger mode change in app via event
                OnModeChangeRequested?.Invoke(audioMode);

                // Broadcast to all clients
                Broadcast(new { type = "status", mode = modeInt });

                Serve(res, JsonConvert.SerializeObject(new { success = true, mode = modeInt }), "application/json");
            }
            catch (Exception ex)
            {
                Log($"[WebRTC] Mode change error: {ex.Message}");
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
            
            Log($"[WebRTC] Client connected: {id}");

            // Send initial status including barge-in setting
            try
            {
                var bargeInEnabled = App.SettingsProvider?.Current?.Asr?.BargeInEnabled ?? false;
                var status = JsonConvert.SerializeObject(new { 
                    type = "status", 
                    mode = GetCurrentMode(),
                    bargeInEnabled = bargeInEnabled
                });
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(status)), WebSocketMessageType.Text, true, ct);
            }
            catch { }

            var buf = new byte[4096];
            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var msg = Encoding.UTF8.GetString(buf, 0, result.Count);
                        await ProcessMessage(ws, msg);
                    }
                }
            }
            catch { }
            finally
            {
                _clients.TryRemove(id, out _);
                if (_clientSendLocks.TryRemove(id, out var sendLock))
                {
                    try { sendLock.Dispose(); } catch { }
                }
                try { ws.Dispose(); } catch { }
                Log($"[WebRTC] Client disconnected: {id}");
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
                    case "diag":
                        try
                        {
                            string kind = (string)msg.kind;
                            int count = (int?)msg.count ?? 0;
                            Log($"[WebRTC][diag] client={ws?.GetHashCode()} kind={kind} count={count}");
                        }
                        catch { }
                        break;

                    case "text":
                        string text = (string)msg.text;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            Log($"[WebRTC] Text message received: {text.Substring(0, Math.Min(50, text.Length))}...");
                            
                            // Check if Ollama is enabled
                            var ollamaEnabled = OllamaService.IsEnabled();
                            Log($"[WebRTC] OllamaService.IsEnabled() = {ollamaEnabled}");
                            
                            if (!ollamaEnabled)
                            {
                                Log("[WebRTC] LLM is disabled - check Ollama settings (enabled checkbox and model)");
                                Broadcast(new { type = "response", text = "[LLM is disabled - enable Ollama in Settings]" });
                                break;
                            }
                            
                            // Mark as WebRTC-sourced and extend the window for TTS
                            _webRtcActive = true;
                            ExtendWebRtcActive(60); // 60 seconds should cover most responses
                            
                            // Determine speaker
                            var speaker = App.SettingsProvider?.Current?.Ollama?.ForcedSpeakerId ?? "User";
                            Log($"[WebRTC] Using speaker: {speaker}");
                            
                            // Notify desktop UI of the text input
                            try { OnWebTextInput?.Invoke(speaker, text); } catch { }
                            
                            // Echo to web clients
                            Broadcast(new { type = "transcription", text });
                            
                            // Send to LLM
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    Log($"[WebRTC] Dispatching to LLM: speaker={speaker}, text={text.Substring(0, Math.Min(30, text.Length))}...");
                                    await OllamaService.DispatchAsync(speaker, text);
                                    Log("[WebRTC] LLM dispatch completed");
                                }
                                catch (Exception ex)
                                {
                                    Log($"[WebRTC] LLM dispatch error: {ex.Message}");
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
                        // Handle audio from web client (base64 PCM)
                        string audioData = (string)msg.data;
                        int sampleRate = (int?)msg.sampleRate ?? 16000;
                        if (!string.IsNullOrEmpty(audioData))
                        {
                            _audioFramesReceived++;
                            
                            // Mark WebRTC as active when receiving audio from web client
                            // This ensures TTS responses go back to the web client
                            _webRtcActive = true;
                            ExtendWebRtcActive(30); // Keep active for 30 seconds after last audio
                            
                            // Log periodically
                            var now = DateTime.UtcNow;
                            if ((now - _lastAudioLogTime).TotalSeconds >= 10)
                            {
                                var suppressed = _audioFramesSuppressed;
                                var received = _audioFramesReceived;
                                Log($"[WebRTC] Audio stats: received={received}, suppressed={suppressed}, ttsSpeaking={_ttsSpeaking}, bargeIn={App.SettingsProvider?.Current?.Asr?.BargeInEnabled ?? true}");
                                _audioFramesReceived = 0;
                                _audioFramesSuppressed = 0;
                                _lastAudioLogTime = now;
                            }
                            
                            // Check suppression AFTER logging so we can see what's happening
                            if (ShouldSuppressAudio())
                            {
                                _audioFramesSuppressed++;
                                break;
                            }
                            
                            try
                            {
                                // Decode base64 to PCM bytes
                                var pcmBytes = Convert.FromBase64String(audioData);
                                var pcm16 = new short[pcmBytes.Length / 2];
                                Buffer.BlockCopy(pcmBytes, 0, pcm16, 0, pcmBytes.Length);
                                
                                // Create audio frame and fire event
                                var frame = new AudioFrame(
                                    Pcm16: pcm16,
                                    SampleRate: sampleRate,
                                    Channels: 1,
                                    TimestampTicks: DateTime.UtcNow.Ticks,
                                    SourceId: "webrtc-client"
                                );
                                
                                // Fire to WebRTC transport's inbound audio handler
                                OnWebAudioReceived?.Invoke(frame);
                            }
                            catch (Exception ex)
                            {
                                Log($"[WebRTC] Audio decode error: {ex.Message}");
                            }
                        }
                        break;

                    case "offer":
                    case "ice":
                        OnWebSocketMessage?.Invoke(ws, message);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"[WebRTC] Message processing error: {ex.Message}");
            }
            
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
                res.AddHeader("Pragma", "no-cache");
                res.AddHeader("Expires", "0");
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

        private static string GetEmbeddedIndexHtml() => @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0, user-scalable=no, maximum-scale=1"">
    <title>Voice AI</title>
    <style>
        :root {
            --bg: #121212;
            --surface: #1E1E1E;
            --surface-light: #2A2A2A;
            --border: #3A3A3A;
            --text: #FFFFFF;
            --text-dim: #B0B0B0;
            --text-muted: #606060;
            --blue: #3B82F6;
            --green: #22C55E;
            --orange: #F59E0B;
            --red: #EF4444;
            --purple: #8B5CF6;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        html, body { height: 100%; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
            background: var(--bg);
            color: var(--text);
            display: flex;
            flex-direction: column;
            height: 100vh;
            height: 100dvh;
            overflow: hidden;
        }
        .header {
            display: flex;
            align-items: center;
            justify-content: space-between;
            padding: 12px;
            background: var(--surface);
            border-bottom: 1px solid var(--border);
        }
        .header h1 { font-size: 15px; font-weight: 600; }
        .status {
            display: flex;
            align-items: center;
            gap: 6px;
            font-size: 11px;
            color: var(--text-dim);
        }
        .dot {
            width: 6px; height: 6px;
            border-radius: 50%;
            background: var(--text-muted);
        }
        .dot.on { background: var(--green); }
        .dot.warn { background: var(--orange); animation: blink 1s infinite; }
        @keyframes blink { 50% { opacity: 0.4; } }
        .modes {
            display: flex;
            gap: 4px;
            padding: 8px;
            background: var(--surface);
            border-bottom: 1px solid var(--border);
        }
        .mode {
            flex: 1;
            padding: 8px 4px;
            border: none;
            border-radius: 6px;
            background: var(--surface-light);
            color: var(--text-muted);
            font-size: 12px;
            cursor: pointer;
            transition: all 0.15s;
        }
        .mode:active { transform: scale(0.98); }
        .mode.active { background: var(--purple); color: white; }
        .mode.mic.active { background: var(--green); }
        .mode.discord.active { background: var(--blue); }
        .chat {
            flex: 1;
            overflow-y: auto;
            padding: 12px;
            display: flex;
            flex-direction: column;
            gap: 6px;
        }
        .msg {
            max-width: 85%;
            padding: 10px 12px;
            border-radius: 16px;
            font-size: 14px;
            line-height: 1.4;
            word-break: break-word;
        }
        .msg.user {
            align-self: flex-end;
            background: var(--purple);
            border-bottom-right-radius: 4px;
        }
        .msg.ai {
            align-self: flex-start;
            background: var(--surface-light);
            border-bottom-left-radius: 4px;
        }
        .msg.typing {
            background: var(--surface-light);
            padding: 14px 16px;
        }
        .msg.typing span {
            display: inline-block;
            width: 6px; height: 6px;
            background: var(--text-muted);
            border-radius: 50%;
            margin-right: 4px;
            animation: dot 1.4s infinite;
        }
        .msg.typing span:nth-child(2) { animation-delay: 0.2s; }
        .msg.typing span:nth-child(3) { animation-delay: 0.4s; margin-right: 0; }
        @keyframes dot { 0%,60%,100% { transform: translateY(0); } 30% { transform: translateY(-4px); } }
        .empty {
            flex: 1;
            display: flex;
            align-items: center;
            justify-content: center;
            color: var(--text-muted);
            font-size: 13px;
        }
        .level {
            height: 3px;
            background: var(--surface);
        }
        .level-fill {
            height: 100%;
            width: 0%;
            background: var(--purple);
            transition: width 0.1s;
        }
        .input-row {
            display: flex;
            padding: 8px;
            gap: 8px;
            background: var(--surface);
            border-top: 1px solid var(--border);
        }
        .input-row input {
            flex: 1;
            padding: 10px 14px;
            border: none;
            border-radius: 20px;
            background: var(--surface-light);
            color: var(--text);
            font-size: 14px;
            outline: none;
        }
        .input-row input::placeholder { color: var(--text-muted); }
        .input-row button {
            width: 40px; height: 40px;
            border: none;
            border-radius: 50%;
            cursor: pointer;
            display: flex;
            align-items: center;
            justify-content: center;
        }
        .input-row button svg { width: 18px; height: 18px; fill: white; }
        .btn-voice { background: var(--green); }
        .btn-voice.active { background: var(--red); }
        .btn-voice:disabled { background: var(--surface-light); opacity: 0.5; }
        .btn-send { background: var(--blue); }
        .btn-send:disabled { opacity: 0.4; }
        .hidden { display: none !important; }
        audio { display: none; }
    </style>
</head>
<body>
    <div class=""header"">
        <h1>Voice AI</h1>
        <div class=""status"">
            <span class=""dot"" id=""dot""></span>
            <span id=""statusText"">Offline</span>
        </div>
    </div>
    <div class=""modes"">
        <button class=""mode mic"" data-mode=""0"" onclick=""setMode(0)"">?? Mic</button>
        <button class=""mode discord"" data-mode=""1"" onclick=""setMode(1)"">?? Discord</button>
        <button class=""mode webrtc"" data-mode=""3"" onclick=""setMode(3)"">?? WebRTC</button>
    </div>
    <div class=""chat"" id=""chat"">
        <div class=""empty"" id=""empty"">No messages yet</div>
    </div>
    <div class=""level""><div class=""level-fill"" id=""level""></div></div>
    <div class=""input-row"">
        <input type=""text"" id=""input"" placeholder=""Type a message..."">
        <button class=""btn-voice"" id=""voiceBtn"" onclick=""toggleVoice()"" disabled>
            <svg viewBox=""0 0 24 24""><path d=""M12 14c1.66 0 3-1.34 3-3V5c0-1.66-1.34-3-3-3S9 3.34 9 5v6c0 1.66 1.34 3 3 3zm5-3c0 2.76-2.24 5-5 5s-5-2.24-5-5H5c0 3.53 2.61 6.43 6 6.92V21h2v-3.08c3.39-.49 6-3.39 6-6.92h-2z""/></svg>
        </button>
        <button class=""btn-send"" id=""sendBtn"" onclick=""send()"">
            <svg viewBox=""0 0 24 24""><path d=""M2.01 21L23 12 2.01 3 2 10l15 2-15 2z""/></svg>
        </button>
    </div>
    <audio id=""audio"" autoplay></audio>
    <script src=""client.js""></script>
</body>
</html>";

        private static string GetEmbeddedClientJs() => @"// Voice AI - WebRTC Client
let ws = null;
let pc = null;
let stream = null;
let audioCtx = null;
let mode = 0;
let voiceOn = false;

// TTS playback state
let ttsAudioCtx = null;
let ttsNextTime = 0;
let ttsSpeaking = false;

const chat = document.getElementById('chat');
const empty = document.getElementById('empty');
const input = document.getElementById('input');
const level = document.getElementById('level');
const dot = document.getElementById('dot');
const statusText = document.getElementById('statusText');
const voiceBtn = document.getElementById('voiceBtn');
const audio = document.getElementById('audio');

function setStatus(text, state) {
    statusText.textContent = text;
    dot.className = 'dot' + (state ? ' ' + state : '');
}

function updateMode(m) {
    mode = m;
    document.querySelectorAll('.mode').forEach(btn => {
        btn.classList.toggle('active', parseInt(btn.dataset.mode) === m);
    });
    voiceBtn.disabled = m !== 3; // WebRTC mode is 3
    if (m !== 3 && voiceOn) stopVoice();
}

async function setMode(m) {
    try {
        const res = await fetch('/api/mode', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ mode: m })
        });
        if (res.ok) updateMode(m);
    } catch (e) {
        console.error('Mode error:', e);
    }
}

function addMsg(text, type) {
    if (empty) empty.classList.add('hidden');
    const div = document.createElement('div');
    div.className = 'msg ' + type;
    div.textContent = text;
    chat.appendChild(div);
    chat.scrollTop = chat.scrollHeight;
    return div;
}

let typingEl = null;
function showTyping() {
    if (typingEl) return;
    if (empty) empty.classList.add('hidden');
    typingEl = document.createElement('div');
    typingEl.className = 'msg ai typing';
    typingEl.innerHTML = '<span></span><span></span><span></span>';
    chat.appendChild(typingEl);
    chat.scrollTop = chat.scrollHeight;
}
function hideTyping() {
    if (typingEl) { typingEl.remove(); typingEl = null; }
}

let streamEl = null;
function appendResponse(text) {
    hideTyping();
    if (!streamEl) {
        streamEl = addMsg('', 'ai');
    }
    streamEl.textContent = text;
    chat.scrollTop = chat.scrollHeight;
}
function finalizeResponse() {
    streamEl = null;
}

function playTtsAudio(base64Data, srcRate) {
    srcRate = srcRate || 24000;
    ttsSpeaking = true;
    
    if (!ttsAudioCtx) {
        var AC = window.AudioContext || window.webkitAudioContext;
        ttsAudioCtx = new AC();
    }
    
    if (ttsAudioCtx.state === 'suspended') {
        ttsAudioCtx.resume();
    }
    
    try {
        var raw = atob(base64Data);
        var bytes = new Uint8Array(raw.length);
        for (var i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
        var pcm16 = new Int16Array(bytes.buffer);
        var floats = new Float32Array(pcm16.length);
        for (var j = 0; j < pcm16.length; j++) floats[j] = pcm16[j] / 32768.0;
        
        var ctxRate = ttsAudioCtx.sampleRate;
        var buffer;
        if (Math.abs(ctxRate - srcRate) < 100) {
            buffer = ttsAudioCtx.createBuffer(1, floats.length, ctxRate);
            buffer.copyToChannel(floats, 0);
        } else {
            var ratio = ctxRate / srcRate;
            var newLen = Math.round(floats.length * ratio);
            buffer = ttsAudioCtx.createBuffer(1, newLen, ctxRate);
            var out = buffer.getChannelData(0);
            for (var k = 0; k < newLen; k++) {
                var srcPos = k / ratio;
                var idx0 = Math.floor(srcPos);
                var idx1 = Math.min(idx0 + 1, floats.length - 1);
                var frac = srcPos - idx0;
                out[k] = floats[idx0] * (1 - frac) + floats[idx1] * frac;
            }
        }
        var source = ttsAudioCtx.createBufferSource();
        source.buffer = buffer;
        source.connect(ttsAudioCtx.destination);
        source.onended = function() {
            if (ttsNextTime <= ttsAudioCtx.currentTime + 0.05) {
                ttsSpeaking = false;
            }
        };
        
        var now = ttsAudioCtx.currentTime;
        if (ttsNextTime < now + 0.01) ttsNextTime = now + 0.01;
        source.start(ttsNextTime);
        ttsNextTime += buffer.duration;
    } catch (e) {
        console.error('TTS playback error:', e);
        ttsSpeaking = false;
    }
}

function stopTtsPlayback() {
    ttsNextTime = 0;
    ttsSpeaking = false;
}

function send() {
    const text = input.value.trim();
    if (!text) return;
    input.value = '';
    addMsg(text, 'user');
    if (ws?.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({ type: 'text', text }));
        showTyping();
    }
}

async function toggleVoice() {
    if (mode !== 3) return; // WebRTC mode is 3
    voiceOn ? stopVoice() : await startVoice();
}

async function startVoice() {
    try {
        stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
        voiceOn = true;
        voiceBtn.classList.add('active');
        startCapture();
        startLevel();
    } catch (e) {
        console.error('Mic error:', e);
        addMsg('Mic error: ' + e.message, 'ai');
    }
}

function stopVoice() {
    voiceOn = false;
    voiceBtn.classList.remove('active');
    level.style.width = '0%';
    if (audioCtx) { audioCtx.close().catch(() => {}); audioCtx = null; }
    if (stream) { stream.getTracks().forEach(t => t.stop()); stream = null; }
}

function startCapture() {
    if (!stream) return;
    var AC = window.AudioContext || window.webkitAudioContext;
    audioCtx = new AC();
    var source = audioCtx.createMediaStreamSource(stream);
    var processor = audioCtx.createScriptProcessor(4096, 1, 1);
    var sampleRate = audioCtx.sampleRate;
    
    processor.onaudioprocess = function(e) {
        if (!voiceOn || !ws || ws.readyState !== 1) return;
        
        var inputData = e.inputBuffer.getChannelData(0);
        
        // Resample to 16kHz
        var ratio = sampleRate / 16000;
        var newLen = Math.round(inputData.length / ratio);
        var resampled = new Float32Array(newLen);
        for (var i = 0; i < newLen; i++) {
            var srcIdx = i * ratio;
            var i0 = Math.floor(srcIdx);
            var i1 = Math.min(i0 + 1, inputData.length - 1);
            var frac = srcIdx - i0;
            resampled[i] = inputData[i0] * (1 - frac) + inputData[i1] * frac;
        }
        
        // Convert to PCM16
        var pcm = new Int16Array(resampled.length);
        for (var j = 0; j < resampled.length; j++) {
            var s = Math.max(-1, Math.min(1, resampled[j]));
            pcm[j] = s < 0 ? s * 0x8000 : s * 0x7FFF;
        }
        
        // Base64 encode
        var bytes = new Uint8Array(pcm.buffer);
        var bin = '';
        for (var k = 0; k < bytes.length; k++) bin += String.fromCharCode(bytes[k]);
        
        ws.send(JSON.stringify({ type: 'audio', data: btoa(bin), sampleRate: 16000 }));
    };
    
    source.connect(processor);
    processor.connect(audioCtx.destination);
}

function startLevel() {
    if (!stream) return;
    var AC = window.AudioContext || window.webkitAudioContext;
    var ctx = new AC();
    var src = ctx.createMediaStreamSource(stream);
    var analyser = ctx.createAnalyser();
    analyser.fftSize = 256;
    src.connect(analyser);
    var data = new Uint8Array(analyser.frequencyBinCount);
    function update() {
        if (!voiceOn) { ctx.close(); return; }
        analyser.getByteFrequencyData(data);
        var avg = data.reduce((a, b) => a + b, 0) / data.length;
        level.style.width = Math.min(100, (avg / 128) * 100) + '%';
        requestAnimationFrame(update);
    }
    update();
}

function connect() {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    ws = new WebSocket(proto + '//' + location.host + '/');
    ws.onopen = () => {
        setStatus('Connected', 'on');
    };
    ws.onmessage = async e => {
        const msg = JSON.parse(e.data);
        switch (msg.type) {
            case 'status':
                if (msg.mode !== undefined) updateMode(msg.mode);
                break;
            case 'transcription':
                addMsg(msg.text, 'user');
                showTyping();
                break;
            case 'response_chunk':
                appendResponse(msg.text);
                break;
            case 'response':
                hideTyping();
                if (!streamEl) addMsg(msg.text, 'ai');
                finalizeResponse();
                break;
            case 'tts_audio':
                if (msg.data) playTtsAudio(msg.data, msg.sampleRate);
                break;
            case 'tts_stop':
                stopTtsPlayback();
                break;
        }
    };
    ws.onclose = () => {
        setStatus('Disconnected', '');
        setTimeout(connect, 2000);
    };
    ws.onerror = () => setStatus('Error', '');
}

input.addEventListener('keydown', e => {
    if (e.key === 'Enter') { e.preventDefault(); send(); }
});

setStatus('Connecting...', 'warn');
connect();";

        #endregion
    }
}
