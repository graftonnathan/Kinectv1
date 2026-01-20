using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1.Voice
{
    /// <summary>
    /// Audio source for Discord voice channel input.
    /// Wraps Discord audio processing and handles resampling 48kHz stereo ? 16kHz mono.
    /// Outputs 16kHz mono PCM16 frames.
    /// 
    /// Note: Unlike LocalMic and WebRTC, Discord audio is pushed from DiscordNetBotManager
    /// via the ProcessDiscordAudio method. This source acts as a bridge/normalizer.
    /// </summary>
    public sealed class DiscordAudioSource : AudioSourceBase
    {
        private readonly object _lock = new object();

        public override AudioSourceType SourceType => AudioSourceType.Discord;

        /// <summary>
        /// Whether Discord bot is connected to a voice channel.
        /// </summary>
        public bool IsConnected => Kinectv1.Discord.DiscordNetBotManager.IsInVoiceChannel;

        public override Task StartAsync(CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_state != AudioSourceState.Stopped) return Task.CompletedTask;
                SetState(AudioSourceState.Starting);
            }

            // Discord audio is pushed externally, not pulled
            // We just set up the state and wait for ProcessDiscordAudio calls
            _isEnabled = true;
            SetState(AudioSourceState.Running);
            Log("[DiscordSource] Started (waiting for audio from Discord bot)");

            return Task.CompletedTask;
        }

        public override Task StopAsync()
        {
            lock (_lock)
            {
                if (_state == AudioSourceState.Stopped) return Task.CompletedTask;
                SetState(AudioSourceState.Stopped);
            }

            _isEnabled = false;
            Log("[DiscordSource] Stopped");

            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); } catch { }
        }

        /// <summary>
        /// Process incoming Discord audio (48kHz stereo PCM16).
        /// Called by DiscordNetBotManager when voice data arrives.
        /// </summary>
        /// <param name="discordAudio">Raw PCM from Discord (48kHz stereo 16-bit)</param>
        /// <param name="audioLength">Number of valid bytes</param>
        /// <param name="username">Discord username for the speaker</param>
        public void ProcessDiscordAudio(byte[] discordAudio, int audioLength, string username = "Discord")
        {
            if (!_isEnabled || discordAudio == null || audioLength <= 0) return;

            try
            {
                // Use existing processor for 48k stereo ? 16k mono conversion
                var (processedAudio, processedLength, rmsLevel) = 
                    Kinectv1.Discord.DiscordAudioProcessor.ProcessDiscordAudio(discordAudio, audioLength, username);

                if (processedAudio == null || processedLength <= 0) return;

                // Create normalized frame
                var frame = new NormalizedAudioFrame(
                    processedAudio,
                    processedLength,
                    rmsLevel,
                    AudioSourceType.Discord,
                    username
                );

                // Emit RMS for UI meter
                EmitRms(frame.Rms);

                // Emit frame for processing
                EmitFrame(frame);
            }
            catch (Exception ex)
            {
                Log($"[DiscordSource] Frame processing error: {ex.Message}");
            }
        }

        /// <summary>
        /// Update connection state based on Discord bot status.
        /// Called by DiscordNetBotManager.
        /// </summary>
        public void UpdateConnectionState(bool connected)
        {
            if (connected && _state == AudioSourceState.Running)
            {
                SetState(AudioSourceState.Connected);
            }
            else if (!connected && _state == AudioSourceState.Connected)
            {
                SetState(AudioSourceState.Running);
            }
        }
    }
}
