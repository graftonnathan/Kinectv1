using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1.Voice
{
    /// <summary>
    /// Unified interface for all audio input sources.
    /// Implementations normalize audio to 16kHz mono PCM16 and emit NormalizedAudioFrame.
    /// </summary>
    public interface IAudioSource : IDisposable
    {
        /// <summary>
        /// Type of this audio source.
        /// </summary>
        AudioSourceType SourceType { get; }

        /// <summary>
        /// Whether this source is currently enabled and processing audio.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Current state of the audio source.
        /// </summary>
        AudioSourceState State { get; }

        /// <summary>
        /// Fired when a normalized audio frame is ready for processing.
        /// Frames are always 16kHz mono PCM16 with pre-computed RMS.
        /// </summary>
        event Action<NormalizedAudioFrame> OnAudioFrame;

        /// <summary>
        /// Fired when RMS level changes (for UI meters).
        /// Value is in 0-10000 scale.
        /// </summary>
        event Action<float> OnRmsLevel;

        /// <summary>
        /// Fired when source state changes.
        /// </summary>
        event Action<AudioSourceState> OnStateChanged;

        /// <summary>
        /// Diagnostic log messages.
        /// </summary>
        event Action<string> OnLog;

        /// <summary>
        /// Start the audio source.
        /// </summary>
        Task StartAsync(CancellationToken ct = default);

        /// <summary>
        /// Stop the audio source and release resources.
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// Enable or disable audio processing without starting/stopping the source.
        /// </summary>
        void SetEnabled(bool enabled);
    }

    /// <summary>
    /// State of an audio source.
    /// </summary>
    public enum AudioSourceState
    {
        /// <summary>Source is stopped and not capturing.</summary>
        Stopped,

        /// <summary>Source is starting up.</summary>
        Starting,

        /// <summary>Source is running and capturing audio.</summary>
        Running,

        /// <summary>Source is waiting for connection (e.g., WebRTC waiting for client).</summary>
        Listening,

        /// <summary>Source has an active connection (e.g., WebRTC connected).</summary>
        Connected,

        /// <summary>Source encountered an error.</summary>
        Error
    }

    /// <summary>
    /// Base class for audio sources with common functionality.
    /// </summary>
    public abstract class AudioSourceBase : IAudioSource
    {
        protected volatile bool _isEnabled;
        protected volatile AudioSourceState _state = AudioSourceState.Stopped;

        public abstract AudioSourceType SourceType { get; }
        public bool IsEnabled => _isEnabled;
        public AudioSourceState State => _state;

        public event Action<NormalizedAudioFrame> OnAudioFrame;
        public event Action<float> OnRmsLevel;
        public event Action<AudioSourceState> OnStateChanged;
        public event Action<string> OnLog;

        public abstract Task StartAsync(CancellationToken ct = default);
        public abstract Task StopAsync();
        public abstract void Dispose();

        public virtual void SetEnabled(bool enabled)
        {
            _isEnabled = enabled;
            Log($"[{SourceType}] {(enabled ? "Enabled" : "Disabled")}");
        }

        protected void EmitFrame(NormalizedAudioFrame frame)
        {
            if (!_isEnabled) return;
            try { OnAudioFrame?.Invoke(frame); } catch { }
        }

        protected void EmitRms(float rms)
        {
            try { OnRmsLevel?.Invoke(rms); } catch { }
        }

        protected void SetState(AudioSourceState state)
        {
            if (_state == state) return;
            _state = state;
            try { OnStateChanged?.Invoke(state); } catch { }
        }

        protected void Log(string message)
        {
            try { OnLog?.Invoke(message); } catch { }
            try { Console.WriteLine(message); } catch { }
        }
    }
}
