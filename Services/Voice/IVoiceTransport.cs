using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1.Voice
{
    /// <summary>
    /// Transport-agnostic voice interface for remote audio sources.
    /// Implementations: WebRtcAudioTransport (replaces TeamTalk)
    /// </summary>
    public interface IVoiceTransport : IDisposable
    {
        /// <summary>
        /// Fired when inbound audio arrives from remote (e.g., iPhone mic ? WPF).
        /// Callback must be fast - do not block or do heavy processing here.
        /// </summary>
        event Action<AudioFrame> OnInboundAudio;

        /// <summary>
        /// Diagnostic/status log messages.
        /// </summary>
        event Action<string> OnLog;

        /// <summary>
        /// Connection state changed (connected, disconnected, error).
        /// </summary>
        event Action<VoiceTransportState> OnStateChanged;

        /// <summary>
        /// Current connection state.
        /// </summary>
        VoiceTransportState State { get; }

        /// <summary>
        /// Start the transport (signaling server, accept connections).
        /// </summary>
        Task StartAsync(CancellationToken ct);

        /// <summary>
        /// Stop the transport and release resources.
        /// </summary>
        Task StopAsync(CancellationToken ct = default);

        /// <summary>
        /// Send TTS audio to the remote client (WPF ? iPhone speaker).
        /// PCM16, 16kHz, mono. Will be resampled to 48kHz for WebRTC.
        /// </summary>
        Task SendTtsAsync(short[] pcm16_16k_mono, CancellationToken ct = default);

        /// <summary>
        /// Get the local URL for clients to connect.
        /// </summary>
        string GetJoinUrl();
    }

    public enum VoiceTransportState
    {
        Stopped,
        Starting,
        Listening,
        Connected,
        Error
    }

    /// <summary>
    /// Immutable audio frame for transport.
    /// </summary>
    public readonly record struct AudioFrame(
        short[] Pcm16,
        int SampleRate,
        int Channels,
        long TimestampTicks,
        string SourceId
    )
    {
        /// <summary>
        /// Duration of this frame in milliseconds.
        /// </summary>
        public double DurationMs => Pcm16 != null && SampleRate > 0 && Channels > 0
            ? (Pcm16.Length / (double)Channels) / SampleRate * 1000.0
            : 0;
    }
}
