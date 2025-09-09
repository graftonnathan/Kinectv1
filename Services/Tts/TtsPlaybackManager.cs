using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
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
                    int srcRate = TtsService.GetSampleRate();

                    // apply gain and convert to IEEE float bytes
                    var floatBytes = new byte[audio.Length * 4];
                    if (gain != 1f)
                    {
                        for (int i = 0; i < audio.Length; i++) audio[i] *= gain;
                    }
                    Buffer.BlockCopy(audio, 0, floatBytes, 0, floatBytes.Length);

                    // resample and convert to 48kHz stereo PCM16
                    byte[] pcm48;
                    var floatFormat = WaveFormat.CreateIeeeFloatWaveFormat(srcRate, 1);
                    var buffer = new BufferedWaveProvider(floatFormat) { BufferLength = floatBytes.Length };
                    buffer.AddSamples(floatBytes, 0, floatBytes.Length);
                    ISampleProvider provider = buffer.ToSampleProvider();
                    var resampled = new WdlResamplingSampleProvider(provider, 48000);
                    var stereo = new MonoToStereoSampleProvider(resampled);
                    var pcm16 = new SampleToWaveProvider16(stereo);
                    using (var outStream = new MemoryStream())
                    {
                        byte[] tmp = new byte[pcm16.WaveFormat.AverageBytesPerSecond];
                        int read;
                        while ((read = pcm16.Read(tmp, 0, tmp.Length)) > 0)
                        {
                            outStream.Write(tmp, 0, read);
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
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        for (int i = 0; i < frames.Length; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            await stream.WriteAsync(frames[i], 0, frames[i].Length, ct);
                            var target = (i + 1) * 20;
                            var wait = target - sw.ElapsedMilliseconds;
                            if (wait > 0)
                                await Task.Delay((int)wait, ct);
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

    }
}
