using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using NAudio.Wave;
using Kinectv1.Tts;
using Discord;
using Discord.Audio;

namespace Kinectv1.Tts
{
    [Flags]
    public enum TtsOutputTarget
    {
        None = 0,
        Local = 1,
        Discord = 2
    }

    internal class TtsPlaybackJob
    {
        public string Text { get; init; }
        public string Speaker { get; init; }
        public TtsOutputTarget Targets { get; init; }
        public int Id { get; init; }
        public CancellationTokenSource Cts { get; init; }
    }

    /// <summary>
    /// Central manager for TTS playback (local + Discord). Two-phase: generate fully, then stream.
    /// Preemption cancels active job cleanly before starting a new one.
    /// </summary>
    public static class TtsPlaybackManager
    {
        private static readonly object _lock = new object();
        private static TtsPlaybackJob _activeJob;
        private static CancellationTokenSource _currentCts;
        private static Task _playbackTask = Task.CompletedTask;
        private static int _jobCounter = 0;
        private static bool _initialized;
        private static readonly SemaphoreSlim _discordStreamLock = new SemaphoreSlim(1, 1);

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
        }

        public static async Task CancelActiveAsync()
        {
            CancellationTokenSource cts;
            Task task;
            lock (_lock)
            {
                cts = _currentCts;
                task = _playbackTask;
            }
            try { cts?.Cancel(); } catch { }
            try { TtsService.MarkExternalCancel(); } catch { }
            if (task != null)
            {
                try { await task.ConfigureAwait(false); } catch { }
            }
        }

        public static void Enqueue(string text, TtsOutputTarget targets, string speaker = null, bool preempt = true)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (targets == TtsOutputTarget.None) return;
            if (!TtsService.IsEnabled()) return;

            Initialize();

            if (preempt)
            {
                try { CancelActiveAsync().GetAwaiter().GetResult(); } catch { }
            }

            var cts = new CancellationTokenSource();
            int id = Interlocked.Increment(ref _jobCounter);
            var job = new TtsPlaybackJob { Text = text, Speaker = speaker, Targets = targets, Id = id, Cts = cts };

            lock (_lock)
            {
                _currentCts = cts;
                _activeJob = job;
            }

            _playbackTask = Task.Run(() => RunJobAsync(job), cts.Token);
        }

        private static async Task RunJobAsync(TtsPlaybackJob job)
        {
            var ct = job.Cts.Token;
            try
            {
                Console.WriteLine($"[Playback] Job {job.Id} start targets={job.Targets} chars={job.Text.Length}");
                var sw = System.Diagnostics.Stopwatch.StartNew();

                var audio = await TtsService.GenerateAudioDataAsync(job.Text, job.Speaker, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) { Console.WriteLine($"[Playback] Job {job.Id} cancelled during generation"); return; }
                if (audio == null || audio.Length == 0) { Console.WriteLine($"[Playback] Job {job.Id} empty audio"); return; }

                var tasks = new ConcurrentBag<Task>();
                if (job.Targets.HasFlag(TtsOutputTarget.Local))
                {
                    tasks.Add(PlayLocalAsync(audio, ct));
                }
                if (job.Targets.HasFlag(TtsOutputTarget.Discord))
                {
                    tasks.Add(PlayDiscordAsync(audio, ct));
                }

                await Task.WhenAll(tasks.ToArray()).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                {
                    Console.WriteLine($"[Playback] Job {job.Id} done in {sw.ElapsedMilliseconds}ms");
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[Playback] Job {job.Id} cancelled (OperationCanceled)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Playback] Job {job.Id} error: {ex.Message}");
            }
            finally
            {
                lock (_lock)
                {
                    if (_activeJob == job) _activeJob = null;
                }
                try { job.Cts.Dispose(); } catch { }
            }
        }

        private static async Task PlayLocalAsync(float[] audio, CancellationToken ct)
        {
            try
            {
                var vol = Math.Max(0f, (float)(Kinectv1.App.SettingsProvider?.Current?.Tts?.LocalVolume ?? 1.0));
                if (vol != 1f)
                {
                    for (int i = 0; i < audio.Length; i++) audio[i] *= vol;
                }
                await AudioDeviceManager.PlayLocallyAsync(audio, TtsService.GetSampleRate(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[Playback][Local] error: {ex.Message}");
            }
        }

        private static async Task PlayDiscordAsync(float[] audio, CancellationToken ct)
        {
            try
            {
                await _discordStreamLock.WaitAsync(ct);
                try
                {
                    var client = Kinectv1.Discord.DiscordNetBotManager.GetClient();
                    if (client == null || !Kinectv1.Discord.DiscordNetBotManager.IsInVoiceChannel) return;
                    var audioClientField = typeof(Kinectv1.Discord.DiscordNetBotManager).GetField("_currentAudioClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    var audioClient = audioClientField?.GetValue(null) as global::Discord.Audio.IAudioClient;
                    if (audioClient == null || audioClient.ConnectionState != global::Discord.ConnectionState.Connected) return;

                float gain = Math.Max(0f, (float)(Kinectv1.App.SettingsProvider?.Current?.Tts?.DiscordVolume ?? 1.0));
                var pcmSrc = FloatsToPcm16(audio, gain);
                int srcRate = TtsService.GetSampleRate();

                byte[] pcm48;
                using (var ms = new MemoryStream(pcmSrc, false))
                using (var raw = new RawSourceWaveStream(ms, new WaveFormat(srcRate, 16, 1)))
                using (var resampler = new MediaFoundationResampler(raw, new WaveFormat(48000, 16, 2)) { ResamplerQuality = 60 })
                using (var outStream = new MemoryStream())
                {
                    byte[] buffer = new byte[resampler.OutputWaveFormat.AverageBytesPerSecond];
                    int read;
                    while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        outStream.Write(buffer, 0, read);
                    }
                    pcm48 = outStream.ToArray();
                }

                const int frameSize = 3840; // 20ms at 48kHz stereo 16-bit
                int frameCount = (pcm48.Length + frameSize - 1) / frameSize;
                var frames = new byte[frameCount][];
                for (int i = 0; i < frameCount; i++)
                {
                    frames[i] = new byte[frameSize];
                    int offset = i * frameSize;
                    int count = Math.Min(frameSize, pcm48.Length - offset);
                    Buffer.BlockCopy(pcm48, offset, frames[i], 0, count);
                    if (count < frameSize)
                        Array.Clear(frames[i], count, frameSize - count);
                }

                using var stream = audioClient.CreatePCMStream(global::Discord.Audio.AudioApplication.Mixed, bitrate: 96000, bufferMillis: 200);

                await audioClient.SetSpeakingAsync(true);
                try
                {
                    foreach (var frame in frames)
                    {
                        ct.ThrowIfCancellationRequested();
                        await stream.WriteAsync(frame, 0, frame.Length, ct);
                        await Task.Delay(20, ct);
                    }
                }
                finally
                {
                    try { await stream.FlushAsync(); } catch { }
                    try { await audioClient.SetSpeakingAsync(false); } catch { }
                }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[Playback][Discord] error: {ex.Message}");
            }
            finally
            {
                try { _discordStreamLock.Release(); } catch { }
            }
        }

        private static byte[] FloatsToPcm16(float[] src, float gain)
        {
            if (gain <= 0f) gain = 1f;
            var dst = new byte[src.Length * 2];
            int j = 0;
            for (int i = 0; i < src.Length; i++)
            {
                float f = src[i] * gain;
                if (f > 0.98f) f = 0.98f; else if (f < -0.98f) f = -0.98f;
                short s = (short)(f * 32767f);
                dst[j++] = (byte)(s & 0xFF);
                dst[j++] = (byte)((s >> 8) & 0xFF);
            }
            return dst;
        }
    }
}
