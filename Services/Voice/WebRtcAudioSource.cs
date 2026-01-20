using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1.Voice
{
    /// <summary>
    /// Audio source for WebRTC browser-based input.
    /// Wraps WebRtcAudioTransport and handles resampling 8kHz ? 16kHz.
    /// Outputs 16kHz mono PCM16 frames.
    /// </summary>
    public sealed class WebRtcAudioSource : AudioSourceBase
    {
        private readonly int _port;
        private WebRtcAudioTransport _transport;
        private CancellationTokenSource _cts;
        private readonly object _lock = new object();

        public override AudioSourceType SourceType => AudioSourceType.WebRtc;

        /// <summary>
        /// URL for clients to connect to this WebRTC source.
        /// </summary>
        public string JoinUrl => _transport?.GetJoinUrl() ?? $"http://localhost:{_port}/";

        public WebRtcAudioSource(int port = 8787)
        {
            _port = port;
        }

        public override async Task StartAsync(CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_state != AudioSourceState.Stopped) return;
                SetState(AudioSourceState.Starting);
            }

            try
            {
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _transport = new WebRtcAudioTransport(_port);

                // Wire events
                _transport.OnLog += msg => Log(msg);
                _transport.OnInboundAudio += OnInboundAudio;
                _transport.OnStateChanged += OnTransportStateChanged;

                await _transport.StartAsync(_cts.Token);

                _isEnabled = true;
                Log($"[WebRtcSource] Started. Join URL: {JoinUrl}");
            }
            catch (Exception ex)
            {
                Log($"[WebRtcSource] Start failed: {ex.Message}");
                SetState(AudioSourceState.Error);
                throw;
            }
        }

        public override async Task StopAsync()
        {
            WebRtcAudioTransport transport;
            CancellationTokenSource cts;

            lock (_lock)
            {
                if (_state == AudioSourceState.Stopped) return;
                transport = _transport;
                cts = _cts;
                _transport = null;
                _cts = null;
                SetState(AudioSourceState.Stopped);
            }

            _isEnabled = false;

            try { cts?.Cancel(); } catch { }
            try { if (transport != null) await transport.StopAsync(); } catch { }
            try { transport?.Dispose(); } catch { }
            try { cts?.Dispose(); } catch { }

            Log("[WebRtcSource] Stopped");
        }

        public override void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); } catch { }
        }

        /// <summary>
        /// Send TTS audio to the connected WebRTC client.
        /// </summary>
        public async Task SendTtsAsync(short[] pcm16_16k_mono, CancellationToken ct = default)
        {
            var transport = _transport;
            if (transport == null) return;
            await transport.SendTtsAsync(pcm16_16k_mono, ct);
        }

        private void OnTransportStateChanged(VoiceTransportState transportState)
        {
            var newState = transportState switch
            {
                VoiceTransportState.Stopped => AudioSourceState.Stopped,
                VoiceTransportState.Starting => AudioSourceState.Starting,
                VoiceTransportState.Listening => AudioSourceState.Listening,
                VoiceTransportState.Connected => AudioSourceState.Connected,
                VoiceTransportState.Error => AudioSourceState.Error,
                _ => AudioSourceState.Stopped
            };

            SetState(newState);
        }

        private void OnInboundAudio(AudioFrame frame)
        {
            if (!_isEnabled) return;

            try
            {
                // Resample 8kHz ? 16kHz
                short[] pcm16k;
                if (frame.SampleRate == 8000)
                {
                    pcm16k = Upsample8kTo16k(frame.Pcm16);
                }
                else if (frame.SampleRate == 16000)
                {
                    pcm16k = frame.Pcm16;
                }
                else
                {
                    // Unsupported sample rate
                    Log($"[WebRtcSource] Unsupported sample rate: {frame.SampleRate}");
                    return;
                }

                // Convert shorts to bytes
                var pcmBytes = ShortsToBytes(pcm16k);

                // Create normalized frame
                var normalizedFrame = NormalizedAudioFrame.Create(
                    pcmBytes,
                    pcmBytes.Length,
                    AudioSourceType.WebRtc,
                    frame.SourceId
                );

                // Emit RMS for UI meter
                EmitRms(normalizedFrame.Rms);

                // Emit frame for processing
                EmitFrame(normalizedFrame);
            }
            catch (Exception ex)
            {
                Log($"[WebRtcSource] Frame processing error: {ex.Message}");
            }
        }

        /// <summary>
        /// Upsample 8kHz to 16kHz using linear interpolation.
        /// </summary>
        private static short[] Upsample8kTo16k(short[] input)
        {
            if (input == null || input.Length == 0) return Array.Empty<short>();

            var output = new short[input.Length * 2];
            for (int i = 0; i < input.Length; i++)
            {
                output[i * 2] = input[i];
                if (i < input.Length - 1)
                    output[i * 2 + 1] = (short)((input[i] + input[i + 1]) / 2);
                else
                    output[i * 2 + 1] = input[i];
            }
            return output;
        }

        /// <summary>
        /// Convert short array to byte array (little-endian).
        /// </summary>
        private static byte[] ShortsToBytes(short[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return Array.Empty<byte>();
            var bytes = new byte[pcm.Length * 2];
            Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
            return bytes;
        }
    }
}
