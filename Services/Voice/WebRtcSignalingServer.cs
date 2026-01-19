using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1.Voice
{
    /// <summary>
    /// In-process HTTP/WebSocket signaling server for WebRTC.
    /// Serves static web client and handles WebSocket signaling.
    /// </summary>
    public sealed class WebRtcSignalingServer
    {
        public event Action<string> OnLog;
        public event Action<WebSocket, string> OnWebSocketMessage;

        private readonly int _port;
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptTask;
        private WebSocket _activeWebSocket;

        public WebRtcSignalingServer(int port)
        {
            _port = port;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{_port}/");

            try
            {
                _listener.Start();
                Log($"[Signaling] HTTP server started on port {_port}");
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5) // Access denied
            {
                // Try localhost only if not running as admin
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();
                Log($"[Signaling] HTTP server started on localhost:{_port} (run as admin for all interfaces)");
            }

            _acceptTask = Task.Run(() => AcceptLoop(_cts.Token), _cts.Token);
            await Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _activeWebSocket?.Dispose(); } catch { }
            try { if (_acceptTask != null) await Task.WhenAny(_acceptTask, Task.Delay(1000)); } catch { }
            try { _cts?.Dispose(); } catch { }
            Log("[Signaling] Server stopped");
        }

        private async void AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = HandleRequest(context, ct);
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Log($"[Signaling] Accept error: {ex.Message}");
                }
            }
        }

        private async Task HandleRequest(HttpListenerContext context, CancellationToken ct)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                var path = request.Url?.AbsolutePath ?? "/";

                // WebSocket upgrade - accept both /ws and any WebSocket upgrade request
                if (request.IsWebSocketRequest)
                {
                    await HandleWebSocket(context, ct);
                    return;
                }

                // Static file serving
                switch (path)
                {
                    case "/":
                    case "/index.html":
                        ServeContent(response, GetIndexHtml(), "text/html");
                        break;

                    case "/client.js":
                        ServeContent(response, GetClientJs(), "application/javascript");
                        break;

                    case "/ws":
                        // WebSocket request that wasn't upgraded - return error
                        response.StatusCode = 426; // Upgrade Required
                        ServeContent(response, "WebSocket upgrade required", "text/plain");
                        break;

                    default:
                        response.StatusCode = 404;
                        ServeContent(response, "Not Found", "text/plain");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"[Signaling] Request error: {ex.Message}");
                try
                {
                    response.StatusCode = 500;
                    response.Close();
                }
                catch { }
            }
        }

        private async Task HandleWebSocket(HttpListenerContext context, CancellationToken ct)
        {
            WebSocketContext wsContext;
            try
            {
                wsContext = await context.AcceptWebSocketAsync(null);
            }
            catch (Exception ex)
            {
                Log($"[Signaling] WebSocket accept error: {ex.Message}");
                return;
            }

            var ws = wsContext.WebSocket;
            _activeWebSocket = ws;
            Log("[Signaling] WebSocket client connected");

            var buffer = new byte[8192];

            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        OnWebSocketMessage?.Invoke(ws, message);
                    }
                }
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                Log("[Signaling] WebSocket client disconnected");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"[Signaling] WebSocket error: {ex.Message}");
            }
            finally
            {
                if (_activeWebSocket == ws) _activeWebSocket = null;
                try { ws.Dispose(); } catch { }
            }
        }

        private void ServeContent(HttpListenerResponse response, string content, string contentType)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                response.ContentType = contentType + "; charset=utf-8";
                response.ContentLength64 = bytes.Length;
                response.AddHeader("Cache-Control", "no-cache");
                response.AddHeader("Access-Control-Allow-Origin", "*");
                response.OutputStream.Write(bytes, 0, bytes.Length);
                response.Close();
            }
            catch { }
        }

        private void Log(string msg)
        {
            try { OnLog?.Invoke(msg); } catch { }
        }

        private static string GetIndexHtml() => @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0, user-scalable=no"">
    <title>Kinect Voice Client</title>
    <style>
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%);
            color: #fff;
            min-height: 100vh;
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            padding: 20px;
        }
        .container {
            background: rgba(255,255,255,0.1);
            border-radius: 20px;
            padding: 30px;
            text-align: center;
            max-width: 400px;
            width: 100%;
            backdrop-filter: blur(10px);
        }
        h1 { font-size: 24px; margin-bottom: 10px; }
        .subtitle { color: #aaa; margin-bottom: 30px; }
        #joinBtn {
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            border: none;
            color: white;
            padding: 18px 50px;
            font-size: 18px;
            border-radius: 30px;
            cursor: pointer;
            transition: transform 0.2s, box-shadow 0.2s;
            margin-bottom: 20px;
        }
        #joinBtn:hover { transform: scale(1.05); box-shadow: 0 10px 30px rgba(102,126,234,0.4); }
        #joinBtn:active { transform: scale(0.98); }
        #joinBtn:disabled { background: #555; cursor: not-allowed; transform: none; }
        #status {
            background: rgba(0,0,0,0.3);
            border-radius: 10px;
            padding: 15px;
            margin-top: 20px;
            font-size: 14px;
            min-height: 60px;
        }
        .status-dot {
            display: inline-block;
            width: 10px;
            height: 10px;
            border-radius: 50%;
            margin-right: 8px;
            animation: pulse 2s infinite;
        }
        .status-dot.connecting { background: #f39c12; }
        .status-dot.connected { background: #2ecc71; animation: none; }
        .status-dot.error { background: #e74c3c; animation: none; }
        @keyframes pulse {
            0%, 100% { opacity: 1; }
            50% { opacity: 0.5; }
        }
        #rmsBar {
            width: 100%;
            height: 8px;
            background: rgba(255,255,255,0.2);
            border-radius: 4px;
            margin-top: 15px;
            overflow: hidden;
        }
        #rmsFill {
            height: 100%;
            background: linear-gradient(90deg, #2ecc71, #f39c12, #e74c3c);
            width: 0%;
            transition: width 0.1s;
        }
        audio { display: none; }
    </style>
</head>
<body>
    <div class=""container"">
        <h1>?? Kinect Voice</h1>
        <p class=""subtitle"">LAN Audio Bridge</p>
        <button id=""joinBtn"" onclick=""join()"">Join Voice</button>
        <div id=""status"">Tap ""Join Voice"" to connect</div>
        <div id=""rmsBar""><div id=""rmsFill""></div></div>
        <audio id=""remoteAudio"" autoplay></audio>
    </div>
    <script src=""client.js""></script>
</body>
</html>";

        private static string GetClientJs() => @"let pc = null;
let ws = null;
let localStream = null;

const statusEl = document.getElementById('status');
const joinBtn = document.getElementById('joinBtn');
const rmsFill = document.getElementById('rmsFill');
const remoteAudio = document.getElementById('remoteAudio');

function setStatus(msg, state) {
    const dot = state ? `<span class=""status-dot ${state}""></span>` : '';
    statusEl.innerHTML = dot + msg;
}

async function join() {
    joinBtn.disabled = true;
    setStatus('Requesting microphone...', 'connecting');
    
    try {
        // Get microphone - must be from user gesture on iOS
        localStream = await navigator.mediaDevices.getUserMedia({
            audio: {
                echoCancellation: true,
                noiseSuppression: true,
                autoGainControl: true,
                sampleRate: 8000 // Request 8kHz to match PCMU
            },
            video: false
        });
        
        setStatus('Connecting to server...', 'connecting');
        
        // Connect WebSocket - use same host as page
        const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
        const wsUrl = `${protocol}//${location.host}/`;
        console.log('Connecting to WebSocket:', wsUrl);
        ws = new WebSocket(wsUrl);
        
        ws.onopen = async () => {
            console.log('WebSocket connected');
            setStatus('Creating peer connection...', 'connecting');
            await createPeerConnection();
        };
        
        ws.onmessage = async (event) => {
            console.log('WS message:', event.data.substring(0, 100));
            const msg = JSON.parse(event.data);
            
            if (msg.type === 'answer') {
                console.log('Received answer');
                await pc.setRemoteDescription(new RTCSessionDescription(msg));
                setStatus('Connected! ??', 'connected');
                startRmsMonitor();
            } else if (msg.type === 'ice' && msg.candidate) {
                console.log('Received ICE candidate');
                try {
                    await pc.addIceCandidate(new RTCIceCandidate(msg.candidate));
                } catch (e) {
                    console.warn('ICE candidate error:', e);
                }
            }
        };
        
        ws.onerror = (e) => {
            setStatus('WebSocket error', 'error');
            console.error('WS error:', e);
        };
        
        ws.onclose = () => {
            setStatus('Disconnected', 'error');
            cleanup();
        };
        
    } catch (err) {
        setStatus('Error: ' + err.message, 'error');
        console.error(err);
        joinBtn.disabled = false;
    }
}

async function createPeerConnection() {
    pc = new RTCPeerConnection({
        iceServers: [] // LAN only - no STUN/TURN
    });
    
    // Add local audio track
    localStream.getTracks().forEach(track => {
        console.log('Adding track:', track.kind, track.label);
        pc.addTrack(track, localStream);
    });
    
    // Handle incoming audio (TTS from WPF)
    pc.ontrack = (event) => {
        console.log('Remote track received:', event.track.kind);
        remoteAudio.srcObject = event.streams[0];
        // iOS needs user interaction to play - we're in gesture context
        remoteAudio.play().catch(e => console.warn('Audio play error:', e));
    };
    
    pc.onicecandidate = (event) => {
        if (event.candidate && ws.readyState === WebSocket.OPEN) {
            console.log('Sending ICE candidate');
            ws.send(JSON.stringify({
                type: 'ice',
                candidate: event.candidate
            }));
        }
    };
    
    pc.oniceconnectionstatechange = () => {
        console.log('ICE connection state:', pc.iceConnectionState);
    };
    
    pc.onconnectionstatechange = () => {
        console.log('Connection state:', pc.connectionState);
        if (pc.connectionState === 'connected') {
            setStatus('Connected! ??', 'connected');
        } else if (pc.connectionState === 'disconnected' || pc.connectionState === 'failed') {
            setStatus('Connection lost', 'error');
        }
    };
    
    // Create offer with specific codec preferences
    const offer = await pc.createOffer({
        offerToReceiveAudio: true,
        offerToReceiveVideo: false
    });
    
    // Modify SDP to prefer PCMU (payload type 0) over other codecs
    let sdp = offer.sdp;
    
    // Find the audio m= line and reorder codecs to put PCMU first
    const lines = sdp.split('\r\n');
    for (let i = 0; i < lines.length; i++) {
        if (lines[i].startsWith('m=audio')) {
            // Parse existing codec list
            const parts = lines[i].split(' ');
            // parts[3+] are payload types
            // Move 0 (PCMU) to first position if present
            const payloadTypes = parts.slice(3);
            const pcmuIdx = payloadTypes.indexOf('0');
            if (pcmuIdx > 0) {
                payloadTypes.splice(pcmuIdx, 1);
                payloadTypes.unshift('0');
                lines[i] = parts.slice(0, 3).concat(payloadTypes).join(' ');
            }
            break;
        }
    }
    sdp = lines.join('\r\n');
    
    await pc.setLocalDescription({type: 'offer', sdp: sdp});
    
    console.log('Sending offer');
    ws.send(JSON.stringify({
        type: 'offer',
        sdp: sdp
    }));
}

function startRmsMonitor() {
    if (!localStream) return;
    
    const audioCtx = new (window.AudioContext || window.webkitAudioContext)();
    const source = audioCtx.createMediaStreamSource(localStream);
    const analyser = audioCtx.createAnalyser();
    analyser.fftSize = 256;
    source.connect(analyser);
    
    const dataArray = new Uint8Array(analyser.frequencyBinCount);
    
    function update() {
        if (!localStream) return;
        analyser.getByteFrequencyData(dataArray);
        const sum = dataArray.reduce((a, b) => a + b, 0);
        const avg = sum / dataArray.length;
        const pct = Math.min(100, (avg / 128) * 100);
        rmsFill.style.width = pct + '%';
        requestAnimationFrame(update);
    }
    update();
}

function cleanup() {
    joinBtn.disabled = false;
    if (localStream) {
        localStream.getTracks().forEach(t => t.stop());
        localStream = null;
    }
    if (pc) {
        pc.close();
        pc = null;
    }
}

// Handle page visibility for iOS
document.addEventListener('visibilitychange', () => {
    if (document.hidden && pc) {
        // Don't disconnect on tab hide - iOS needs this
    }
});
";
    }
}
