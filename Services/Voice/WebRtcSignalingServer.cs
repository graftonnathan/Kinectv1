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
    /// </summary>
    public sealed class WebRtcSignalingServer
    {
        public event Action<string> OnLog;
        public event Action<WebSocket, string> OnWebSocketMessage;

        private readonly int _port;
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptTask;
        
        // Track mode change requests to trigger MainWindow
        public static event Action<AudioInMode> OnModeChangeRequested;
        
        // Allow MainWindow to receive text input from web UI
        public static event Action<string, string> OnWebTextInput; // (speaker, text)
        
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
        private int _clientId = 0;
        
        private static readonly string _wwwrootPath;

        // Track if we're receiving WebRTC audio (to filter messages)
        private static volatile bool _webRtcActive = false;
        public static bool IsWebRtcActive => _webRtcActive;
        public static void SetWebRtcActive(bool active) => _webRtcActive = active;

        static WebRtcSignalingServer()
        {
            _wwwrootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "webrtc");
        }

        public WebRtcSignalingServer(int port)
        {
            _port = port;
        }

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
            _instance = this; // Set instance for static broadcast access
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{_port}/");

            try
            {
                _listener.Start();
                Log($"[WebRTC] Server started on port {_port}");
                Log($"[WebRTC] Web content path: {_wwwrootPath}");
                Log($"[WebRTC] index.html exists: {File.Exists(Path.Combine(_wwwrootPath, "index.html"))}");
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();
                Log($"[WebRTC] Server started on localhost:{_port}");
            }

            _acceptTask = Task.Run(() => AcceptLoop(_cts.Token), _cts.Token);
            SubscribeToEvents();
            await Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            UnsubscribeFromEvents();
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            
            foreach (var client in _clients.Values)
                try { client.Dispose(); } catch { }
            _clients.Clear();
            
            try { if (_acceptTask != null) await Task.WhenAny(_acceptTask, Task.Delay(1000)); } catch { }
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
            }
            catch { }
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
            if (GetCurrentMode() != 3 && !_webRtcActive) return; // WebRtcVoice = 3
            _buffer += chunk;
            Broadcast(new { type = "response_chunk", text = _buffer });
        }

        private void OnResponse(string response)
        {
            if (GetCurrentMode() != 3 && !_webRtcActive) return; // WebRtcVoice = 3
            _buffer = "";
            Broadcast(new { type = "response", text = response });
        }

        private void Broadcast(object data)
        {
            var json = JsonConvert.SerializeObject(data);
            var bytes = Encoding.UTF8.GetBytes(json);
            
            foreach (var ws in _clients.Values)
            {
                if (ws.State == WebSocketState.Open)
                {
                    try { _ = ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
                    catch { }
                }
            }
        }

        private async void AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
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
            
            Log($"[WebRTC] Client connected: {id}");

            // Send initial status
            try
            {
                var status = JsonConvert.SerializeObject(new { type = "status", mode = GetCurrentMode() });
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
                    case "text":
                        string text = (string)msg.text;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            Log($"[WebRTC] Text message received: {text.Substring(0, Math.Min(50, text.Length))}...");
                            
                            // Mark as WebRTC-sourced
                            _webRtcActive = true;
                            
                            // Determine speaker
                            var speaker = App.SettingsProvider?.Current?.Ollama?.ForcedSpeakerId ?? "User";
                            
                            // Notify desktop UI of the text input
                            try { OnWebTextInput?.Invoke(speaker, text); } catch { }
                            
                            // Echo to web clients
                            Broadcast(new { type = "transcription", text });
                            
                            // Send to LLM
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await OllamaService.DispatchAsync(speaker, text);
                                }
                                catch (Exception ex)
                                {
                                    Log($"[WebRTC] LLM dispatch error: {ex.Message}");
                                }
                                finally { _webRtcActive = false; }
                            });
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
        <button class=""mode webrtc"" data-mode=""2"" onclick=""setMode(2)"">?? WebRTC</button>
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
    voiceBtn.disabled = m !== 2;
    if (m !== 2 && voiceOn) stopVoice();
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

function send() {
    const text = input.value.trim();
    if (!text) return;
    input.value = '';
    addMsg(text, 'user');
    if (ws?.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({ type: 'text', text }));
    }
}

async function toggleVoice() {
    if (mode !== 2) return;
    voiceOn ? stopVoice() : await startVoice();
}

async function startVoice() {
    try {
        stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
        voiceOn = true;
        voiceBtn.classList.add('active');
        startLevel();
        if (ws?.readyState === WebSocket.OPEN) {
            await createPC();
        }
    } catch (e) {
        console.error('Mic error:', e);
    }
}

function stopVoice() {
    voiceOn = false;
    voiceBtn.classList.remove('active');
    level.style.width = '0%';
    if (audioCtx) { audioCtx.close().catch(() => {}); audioCtx = null; }
    if (stream) { stream.getTracks().forEach(t => t.stop()); stream = null; }
    if (pc) { pc.close(); pc = null; }
}

function startLevel() {
    if (!stream) return;
    audioCtx = new (window.AudioContext || window.webkitAudioContext)();
    const src = audioCtx.createMediaStreamSource(stream);
    const analyser = audioCtx.createAnalyser();
    analyser.fftSize = 256;
    src.connect(analyser);
    const data = new Uint8Array(analyser.frequencyBinCount);
    function update() {
        if (!voiceOn) return;
        analyser.getByteFrequencyData(data);
        const avg = data.reduce((a, b) => a + b, 0) / data.length;
        level.style.width = Math.min(100, (avg / 128) * 100) + '%';
        requestAnimationFrame(update);
    }
    update();
}

async function createPC() {
    pc = new RTCPeerConnection({ iceServers: [] });
    stream.getTracks().forEach(t => pc.addTrack(t, stream));
    pc.ontrack = e => { audio.srcObject = e.streams[0]; audio.play().catch(() => {}); };
    pc.onicecandidate = e => {
        if (e.candidate && ws?.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify({ type: 'ice', candidate: e.candidate }));
        }
    };
    pc.onconnectionstatechange = () => {
        if (pc.connectionState === 'failed' || pc.connectionState === 'disconnected') {
            stopVoice();
        }
    };
    const offer = await pc.createOffer({ offerToReceiveAudio: true });
    let sdp = offer.sdp;
    const lines = sdp.split('\r\n');
    for (let i = 0; i < lines.length; i++) {
        if (lines[i].startsWith('m=audio')) {
            const p = lines[i].split(' ');
            const pts = p.slice(3);
            const idx = pts.indexOf('0');
            if (idx > 0) { pts.splice(idx, 1); pts.unshift('0'); lines[i] = p.slice(0, 3).concat(pts).join(' '); }
            break;
        }
    }
    sdp = lines.join('\r\n');
    await pc.setLocalDescription({ type: 'offer', sdp });
    ws.send(JSON.stringify({ type: 'offer', sdp }));
}

function connect() {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    ws = new WebSocket(proto + '//' + location.host + '/');
    ws.onopen = () => {
        setStatus('Connected', 'on');
        if (voiceOn && stream && mode === 2) createPC();
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
            case 'answer':
                if (pc) await pc.setRemoteDescription(new RTCSessionDescription(msg));
                break;
            case 'ice':
                if (pc && msg.candidate) {
                    try { await pc.addIceCandidate(new RTCIceCandidate(msg.candidate)); } catch (e) {}
                }
                break;
        }
    };
    ws.onclose = () => {
        setStatus('Disconnected', '');
        stopVoice();
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
