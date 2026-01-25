// Voice AI - WebRTC Client (iOS Safari Compatible)

var MODE_MIC = 0;
var MODE_DISCORD = 1;
var MODE_WEBRTC = 2;  // Must match C# AudioInMode.WebRtcVoice enum value

var ws = null;
var stream = null;
var audioCtx = null;
var mode = MODE_MIC;
var voiceOn = false;
var pendingText = null;

// Transcription mode
var transcriptionMode = false;

var captureProcessor = null;
var captureSource = null;
var captureGain = null;

// Input gain boost for low volume capture (adjustable)
// Values > 1.0 amplify the signal before sending to server
var CAPTURE_INPUT_GAIN = 2.0; // 2x boost for better STT recognition

// TTS
var ttsAudioEl = null;
var audioUnlocked = false;
var ttsSpeaking = false;
var ttsEndTime = 0;
var ttsLastAudioAt = 0;
// Increased suppression to reduce self-hearing on speakerphone / iOS.
// Keep this small so barge-in remains responsive.
var TTS_MIC_SUPPRESS_MS = 800;

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

// WebSocket reconnection state
var wsReconnectAttempt = 0;
var wsReconnectTimer = null;
var wsLastPongTime = 0;
var wsPingInterval = null;
var wsConnectionId = 0; // Track connection instance to prevent stale handlers
var wsConnectStartTime = 0; // Track when connection attempt started

// Reconnection timing - more aggressive for iOS
var WS_RECONNECT_BASE_MS = 500;     // Start faster
var WS_RECONNECT_MAX_MS = 5000;     // Cap lower
var WS_PING_INTERVAL_MS = 10000;    // Ping more frequently
var WS_PONG_TIMEOUT_MS = 3000;      // Shorter pong timeout
var WS_CONNECT_TIMEOUT_MS = 8000;   // Shorter connect timeout for stale detection
var WS_OPEN_CONFIRM_MS = 2000;      // Time to wait for first message after open

var chat, empty, input, level, dot, statusText, micBtn, thumb, sendBtn, transcribeBtn, transcriptionBanner;

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
    console.log('[Mode] Updating UI to mode: ' + m);
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
    var switchingToWebRtc = (m === MODE_WEBRTC && mode !== MODE_WEBRTC);
    
    // Update UI immediately for responsiveness
    updateMode(m);
    
    fetch('/api/mode', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ mode: m })
    }).then(function (res) { 
        if (res.ok) {
            console.log('[Mode] Server accepted mode change to: ' + m);
            // Force reconnect when switching to WebRTC for fresh connection
            if (switchingToWebRtc) {
                console.log('[WS] Switching to WebRTC mode - forcing fresh connection');
                forceReconnect();
            }
        } else {
            console.log('[Mode] Server rejected mode change');
        }
    }).catch(function(err) {
        console.log('[Mode] Failed to set mode: ' + err);
    });
}

function addMsg(text, type, speaker) {
    if (empty) empty.classList.add('hidden');
    var div = document.createElement('div');
    div.className = 'msg ' + type;
    
    // For transcription messages, show speaker label
    if (type === 'transcription' && speaker) {
        div.innerHTML = '<strong>' + escapeHtml(speaker) + ':</strong> ' + escapeHtml(text);
    } else {
        div.textContent = text;
    }
    
    chat.appendChild(div);
    chat.scrollTop = chat.scrollHeight;
    return div;
}

function escapeHtml(text) {
    var div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
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

function enqueueTtsChunk(base64Data, srcRate = ttsSampleRate) {
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
            var thr = Math.min(TTS_LEAK_GATE_MAX, Math.max(TTS_LEAK_RMS_GATE, _ambientRms * TTS_LEAK_MULTIPLIER));
            if (rms >= thr) return;
        }

        // Convert to PCM16 at capture/device rate with input gain boost.
        // Let the server do resampling + ASR-friendly preprocessing.
        var pcm = new Int16Array(inputData.length);
        var gain = CAPTURE_INPUT_GAIN || 1.0;
        for (var i = 0; i < inputData.length; i++) {
            // Apply gain and soft-clip to avoid harsh distortion
            var s = inputData[i] * gain;
            // Soft clipping using tanh for values approaching limits
            if (s > 0.9) s = 0.9 + 0.1 * Math.tanh((s - 0.9) * 10);
            else if (s < -0.9) s = -0.9 + 0.1 * Math.tanh((s + 0.9) * 10);
            s = Math.max(-1, Math.min(1, s));
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
                prerollMs: PREROLL_MS,
                inputGain: gain
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
                if (j.mode !== undefined && j.mode !== mode) {
                    console.log('[Poll] Mode changed from server: ' + j.mode);
                    updateMode(j.mode);
                }
                if (j.bargeInEnabled !== undefined) bargeInEnabled = j.bargeInEnabled;
                if (j.transcriptionEnabled !== undefined) updateTranscriptionMode(j.transcriptionEnabled);
                updateDspSettings(j);
            })
            .catch(function () { });
    }, 2000);
}

function stopStatusPoll() {
    if (!statusPollTimer) return;
    clearInterval(statusPollTimer);
    statusPollTimer = null;
}

// Force a fresh WebSocket reconnection (for mode switches or manual refresh)
function forceReconnect() {
    console.log('[WS] Force reconnect requested');
    wsReconnectAttempt = 0;
    wsFirstMessageReceived = false;
    cleanupWebSocket();
    
    // Small delay to ensure cleanup completes
    setTimeout(function() {
        connect();
    }, 100);
}

// Clean up WebSocket state before reconnecting
function cleanupWebSocket() {
    // Stop ping interval
    if (wsPingInterval) {
        clearInterval(wsPingInterval);
        wsPingInterval = null;
    }
    
    // Clear reconnect timer
    if (wsReconnectTimer) {
        clearTimeout(wsReconnectTimer);
        wsReconnectTimer = null;
    }
    
    // Force close existing socket
    if (ws) {
        try {
            // Remove handlers to prevent double-firing
            ws.onopen = null;
            ws.onmessage = null;
            ws.onclose = null;
            ws.onerror = null;
            
            if (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING) {
                ws.close(1000, 'Reconnecting');
            }
        } catch (e) { }
        ws = null;
    }
}

// Start WebSocket ping/pong keepalive
function startPingInterval(connectionId) {
    if (wsPingInterval) {
        clearInterval(wsPingInterval);
    }
    
    wsLastPongTime = Date.now();
    
    wsPingInterval = setInterval(function () {
        // Check if this is still the active connection
        if (connectionId !== wsConnectionId) {
            clearInterval(wsPingInterval);
            wsPingInterval = null;
            return;
        }
        
        if (!ws || ws.readyState !== WebSocket.OPEN) {
            return;
        }
        
        // Check for pong timeout
        var timeSincePong = Date.now() - wsLastPongTime;
        if (timeSincePong > WS_PING_INTERVAL_MS + WS_PONG_TIMEOUT_MS) {
            console.log('[WS] Pong timeout (' + timeSincePong + 'ms), forcing reconnect');
            setStatus('Reconnecting...', 'warn');
            forceReconnect();
            return;
        }
        
        // Send ping
        try {
            ws.send(JSON.stringify({ type: 'ping', ts: Date.now() }));
        } catch (e) {
            console.error('[WS] Ping send error:', e);
            forceReconnect();
        }
    }, WS_PING_INTERVAL_MS);
}

// Schedule reconnection with exponential backoff
function scheduleReconnect() {
    if (wsReconnectTimer) return;
    
    var delay = Math.min(
        WS_RECONNECT_BASE_MS * Math.pow(1.5, wsReconnectAttempt),
        WS_RECONNECT_MAX_MS
    );
    
    console.log('[WS] Scheduling reconnect in ' + delay + 'ms (attempt ' + (wsReconnectAttempt + 1) + ')');
    setStatus('Retry in ' + Math.round(delay/1000) + 's...', 'warn');
    
    wsReconnectTimer = setTimeout(function () {
        wsReconnectTimer = null;
        wsReconnectAttempt++;
        connect();
    }, delay);
}

function connect() {
    // Clean up any existing connection first
    cleanupWebSocket();
    
    // Increment connection ID to invalidate stale handlers
    wsConnectionId++;
    var myConnectionId = wsConnectionId;
    wsConnectStartTime = Date.now();
    wsFirstMessageReceived = false;
    
    var proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    // Add cache-busting and connection ID to prevent stale connections
    var url = proto + '//' + location.host + '/ws?t=' + Date.now();
    
    console.log('[WS] Connecting to: ' + url);
    setStatus('Connecting...', 'warn');

    try {
        ws = new WebSocket(url);
    } catch (e) {
        console.error('[WS] WebSocket creation failed:', e);
        setStatus('Connection failed', '');
        scheduleReconnect();
        return;
    }

    var connectTimeout = setTimeout(function () {
        if (myConnectionId !== wsConnectionId) return;
        
        if (!wsFirstMessageReceived) {
            var elapsed = Date.now() - wsConnectStartTime;
            console.log('[WS] Connection timeout after ' + elapsed + 'ms (no message received)');
            setStatus('Timeout', '');
            cleanupWebSocket();
            scheduleReconnect();
        }
    }, WS_CONNECT_TIMEOUT_MS);

    ws.onopen = function () {
        if (myConnectionId !== wsConnectionId) {
            // Stale connection, close it
            try { ws.close(); } catch (e) { }
            return;
        }
        
        console.log('[WS] Socket opened, waiting for server status...');
        setStatus('Waiting...', 'warn');
    };

    ws.onmessage = function (e) {
        if (myConnectionId !== wsConnectionId) return;
        
        // First message received - connection verified!
        if (!wsFirstMessageReceived) {
            wsFirstMessageReceived = true;
            clearTimeout(connectTimeout);
            
            console.log('[WS] Connection verified');
            setStatus('Connected', 'on');
            
            wsReconnectAttempt = 0;
            stopStatusPoll();
            startPingInterval(myConnectionId);
            
            // Play any pending TTS chunks
            if (pendingTtsChunks.length && audioUnlocked) {
                try {
                    pendingTtsChunks.forEach(function (c) { enqueueTtsChunk(c.data, c.rate); });
                    pendingTtsChunks = [];
                    flushTtsBufferToAudioEl(true);
                } catch (e) { }
            }
        }
        
        var msg;
        try { msg = JSON.parse(e.data); } catch (err) { return; }

        // Handle all message types
        switch (msg.type) {
            case 'pong':
                wsLastPongTime = Date.now();
                break;
                
            case 'status':
                console.log('[WS] Received status: mode=' + msg.mode);
                if (msg.mode !== undefined && msg.mode !== mode) {
                    updateMode(msg.mode);
                }
                if (msg.bargeInEnabled !== undefined) bargeInEnabled = msg.bargeInEnabled;
                if (msg.transcriptionEnabled !== undefined) updateTranscriptionMode(msg.transcriptionEnabled);
                break;
                
            case 'transcription_mode':
                if (msg.enabled !== undefined) updateTranscriptionMode(msg.enabled);
                break;
                
            case 'transcription_diarized':
                var speaker = msg.speaker || 'Unknown';
                var text = msg.text || '';
                addMsg(text, 'transcription', speaker);
                break;
                
            case 'transcription':
                if (pendingText && msg.text === pendingText) {
                    pendingText = null;
                } else {
                    var msgType = transcriptionMode ? 'transcription' : 'user';
                    var spkr = msg.speaker || null;
                    addMsg(msg.text, msgType, spkr);
                    if (!transcriptionMode) {
                        showTyping();
                    }
                }
                break;
                
            case 'response_chunk':
                if (!transcriptionMode) {
                    appendResponse(msg.text);
                }
                break;
                
            case 'response':
                if (!transcriptionMode) {
                    hideTyping();
                    if (!streamEl) addMsg(msg.text, 'ai');
                    finalizeResponse();
                }
                break;
                
            case 'tts_audio':
                if (msg.data && !transcriptionMode) {
                    playTtsAudio(msg.data, msg.sampleRate || 24000);
                }
                break;
                
            case 'tts_stop':
                stopTtsPlayback();
                break;
        }
    };

    ws.onclose = function (event) {
        if (myConnectionId !== wsConnectionId) return;
        
        clearTimeout(connectTimeout);
        console.log('[WS] Closed: code=' + event.code + ', wasVerified=' + wsFirstMessageReceived);
        setStatus('Disconnected', '');
        
        // Clean up ping interval
        if (wsPingInterval) {
            clearInterval(wsPingInterval);
            wsPingInterval = null;
        }
        
        // Start status polling as fallback
        startStatusPoll();
        
        // Schedule reconnect
        scheduleReconnect();
    };

    ws.onerror = function (event) {
        if (myConnectionId !== wsConnectionId) return;
        
        console.error('[WS] Error');
        setStatus('Error', '');
        
        // Start status polling as fallback
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
        addMsg('⚠️ Not connected. Tap status to reconnect.', 'ai');
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
                addMsg('⏱️ Mic request timed out. Check Settings > Safari > Microphone', 'ai');
            });
        } catch (e) {
            addMsg('⏱️ Mic request timed out.', 'ai');
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
            addMsg('❌ Microphone permission denied.', 'ai');
        } else if (e.name === 'NotFoundError') {
            addMsg('❌ No microphone found.', 'ai');
        } else if (e.name === 'NotReadableError' || e.name === 'AbortError') {
            addMsg('❌ Microphone busy. Close other apps.', 'ai');
        } else if (e.name === 'SecurityError') {
            addMsg('❌ Security error - HTTPS required.', 'ai');
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
                addMsg('⚠️ Mic stream ended. Tap mic to restart.', 'ai');
                stopVoice();
            }
        }
    }, 5000);
}

// Capture constraint defaults
// These are loaded from server settings via /api/status
var CAPTURE_ECHO_CANCELLATION = false;
var CAPTURE_NOISE_SUPPRESSION = false;
var CAPTURE_AUTO_GAIN = false;

// Allow querystring override: ?dsp=1 enables all, ?raw=1 disables all
try {
    if (location && location.search) {
        if (location.search.indexOf('dsp=1') >= 0) {
            CAPTURE_ECHO_CANCELLATION = true;
            CAPTURE_NOISE_SUPPRESSION = true;
            CAPTURE_AUTO_GAIN = true;
        }
        if (location.search.indexOf('raw=1') >= 0) {
            CAPTURE_ECHO_CANCELLATION = false;
            CAPTURE_NOISE_SUPPRESSION = false;
            CAPTURE_AUTO_GAIN = false;
        }
    }
} catch (e) { }

// Update DSP settings from server status
function updateDspSettings(status) {
    if (status.webRtcEchoCancellation !== undefined) {
        CAPTURE_ECHO_CANCELLATION = status.webRtcEchoCancellation;
    }
    if (status.webRtcNoiseSuppression !== undefined) {
        CAPTURE_NOISE_SUPPRESSION = status.webRtcNoiseSuppression;
    }
    if (status.webRtcAutoGainControl !== undefined) {
        CAPTURE_AUTO_GAIN = status.webRtcAutoGainControl;
    }
}

function init() {
    console.log('[Init] Starting...');
    
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
    transcribeBtn = document.getElementById('transcribeBtn');
    transcriptionBanner = document.getElementById('transcriptionBanner');

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

    if (transcribeBtn) {
        transcribeBtn.ontouchend = function (e) {
            e.preventDefault();
            e.stopPropagation();
            onTranscribeClick();
        };
        transcribeBtn.onclick = function (e) {
            e.preventDefault();
            e.stopPropagation();
            onTranscribeClick();
        };
    }

    // Allow tapping status indicator to force reconnect
    var statusEl = document.getElementById('status');
    if (statusEl) {
        statusEl.style.cursor = 'pointer';
        statusEl.title = 'Tap to reconnect';
        statusEl.ontouchend = function (e) {
            e.preventDefault();
            e.stopPropagation();
            onStatusTap();
        };
        statusEl.onclick = function (e) {
            e.preventDefault();
            e.stopPropagation();
            onStatusTap();
        };
    }
    
    // Also make dot clickable for reconnect
    if (dot) {
        dot.style.cursor = 'pointer';
        dot.title = 'Tap to reconnect';
    }

    document.querySelectorAll('.switch-opt').forEach(function (opt) {
        opt.onclick = function () {
            unlockAudio();
            setMode(parseInt(this.dataset.mode, 10));
        };
        // Add touch handler for iOS
        opt.ontouchend = function (e) {
            e.preventDefault();
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
    
    // Handle page visibility changes (iOS Safari can suspend WebSocket when backgrounded)
    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'visible') {
            console.log('[Visibility] Page visible, checking connection...');
            // Give a moment for the socket to "wake up"
            setTimeout(function () {
                if (!ws || ws.readyState !== WebSocket.OPEN || !wsFirstMessageReceived) {
                    console.log('[Visibility] Connection stale, reconnecting...');
                    forceReconnect();
                } else {
                    // Send a ping to verify connection is alive
                    try {
                        ws.send(JSON.stringify({ type: 'ping', ts: Date.now() }));
                    } catch (e) {
                        console.log('[Visibility] Ping failed, reconnecting...');
                        forceReconnect();
                    }
                }
            }, 500);
        }
    });

    // Fetch initial status before connecting WebSocket
    fetch('/api/status', { cache: 'no-store' })
        .then(function(r) { return r.ok ? r.json() : null; })
        .then(function(j) {
            if (j && j.mode !== undefined) {
                console.log('[Init] Initial mode from server: ' + j.mode);
                updateMode(j.mode);
            }
            if (j && j.bargeInEnabled !== undefined) bargeInEnabled = j.bargeInEnabled;
            if (j && j.transcriptionEnabled !== undefined) updateTranscriptionMode(j.transcriptionEnabled);
            if (j) updateDspSettings(j);
        })
        .catch(function() {})
        .finally(function() {
            setStatus('Connecting...', 'warn');
            connect();
        });
}

// Handle tap on status indicator to force reconnect
function onStatusTap() {
    console.log('[WS] Status tapped - forcing reconnect');
    addMsg('🔄 Reconnecting...', 'ai');
    forceReconnect();
}

// transcription mode functions
function updateTranscriptionMode(enabled) {
    transcriptionMode = enabled;
    if (transcribeBtn) {
        transcribeBtn.classList.toggle('on', enabled);
    }
    if (transcriptionBanner) {
        transcriptionBanner.classList.toggle('visible', enabled);
    }
    // If transcription mode is disabled, stop mic capture so the UI mic button is off
    if (!enabled && voiceOn) {
        stopVoice();
    }
}

function setTranscriptionMode(enabled) {
    fetch('/api/transcription', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled: enabled })
    }).then(function (res) { 
        if (res.ok) {
            updateTranscriptionMode(enabled);
        }
    }).catch(function() {
        // Fallback: toggle locally even if API fails
        updateTranscriptionMode(enabled);
    });
}

function onTranscribeClick() {
    unlockAudio();
    setTranscriptionMode(!transcriptionMode);
}

// Initialize when DOM is ready
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
} else {
    init();
}
