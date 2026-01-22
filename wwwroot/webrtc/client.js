// Voice AI - WebRTC Client (iOS Safari Compatible)

var MODE_MIC = 0;
var MODE_DISCORD = 1;
var MODE_WEBRTC = 3;

var ws = null;
var stream = null;
var audioCtx = null;
var mode = MODE_MIC;
var voiceOn = false;
var pendingText = null;

var captureProcessor = null;
var captureSource = null;
var captureGain = null;

// TTS
var ttsAudioEl = null;
var audioUnlocked = false;
var ttsSpeaking = false;
var ttsEndTime = 0;
var ttsLastAudioAt = 0;
// Increased suppression to reduce self-hearing on speakerphone / iOS.
// Keep this small so barge-in remains responsive.
var TTS_MIC_SUPPRESS_MS = 200;

// During TTS playback, do NOT fully suppress mic (would break barge-in).
var BARGE_IN_TTS_LEAK_GATE_ENABLED = false;

// Adaptive gate: track ambient RMS when TTS is not playing, then gate only frames far above that.
// (Disabled when BARGE_IN_TTS_LEAK_GATE_ENABLED is false.)
var TTS_LEAK_RMS_GATE = 0.22; // absolute fallback
var _ambientRms = 0.02;
var _ambientRmsLastAt = 0;
var AMBIENT_RMS_UPDATE_MS = 120;
var AMBIENT_RMS_ALPHA = 0.08;
var TTS_LEAK_MULTIPLIER = 3.5;
var TTS_LEAK_GATE_MAX = 0.32;

// Barge-in support
var bargeInEnabled = true; // updated from server status

// Keepalive interval to prevent audio context suspension
var keepaliveInterval = null;

// Track pending TTS chunks while waiting for unlock
var pendingTtsChunks = [];
var MAX_PENDING_CHUNKS = 50;

// iOS Safari detection
var isIOS = /iPad|iPhone|iPod/.test(navigator.userAgent) ||
    (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
var isSafari = /^((?!chrome|android).)*safari/i.test(navigator.userAgent);
var needsIOSWorkaround = isIOS || (isSafari && navigator.maxTouchPoints > 0);

var chat, empty, input, level, dot, statusText, micBtn, thumb, sendBtn;

function estimateRmsFloat(buf) {
    if (!buf || buf.length <= 0) return 0;
    var sum = 0;
    for (var i = 0; i < buf.length; i++) {
        var v = buf[i];
        sum += v * v;
    }
    return Math.sqrt(sum / buf.length);
}

function shouldSuppressMic() {
    if (!bargeInEnabled) return false;
    if (!ttsLastAudioAt) return false;
    return (Date.now() - ttsLastAudioAt) < TTS_MIC_SUPPRESS_MS;
}

function setStatus(text, state) {
    if (statusText) statusText.textContent = text;
    if (dot) dot.className = 'dot' + (state ? ' ' + state : '');
}

function modeToPos(m) {
    if (m === MODE_MIC) return '';
    if (m === MODE_DISCORD) return 'p1';
    if (m === MODE_WEBRTC) return 'p2';
    return '';
}

function updateMode(m) {
    mode = m;
    if (thumb) thumb.className = 'switch-thumb ' + modeToPos(m);
    document.querySelectorAll('.switch-opt').forEach(function (opt) {
        opt.classList.toggle('active', parseInt(opt.dataset.mode, 10) === m);
    });
    if (level) level.className = 'level-bar ' + (m === MODE_MIC ? 'mic' : m === MODE_DISCORD ? 'discord' : 'webrtc');

    if (micBtn) micBtn.disabled = (m !== MODE_WEBRTC);
    if (m !== MODE_WEBRTC && voiceOn) stopVoice();
}

function setMode(m) {
    fetch('/api/mode', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ mode: m })
    }).then(function (res) { if (res.ok) updateMode(m); });
}

function addMsg(text, type) {
    if (empty) empty.classList.add('hidden');
    var div = document.createElement('div');
    div.className = 'msg ' + type;
    div.textContent = text;
    chat.appendChild(div);
    chat.scrollTop = chat.scrollHeight;
    return div;
}

var typingEl = null;
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

var streamEl = null;
function appendResponse(text) {
    hideTyping();
    if (!streamEl) streamEl = addMsg('', 'ai');
    streamEl.textContent = text;
    chat.scrollTop = chat.scrollHeight;
}
function finalizeResponse() { streamEl = null; }

// iOS Safari audio unlock must happen on user gesture
function unlockAudio() {
    if (audioUnlocked) return true;

    try {
        if (needsIOSWorkaround && ttsAudioEl) {
            ttsAudioEl.setAttribute('playsinline', '');
            ttsAudioEl.setAttribute('webkit-playsinline', '');
            ttsAudioEl.muted = false;
            ttsAudioEl.volume = 1.0;

            // Try to "activate" media playback pipeline.
            var p = ttsAudioEl.play();
            if (p && p.catch) p.catch(function () { });
        }

        audioUnlocked = true;
        return true;
    } catch (e) {
        console.error('[Audio] Unlock error:', e);
        return false;
    }
}

// --- TTS playback via <audio> element (WAV Blob) ---
// NOTE: This logic intentionally plays audio in larger segments to avoid choppy playback on iOS.
var ttsPcmChunks = [];
var ttsPcmTotalBytes = 0;
var ttsSampleRate = 24000;
var ttsPlayInProgress = false;
var ttsFlushTimer = null;
var ttsChunkCount = 0;
var ttsUtteranceId = 0;
var ttsLastChunkAt = 0;

// Tuning
var TTS_UTTERANCE_GAP_MS = 1200;   // treat >1.2s gap as new utterance
var TTS_FINALIZE_GAP_MS = 450;     // wait a bit longer before forcing tail flush (prevents early cut/blips)
var TTS_MIN_START_BYTES = 36000;   // ~0.75s @ 24kHz mono 16-bit (more prebuffer for first words)
var TTS_MIN_SEGMENT_BYTES = 24000; // keep segments >= ~0.5s to avoid choppy sentence boundaries
var TTS_FADE_MS = 8;              // small fade to hide boundary clicks/gaps
var ttsStartedThisUtterance = false;

function base64ToUint8Array(b64) {
    var raw = atob(b64);
    var arr = new Uint8Array(raw.length);
    for (var i = 0; i < raw.length; i++) arr[i] = raw.charCodeAt(i);
    return arr;
}

function writeAscii(view, offset, str) {
    for (var i = 0; i < str.length; i++) view.setUint8(offset + i, str.charCodeAt(i));
}

function makeWavFromPcm16(pcmBytes, sampleRate) {
    var numChannels = 1;
    var bitsPerSample = 16;
    var byteRate = sampleRate * numChannels * (bitsPerSample / 8);
    var blockAlign = numChannels * (bitsPerSample / 8);

    var buffer = new ArrayBuffer(44 + pcmBytes.length);
    var view = new DataView(buffer);

    writeAscii(view, 0, 'RIFF');
    view.setUint32(4, 36 + pcmBytes.length, true);
    writeAscii(view, 8, 'WAVE');

    writeAscii(view, 12, 'fmt ');
    view.setUint32(16, 16, true);
    view.setUint16(20, 1, true);
    view.setUint16(22, numChannels, true);
    view.setUint32(24, sampleRate, true);
    view.setUint32(28, byteRate, true);
    view.setUint16(32, blockAlign, true);
    view.setUint16(34, bitsPerSample, true);

    writeAscii(view, 36, 'data');
    view.setUint32(40, pcmBytes.length, true);

    new Uint8Array(buffer, 44).set(pcmBytes);
    return buffer;
}

function resetTtsBuffer() {
    ttsPcmChunks = [];
    ttsPcmTotalBytes = 0;
    ttsSampleRate = 24000;
}

function startNewUtterance(nowMs) {
    ttsUtteranceId++;
    ttsLastChunkAt = nowMs;
    ttsStartedThisUtterance = false;
    ttsPlayInProgress = false;
    resetTtsBuffer();
}

function enqueueTtsChunk(base64Data, srcRate) {
    if (!base64Data) return;

    var nowMs = Date.now();

    if (!ttsLastChunkAt || (nowMs - ttsLastChunkAt) > TTS_UTTERANCE_GAP_MS) {
        startNewUtterance(nowMs);
    }

    ttsLastChunkAt = nowMs;
    ttsSampleRate = srcRate || ttsSampleRate || 24000;

    var bytes = base64ToUint8Array(base64Data);
    ttsPcmChunks.push(bytes);
    ttsPcmTotalBytes += bytes.length;

    ttsLastAudioAt = nowMs;
    ttsSpeaking = true;
    ttsEndTime = 0;
}

function applyFadeInOutPcm16LE(pcmBytes, sampleRate, fadeMs) {
    if (!pcmBytes || pcmBytes.length < 4) return;
    var fadeSamples = Math.floor((sampleRate * (fadeMs / 1000)));
    if (fadeSamples <= 0) return;

    var sampleCount = Math.floor(pcmBytes.length / 2);
    if (fadeSamples * 2 > sampleCount) fadeSamples = Math.floor(sampleCount / 2);
    if (fadeSamples <= 0) return;

    var dv = new DataView(pcmBytes.buffer, pcmBytes.byteOffset, pcmBytes.byteLength);

    for (var i = 0; i < fadeSamples; i++) {
        var gIn = i / fadeSamples;
        var gOut = (fadeSamples - 1 - i) / fadeSamples;

        var sIn = dv.getInt16(i * 2, true);
        dv.setInt16(i * 2, Math.max(-32768, Math.min(32767, Math.round(sIn * gIn))), true);

        var outIdx = sampleCount - 1 - i;
        var sOut = dv.getInt16(outIdx * 2, true);
        dv.setInt16(outIdx * 2, Math.max(-32768, Math.min(32767, Math.round(sOut * gOut))), true);
    }
}

function flushTtsBufferToAudioEl(force) {
    if (!ttsAudioEl) return;
    if (ttsPcmTotalBytes <= 0) return;
    if (ttsPlayInProgress) return;

    // More conservative start buffering for the first segment of an utterance
    if (!ttsStartedThisUtterance) {
        if (!force && ttsPcmTotalBytes < TTS_MIN_START_BYTES) return;
    } else {
        if (!force && ttsPcmTotalBytes < TTS_MIN_SEGMENT_BYTES) return;
    }

    var pcm = new Uint8Array(ttsPcmTotalBytes);
    var off = 0;
    for (var i = 0; i < ttsPcmChunks.length; i++) {
        pcm.set(ttsPcmChunks[i], off);
        off += ttsPcmChunks[i].length;
    }

    // Apply a tiny fade to reduce audible discontinuities at segment boundaries
    applyFadeInOutPcm16LE(pcm, ttsSampleRate, TTS_FADE_MS);

    var wavBuf = makeWavFromPcm16(pcm, ttsSampleRate);
    var blob = new Blob([wavBuf], { type: 'audio/wav' });
    var url = URL.createObjectURL(blob);

    try { if (ttsAudioEl.src) URL.revokeObjectURL(ttsAudioEl.src); } catch (e) { }
    try { ttsAudioEl.srcObject = null; } catch (e) { }

    ttsAudioEl.src = url;

    try {
        ttsAudioEl.muted = false;
        ttsAudioEl.volume = 1.0;
        ttsAudioEl.setAttribute('playsinline', '');
        ttsAudioEl.setAttribute('webkit-playsinline', '');
    } catch (e) { }

    ttsPlayInProgress = true;
    ttsStartedThisUtterance = true;

    // Treat as "speaking" during playback so mic capture can suppress reliably.
    ttsSpeaking = true;
    ttsLastAudioAt = Date.now();

    // Clear buffer now so any new chunks become the next segment.
    resetTtsBuffer();

    var p = ttsAudioEl.play();
    if (p && p.catch) p.catch(function (err) {
        console.error('[TTS] audioEl.play failed:', err);
        ttsPlayInProgress = false;
    });

    ttsAudioEl.onended = function () {
        ttsPlayInProgress = false;
        ttsEndTime = Date.now();
        ttsLastAudioAt = ttsEndTime;

        // If we accumulated enough for another segment while playing, start it.
        flushTtsBufferToAudioEl(false);

        // If nothing buffered and we've been idle, mark not speaking.
        if (ttsPcmTotalBytes === 0 && (Date.now() - ttsLastChunkAt) > TTS_FINALIZE_GAP_MS) {
            // Delay clearing speaking a bit to hide iOS tail blips / decoding artifacts.
            setTimeout(function () {
                if (ttsPcmTotalBytes === 0 && !ttsPlayInProgress) {
                    ttsSpeaking = false;
                }
            }, Math.max(0, TTS_MIC_SUPPRESS_MS - 200));
        }
    };
}

function playTtsAudio(base64Data, srcRate) {
    srcRate = srcRate || 24000;

    ttsChunkCount++;
    if (ws && ws.readyState === 1 && (ttsChunkCount % 50 === 0)) {
        try { ws.send(JSON.stringify({ type: 'diag', kind: 'tts_chunk', count: ttsChunkCount })); } catch (e) { }
    }

    if (!audioUnlocked) {
        if (pendingTtsChunks.length === 0) addMsg('⚠️ Tap screen to enable audio playback', 'ai');
        if (pendingTtsChunks.length < MAX_PENDING_CHUNKS) pendingTtsChunks.push({ data: base64Data, rate: srcRate });
        unlockAudio();
        return;
    }

    enqueueTtsChunk(base64Data, srcRate);

    if (!ttsFlushTimer) {
        ttsFlushTimer = setInterval(function () {
            if (!ttsSpeaking) {
                clearInterval(ttsFlushTimer);
                ttsFlushTimer = null;
                return;
            }

            var idleMs = Date.now() - ttsLastChunkAt;

            // If we have buffered audio and chunks stopped briefly, force-flush the tail once.
            // But avoid forcing too early for the first segment.
            if (!ttsPlayInProgress && ttsPcmTotalBytes > 0 && idleMs >= TTS_FINALIZE_GAP_MS) {
                flushTtsBufferToAudioEl(true);
                return;
            }

            flushTtsBufferToAudioEl(false);
        }, 80);
    }

    flushTtsBufferToAudioEl(false);
}

function stopTtsPlayback() {
    try {
        if (ttsAudioEl) {
            try { ttsAudioEl.pause(); } catch (e) { }
            try { ttsAudioEl.currentTime = 0; } catch (e) { }
        }
    } catch (e) { }

    if (ttsFlushTimer) {
        clearInterval(ttsFlushTimer);
        ttsFlushTimer = null;
    }

    resetTtsBuffer();
    ttsPlayInProgress = false;
    ttsSpeaking = false;
    ttsEndTime = Date.now();
    ttsLastAudioAt = 0;
    ttsLastChunkAt = 0;
    ttsStartedThisUtterance = false;
    pendingTtsChunks = [];
}

function cleanupCapture() {
    if (keepaliveInterval) {
        clearInterval(keepaliveInterval);
        keepaliveInterval = null;
    }
    if (captureProcessor) {
        try {
            captureProcessor.disconnect();
            captureProcessor.onaudioprocess = null;
        } catch (e) { }
        captureProcessor = null;
    }
    if (captureSource) {
        try { captureSource.disconnect(); } catch (e) { }
        captureSource = null;
    }
    if (captureGain) {
        try { captureGain.disconnect(); } catch (e) { }
        captureGain = null;
    }
    if (audioCtx) {
        try { audioCtx.close(); } catch (e) { }
        audioCtx = null;
    }
    if (stream) {
        stream.getTracks().forEach(function (t) { t.stop(); });
        stream = null;
    }
}

function stopVoice() {
    voiceOn = false;
    if (micBtn) micBtn.classList.remove('on');
    if (level) level.style.width = '0%';
    cleanupCapture();
    addMsg('🔇 Microphone stopped', 'ai');
}

function resampleTo16k(inputData, inputSampleRate) {
    if (inputSampleRate === 16000) return inputData;
    var ratio = inputSampleRate / 16000;
    var newLength = Math.round(inputData.length / ratio);
    var result = new Float32Array(newLength);
    for (var i = 0; i < newLength; i++) {
        var srcIndex = i * ratio;
        var idx0 = Math.floor(srcIndex);
        var idx1 = Math.min(idx0 + 1, inputData.length - 1);
        var frac = srcIndex - idx0;
        result[i] = inputData[idx0] * (1 - frac) + inputData[idx1] * frac;
    }
    return result;
}

// --- WebRTC mic pre-roll to avoid clipped word starts ---
var PREROLL_MS = 250;
var prerollPcmBytes = [];
var prerollTotalBytes = 0;

// Disable client-side preroll merging by default for iPhone speakerphone.
// The server already does its own frame handling; merging here can duplicate audio and
// degrade STT (echo-like smearing) on iOS.
var PREROLL_ENABLED = false;

function resetPreroll() {
    prerollPcmBytes = [];
    prerollTotalBytes = 0;
}

function pushPreroll(pcmBytes, maxBytes) {
    if (!PREROLL_ENABLED) return;
    if (!pcmBytes || pcmBytes.length <= 0) return;
    prerollPcmBytes.push(pcmBytes);
    prerollTotalBytes += pcmBytes.length;
    while (prerollTotalBytes > maxBytes && prerollPcmBytes.length) {
        var first = prerollPcmBytes.shift();
        prerollTotalBytes -= first.length;
    }
}

function concatPrerollWithCurrent(currentBytes) {
    if (!PREROLL_ENABLED) return currentBytes;
    if (!currentBytes || currentBytes.length <= 0) return currentBytes;
    if (!prerollPcmBytes.length) return currentBytes;

    var out = new Uint8Array(prerollTotalBytes + currentBytes.length);
    var off = 0;
    for (var i = 0; i < prerollPcmBytes.length; i++) {
        out.set(prerollPcmBytes[i], off);
        off += prerollPcmBytes[i].length;
    }
    out.set(currentBytes, off);
    return out;
}

function startCapture() {
    if (!stream) return;
    var AC = window.AudioContext || window.webkitAudioContext;
    try { audioCtx = new AC(); } catch (e) { addMsg('AudioContext error: ' + e.message, 'ai'); return; }

    resetPreroll();

    var actualSampleRate = audioCtx.sampleRate;
    captureSource = audioCtx.createMediaStreamSource(stream);

    // Avoid routing mic capture to speakers.
    captureGain = audioCtx.createGain();
    captureGain.gain.value = 0;

    // Lower buffer size reduces latency (helps word onsets)
    var bufferSize = 2048;
    captureProcessor = audioCtx.createScriptProcessor(bufferSize, 1, 1);

    captureProcessor.onaudioprocess = function (e) {
        if (!voiceOn) return;
        if (!ws || ws.readyState !== 1) return;
        if (shouldSuppressMic()) return;

        var inputData = e.inputBuffer.getChannelData(0);

        // Update ambient RMS estimate (best-effort, used only if gating is enabled).
        var nowMs = Date.now();
        if (!_ambientRmsLastAt || (nowMs - _ambientRmsLastAt) >= AMBIENT_RMS_UPDATE_MS) {
            var a = estimateRmsFloat(inputData);
            _ambientRms = (1.0 - AMBIENT_RMS_ALPHA) * _ambientRms + AMBIENT_RMS_ALPHA * a;
            _ambientRmsLastAt = nowMs;
        }

        // Optional: TTS leakage gate (disabled by default because it can interfere with barge-in).
        if (BARGE_IN_TTS_LEAK_GATE_ENABLED && ttsSpeaking) {
            var rms = estimateRmsFloat(inputData);
            var thr = Math.min(TTS_LEAK_GATE_MAX, Math.Max(TTS_LEAK_RMS_GATE, _ambientRms * TTS_LEAK_MULTIPLIER));
            if (rms >= thr) return;
        }

        // Convert to PCM16 at capture/device rate.
        // Let the server do resampling + ASR-friendly preprocessing.
        var pcm = new Int16Array(inputData.length);
        for (var i = 0; i < inputData.length; i++) {
            var s = Math.max(-1, Math.min(1, inputData[i]));
            pcm[i] = s < 0 ? s * 0x8000 : s * 0x7FFF;
        }

        var bytes = new Uint8Array(pcm.buffer);

        // Keep ~PREROLL_MS of prior audio to avoid clipped word starts.
        var prerollMaxBytes = Math.floor((actualSampleRate * (PREROLL_MS / 1000)) * 2);
        var combined = concatPrerollWithCurrent(bytes);
        pushPreroll(bytes.slice ? bytes.slice(0) : new Uint8Array(bytes), prerollMaxBytes);

        // Base64 encode (combined)
        var bin = '';
        for (var j = 0; j < combined.length; j++) bin += String.fromCharCode(combined[j]);

        try {
            ws.send(JSON.stringify({
                type: 'audio',
                data: btoa(bin),
                sampleRate: actualSampleRate,
                cap: {
                    echoCancellation: CAPTURE_ECHO_CANCELLATION,
                    noiseSuppression: CAPTURE_NOISE_SUPPRESSION,
                    autoGainControl: CAPTURE_AUTO_GAIN
                },
                prerollMs: PREROLL_MS
            }));
        } catch (err) {
            console.error('[Audio] Send error:', err);
        }
    };

    captureSource.connect(captureProcessor);
    captureProcessor.connect(captureGain);

    // Keep graph alive without audible output.
    var silentSink = audioCtx.createMediaStreamDestination();
    captureGain.connect(silentSink);
}

function startLevel() {
    if (!stream) return;
    var AC = window.AudioContext || window.webkitAudioContext;
    var ctx;
    try { ctx = new AC(); } catch (e) { return; }
    var src = ctx.createMediaStreamSource(stream);
    var an = ctx.createAnalyser();
    an.fftSize = 256;
    src.connect(an);
    var arr = new Uint8Array(an.frequencyBinCount);
    function tick() {
        if (!voiceOn) { try { ctx.close(); } catch (e) { } return; }
        an.getByteFrequencyData(arr);
        var sum = 0; for (var i = 0; i < arr.length; i++) sum += arr[i];
        var avg = sum / arr.length;
        if (level) level.style.width = Math.min(100, (avg / 128) * 100) + '%';
        requestAnimationFrame(tick);
    }
    tick();
}

var statusPollTimer = null;

function startStatusPoll() {
    if (statusPollTimer) return;
    statusPollTimer = setInterval(function () {
        fetch('/api/status', { cache: 'no-store' })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (j) {
                if (!j) return;
                if (j.mode !== undefined) updateMode(j.mode);
                if (j.bargeInEnabled !== undefined) bargeInEnabled = j.bargeInEnabled;
            })
            .catch(function () { });
    }, 1000);
}

function stopStatusPoll() {
    if (!statusPollTimer) return;
    clearInterval(statusPollTimer);
    statusPollTimer = null;
}

function connect() {
    var proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    ws = new WebSocket(proto + '//' + location.host + '/ws?ts=' + Date.now());

    startStatusPoll();

    ws.onopen = function () {
        setStatus('Connected', 'on');
        stopStatusPoll();

        // If we have pending TTS chunks from before unlock, try to play them once connected.
        if (pendingTtsChunks.length && audioUnlocked) {
            try {
                pendingTtsChunks.forEach(function (c) { enqueueTtsChunk(c.data, c.rate); });
                pendingTtsChunks = [];
                flushTtsBufferToAudioEl(true);
            } catch (e) { }
        }
    };

    ws.onmessage = function (e) {
        var msg;
        try { msg = JSON.parse(e.data); } catch (err) { return; }

        if (msg.type === 'status') {
            if (msg.mode !== undefined) updateMode(msg.mode);
            if (msg.bargeInEnabled !== undefined) bargeInEnabled = msg.bargeInEnabled;
        } else if (msg.type === 'transcription') {
            if (pendingText && msg.text === pendingText) {
                pendingText = null;
            } else {
                addMsg(msg.text, 'user');
                showTyping();
            }
        } else if (msg.type === 'response_chunk') {
            appendResponse(msg.text);
        } else if (msg.type === 'response') {
            hideTyping();
            if (!streamEl) addMsg(msg.text, 'ai');
            finalizeResponse();
        } else if (msg.type === 'tts_audio' && msg.data) {
            playTtsAudio(msg.data, msg.sampleRate || 24000);
        } else if (msg.type === 'tts_stop') {
            stopTtsPlayback();
        }
    };

    ws.onclose = function () {
        setStatus('Disconnected', '');
        // keep status polling so app->web mode still updates
        startStatusPoll();
        setTimeout(connect, 2000);
    };

    ws.onerror = function () {
        setStatus('Error', '');
        // keep status polling so app->web mode still updates
        startStatusPoll();
    };
}

function send() {
    var text = input && input.value ? input.value.trim() : '';
    if (!text) return;
    unlockAudio();
    if (input) input.value = '';
    addMsg(text, 'user');
    pendingText = text;
    setTimeout(function () { if (pendingText === text) pendingText = null; }, 2000);
    if (ws && ws.readyState === 1) {
        ws.send(JSON.stringify({ type: 'text', text: text }));
        showTyping();
    }
}

function onMicClick() {
    if (mode !== MODE_WEBRTC) {
        addMsg('Please select WebRTC mode first', 'ai');
        return;
    }

    unlockAudio();

    if (voiceOn) stopVoice();
    else startVoice();
}

function startVoice() {
    cleanupCapture();

    if (!ws || ws.readyState !== 1) {
        addMsg('⚠️ Not connected. Please wait for Connected status, then try mic again.', 'ai');
        return;
    }

    addMsg('Requesting microphone access...', 'ai');

    var done = false;
    var timeoutId = setTimeout(function () {
        if (done) return;
        // iOS Safari can hang when advanced constraints are present; retry with plain audio.
        try {
            navigator.mediaDevices.getUserMedia({ audio: true, video: false }).then(function (s2) {
                if (done) { try { s2.getTracks().forEach(function (t) { t.stop(); }); } catch (e) { } return; }
                done = true;
                stream = s2;
                voiceOn = true;
                if (micBtn) micBtn.classList.add('on');
                addMsg('✅ Microphone active - speak now!', 'ai');
                startCapture();
                startLevel();
                startKeepalive();
            }).catch(function () {
                addMsg('⏱️ Mic request timed out. Try: Settings > Safari > Camera & Microphone > Allow', 'ai');
            });
        } catch (e) {
            addMsg('⏱️ Mic request timed out. Try: Settings > Safari > Camera & Microphone > Allow', 'ai');
        }
    }, 5000);

    var constraints = {
        audio: {
            echoCancellation: CAPTURE_ECHO_CANCELLATION,
            noiseSuppression: CAPTURE_NOISE_SUPPRESSION,
            autoGainControl: CAPTURE_AUTO_GAIN
        },
        video: false
    };

    var promise;
    try {
        promise = navigator.mediaDevices.getUserMedia(constraints);
    } catch (e) {
        clearTimeout(timeoutId);
        addMsg('❌ getUserMedia not available: ' + e.message, 'ai');
        return;
    }

    promise.then(function (s) {
        if (done) { try { s.getTracks().forEach(function (t) { t.stop(); }); } catch (e) { } return; }
        done = true;
        clearTimeout(timeoutId);
        stream = s;
        voiceOn = true;
        if (micBtn) micBtn.classList.add('on');
        addMsg('✅ Microphone active - speak now!', 'ai');
        startCapture();
        startLevel();
        startKeepalive();
    }).catch(function (e) {
        done = true;
        clearTimeout(timeoutId);
        var msg = e.message || e.name || 'Unknown error';

        if (e.name === 'NotAllowedError' || e.name === 'PermissionDeniedError') {
            addMsg('❌ Microphone permission denied. Go to Settings > Safari > Camera & Microphone Access and enable it for this site.', 'ai');
        } else if (e.name === 'NotFoundError') {
            addMsg('❌ No microphone found on this device.', 'ai');
        } else if (e.name === 'NotReadableError' || e.name === 'AbortError') {
            addMsg('❌ Microphone is busy or not readable. Close other apps using mic.', 'ai');
        } else if (e.name === 'SecurityError') {
            addMsg('❌ Security error - microphone requires HTTPS on this browser. Try enabling HTTPS in settings.', 'ai');
        } else {
            addMsg('❌ Mic error: ' + msg, 'ai');
        }
    });
}

function startKeepalive() {
    if (keepaliveInterval) clearInterval(keepaliveInterval);

    keepaliveInterval = setInterval(function () {
        if (!voiceOn) {
            clearInterval(keepaliveInterval);
            keepaliveInterval = null;
            return;
        }

        if (audioCtx && audioCtx.state === 'suspended') {
            audioCtx.resume();
        }

        if (stream) {
            var tracks = stream.getAudioTracks();
            if (tracks.length === 0 || tracks[0].readyState === 'ended') {
                addMsg('⚠️ Microphone stream ended. Click mic to restart.', 'ai');
                stopVoice();
            }
        }
    }, 5000);
}

// Capture constraint defaults (avoid undefined globals)
var CAPTURE_ECHO_CANCELLATION = true;
var CAPTURE_NOISE_SUPPRESSION = true;
var CAPTURE_AUTO_GAIN = true;

// Allow querystring override: ?raw=1 disables the processing constraints
try {
    if (location && location.search && location.search.indexOf('raw=1') >= 0) {
        CAPTURE_ECHO_CANCELLATION = false;
        CAPTURE_NOISE_SUPPRESSION = false;
        CAPTURE_AUTO_GAIN = false;
    }
} catch (e) { }

function init() {
    chat = document.getElementById('chat');
    empty = document.getElementById('empty');
    input = document.getElementById('input');
    level = document.getElementById('level');
    dot = document.getElementById('dot');
    statusText = document.getElementById('statusText');
    micBtn = document.getElementById('micBtn');
    thumb = document.getElementById('thumb');
    sendBtn = document.getElementById('sendBtn');
    ttsAudioEl = document.getElementById('ttsAudio');

    if (sendBtn) {
        sendBtn.ontouchend = function (e) { e.preventDefault(); send(); };
        sendBtn.onclick = function (e) { e.preventDefault(); send(); };
    }

    if (micBtn) {
        micBtn.ontouchend = function (e) {
            e.preventDefault();
            e.stopPropagation();
            onMicClick();
        };
        micBtn.onclick = function (e) {
            e.preventDefault();
            e.stopPropagation();
            onMicClick();
        };
    }

    document.querySelectorAll('.switch-opt').forEach(function (opt) {
        opt.onclick = function () {
            unlockAudio();
            setMode(parseInt(this.dataset.mode, 10));
        };
    });

    if (input) {
        input.onkeydown = function (e) {
            if (e.key === 'Enter') { e.preventDefault(); send(); }
        };
    }

    function handleUserGesture() {
        if (!audioUnlocked) unlockAudio();
    }

    document.addEventListener('touchstart', handleUserGesture, { passive: true, capture: true });
    document.addEventListener('touchend', handleUserGesture, { passive: true, capture: true });
    document.addEventListener('click', handleUserGesture, { passive: true, capture: true });

    setStatus('Connecting...', 'warn');
    connect();
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
} else {
    init();
}
