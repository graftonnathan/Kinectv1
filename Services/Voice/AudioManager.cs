using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kinectv1.Settings;

namespace Kinectv1.Voice
{
    /// <summary>
    /// Centralized audio input manager that coordinates all audio sources.
    /// Handles mode switching, source lifecycle, and unified audio frame routing.
    /// </summary>
    public sealed class AudioManager : IDisposable
    {
        private static AudioManager _instance;
        private static readonly object _instanceLock = new object();

        private readonly Dictionary<AudioSourceType, IAudioSource> _sources = new();
        private readonly object _lock = new object();
        private AudioSourceType _activeSource = AudioSourceType.LocalMic;
        private bool _disposed;

        /// <summary>
        /// Singleton instance of the AudioManager.
        /// </summary>
        public static AudioManager Instance
        {
            get
            {
                lock (_instanceLock)
                {
                    if (_instance == null)
                    {
                        _instance = new AudioManager();
                    }
                    return _instance;
                }
            }
        }

        /// <summary>
        /// Fired when any enabled source produces an audio frame.
        /// Frames are normalized to 16kHz mono PCM16.
        /// </summary>
        public event Action<NormalizedAudioFrame> OnAudioFrame;

        /// <summary>
        /// Fired when RMS level changes for any source.
        /// Tuple: (sourceType, rmsValue)
        /// </summary>
        public event Action<AudioSourceType, float> OnRmsLevel;

        /// <summary>
        /// Fired when any source state changes.
        /// </summary>
        public event Action<AudioSourceType, AudioSourceState> OnSourceStateChanged;

        /// <summary>
        /// Diagnostic log messages.
        /// </summary>
        public event Action<string> OnLog;

        /// <summary>
        /// Currently active audio source type.
        /// </summary>
        public AudioSourceType ActiveSource => _activeSource;

        /// <summary>
        /// Get a specific audio source by type.
        /// </summary>
        public IAudioSource GetSource(AudioSourceType type)
        {
            lock (_lock)
            {
                return _sources.TryGetValue(type, out var source) ? source : null;
            }
        }

        /// <summary>
        /// Get the WebRTC source for TTS output.
        /// </summary>
        public WebRtcAudioSource WebRtcSource => GetSource(AudioSourceType.WebRtc) as WebRtcAudioSource;

        /// <summary>
        /// Get the Discord source for audio processing.
        /// </summary>
        public DiscordAudioSource DiscordSource => GetSource(AudioSourceType.Discord) as DiscordAudioSource;

        private AudioManager()
        {
            InitializeSources();
        }

        private void InitializeSources()
        {
            // Create all audio sources
            var localMic = new LocalMicSource();
            var webRtc = new WebRtcAudioSource(GetWebRtcPort());
            var discord = new DiscordAudioSource();

            // Wire events
            WireSource(localMic);
            WireSource(webRtc);
            WireSource(discord);

            lock (_lock)
            {
                _sources[AudioSourceType.LocalMic] = localMic;
                _sources[AudioSourceType.WebRtc] = webRtc;
                _sources[AudioSourceType.Discord] = discord;
            }

            Log("[AudioManager] Sources initialized");
        }

        private void WireSource(IAudioSource source)
        {
            source.OnAudioFrame += frame =>
            {
                if (frame.Source == _activeSource || frame.Source == AudioSourceType.Preroll)
                {
                    try { OnAudioFrame?.Invoke(frame); } catch { }
                }
            };

            source.OnRmsLevel += rms =>
            {
                try { OnRmsLevel?.Invoke(source.SourceType, rms); } catch { }
            };

            source.OnStateChanged += state =>
            {
                try { OnSourceStateChanged?.Invoke(source.SourceType, state); } catch { }
            };

            source.OnLog += msg => Log(msg);
        }

        /// <summary>
        /// Set the active audio source. Disables other sources.
        /// </summary>
        public async Task SetActiveSourceAsync(AudioSourceType sourceType)
        {
            if (_disposed) return;

            IAudioSource newSource;
            List<IAudioSource> otherSources = new();

            lock (_lock)
            {
                if (_activeSource == sourceType) return;

                _activeSource = sourceType;

                foreach (var kvp in _sources)
                {
                    if (kvp.Key == sourceType)
                        newSource = kvp.Value;
                    else
                        otherSources.Add(kvp.Value);
                }

                newSource = _sources.TryGetValue(sourceType, out var s) ? s : null;
            }

            Log($"[AudioManager] Switching to {sourceType}");

            // Disable other sources
            foreach (var source in otherSources)
            {
                source.SetEnabled(false);
            }

            // Enable and start the new source if needed
            if (newSource != null)
            {
                newSource.SetEnabled(true);

                if (newSource.State == AudioSourceState.Stopped)
                {
                    try
                    {
                        await newSource.StartAsync();
                    }
                    catch (Exception ex)
                    {
                        Log($"[AudioManager] Failed to start {sourceType}: {ex.Message}");
                    }
                }
            }

            // Stop sources that are no longer active (except Discord which is managed externally)
            foreach (var source in otherSources)
            {
                if (source.SourceType != AudioSourceType.Discord &&
                    source.State != AudioSourceState.Stopped)
                {
                    try
                    {
                        await source.StopAsync();
                    }
                    catch (Exception ex)
                    {
                        Log($"[AudioManager] Failed to stop {source.SourceType}: {ex.Message}");
                    }
                }
            }

            // Notify VoiceRecognizer of mode change
            UpdateVoiceRecognizerState();

            Log($"[AudioManager] Active source: {sourceType}");
        }

        /// <summary>
        /// Start the audio manager with the configured default source.
        /// </summary>
        public async Task StartAsync(CancellationToken ct = default)
        {
            if (_disposed) return;

            // Determine initial source from settings
            var inputMode = App.SettingsProvider?.Current?.App?.InputMode ?? AudioInMode.LocalMic;
            var sourceType = inputMode switch
            {
                AudioInMode.LocalMic => AudioSourceType.LocalMic,
                AudioInMode.DiscordVoice => AudioSourceType.Discord,
                AudioInMode.WebRtcVoice => AudioSourceType.WebRtc,
                _ => AudioSourceType.LocalMic
            };

            await SetActiveSourceAsync(sourceType);
        }

        /// <summary>
        /// Stop all audio sources.
        /// </summary>
        public async Task StopAsync()
        {
            if (_disposed) return;

            List<IAudioSource> sources;
            lock (_lock)
            {
                sources = new List<IAudioSource>(_sources.Values);
            }

            foreach (var source in sources)
            {
                try
                {
                    source.SetEnabled(false);
                    await source.StopAsync();
                }
                catch (Exception ex)
                {
                    Log($"[AudioManager] Error stopping {source.SourceType}: {ex.Message}");
                }
            }

            Log("[AudioManager] All sources stopped");
        }

        /// <summary>
        /// Forward Discord audio to the Discord source.
        /// Called by DiscordNetBotManager.
        /// </summary>
        public void ProcessDiscordAudio(byte[] audioData, int length, string username)
        {
            if (_disposed) return;

            var discordSource = GetSource(AudioSourceType.Discord) as DiscordAudioSource;
            discordSource?.ProcessDiscordAudio(audioData, length, username);
        }

        /// <summary>
        /// Update Discord connection state.
        /// Called by DiscordNetBotManager.
        /// </summary>
        public void UpdateDiscordConnectionState(bool connected)
        {
            var discordSource = GetSource(AudioSourceType.Discord) as DiscordAudioSource;
            discordSource?.UpdateConnectionState(connected);
        }

        private void UpdateVoiceRecognizerState()
        {
            try
            {
                VoiceRecognizer.SetMicrophoneInputEnabled(_activeSource == AudioSourceType.LocalMic);
                VoiceRecognizer.SetDiscordInputEnabled(_activeSource == AudioSourceType.Discord);
                VoiceRecognizer.SetWebRtcInputEnabled(_activeSource == AudioSourceType.WebRtc);
            }
            catch (Exception ex)
            {
                Log($"[AudioManager] Error updating VoiceRecognizer: {ex.Message}");
            }
        }

        private int GetWebRtcPort()
        {
            try
            {
                return App.SettingsProvider?.Current?.WebRtc?.Port ?? 8787;
            }
            catch
            {
                return 8787;
            }
        }

        private void Log(string message)
        {
            try { OnLog?.Invoke(message); } catch { }
            try { Console.WriteLine(message); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { StopAsync().GetAwaiter().GetResult(); } catch { }

            lock (_lock)
            {
                foreach (var source in _sources.Values)
                {
                    try { source.Dispose(); } catch { }
                }
                _sources.Clear();
            }

            lock (_instanceLock)
            {
                if (_instance == this)
                    _instance = null;
            }
        }
    }
}
