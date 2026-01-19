using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kinectv1.Voice;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace Kinectv1.Voice
{
    /// <summary>
    /// WebRTC audio transport for LAN-only bidirectional audio.
    /// iPhone mic ? WPF (for RMS + Vosk STT)
    /// WPF TTS ? iPhone speaker
    /// </summary>
    public sealed class WebRtcAudioTransport : IVoiceTransport
    {
        public event Action<AudioFrame> OnInboundAudio;
        public event Action<string> OnLog;
        public event Action<VoiceTransportState> OnStateChanged;

        private readonly object _lock = new object();
        private volatile VoiceTransportState _state = VoiceTransportState.Stopped;
        private CancellationTokenSource _cts;

        // Signaling server
        private WebRtcSignalingServer _signalingServer;
        private int _port;

        // WebRTC peer connection
        private RTCPeerConnection _pc;
        private MediaStreamTrack _audioTrack;

        // Outbound TTS queue (bounded, drop-oldest)
        private readonly DroppingAudioQueue<short[]> _ttsQueue = new(capacity: 100); // ~2s @ 20ms frames
        private Task _ttsSenderTask;

        // Diagnostics
        private int _inboundFramesThisSecond;
        private int _outboundFramesThisSecond;
        private DateTime _lastDiagUtc = DateTime.MinValue;
        private DateTime _lastInboundFrameUtc = DateTime.MinValue;

        public VoiceTransportState State => _state;

        public WebRtcAudioTransport(int port = 8787)
        {
            _port = port;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                if (_state != VoiceTransportState.Stopped) return;
                _state = VoiceTransportState.Starting;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            }

            try
            {
                OnStateChanged?.Invoke(_state);
                Log($"[WebRTC] Starting signaling server on port {_port}...");

                // Start signaling server
                _signalingServer = new WebRtcSignalingServer(_port);
                _signalingServer.OnLog += Log;
                _signalingServer.OnWebSocketMessage += HandleSignalingMessage;
                await _signalingServer.StartAsync(_cts.Token);

                // Start TTS sender task
                _ttsSenderTask = Task.Run(() => TtsSenderLoop(_cts.Token), _cts.Token);

                // Start diagnostics
                _ = Task.Run(() => DiagnosticsLoop(_cts.Token), _cts.Token);

                lock (_lock) _state = VoiceTransportState.Listening;
                OnStateChanged?.Invoke(_state);

                var url = GetJoinUrl();
                Log($"[WebRTC] Listening. Join URL: {url}");
            }
            catch (Exception ex)
            {
                Log($"[WebRTC] Start failed: {ex.Message}");
                lock (_lock) _state = VoiceTransportState.Error;
                OnStateChanged?.Invoke(_state);
                throw;
            }
        }

        public async Task StopAsync(CancellationToken ct = default)
        {
            CancellationTokenSource cts;
            RTCPeerConnection pc;
            WebRtcSignalingServer server;
            Task sender;

            lock (_lock)
            {
                if (_state == VoiceTransportState.Stopped) return;
                _state = VoiceTransportState.Stopped;

                cts = _cts;
                pc = _pc;
                server = _signalingServer;
                sender = _ttsSenderTask;

                _cts = null;
                _pc = null;
                _signalingServer = null;
                _ttsSenderTask = null;
            }

            OnStateChanged?.Invoke(_state);

            try { cts?.Cancel(); } catch { }
            try { if (sender != null) await Task.WhenAny(sender, Task.Delay(1000)); } catch { }
            try { pc?.Close("shutdown"); } catch { }
            try { pc?.Dispose(); } catch { }
            try { await server?.StopAsync(); } catch { }
            try { cts?.Dispose(); } catch { }

            _ttsQueue.Clear();
            Log("[WebRTC] Stopped");
        }

        public async Task SendTtsAsync(short[] pcm16_16k_mono, CancellationToken ct = default)
        {
            if (pcm16_16k_mono == null || pcm16_16k_mono.Length == 0) return;
            if (_state != VoiceTransportState.Connected) return;

            // Frame into 20ms chunks (320 samples @ 16kHz) and enqueue
            const int frameSize = 320;
            int offset = 0;

            while (offset < pcm16_16k_mono.Length)
            {
                int remaining = pcm16_16k_mono.Length - offset;
                int chunkSize = Math.Min(frameSize, remaining);

                var chunk = new short[chunkSize];
                Array.Copy(pcm16_16k_mono, offset, chunk, 0, chunkSize);
                _ttsQueue.Enqueue(chunk);

                offset += chunkSize;
            }

            await Task.CompletedTask;
        }

        public string GetJoinUrl()
        {
            var ip = GetLocalIPAddress();
            return $"http://{ip}:{_port}/";
        }

        private async void HandleSignalingMessage(WebSocket ws, string message)
        {
            try
            {
                var json = JObject.Parse(message);
                var type = json["type"]?.ToString();

                switch (type)
                {
                    case "offer":
                        await HandleOffer(ws, json["sdp"]?.ToString());
                        break;

                    case "ice":
                        await HandleIceCandidate(json["candidate"]);
                        break;

                    default:
                        Log($"[WebRTC] Unknown message type: {type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"[WebRTC] HandleSignalingMessage error: {ex.Message}");
            }
        }

        private async Task HandleOffer(WebSocket ws, string sdp)
        {
            if (string.IsNullOrWhiteSpace(sdp))
            {
                Log("[WebRTC] Received empty offer SDP");
                return;
            }

            Log("[WebRTC] Received offer, creating peer connection...");
            Log($"[WebRTC] Offer SDP (first 500 chars): {sdp.Substring(0, Math.Min(500, sdp.Length))}...");

            // Close existing connection if any
            try { _pc?.Close("new offer"); _pc?.Dispose(); } catch { }

            // Create peer connection
            var config = new RTCConfiguration
            {
                iceServers = new System.Collections.Generic.List<RTCIceServer>()
                // No STUN/TURN for LAN-only
            };

            _pc = new RTCPeerConnection(config);

            // Wire events
            _pc.onconnectionstatechange += state =>
            {
                Log($"[WebRTC] Connection state: {state}");
                if (state == RTCPeerConnectionState.connected)
                {
                    lock (_lock) _state = VoiceTransportState.Connected;
                    OnStateChanged?.Invoke(_state);
                }
                else if (state == RTCPeerConnectionState.disconnected || 
                         state == RTCPeerConnectionState.failed ||
                         state == RTCPeerConnectionState.closed)
                {
                    lock (_lock) if (_state == VoiceTransportState.Connected) _state = VoiceTransportState.Listening;
                    OnStateChanged?.Invoke(_state);
                }
            };

            _pc.onicecandidate += candidate =>
            {
                if (candidate != null)
                {
                    Log($"[WebRTC] Sending ICE candidate: {candidate.candidate?.Substring(0, Math.Min(50, candidate.candidate?.Length ?? 0))}...");
                    var iceMsg = JsonConvert.SerializeObject(new
                    {
                        type = "ice",
                        candidate = new
                        {
                            candidate = candidate.candidate,
                            sdpMid = candidate.sdpMid,
                            sdpMLineIndex = candidate.sdpMLineIndex
                        }
                    });
                    _ = SendWebSocketMessage(ws, iceMsg);
                }
            };

            // Handle incoming audio - use the generic RTP packet received event
            _pc.OnRtpPacketReceived += (IPEndPoint ep, SDPMediaTypesEnum mt, RTPPacket pkt) =>
            {
                if (mt == SDPMediaTypesEnum.audio)
                {
                    HandleInboundAudioPacket(pkt);
                }
            };

            // Add audio formats that Safari supports
            // Safari prefers Opus but PCMU/PCMA are widely supported fallbacks
            var audioFormats = new System.Collections.Generic.List<SDPAudioVideoMediaFormat>
            {
                new SDPAudioVideoMediaFormat(new AudioFormat(AudioCodecsEnum.PCMU, 0, 8000, 1)),
                new SDPAudioVideoMediaFormat(new AudioFormat(AudioCodecsEnum.PCMA, 8, 8000, 1))
            };

            // Create audio track for receiving - SendRecv to receive audio from browser
            _audioTrack = new MediaStreamTrack(audioFormats[0].ToAudioFormat(), MediaStreamStatusEnum.SendRecv);
            _pc.addTrack(_audioTrack);

            // Set remote description (offer)
            var setResult = _pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = sdp
            });
            Log($"[WebRTC] setRemoteDescription result: {setResult}");

            // Create answer
            var answer = _pc.createAnswer();
            await _pc.setLocalDescription(answer);

            Log($"[WebRTC] Answer SDP (first 500 chars): {answer.sdp?.Substring(0, Math.Min(500, answer.sdp?.Length ?? 0))}...");

            // Send answer
            var answerMsg = JsonConvert.SerializeObject(new
            {
                type = "answer",
                sdp = answer.sdp
            });
            await SendWebSocketMessage(ws, answerMsg);

            Log("[WebRTC] Answer sent");
        }

        private async Task HandleIceCandidate(JToken candidateJson)
        {
            if (_pc == null || candidateJson == null) return;

            try
            {
                var candidate = candidateJson["candidate"]?.ToString();
                var sdpMid = candidateJson["sdpMid"]?.ToString();
                var sdpMLineIndex = candidateJson["sdpMLineIndex"]?.Value<ushort>() ?? 0;

                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    _pc.addIceCandidate(new RTCIceCandidateInit
                    {
                        candidate = candidate,
                        sdpMid = sdpMid,
                        sdpMLineIndex = sdpMLineIndex
                    });
                    Log($"[WebRTC] Added ICE candidate");
                }
            }
            catch (Exception ex)
            {
                Log($"[WebRTC] ICE candidate error: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        private void HandleInboundAudioPacket(RTPPacket pkt)
        {
            try
            {
                // Decode RTP payload to PCM
                var payload = pkt.Payload;
                if (payload == null || payload.Length == 0)
                {
                    return;
                }

                // Log occasionally for debugging
                if (_inboundFramesThisSecond == 0)
                {
                    Log($"[WebRTC] Audio packet received: PT={pkt.Header.PayloadType} len={payload.Length} seq={pkt.Header.SequenceNumber}");
                }

                // Decode based on payload type
                // PT 0 = PCMU (G.711 ?-law)
                // PT 8 = PCMA (G.711 A-law)
                short[] pcm16;
                int sampleRate = 8000;

                if (pkt.Header.PayloadType == 0) // PCMU
                {
                    pcm16 = DecodeMuLaw(payload);
                }
                else if (pkt.Header.PayloadType == 8) // PCMA
                {
                    pcm16 = DecodeALaw(payload);
                }
                else
                {
                    // Unknown payload type - log and skip
                    if (_inboundFramesThisSecond == 0)
                    {
                        Log($"[WebRTC] Unknown payload type {pkt.Header.PayloadType}, cannot decode");
                    }
                    return;
                }

                if (pcm16 == null || pcm16.Length == 0) return;

                _lastInboundFrameUtc = DateTime.UtcNow;
                Interlocked.Increment(ref _inboundFramesThisSecond);

                // Fire event (must be fast - no heavy processing!)
                var frame = new AudioFrame(
                    Pcm16: pcm16,
                    SampleRate: sampleRate,
                    Channels: 1,
                    TimestampTicks: DateTime.UtcNow.Ticks,
                    SourceId: "webrtc-client"
                );

                OnInboundAudio?.Invoke(frame);
            }
            catch (Exception ex)
            {
                Log($"[WebRTC] Inbound audio error: {ex.Message}");
            }
        }

        private static short[] DecodeMuLaw(byte[] mulaw)
        {
            if (mulaw == null) return Array.Empty<short>();
            var pcm = new short[mulaw.Length];
            for (int i = 0; i < mulaw.Length; i++)
            {
                pcm[i] = MuLawDecoder.MuLawToLinear(mulaw[i]);
            }
            return pcm;
        }

        private static short[] DecodeALaw(byte[] alaw)
        {
            if (alaw == null) return Array.Empty<short>();
            var pcm = new short[alaw.Length];
            for (int i = 0; i < alaw.Length; i++)
            {
                pcm[i] = ALawDecoder.ALawToLinear(alaw[i]);
            }
            return pcm;
        }

        private void TtsSenderLoop(CancellationToken ct)
        {
            // Pace TTS frames at 20ms intervals to prevent burst-sending
            const int frameIntervalMs = 20;
            var nextSendTime = DateTime.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_pc == null || _state != VoiceTransportState.Connected)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    // Wait until it's time to send next frame
                    var now = DateTime.UtcNow;
                    if (now < nextSendTime)
                    {
                        var waitMs = (int)(nextSendTime - now).TotalMilliseconds;
                        if (waitMs > 0) Thread.Sleep(Math.Min(waitMs, frameIntervalMs));
                        continue;
                    }

                    if (_ttsQueue.TryDequeue(out var pcm16k))
                    {
                        // Resample 16kHz ? 8kHz for PCMU
                        var pcm8k = Resample16kTo8k(pcm16k);

                        // Encode to ?-law
                        var mulaw = EncodeMuLaw(pcm8k);

                        // Send RTP packet
                        if (_audioTrack != null && mulaw.Length > 0)
                        {
                            // SIPSorcery handles RTP packetization
                            _pc.SendAudio((uint)(mulaw.Length * 8 / 8), mulaw); // duration in samples
                        }

                        Interlocked.Increment(ref _outboundFramesThisSecond);
                        nextSendTime = now.AddMilliseconds(frameIntervalMs);
                    }
                    else
                    {
                        // No data - send silence or skip
                        Thread.Sleep(5);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log($"[WebRTC] TTS sender error: {ex.Message}");
                    Thread.Sleep(100);
                }
            }
        }

        private static short[] Resample16kTo8k(short[] input)
        {
            if (input == null || input.Length == 0) return Array.Empty<short>();
            var output = new short[input.Length / 2];
            for (int i = 0; i < output.Length; i++)
            {
                // Simple decimation with averaging
                int idx = i * 2;
                if (idx + 1 < input.Length)
                    output[i] = (short)((input[idx] + input[idx + 1]) / 2);
                else
                    output[i] = input[idx];
            }
            return output;
        }

        private static byte[] EncodeMuLaw(short[] pcm)
        {
            if (pcm == null) return Array.Empty<byte>();
            var mulaw = new byte[pcm.Length];
            for (int i = 0; i < pcm.Length; i++)
            {
                mulaw[i] = MuLawEncoder.LinearToMuLaw(pcm[i]);
            }
            return mulaw;
        }

        private async Task SendWebSocketMessage(WebSocket ws, string message)
        {
            if (ws == null || ws.State != WebSocketState.Open) return;

            try
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log($"[WebRTC] WS send error: {ex.Message}");
            }
        }

        private void DiagnosticsLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Thread.Sleep(1000);

                    var now = DateTime.UtcNow;
                    var inFps = Interlocked.Exchange(ref _inboundFramesThisSecond, 0);
                    var outFps = Interlocked.Exchange(ref _outboundFramesThisSecond, 0);
                    var (qDepth, qDropped, totalDropped, depthMs) = _ttsQueue.GetStats(16000, 320);
                    var lastIn = _lastInboundFrameUtc == DateTime.MinValue ? "never" : $"{(now - _lastInboundFrameUtc).TotalMilliseconds:F0}ms ago";

                    Log($"[WebRTC][diag] state={_state} inFps={inFps} outFps={outFps} ttsQ={qDepth} ({depthMs:F0}ms) dropped={qDropped} lastIn={lastIn}");
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        private static string GetLocalIPAddress()
        {
            try
            {
                // Find the first non-loopback IPv4 address
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
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

        private void Log(string msg)
        {
            try { OnLog?.Invoke(msg); } catch { }
        }

        public void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); } catch { }
        }
    }

    /// <summary>
    /// G.711 ?-law encoder
    /// </summary>
    internal static class MuLawEncoder
    {
        private const int BIAS = 0x84;
        private const int MAX = 32635;

        public static byte LinearToMuLaw(short sample)
        {
            int sign = (sample >> 8) & 0x80;
            if (sign != 0) sample = (short)-sample;
            if (sample > MAX) sample = MAX;
            sample = (short)(sample + BIAS);

            int exponent = 7;
            int mask = 0x4000;
            while ((sample & mask) == 0 && exponent > 0)
            {
                exponent--;
                mask >>= 1;
            }

            int mantissa = (sample >> (exponent + 3)) & 0x0F;
            byte mulaw = (byte)(~(sign | (exponent << 4) | mantissa));
            return mulaw;
        }
    }

    /// <summary>
    /// G.711 ?-law decoder
    /// </summary>
    internal static class MuLawDecoder
    {
        private static readonly short[] _table = new short[256];

        static MuLawDecoder()
        {
            for (int i = 0; i < 256; i++)
            {
                byte mulaw = (byte)~i;
                int sign = mulaw & 0x80;
                int exponent = (mulaw >> 4) & 0x07;
                int mantissa = mulaw & 0x0F;
                int sample = ((mantissa << 3) + 0x84) << exponent;
                sample -= 0x84;
                _table[i] = (short)(sign != 0 ? -sample : sample);
            }
        }

        public static short MuLawToLinear(byte mulaw) => _table[mulaw];
    }

    /// <summary>
    /// G.711 A-law decoder
    /// </summary>
    internal static class ALawDecoder
    {
        private static readonly short[] _table = new short[256];

        static ALawDecoder()
        {
            for (int i = 0; i < 256; i++)
            {
                byte alaw = (byte)(i ^ 0x55);
                int sign = alaw & 0x80;
                int exponent = (alaw >> 4) & 0x07;
                int mantissa = alaw & 0x0F;

                int sample;
                if (exponent == 0)
                {
                    sample = (mantissa << 4) + 8;
                }
                else
                {
                    sample = ((mantissa << 4) + 0x108) << (exponent - 1);
                }

                _table[i] = (short)(sign != 0 ? -sample : sample);
            }
        }

        public static short ALawToLinear(byte alaw) => _table[alaw];
    }
}
