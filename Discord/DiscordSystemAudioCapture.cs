using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using Kinectv1.Settings;

namespace Kinectv1.Discord
{
    // Lightweight system audio (loopback) capture to pipe Discord client output into the STT pipeline
    // Avoids Discord voice gateway + native opus/libsodium. Captures default output mix.
    public static class DiscordSystemAudioCapture
    {
        private static WasapiLoopbackCapture _capture;
        private static volatile bool _isRunning;
        private static readonly object _lock = new object();

        public static bool IsRunning => _isRunning;

        public static void Start()
        {
            lock (_lock)
            {
                if (_isRunning) return;

                // Guard: Do not start system loopback when Discord voice ingest is active
                var audioMode = Kinectv1.App.SettingsProvider?.Current?.App?.InputMode ?? AudioInMode.LocalMic;
                if (audioMode == AudioInMode.DiscordVoice)
                {
                    Console.WriteLine("🚫 System loopback capture blocked - Discord voice ingest is active");
                    return;
                }

                // Guard: In WebRTC mode, all audio input should come from the WebUI.
                // System loopback can include mic sidetone / app audio and will bleed into STT.
                if (audioMode == AudioInMode.WebRtcVoice)
                {
                    Console.WriteLine("🚫 System loopback capture blocked - WebRTC mode is active");
                    return;
                }

                try
                {
                    _capture = new WasapiLoopbackCapture(); // default output device

                    _capture.DataAvailable += OnDataAvailable;
                    _capture.RecordingStopped += OnRecordingStopped;

                    _capture.StartRecording();
                    _isRunning = true;
                    Console.WriteLine("🎧 System loopback capture started (default output)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Failed to start system loopback capture: {ex.Message}");
                    Stop();
                }
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                try
                {
                    if (_capture != null)
                    {
                        _capture.DataAvailable -= OnDataAvailable;
                        _capture.RecordingStopped -= OnRecordingStopped;
                        if (_isRunning)
                            _capture.StopRecording();
                        _capture.Dispose();
                        _capture = null;
                    }
                }
                catch { /* ignore */ }
                finally
                {
                    if (_isRunning)
                    {
                        Console.WriteLine("🛑 System loopback capture stopped");
                    }
                    _isRunning = false;
                }
            }
        }

        private static void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                Console.WriteLine($"⚠️ Loopback capture error: {e.Exception.Message}");
            }
        }

        private static void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            try
            {
                if (e.BytesRecorded <= 0) return;
                if (!DiscordNetBotManager.IsRunning) return;

                // Convert from device format to 48kHz stereo 16-bit PCM
                var srcFormat = (_capture?.WaveFormat) ?? new WaveFormat(48000, 16, 2);
                byte[] pcm48kStereoBytes;

                using (var srcMs = new MemoryStream(e.Buffer, 0, e.BytesRecorded, writable: false))
                using (var src = new RawSourceWaveStream(srcMs, srcFormat))
                using (var resampler = new MediaFoundationResampler(src, new WaveFormat(48000, 16, 2)))
                using (var outMs = new MemoryStream())
                {
                    resampler.ResamplerQuality = 60;
                    var buffer = new byte[8192];
                    int read;
                    while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        outMs.Write(buffer, 0, read);
                    }
                    pcm48kStereoBytes = outMs.ToArray();
                }

                if (pcm48kStereoBytes != null && pcm48kStereoBytes.Length > 0)
                {
                    // Pipe into Discord.Net processing path
                    DiscordNetBotManager.ProcessVoiceData(pcm48kStereoBytes, "DiscordOutput");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Loopback capture processing error: {ex.Message}");
            }
        }
    }
}
