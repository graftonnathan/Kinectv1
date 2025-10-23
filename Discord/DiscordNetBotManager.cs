using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Audio;
using Discord.Audio.Streams;
using Discord.Commands;
using Discord.WebSocket;
using System.Reflection;
using System.Collections.Concurrent;
using System.Diagnostics;
using Discord.Net; // Added for HttpException
using Kinectv1.Tts;
using System.Security.Cryptography; // added for single-instance hash
using System.Text; // added for single-instance hash
using System.IO;
using System.Threading.Channels; // TTS queue
using NAudio.Wave; // resampler

namespace Kinectv1.Discord
{
    public static class DiscordNetBotManager
    {
        private static System.Threading.Mutex _instanceMutex;
        private static bool _ownsMutex;
        private static readonly SemaphoreSlim _voiceOpLock = new SemaphoreSlim(1, 1);
        public static SemaphoreSlim VoiceOpLock => _voiceOpLock;
        public static event Action<string> OnBotStatusChanged; public static event Action<string, string> OnVoiceMessageReceived; public static event Action<string> OnErrorOccurred;
        private static DiscordSocketClient _client; private static CommandService _commands; private static IAudioClient _currentAudioClient; private static AudioOutStream _discordPcmStream;
        private static bool _isRunning = false; private static bool _isInitialized = false; private static ulong? _currentChannelId = null; private static string _currentChannelName = null;
        private static CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private static int _messageHandlerHooked = 0; private static int _modulesRegistered = 0; private static int _clientCreated = 0; private static int _commandServiceCreated = 0; private static int _startupInProgress = 0; private static int _shutdownInProgress = 0; private static int _gatewayResetInProgress = 0; private static int _sessionResetInProgress = 0;

        // === Refactored TTS state (persistent stream + queue + generation guard) ===
        private class TtsJob { public string Text; public string Speaker; public CancellationTokenSource Cts; public TaskCompletionSource<bool> Tcs; public int Generation; }
        private static readonly Channel<TtsJob> _ttsChannel = Channel.CreateBounded<TtsJob>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        private static readonly object _ttsLock = new object();
        private static CancellationTokenSource _currentTtsCts; // active job CTS
        private static volatile bool _ttsWorkerRunning = false;
        private static int _ttsGeneration = 0; // increment per enqueue
        private static CancellationTokenSource _speakingHoldCts; // post playback hold

        // Diagnostics for voice join
        private static string _lastObservedSessionId; private static int _joinSequence = 0;
        private static readonly ConcurrentDictionary<ulong,(AudioInStream stream, CancellationTokenSource cts)> _activeInputStreams = new(); private static int _unobservedHooked = 0;

        // ================== Shutdown / lifecycle ==================
        public static async Task ShutdownAsync()
        {
            if (Interlocked.CompareExchange(ref _shutdownInProgress, 1, 0) != 0) return;
            try
            {
                if (!_isRunning && _client == null && _currentAudioClient == null) return;
                _isRunning = false;
                try { CancelCurrentTts(); } catch { }
                await _voiceOpLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_currentAudioClient != null)
                    {
                        try { await _currentAudioClient.StopAsync().ConfigureAwait(false); } catch { }
                        try { _currentAudioClient.Dispose(); } catch { }
                        _currentAudioClient = null; _currentChannelId = null; _currentChannelName = null;
                    }
                }
                finally { _voiceOpLock.Release(); }
                if (_client != null)
                {
                    try { await _client.StopAsync().ConfigureAwait(false); } catch { }
                    try { await _client.LogoutAsync().ConfigureAwait(false); } catch { }
                    try { _client.Dispose(); } catch { }
                    _client = null;
                }
                try { _discordPcmStream?.Dispose(); } catch { _discordPcmStream = null; }
                _commands = null; Interlocked.Exchange(ref _messageHandlerHooked, 0); Interlocked.Exchange(ref _modulesRegistered, 0); Interlocked.Exchange(ref _clientCreated, 0); Interlocked.Exchange(ref _commandServiceCreated, 0);
                try { _ttsChannel.Writer.TryComplete(); } catch { }
                OnBotStatusChanged?.Invoke("Disconnected");
            }
            finally
            {
                try { _cancellationTokenSource?.Dispose(); } catch { }
                _cancellationTokenSource = null; Interlocked.Exchange(ref _shutdownInProgress, 0);
                try { if (_ownsMutex) { _instanceMutex?.ReleaseMutex(); _ownsMutex = false; } } catch { }
                try { _instanceMutex?.Dispose(); } catch { _instanceMutex = null; }
            }
        }

        public static async Task LeaveAllVoiceAsync()
        {
            try
            {
                await _voiceOpLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    var client = _currentAudioClient;
                    if (client != null)
                    {
                        try { await client.StopAsync().ConfigureAwait(false); } catch { }
                        try { client.Dispose(); } catch { }
                    }
                }
                finally { _voiceOpLock.Release(); }
            }
            catch { }
            finally
            {
                try { _discordPcmStream?.Dispose(); } catch { }
                _discordPcmStream = null; _currentAudioClient = null; _currentChannelId = null; _currentChannelName = null;
            }
        }

        public static async Task CloseGatewayAsync()
        {
            try
            {
                var client = _client; if (client == null) return;
                try { await client.StopAsync().ConfigureAwait(false); } catch { }
                try { await client.LogoutAsync().ConfigureAwait(false); } catch { }
                try { client.Dispose(); } catch { }
            }
            finally
            {
                try { _discordPcmStream?.Dispose(); } catch { _discordPcmStream = null; }
                _client = null; _isRunning = false; _isInitialized = false; _commands = null;
                Interlocked.Exchange(ref _messageHandlerHooked, 0); Interlocked.Exchange(ref _modulesRegistered, 0); Interlocked.Exchange(ref _clientCreated, 0); Interlocked.Exchange(ref _commandServiceCreated, 0);
                try { if (_ownsMutex) { _instanceMutex?.ReleaseMutex(); _ownsMutex = false; } } catch { }
                try { _instanceMutex?.Dispose(); } catch { _instanceMutex = null; }
            }
        }

        public static void LeaveAllVoice()
        {
            try
            {
                var client = _currentAudioClient; if (client != null) { try { client.StopAsync().Wait(1000); } catch { } try { client.Dispose(); } catch { } }
            }
            catch { }
            finally
            { try { _discordPcmStream?.Dispose(); } catch { _discordPcmStream = null; } _currentAudioClient = null; _currentChannelId = null; _currentChannelName = null; }
        }

        public static bool IsRunning => _isRunning; public static bool IsInVoiceChannel => _currentAudioClient != null && _currentAudioClient.ConnectionState == ConnectionState.Connected; public static DiscordSocketClient GetClient() => _client;

        public static async Task<bool> TestConfigurationAsync()
        { try { var token = Kinectv1.App.SettingsProvider?.Current?.Discord?.Token; var enabled = Kinectv1.App.SettingsProvider?.Current?.Discord?.Enabled ?? false; if (!enabled) return false; if (string.IsNullOrEmpty(token) || token.Length < 50) { OnErrorOccurred?.Invoke("Discord bot token invalid or too short. Check Discord token in settings."); return false; } return true; } catch (Exception ex) { OnErrorOccurred?.Invoke($"Discord config test failed: {ex.Message}"); return false; } }

        public static async Task<bool> StartAsync()
        {
            if (Interlocked.CompareExchange(ref _startupInProgress, 1, 0) != 0) return _isRunning;
            try
            {
                if (_isRunning) return true;
                var nativesLoaded = DiscordNativeLoader.LoadNativeLibraries(); if (!nativesLoaded) Console.WriteLine("Native libs not fully loaded");
                if (!await TestConfigurationAsync()) return false;
                try
                {
                    var token = Kinectv1.App.SettingsProvider?.Current?.Discord?.Token ?? string.Empty;
                    byte[] tokenHashBytes; using (var sha = SHA256.Create()) tokenHashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(token));
                    var tokenKey = Convert.ToBase64String(tokenHashBytes); var mutexName = $"Global\\DiscordBot_{tokenKey}"; _instanceMutex = new System.Threading.Mutex(true, mutexName, out _ownsMutex); if (!_ownsMutex) { OnErrorOccurred?.Invoke("Another instance of this bot is already running on this token. Aborting to avoid 4006 loops."); return false; }
                }
                catch (Exception mex) { OnErrorOccurred?.Invoke($"Single-instance check failed: {mex.Message}"); }
                if (Interlocked.CompareExchange(ref _clientCreated, 1, 0) == 0)
                {
                    _client = new DiscordSocketClient(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates | GatewayIntents.MessageContent | GatewayIntents.GuildMessages, LogLevel = LogSeverity.Debug, ConnectionTimeout = 30000, DefaultRetryMode = RetryMode.AlwaysRetry, MessageCacheSize = 100 });
                    _client.Log += Log; _client.Ready += Client_Ready; _client.UserVoiceStateUpdated += Client_UserVoiceStateUpdated; _client.VoiceServerUpdated += Client_VoiceServerUpdated; _client.Disconnected += Client_Disconnected;
                }
                if (Interlocked.CompareExchange(ref _messageHandlerHooked, 1, 0) == 0) _client.MessageReceived += HandleCommandAsync;
                if (Interlocked.CompareExchange(ref _commandServiceCreated, 1, 0) == 0) _commands = new CommandService(new CommandServiceConfig { DefaultRunMode = RunMode.Async, LogLevel = LogSeverity.Info });
                if (Interlocked.CompareExchange(ref _modulesRegistered, 1, 0) == 0) await _commands.AddModuleAsync<DiscordNetVoiceCommands>(null);
                await _client.LoginAsync(TokenType.Bot, Kinectv1.App.SettingsProvider?.Current?.Discord?.Token); await _client.StartAsync();
                var readyTimeout = DateTime.UtcNow.AddSeconds(30); while (_client.ConnectionState != ConnectionState.Connected && DateTime.UtcNow < readyTimeout) await Task.Delay(250);
                _isRunning = _client.ConnectionState == ConnectionState.Connected; OnBotStatusChanged?.Invoke(_isRunning ? "Connected" : "Failed"); return _isRunning;
            }
            catch (Exception ex)
            { OnErrorOccurred?.Invoke($"Failed to start: {ex.Message}"); _isRunning = false; Interlocked.Exchange(ref _clientCreated, 0); Interlocked.Exchange(ref _messageHandlerHooked, 0); Interlocked.Exchange(ref _commandServiceCreated, 0); Interlocked.Exchange(ref _modulesRegistered, 0); try { if (_ownsMutex) { _instanceMutex?.ReleaseMutex(); _ownsMutex = false; } } catch { } try { _instanceMutex?.Dispose(); } catch { _instanceMutex = null; } return false; }
            finally { Interlocked.Exchange(ref _startupInProgress, 0); }
        }

        public static void ProcessVoiceData(byte[] audioData, string username)
        { try { if (!_isRunning || !(Kinectv1.App.SettingsProvider?.Current?.Discord?.Enabled ?? false)) return; if (!VoiceRecognizer.IsDiscordInputEnabled()) return; if (audioData?.Length < 100) return; var (processedAudio, processedLength, rawRms) = DiscordAudioProcessor.ProcessDiscordAudio(audioData, audioData.Length, username); float scaledRms = rawRms > 0f ? Math.Min(10000f, (rawRms / 32768f) * 10000f) : 0f; VoiceRecognizer.OnDiscordRmsLevel?.Invoke(scaledRms); if (processedAudio != null && processedLength > 320 && VoiceRecognizer.IsReady()) { SpeakerIdentifier.SetDiscordSpeakerHint(username); VoiceRecognizer.ProcessExternalAudio(processedAudio, processedLength, $"Discord:{username}"); } } catch (Exception ex) { OnErrorOccurred?.Invoke($"Voice processing error: {ex.Message}"); } }

        public static async Task SetVoiceConnection(IAudioClient audioClient, ulong channelId, string channelName)
        {
            try
            {
                _currentAudioClient = audioClient; _currentChannelId = channelId; _currentChannelName = channelName;
                try { _discordPcmStream?.Dispose(); } catch { } _discordPcmStream = _currentAudioClient?.CreatePCMStream(AudioApplication.Mixed, bitrate: 96000, bufferMillis: 200);
                if (_currentAudioClient != null)
                {
                    _currentAudioClient.StreamCreated += async (userId, audioStream) => { if (_client != null && userId == _client.CurrentUser.Id) return; await HandleUserAudioStream(userId, audioStream); };
                    foreach (var stream in _currentAudioClient.GetStreams()) { if (_client != null && stream.Key == _client.CurrentUser.Id) continue; await HandleUserAudioStream(stream.Key, stream.Value); }
                }
                OnBotStatusChanged?.Invoke($"In voice channel: {channelName}");
            }
            catch (Exception ex) { OnErrorOccurred?.Invoke($"Voice connection setup error: {ex.Message}"); }
        }

        private static async Task HandleUserAudioStream(ulong userId, AudioInStream audioStream)
        {
            _ = Task.Run(async () =>
            {
                var cts = new CancellationTokenSource(); _activeInputStreams[userId] = (audioStream, cts);
                try
                {
                    var buffer = new byte[3840];
                    while (!cts.IsCancellationRequested && _currentAudioClient?.ConnectionState == ConnectionState.Connected)
                    {
                        int bytesRead = 0; try { bytesRead = await audioStream.ReadAsync(buffer, 0, buffer.Length); } catch { break; }
                        if (bytesRead > 0) { var name = ResolveUserDisplayName(userId); ProcessVoiceData(buffer.AsSpan(0, bytesRead).ToArray(), name); } else await Task.Delay(1, cts.Token);
                    }
                }
                catch { }
                finally { _activeInputStreams.TryRemove(userId, out var tup); try { tup.stream?.Dispose(); } catch { } try { tup.cts?.Dispose(); } catch { } }
            });
        }

        public static async Task ForceCloseAllVoicePipesAsync()
        {
            try
            {
                foreach (var kv in _activeInputStreams.ToArray()) if (_activeInputStreams.TryRemove(kv.Key, out var tuple)) { try { tuple.cts.Cancel(); } catch { } try { tuple.stream.Dispose(); } catch { } try { tuple.cts.Dispose(); } catch { } }
                var ac = _currentAudioClient; if (ac != null) { try { await ac.StopAsync(); } catch { } try { ac.Dispose(); } catch { } }
            }
            finally { try { _discordPcmStream?.Dispose(); } catch { _discordPcmStream = null; } _currentAudioClient = null; _currentChannelId = null; _currentChannelName = null; }
        }

        public static string GetStatusSummary()
        { try { var enabled = Kinectv1.App.SettingsProvider?.Current?.Discord?.Enabled ?? false; var prefix = Kinectv1.App.SettingsProvider?.Current?.Discord?.Prefix; var connectionState = _client?.ConnectionState.ToString() ?? "Disconnected"; var guilds = _client?.Guilds; var latency = _client?.Latency ?? 0; var voiceStatus = _currentAudioClient != null ? $"In {_currentChannelName}" : "Not connected"; var guildCount = guilds?.Count ?? 0; var channelList = guildCount > 0 ? string.Join(", ", guilds.Select(g => g.GetUser(_client.CurrentUser.Id)?.VoiceChannel?.Name).Distinct().Where(n => !string.IsNullOrEmpty(n)).Take(5)) : "None"; return $"Bot enabled: {enabled}\nRunning: {_isRunning}\nCommand prefix: {prefix}\nConnection state: {connectionState}\nGuilds: {guildCount}\nLatency: {latency}ms\nVoice connection: {voiceStatus}\nActive channels: {channelList}"; } catch (Exception ex) { return $"Status error: {ex.Message}"; } }

        public static async Task<IAudioClient> JoinVoiceAsync(IVoiceChannel voiceChannel, int maxRetries = 3)
        {
            if (voiceChannel == null) throw new ArgumentNullException(nameof(voiceChannel)); var seq = Interlocked.Increment(ref _joinSequence); var preJoinSession = _lastObservedSessionId; Console.WriteLine($"[JoinDBG] seq={seq} guild={voiceChannel.Guild.Id} chan={voiceChannel.Name} start lastSession={preJoinSession ?? "None"}"); bool performedPreClean = false; try { if (voiceChannel is SocketVoiceChannel svc) { var me = svc.Guild.CurrentUser; if (me?.VoiceChannel != null && me.VoiceChannel.Id != voiceChannel.Id) { performedPreClean = true; try { await me.VoiceChannel.DisconnectAsync(); } catch { } await WaitForVoiceNullAsync(seq, 1500); } } } catch { } if (performedPreClean) { await Task.Delay(2000); }
            var sw = Stopwatch.StartNew();
            for (int attempt = 1; attempt <= Math.Max(1, maxRetries); attempt++)
            {
                var attemptSessionPre = _lastObservedSessionId;
                try
                {
                    Console.WriteLine($"[JoinDBG] seq={seq} attempt={attempt} session(before)={attemptSessionPre ?? "None"}");
                    var connectTask = voiceChannel.ConnectAsync(selfDeaf: false, selfMute: false); var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(20))); if (completed != connectTask) throw new TimeoutException("ConnectAsync timed out after 20s"); var ac = await connectTask; if (ac == null) throw new InvalidOperationException("ConnectAsync returned null"); await Task.Delay(300); var afterSession = _lastObservedSessionId; if (attemptSessionPre != null && afterSession == attemptSessionPre) { Console.WriteLine($"[JoinDBG] seq={seq} WARNING: session id unchanged ({afterSession}) after successful ConnectAsync"); } Console.WriteLine($"[JoinDBG] seq={seq} success after {sw.ElapsedMilliseconds}ms state={ac.ConnectionState} session(now)={afterSession ?? "None"}"); return ac;
                }
                catch (HttpException hex) when (hex.DiscordCode.HasValue && (int)hex.DiscordCode.Value == 4006) { await ForceCloseAllVoicePipesAsync(); await WaitForVoiceNullAsync(seq, 1000); if (attempt == maxRetries) throw; await Task.Delay(2500); }
                catch (TimeoutException) { if (attempt == maxRetries) throw; await ForceCloseAllVoicePipesAsync(); await WaitForVoiceNullAsync(seq, 800); await Task.Delay(1200); }
                catch (Exception) { if (attempt == maxRetries) throw; await ForceCloseAllVoicePipesAsync(); await WaitForVoiceNullAsync(seq, 600); await Task.Delay(800); }
            }
            throw new InvalidOperationException($"Voice join failed seq={seq}");
        }

        private static async Task WaitForVoiceNullAsync(int seq, int timeoutMs)
        { try { var start = Stopwatch.StartNew(); while (start.ElapsedMilliseconds < timeoutMs) { bool inChannel = _client?.Guilds.Any(g => g.CurrentUser?.VoiceChannel != null) ?? false; if (!inChannel) { Console.WriteLine($"[JoinDBG] seq={seq} voice cleared after {start.ElapsedMilliseconds}ms"); return; } await Task.Delay(100); } Console.WriteLine($"[JoinDBG] seq={seq} voice NOT cleared after {timeoutMs}ms (session={_lastObservedSessionId ?? "None"})"); } catch { } }

        public static async Task OnVoiceChannelLeft()
        { try { foreach (var kv in _activeInputStreams.ToArray()) if (_activeInputStreams.TryRemove(kv.Key, out var tuple)) { try { tuple.cts.Cancel(); } catch { } try { tuple.stream.Dispose(); } catch { } try { tuple.cts.Dispose(); } catch { } } if (_currentAudioClient != null) { try { await _currentAudioClient.StopAsync(); } catch { } try { _currentAudioClient.Dispose(); } catch { } _currentAudioClient = null; } try { _discordPcmStream?.Dispose(); } catch { _discordPcmStream = null; } _currentChannelId = null; _currentChannelName = null; } catch (Exception ex) { Console.WriteLine($"leave cleanup error: {ex.Message}"); } }

        public static async Task FullShutdownAsync()
        { try { await _voiceOpLock.WaitAsync().ConfigureAwait(false); try { var client = _client; if (client != null) foreach (var g in client.Guilds) { try { var me = g.CurrentUser; if (me?.VoiceChannel != null) { try { await me.VoiceChannel.DisconnectAsync(); } catch { } try { await me.ModifyAsync(p => p.Channel = null); } catch { } } } catch { } } } finally { _voiceOpLock.Release(); } try { await LeaveAllVoiceAsync(); } catch { } try { var c = _client; if (c != null) { try { await c.StopAsync().ConfigureAwait(false); } catch { } try { await c.LogoutAsync().ConfigureAwait(false); } catch { } try { c.Dispose(); } catch { } _client = null; } } catch { } } catch { } }

        public static void ForceImmediateVoiceClose()
        { try { var client = _client; if (client != null) foreach (var g in client.Guilds) { try { var me = g.CurrentUser; if (me?.VoiceChannel != null) { try { me.VoiceChannel.DisconnectAsync().GetAwaiter().GetResult(); } catch { } try { me.ModifyAsync(p => p.Channel = null).GetAwaiter().GetResult(); } catch { } } } catch { } } var ac = _currentAudioClient; if (ac != null) { try { ac.StopAsync().GetAwaiter().GetResult(); } catch { } try { ac.Dispose(); } catch { } _currentAudioClient = null; } try { _discordPcmStream?.Dispose(); } catch { _discordPcmStream = null; } if (client != null) { try { client.StopAsync().GetAwaiter().GetResult(); } catch { } try { client.LogoutAsync().GetAwaiter().GetResult(); } catch { } try { client.Dispose(); } catch { } _client = null; } } catch { } }

        // ================== Refactored TTS public enqueue ==================
        public static Task<bool> SendTtsToDiscordAsync(string text, string speakerRefId = null)
        {
            if (string.IsNullOrWhiteSpace(text) || !IsInVoiceChannel || !TtsService.IsEnabled()) return Task.FromResult(false);
            var cts = new CancellationTokenSource(); var prev = Interlocked.Exchange(ref _currentTtsCts, cts); if (prev != null) { try { prev.Cancel(); } catch { } try { prev.Dispose(); } catch { } }
            var gen = Interlocked.Increment(ref _ttsGeneration);
            var job = new TtsJob { Text = text, Speaker = speakerRefId, Cts = cts, Generation = gen, Tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
            if (!_ttsChannel.Writer.TryWrite(job)) job.Tcs.TrySetResult(false); else StartTtsWorkerIfNeeded();
            return job.Tcs.Task;
        }

        private static void StartTtsWorkerIfNeeded()
        {
            if (_ttsWorkerRunning) return; lock (_ttsLock) { if (_ttsWorkerRunning) return; _ttsWorkerRunning = true; _ = Task.Run(async () => { try { while (await _ttsChannel.Reader.WaitToReadAsync()) { if (!_ttsChannel.Reader.TryRead(out var job)) continue; bool ok = false; try { ok = await SendTtsToDiscordCoreAsync(job).ConfigureAwait(false); } catch (OperationCanceledException) { ok = false; } catch (Exception ex) { Console.WriteLine($"[Discord][TTS] job error: {ex.Message}"); } finally { job.Tcs.TrySetResult(ok); } } } finally { lock (_ttsLock) { _ttsWorkerRunning = false; } } }); } }

        private static async Task<bool> SendTtsToDiscordCoreAsync(TtsJob job)
        {
            var localGen = job.Generation; var ct = job.Cts.Token;
            if (_currentAudioClient == null || _currentAudioClient.ConnectionState != ConnectionState.Connected) return false;
            // Synthesis
            var floatData = await TtsService.GenerateAudioDataAsync(job.Text, job.Speaker, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || floatData == null || floatData.Length == 0) return false;
            // Volume scaling (discord specific optional setting) – fallback 1.0
            float vol = 1.0f;
            for (int i = 0; i < floatData.Length; i++) { var f = floatData[i] * vol; if (f > 0.98f) f = 0.98f; else if (f < -0.98f) f = -0.98f; floatData[i] = f; }
            // Convert float -> 16-bit PCM (24k mono)
            var monoPcm = new byte[floatData.Length * 2]; int bp = 0; foreach (var f in floatData) { short s = (short)Math.Round(f * 32767f); monoPcm[bp++] = (byte)(s & 0xFF); monoPcm[bp++] = (byte)((s >> 8) & 0xFF); }
            // Resample to 48k stereo via MediaFoundationResampler
            using var srcStream = new MemoryStream(monoPcm, writable: false);
            using var raw = new RawSourceWaveStream(srcStream, new WaveFormat(24000, 16, 1));
            using var resampler = new MediaFoundationResampler(raw, new WaveFormat(48000, 16, 2)) { ResamplerQuality = 60 };

            CancelSpeakingHoldSafe();
            if (localGen < Volatile.Read(ref _ttsGeneration)) return false; // superseded before start
            try { await _currentAudioClient.SetSpeakingAsync(true).ConfigureAwait(false); } catch { }
            await Task.Delay(60, ct).ConfigureAwait(false); // allow state propagate

            const int frameBytes = 3840; // 20ms @48k stereo 16-bit
            var carry = new byte[frameBytes]; int carryLen = 0; var buf = new byte[8192]; int n; var sw = Stopwatch.StartNew(); int frames = 0; int written = 0;
            try
            {
                while ((n = resampler.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested(); if (localGen != Volatile.Read(ref _ttsGeneration)) break;
                    int offset = 0;
                    while (offset < n)
                    {
                        int toCopy = Math.Min(frameBytes - carryLen, n - offset); Buffer.BlockCopy(buf, offset, carry, carryLen, toCopy); carryLen += toCopy; offset += toCopy;
                        if (carryLen == frameBytes)
                        {
                            long targetMs = frames * 20L; long now = sw.ElapsedMilliseconds; if (now < targetMs) { int delay = (int)(targetMs - now); if (delay > 0) await Task.Delay(delay, ct).ConfigureAwait(false); }
                            var stream = _discordPcmStream ?? _currentAudioClient.CreatePCMStream(AudioApplication.Mixed, bitrate: 96000, bufferMillis: 200); if (_discordPcmStream == null) _discordPcmStream = stream;
                            await stream.WriteAsync(carry, 0, frameBytes, ct).ConfigureAwait(false); written += frameBytes; frames++; carryLen = 0;
                        }
                    }
                }
                if (carryLen > 0 && !ct.IsCancellationRequested)
                {
                    Array.Clear(carry, carryLen, frameBytes - carryLen);
                    long targetMs = frames * 20L; long now = sw.ElapsedMilliseconds; if (now < targetMs) { int delay = (int)(targetMs - now); if (delay > 0) await Task.Delay(delay, ct).ConfigureAwait(false); }
                    var stream = _discordPcmStream ?? _currentAudioClient.CreatePCMStream(AudioApplication.Mixed, bitrate: 96000, bufferMillis: 200); if (_discordPcmStream == null) _discordPcmStream = stream;
                    await stream.WriteAsync(carry, 0, frameBytes, ct).ConfigureAwait(false); written += frameBytes; frames++;
                }
                try { await (_discordPcmStream?.FlushAsync(ct) ?? Task.CompletedTask).ConfigureAwait(false); } catch { }
            }
            finally { _ = SpeakingIdleHoldAsync(localGen); }
            return !ct.IsCancellationRequested && localGen == Volatile.Read(ref _ttsGeneration) && written > 0;
        }

        private static void CancelSpeakingHoldSafe() { try { _speakingHoldCts?.Cancel(); } catch { } try { _speakingHoldCts?.Dispose(); } catch { } _speakingHoldCts = null; }
        private static async Task SpeakingIdleHoldAsync(int generation)
        { CancelSpeakingHoldSafe(); var cts = new CancellationTokenSource(); _speakingHoldCts = cts; try { await Task.Delay(250, cts.Token).ConfigureAwait(false); if (generation == Volatile.Read(ref _ttsGeneration) && _currentAudioClient != null && _currentAudioClient.ConnectionState == ConnectionState.Connected) await _currentAudioClient.SetSpeakingAsync(false).ConfigureAwait(false); } catch (OperationCanceledException) { } catch { } finally { try { cts.Dispose(); } catch { } if (_speakingHoldCts == cts) _speakingHoldCts = null; } }

        public static void CancelCurrentTts() { try { _currentTtsCts?.Cancel(); } catch { } }

        private static string ResolveUserDisplayName(ulong userId)
        { try { if (_client != null && _currentChannelId.HasValue) { var chan = _client.GetChannel(_currentChannelId.Value) as SocketVoiceChannel; var gu = chan?.Guild?.GetUser(userId); if (gu != null) { if (!string.IsNullOrWhiteSpace(gu.Nickname)) return gu.Nickname; if (!string.IsNullOrWhiteSpace(gu.Username)) return gu.Username; return gu.DisplayName; } } var user = _client?.GetUser(userId); if (user != null) { var name = (user as SocketUser)?.Username; if (!string.IsNullOrWhiteSpace(name)) return name; } } catch { } return $"User{userId}"; }

        private static Task Log(LogMessage msg) { Console.WriteLine($"[{msg.Severity}] {msg.Source}: {msg.Message}"); if (msg.Exception != null) Console.WriteLine(msg.Exception); return Task.CompletedTask; }
        private static Task Client_Ready() { Console.WriteLine($"?? Discord.Net bot ready as {_client.CurrentUser.Username}#{_client.CurrentUser.Discriminator}"); _isInitialized = true; OnBotStatusChanged?.Invoke($"Ready as {_client.CurrentUser.Username}"); return Task.CompletedTask; }
        private static async Task HandleCommandAsync(SocketMessage messageParam) { try { var message = messageParam as SocketUserMessage; if (message == null) return; int argPos = 0; var prefix = Kinectv1.App.SettingsProvider?.Current?.Discord?.Prefix; if (!(message.HasStringPrefix(prefix, ref argPos) || message.HasMentionPrefix(_client.CurrentUser, ref argPos)) || message.Author.IsBot) return; var context = new SocketCommandContext(_client, message); var result = await _commands.ExecuteAsync(context, argPos, null); if (!result.IsSuccess) await context.Channel.SendMessageAsync($"Command failed: {result.ErrorReason}"); } catch (Exception ex) { Console.WriteLine($"HandleCommandAsync exception: {ex.Message}"); } }
        private static Task Client_UserVoiceStateUpdated(SocketUser user, SocketVoiceState before, SocketVoiceState after) { if (user.Id == _client.CurrentUser.Id) { var timingMs = DateTime.UtcNow.ToString("HH:mm:ss.fff"); _lastObservedSessionId = after.VoiceSessionId; Console.WriteLine($"[{timingMs}] SELF VoiceState: {before.VoiceChannel?.Name} -> {after.VoiceChannel?.Name} | session={after.VoiceSessionId ?? "None"}"); } return Task.CompletedTask; }
        private static Task Client_VoiceServerUpdated(SocketVoiceServer voiceServer) { var timingMs = DateTime.UtcNow.ToString("HH:mm:ss.fff"); Console.WriteLine($"[{timingMs}] VoiceServer: guild={voiceServer.Guild.Id} endpoint={MaskEndpoint(voiceServer.Endpoint)} token={MaskToken(voiceServer.Token)}"); return Task.CompletedTask; }
        private static Task Client_Disconnected(Exception ex) { Console.WriteLine($"[Discord][GW] Disconnected: {ex?.Message ?? "no exception"}. Scheduling full gateway reset."); if (Interlocked.CompareExchange(ref _gatewayResetInProgress, 1, 0) == 0) { _ = Task.Run(async () => { try { await Task.Delay(1500); try { await ForceCloseAllVoicePipesAsync(); } catch { } try { await CloseGatewayAsync(); } catch { } await Task.Delay(500); var restarted = await StartAsync(); Console.WriteLine(restarted ? "[Discord][GW] Auto reset complete." : "[Discord][GW] Auto reset failed."); } finally { Interlocked.Exchange(ref _gatewayResetInProgress, 0); } }); } return Task.CompletedTask; }

        public static async Task<bool> ClearSessionAsync(bool restartGateway = true)
        { if (Interlocked.CompareExchange(ref _sessionResetInProgress, 1, 0) != 0) return false; try { try { await ForceCloseAllVoicePipesAsync(); } catch { } try { await CloseGatewayAsync(); } catch { } if (restartGateway) { await Task.Delay(1500); return await StartAsync(); } return true; } finally { Interlocked.Exchange(ref _sessionResetInProgress, 0); } }
        public static Task<bool> OnModeSwitchAsync() => ClearSessionAsync(true);

        static DiscordNetBotManager() { try { AppDomain.CurrentDomain.ProcessExit += (_, __) => { try { ClearSessionAsync(false).GetAwaiter().GetResult(); } catch { } }; } catch { } }
        private static string MaskSessionId(string sessionId) => string.IsNullOrEmpty(sessionId) ? "None" : sessionId.Substring(0, Math.Min(8, sessionId.Length)) + "***";
        private static string MaskToken(string token) => string.IsNullOrEmpty(token) ? "None" : token.Substring(0, Math.Min(8, token.Length)) + "***";
        private static string MaskEndpoint(string endpoint) => string.IsNullOrEmpty(endpoint) ? "None" : endpoint.Contains(".") ? endpoint.Split('.')[0] + ".***" : endpoint;
    }
}