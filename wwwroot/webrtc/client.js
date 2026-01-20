// Voice AI - WebRTC Client
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
const slider = document.getElementById('slider');

// Status
function setStatus(text, state) {
    statusText.textContent = text;
    dot.className = 'dot' + (state ? ' ' + state : '');
}

// Mode UI - 3-position switch
function updateMode(m) {
    mode = m;
    
    // Update slider position
    slider.className = 'switch-slider' + (m === 1 ? ' pos-1' : m === 2 ? ' pos-2' : '');
    
    // Update active option
    document.querySelectorAll('.switch-option').forEach(opt => {
        opt.classList.toggle('active', parseInt(opt.dataset.mode) === m);
    });
    
    // Update level bar color
    const levelClasses = ['mic', 'discord', 'webrtc'];
    level.className = 'level-fill ' + levelClasses[m];
    
    // Voice button only enabled in WebRTC mode
    voiceBtn.disabled = m !== 2;
    if (m !== 2 && voiceOn) stopVoice();
}

// Set mode on server
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

// Add message
function addMsg(text, type) {
    if (empty) empty.classList.add('hidden');
    const div = document.createElement('div');
    div.className = 'msg ' + type;
    div.textContent = text;
    chat.appendChild(div);
    chat.scrollTop = chat.scrollHeight;
    return div;
}

// Typing indicator
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

// Streaming response
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

// Send text
function send() {
    const text = input.value.trim();
    if (!text) return;
    input.value = '';
    addMsg(text, 'user');
    if (ws?.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({ type: 'text', text }));
    }
}

// Voice toggle
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

// Audio level
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

// WebRTC
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
    // Prefer PCMU
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

// WebSocket
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

// Input enter
input.addEventListener('keydown', e => {
    if (e.key === 'Enter') { e.preventDefault(); send(); }
});

// Init
setStatus('Connecting...', 'warn');
connect();
