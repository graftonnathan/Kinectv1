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
using System.IO;
using NAudio.Wave;
using System.Diagnostics;
using System.Threading.Channels;
using Discord.Net; // Added for HttpException
using Kinectv1.Tts;
using System.Security.Cryptography; // added for single-instance hash
using System.Text; // added for single-instance hash

namespace Kinectv1.Discord
{
    public static class DiscordNetBotManager
    {
        // Added single-instance mutex fields (were missing before)
        private static System.Threading.Mutex _instanceMutex;
        private static bool _ownsMutex;
        // Global voice operation lock to prevent overlapping join/leave/record ops
        private static readonly SemaphoreSlim _voiceOpLock = new SemaphoreSlim(1, 1);
        public static SemaphoreSlim VoiceOpLock => _voiceOpLock;

        // Events
        public static event Action<string> OnBotStatusChanged;
        public static event Action<string, string> OnVoiceMessageReceived;
        public static event Action<string> OnErrorOccurred;

        // Discord.Net client and services
        private static DiscordSocketClient _client;
        private static CommandService _commands;
        private static IAudioClient _currentAudioClient;
        // REMOVED persistent AudioOutStream field (use short-lived streams per playback)

        // State tracking
        private static bool _isRunning = false;
        private static bool _isInitialized = false;
        private static ulong? _currentChannelId = null;
        private static string _currentChannelName = null;

        // Cancellation token for background operations
        private static CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        // Atomic init/shutdown flags
        private static int _messageHandlerHooked = 0;
        private static int _modulesRegistered = 0;
        private static int _clientCreated = 0;
        private static int _commandServiceCreated = 0;
        private static int _startupInProgress = 0;
        private static int _shutdownInProgress = 0;

        // Speaking CTS (managed atomically)
        private static CancellationTokenSource _currentSpeakCts;

        private static volatile int _speakGeneration = 0;
        private static readonly SemaphoreSlim _speakLock = new SemaphoreSlim(1, 1);
        private static CancellationTokenSource _speakHoldCts;

        // TTS queue with System.Threading.Channels - length=1 with preempt policy
        private class TtsJob
        {
            public string Text { get; set; }
            public string SpeakerRefId { get; set; }
            public TaskCompletionSource<bool> Tcs { get; set; }
            public CancellationTokenSource Cts { get; set; }
            public int Generation { get; set; }
        }
        private static readonly Channel<TtsJob> _ttsChannel = Channel.CreateBounded<TtsJob>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        private static readonly ChannelWriter<TtsJob> _ttsWriter = _ttsChannel.Writer;
        private static readonly ChannelReader<TtsJob> _ttsReader = _ttsChannel.Reader;
        private static volatile bool _ttsWorkerRunning = false;
        private static readonly object _ttsWorkerLock = new object();
        private static readonly object _ttsCancelLock = new object();
        private static CancellationTokenSource _currentTtsCts;
        private static volatile bool _ttsPlaying = false;
        private static int _sttBargeHooked = 0;
        private static DateTime _lastTtsEnded = DateTime.MinValue;
        private static int _ttsGeneration = 0;
        // Discord TTS generation + write serialization
        private static int _discordTtsGeneration = 0;
        private static readonly SemaphoreSlim _discordTtsWriteLock = new SemaphoreSlim(1,1);
        private static volatile bool _discordTtsActive = false; // indicates an active Discord TTS write

        // Diagnostics for voice join
        private static string _lastObservedSessionId;
        private static int _joinSequence = 0;
        private static void EnsureSttBargeInHook() { /* intentionally no-op placeholder */ }

        private const int MAX_TTS_QUEUE_SIZE = 1;
        private static long _totalTtsDrops = 0;

        private class VoiceJoinHandshake { }

        /// <summary>
        /// Voice join handshake state for preventing 4006 errors
        /// </summary>
        // private class VoiceJoinHandshake { } // Removed redundant VoiceJoinHandshake class duplicate

        /// <summary>
        /// Enhanced Discord.Net bot shutdown with proper blocking pattern for application exit
        /// Adds watchdog timeouts to force close stuck sessions.
        /// </summary>
        public static async Task ShutdownAsync()
        {
            // ATOMIC CHECK: Prevent multiple shutdown attempts
            if (Interlocked.CompareExchange(ref _shutdownInProgress, 1, 0) != 0)
                return;

            try
            {
                if (!_isRunning && _client == null && _currentAudioClient == null)
                {
                    return;
                }
                _isRunning = false;

                // Ensure no overlapping voice operations
                await _voiceOpLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_currentAudioClient != null)
                    {
                        try
                        {
                            var stopTask = _currentAudioClient.StopAsync();
                            await Task.WhenAny(stopTask, Task.Delay(2000)).ConfigureAwait(false); // 2s watchdog
                        }
                        catch { }
                        try { _currentAudioClient.Dispose(); } catch { }
                        _currentAudioClient = null;
                        _currentChannelId = null;
                        _currentChannelName = null;
                    }
                }
                finally
                {
                    _voiceOpLock.Release();
                }

                if (_client != null)
                {
                    try
                    {
                        var stopTask = _client.StopAsync();
                        var completed = await Task.WhenAny(stopTask, Task.Delay(2000)).ConfigureAwait(false);
                        if (completed != stopTask)
                        {
                            Console.WriteLine("[Discord] Watchdog: Client.StopAsync timed out, forcing dispose");
                        }
                    }
                    catch { }
                    try { await _client.LogoutAsync().ConfigureAwait(false); } catch { }
                    try { _client.Dispose(); } catch { }
                    _client = null;
                }

                _commands = null;
                Interlocked.Exchange(ref _messageHandlerHooked, 0);
                Interlocked.Exchange(ref _modulesRegistered, 0);
                Interlocked.Exchange(ref _clientCreated, 0);
                Interlocked.Exchange(ref _commandServiceCreated, 0);
                
                // Clean up TTS channel with proper disposal and cancellation
                try 
                { 
                    _ttsWriter?.Complete();
                    while (_ttsReader.TryRead(out var job))
                    {
                        try { job.Cts?.Cancel(); job.Tcs?.SetCanceled(); } catch { }
                    }
                } 
                catch { }
                
                OnBotStatusChanged?.Invoke("Disconnected");
            }
            finally
            {
                try { _cancellationTokenSource?.Dispose(); } catch { }
                _cancellationTokenSource = null;
                Interlocked.Exchange(ref _shutdownInProgress, 0);
                // release single-instance mutex
                try { if (_ownsMutex) { _instanceMutex?.ReleaseMutex(); _ownsMutex = false; } } catch { }
                try { _instanceMutex?.Dispose(); } catch { _instanceMutex = null; }
            }
        }

        /// <summary>
        /// Asynchronously leave all voice channels and dispose the current audio client.
        /// Includes a 2s watchdog around StopAsync.
        /// </summary>
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
                        try
                        {
                            var stopTask = client.StopAsync();
                            await Task.WhenAny(stopTask, Task.Delay(2000)).ConfigureAwait(false);
                        }
                        catch { }
                        try { client.Dispose(); } catch { }
                    }
                }
                finally
                {
                    _voiceOpLock.Release();
                }
            }
            catch { }
            finally
            {
                _currentAudioClient = null;
                _currentChannelId = null;
                _currentChannelName = null;
            }
        }

        /// <summary>
        /// Close the Discord gateway connection and dispose the client with a 2s watchdog.
        /// </summary>
        public static async Task CloseGatewayAsync()
        {
            try
            {
                var client = _client;
                if (client == null) return;

                try
                {
                    var stopTask = client.StopAsync();
                    await Task.WhenAny(stopTask, Task.Delay(2000)).ConfigureAwait(false);
                }
                catch { }

                try
                {
                    var logoutTask = client.LogoutAsync();
                    await Task.WhenAny(logoutTask, Task.Delay(2000)).ConfigureAwait(false);
                }
                catch { }

                try { client.Dispose(); } catch { }
            }
            finally
            {
                _client = null;
                _isRunning = false;
                _isInitialized = false;
                _commands = null;
                Interlocked.Exchange(ref _messageHandlerHooked, 0);
                Interlocked.Exchange(ref _modulesRegistered, 0);
                Interlocked.Exchange(ref _clientCreated, 0);
                Interlocked.Exchange(ref _commandServiceCreated, 0);
                // release single-instance mutex
                try { if (_ownsMutex) { _instanceMutex?.ReleaseMutex(); _ownsMutex = false; } } catch { }
                try { _instanceMutex?.Dispose(); } catch { _instanceMutex = null; }
            }
        }

        /// <summary>
        /// Synchronously leave all voice channels and dispose the current audio client.
        /// Safe to call during App exit.
        /// </summary>
        public static void LeaveAllVoice()
        {
            try
            {
                var client = _currentAudioClient;
                if (client != null)
                {
                    try { client.StopAsync().Wait(1000); } catch { }
                    try { client.Dispose(); } catch { }
                }
            }
            catch { }
            finally
            {
                _currentAudioClient = null;
                _currentChannelId = null;
                _currentChannelName = null;
            }
        }

        /// <summary>
        /// Gets whether the Discord bot is currently running
        /// </summary>
        public static bool IsRunning => _isRunning;

        /// <summary>
        /// Gets whether the bot is connected to a voice channel
        /// </summary>
        public static bool IsInVoiceChannel => _currentAudioClient != null && _currentAudioClient.ConnectionState == ConnectionState.Connected;

        /// <summary>
        /// Get the Discord client for internal use by voice commands
        /// </summary>
        public static DiscordSocketClient GetClient() => _client;

        /// <summary>
        /// Test if the Discord bot configuration is valid
        /// </summary>
        public static async Task<bool> TestConfigurationAsync()
        {
            try
            {
                var token = Kinectv1.App.SettingsProvider?.Current?.Discord?.Token;
                var enabled = Kinectv1.App.SettingsProvider?.Current?.Discord?.Enabled ?? false;
                if (!enabled)
                {
                    Console.WriteLine("Discord bot disabled in settings");
                    return false;
                }
                if (string.IsNullOrEmpty(token) || token.Length < 50)
                {
                    OnErrorOccurred?.Invoke("Discord bot token invalid or too short. Check Discord token in settings.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                OnErrorOccurred?.Invoke($"Discord config test failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Start the Discord.Net bot with enhanced double registration prevention
        /// </summary>
        public static async Task<bool> StartAsync()
        {
            if (Interlocked.CompareExchange(ref _startupInProgress, 1, 0) != 0)
                return _isRunning;

            try
            {
                if (_isRunning) return true;

                // Load native libraries first
                var nativesLoaded = DiscordNativeLoader.LoadNativeLibraries();
                if (!nativesLoaded) Console.WriteLine("Native libs not fully loaded");

                // Test configuration first
                if (!await TestConfigurationAsync()) return false;

                // Acquire single-instance mutex per token (hashed) to prevent competing processes
                try
                {
                    var token = Kinectv1.App.SettingsProvider?.Current?.Discord?.Token ?? string.Empty;
                    byte[] tokenHashBytes;
                    using (var sha = SHA256.Create())
                    {
                        tokenHashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(token));
                    }
                    var tokenKey = Convert.ToBase64String(tokenHashBytes);
                    var mutexName = $"Global\\DiscordBot_{tokenKey}";
                    _instanceMutex = new System.Threading.Mutex(true, mutexName, out _ownsMutex);
                    if (!_ownsMutex)
                    {
                        OnErrorOccurred?.Invoke("Another instance of this bot is already running on this token. Aborting to avoid 4006 loops.");
                        return false;
                    }
                }
                catch (Exception mex)
                {
                    OnErrorOccurred?.Invoke($"Single-instance check failed: {mex.Message}");
                }

                // ATOMIC OPERATIONS: Create Discord client and services with atomic protection
                DiscordSocketClient clientToUse = null;
                CommandService commandsToUse = null;

                if (Interlocked.CompareExchange(ref _clientCreated, 1, 0) == 0)
                {
                    _client = new DiscordSocketClient(new DiscordSocketConfig
                    {
                        GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates | GatewayIntents.MessageContent | GatewayIntents.GuildMessages,
                        LogLevel = LogSeverity.Debug,
                        ConnectionTimeout = 30000,
                        DefaultRetryMode = RetryMode.AlwaysRetry,
                        MessageCacheSize = 100
                    });

                    _client.Log += Log;
                    _client.Ready += Client_Ready;
                    _client.UserVoiceStateUpdated += Client_UserVoiceStateUpdated;
                    _client.VoiceServerUpdated += Client_VoiceServerUpdated;
                }

                clientToUse = _client; // Get reference for use

                // ATOMIC MESSAGE HANDLER HOOK - Prevent double hooking
                if (Interlocked.CompareExchange(ref _messageHandlerHooked, 1, 0) == 0)
                    _client.MessageReceived += HandleCommandAsync;

                // ATOMIC COMMAND SERVICE CREATION - Prevent double creation
                if (Interlocked.CompareExchange(ref _commandServiceCreated, 1, 0)
                    == 0)
                    _commands = new CommandService(new CommandServiceConfig { DefaultRunMode = RunMode.Async, LogLevel = LogSeverity.Info });

                commandsToUse = _commands; // Get reference for use

                // ATOMIC MODULE REGISTRATION - Prevent double registration
                if (Interlocked.CompareExchange(ref _modulesRegistered, 1, 0) == 0)
                    await _commands.AddModuleAsync<DiscordNetVoiceCommands>(null);

                await clientToUse.LoginAsync(TokenType.Bot, Kinectv1.App.SettingsProvider?.Current?.Discord?.Token);
                await clientToUse.StartAsync();

                var readyTimeout = DateTime.UtcNow.AddSeconds(30);
                while (clientToUse.ConnectionState != ConnectionState.Connected && DateTime.UtcNow < readyTimeout)
                    await Task.Delay(250);

                _isRunning = clientToUse.ConnectionState == ConnectionState.Connected;
                EnsureSttBargeInHook();
                OnBotStatusChanged?.Invoke(_isRunning ? "Connected" : "Failed");
                return _isRunning;
            }
            catch (Exception ex)
            {
                OnErrorOccurred?.Invoke($"Failed to start: {ex.Message}");
                _isRunning = false;
                Interlocked.Exchange(ref _clientCreated, 0);
                Interlocked.Exchange(ref _messageHandlerHooked, 0);
                Interlocked.Exchange(ref _commandServiceCreated, 0);
                Interlocked.Exchange(ref _modulesRegistered, 0);
                // release mutex if we acquired it but failed
                try { if (_ownsMutex) { _instanceMutex?.ReleaseMutex(); _ownsMutex = false; } } catch { }
                try { _instanceMutex?.Dispose(); } catch { _instanceMutex = null; }
                return false;
            }
            finally
            {
                Interlocked.Exchange(ref _startupInProgress, 0);
            }
        }

        /// <summary>
        /// Process Discord voice audio data using Discord.Net
        /// </summary>
        public static void ProcessVoiceData(byte[] audioData, string username)
        {
            try
            {
                if (!_isRunning || !(Kinectv1.App.SettingsProvider?.Current?.Discord?.Enabled ?? false)) return;
                if (!VoiceRecognizer.IsDiscordInputEnabled()) return;
                if (audioData?.Length < 100) return;

                var (processedAudio, processedLength, rawRms) = DiscordAudioProcessor.ProcessDiscordAudio(audioData, audioData.Length, username);
                // Align Discord RMS scaling with local mic (0..10000 range) for uniform UI + VAD perception
                float scaledRms = 0f;
                if (rawRms > 0f)
                {
                    // rawRms from AudioUtils.CalculateRms is 0..32768 (PCM amplitude). Normalize then scale to 0..10000 like mic (ComputeRms16 logic)
                    scaledRms = (rawRms / 32768f) * 10000f;
                    if (scaledRms > 10000f) scaledRms = 10000f;
                }
                VoiceRecognizer.OnDiscordRmsLevel?.Invoke(scaledRms); // early UI update (external path also raises inside recognizer)

                if (processedAudio != null && processedLength > 320 && VoiceRecognizer.IsReady())
                {
                    SpeakerIdentifier.SetDiscordSpeakerHint(username);
                    VoiceRecognizer.ProcessExternalAudio(processedAudio, processedLength, $"Discord:{username}");
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred?.Invoke($"Voice processing error: {ex.Message}");
            }
        }

        /// <summary>
        /// Set the current voice connection (called from join command)
        /// </summary>
        public static async Task SetVoiceConnection(IAudioClient audioClient, ulong channelId, string channelName)
        {
            try
            {
                // Store the connection reference
                _currentAudioClient = audioClient;
                _currentChannelId = channelId;
                _currentChannelName = channelName;

                if (_currentAudioClient != null)
                {
                    // 1. Subscribe to audio.StreamCreated (new talkers)
                    _currentAudioClient.StreamCreated += async (userId, audioStream) =>
                    {
                        // Ignore our own bot's stream if ever surfaced
                        if (_client != null && userId == _client.CurrentUser.Id) return;
                        await HandleUserAudioStream(userId, audioStream);
                    };

                    // 2. Enumerate audio.GetStreams() once (talkers that already started before we subscribed)
                    var existingStreams = _currentAudioClient.GetStreams();

                    foreach (var stream in existingStreams)
                    {
                        if (_client != null && stream.Key == _client.CurrentUser.Id) continue; // ignore self
                        await HandleUserAudioStream(stream.Key, stream.Value);
                    }
                }

                OnBotStatusChanged?.Invoke($"In voice channel: {channelName}");
            }
            catch (Exception ex)
            {
                OnErrorOccurred?.Invoke($"Voice connection setup error: {ex.Message}");
            }
        }

        private static readonly ConcurrentDictionary<ulong,(AudioInStream stream, CancellationTokenSource cts)> _activeInputStreams = new();
        private static int _unobservedHooked = 0;

        /// <summary>
        /// Handle user audio stream: Read from AudioInStream to get 48 kHz PCM directly
        /// </summary>
        private static async Task HandleUserAudioStream(ulong userId, AudioInStream audioStream)
        {
            _ = Task.Run(async () =>
            {
                var cts = new CancellationTokenSource();
                _activeInputStreams[userId] = (audioStream, cts);
                try
                {
                    var buffer = new byte[3840]; // 20ms of 48kHz stereo
                    while (!cts.IsCancellationRequested && _currentAudioClient?.ConnectionState == ConnectionState.Connected)
                    {
                        try
                        {
                            // Read directly from AudioInStream - it's already decoded PCM
                            var bytesRead = await audioStream.ReadAsync(buffer, 0, buffer.Length);

                            if (bytesRead > 0)
                            {
                                // Resolve a friendly name for logs and speaker hint
                                var name = ResolveUserDisplayName(userId);

                                // Forward partial frames exactly (no drop of <1920 short reads)
                                ProcessVoiceData(buffer.AsSpan(0, bytesRead).ToArray(), name);
                            }
                            else
                            {
                                await Task.Delay(1, cts.Token);
                            }
                        }
                        catch (Exception streamEx)
                        {
                            Console.WriteLine($"?? Stream error for user {userId}: {streamEx.Message}", 3);
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"? Error in audio handler for user {userId}: {ex.Message}", 2);
                }
                finally
                {
                    _activeInputStreams.TryRemove(userId, out var tuple);
                    try { tuple.stream?.Dispose(); } catch { }
                    try { tuple.cts?.Dispose(); } catch { }
                }
            });
        }

        /// <summary>
        /// Close all active voice pipes and cancel ongoing audio streams
        /// </summary>
        public static async Task ForceCloseAllVoicePipesAsync()
        {
            try
            {
                foreach (var kv in _activeInputStreams.ToArray())
                {
                    if (_activeInputStreams.TryRemove(kv.Key, out var tuple))
                    {
                        try { tuple.cts.Cancel(); } catch { }
                        try { tuple.stream.Dispose(); } catch { }
                        try { tuple.cts.Dispose(); } catch { }
                    }
                }
                var ac = _currentAudioClient;
                if (ac != null)
                {
                    try { await ac.StopAsync(); } catch { }
                    try { ac.Dispose(); } catch { }
                }
            }
            finally
            {
                _currentAudioClient = null;
                _currentChannelId = null;
                _currentChannelName = null;
            }
        }

        /// <summary>
        /// Get Discord.Net bot status summary (single source for UI and commands)
        /// </summary>
        public static string GetStatusSummary()
        {
            try
            {
                var enabled = Kinectv1.App.SettingsProvider?.Current?.Discord?.Enabled ?? false;
                var prefix = Kinectv1.App.SettingsProvider?.Current?.Discord?.Prefix;
                var connectionState = _client?.ConnectionState.ToString() ?? "Disconnected";
                var guildCount = _client?.Guilds?.Count ?? 0;
                var latency = _client?.Latency ?? 0;
                return $"Bot enabled: {enabled}\n" +
                       $"Running: {_isRunning}\n" +
                       $"Command prefix: {prefix}\n" +
                       $"Connection state: {connectionState}\n" +
                       $"Guilds: {guildCount}\n" +
                       $"Latency: {latency}ms\n" +
                       $"Voice connection: {(_currentAudioClient != null ? $"In {_currentChannelName}" : "Not connected")}";
            }
            catch (Exception ex) { return $"Status error: {ex.Message}"; }
        }

        /// <summary>
        /// Enhanced voice join with retry + 4006 hard pipe reset handling.
        /// </summary>
        public static async Task<IAudioClient> JoinVoiceAsync(IVoiceChannel voiceChannel, int maxRetries = 3)
        {
            if (voiceChannel == null) throw new ArgumentNullException(nameof(voiceChannel));
            var seq = Interlocked.Increment(ref _joinSequence);
            var preJoinSession = _lastObservedSessionId; // capture session before any attempt
            Console.WriteLine($"[JoinDBG] seq={seq} guild={voiceChannel.Guild.Id} chan={voiceChannel.Name} start lastSession={preJoinSession ?? "None"}");

            bool performedPreClean = false;
            try
            {
                if (voiceChannel is SocketVoiceChannel svc)
                {
                    var me = svc.Guild.CurrentUser;
                    if (me?.VoiceChannel != null && me.VoiceChannel.Id != voiceChannel.Id)
                    {
                        performedPreClean = true;
                        Console.WriteLine($"[JoinDBG] seq={seq} pre-clean disconnect from {me.VoiceChannel.Name}");
                        try { await me.VoiceChannel.DisconnectAsync(); } catch { }
                        await WaitForVoiceNullAsync(seq, 1500);
                    }
                }
            }
            catch { }

            // Cooldown to allow old session to retire (important after leaving previous channel)
            if (performedPreClean)
            {
                Console.WriteLine($"[JoinDBG] seq={seq} cooldown after pre-clean (2000ms)");
                await Task.Delay(2000);
            }

            var sw = Stopwatch.StartNew();
            for (int attempt = 1; attempt <= Math.Max(1, maxRetries); attempt++)
            {
                var attemptSessionPre = _lastObservedSessionId;
                try
                {
                    Console.WriteLine($"[JoinDBG] seq={seq} attempt={attempt} session(before)={attemptSessionPre ?? "None"}");
                    var connectTask = voiceChannel.ConnectAsync(selfDeaf: false, selfMute: false);
                    var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(20)));
                    if (completed != connectTask) throw new TimeoutException("ConnectAsync timed out after 20s");
                    var ac = await connectTask;
                    if (ac == null) throw new InvalidOperationException("ConnectAsync returned null");
                    await Task.Delay(300);
                    var afterSession = _lastObservedSessionId;
                    if (attemptSessionPre != null && afterSession == attemptSessionPre)
                    {
                        Console.WriteLine($"[JoinDBG] seq={seq} WARNING: session id unchanged ({afterSession}) after successful ConnectAsync");
                    }
                    Console.WriteLine($"[JoinDBG] seq={seq} success after {sw.ElapsedMilliseconds}ms state={ac.ConnectionState} session(now)={afterSession ?? "None"}");
                    return ac;
                }
                catch (HttpException hex) when (hex.DiscordCode.HasValue && (int)hex.DiscordCode.Value == 4006)
                {
                    Console.WriteLine($"[JoinDBG] seq={seq} 4006 attempt={attempt} invoking ForceCloseAllVoicePipes + cooldown");
                    await ForceCloseAllVoicePipesAsync();
                    await WaitForVoiceNullAsync(seq, 1000);
                    if (attempt == maxRetries)
                        throw; // bubble last 4006
                    Console.WriteLine($"[JoinDBG] seq={seq} 4006 cooldown 2500ms before retry");
                    await Task.Delay(2500); // give region time to retire session
                }
                catch (TimeoutException tex)
                {
                    Console.WriteLine($"[JoinDBG] seq={seq} timeout attempt={attempt} {tex.Message}");
                    if (attempt == maxRetries) throw;
                    await ForceCloseAllVoicePipesAsync();
                    await WaitForVoiceNullAsync(seq, 800);
                    await Task.Delay(1200);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[JoinDBG] seq={seq} fail attempt={attempt} {ex.GetType().Name}:{ex.Message}");
                    if (attempt == maxRetries) throw;
                    await ForceCloseAllVoicePipesAsync();
                    await WaitForVoiceNullAsync(seq, 600);
                    await Task.Delay(800);
                }
            }
            throw new InvalidOperationException($"Voice join failed seq={seq}");
        }

        private static async Task WaitForVoiceNullAsync(int seq, int timeoutMs)
        {
            try
            {
                var start = Stopwatch.StartNew();
                while (start.ElapsedMilliseconds < timeoutMs)
                {
                    bool inChannel = _client?.Guilds.Any(g => g.CurrentUser?.VoiceChannel != null) ?? false;
                    if (!inChannel)
                    {
                        Console.WriteLine($"[JoinDBG] seq={seq} voice cleared after {start.ElapsedMilliseconds}ms");
                        return;
                    }
                    await Task.Delay(100);
                }
                Console.WriteLine($"[JoinDBG] seq={seq} voice NOT cleared after {timeoutMs}ms (session={_lastObservedSessionId ?? "None"})");
            }
            catch { }
        }

        /// <summary>
        /// Standard leave invoked by commands; flushes input streams and audio client.
        /// </summary>
        public static async Task OnVoiceChannelLeft()
        {
            try
            {
                // cancel inbound streams first
                foreach (var kv in _activeInputStreams.ToArray())
                {
                    if (_activeInputStreams.TryRemove(kv.Key, out var tuple))
                    {
                        try { tuple.cts.Cancel(); } catch { }
                        try { tuple.stream.Dispose(); } catch { }
                        try { tuple.cts.Dispose(); } catch { }
                    }
                }
                if (_currentAudioClient != null)
                {
                    try { await _currentAudioClient.StopAsync(); } catch { }
                    try { _currentAudioClient.Dispose(); } catch { }
                    _currentAudioClient = null;
                }
                _currentChannelId = null;
                _currentChannelName = null;
            }
            catch (Exception ex) { Console.WriteLine($"leave cleanup error: {ex.Message}"); }
        }

        /// <summary>
        /// Enhanced Discord.Net bot shutdown with proper blocking pattern for application exit
        /// Adds watchdog timeouts to force close stuck sessions.
        /// </summary>
        public static async Task FullShutdownAsync()
        {
            try
            {
                await _voiceOpLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    var client = _client;
                    if (client != null)
                    {
                        foreach (var g in client.Guilds)
                        {
                            try
                            {
                                var me = g.CurrentUser;
                                if (me?.VoiceChannel != null)
                                {
                                    try { await me.VoiceChannel.DisconnectAsync(); } catch { }
                                    try { await me.ModifyAsync(p => p.Channel = null); } catch { }
                                }
                            }
                            catch { }
                        }
                    }
                }
                finally { _voiceOpLock.Release(); }

                try { await LeaveAllVoiceAsync(); } catch { }

                try
                {
                    var c = _client;
                    if (c != null)
                    {
                        try { await c.StopAsync().ConfigureAwait(false); } catch { }
                        try { await c.LogoutAsync().ConfigureAwait(false); } catch { }
                        try { c.Dispose(); } catch { }
                        _client = null;
                    }
                }
                catch { }
            }
            catch { }
        }

        public static void ForceImmediateVoiceClose()
        {
            try
            {
                // Synchronous best-effort cleanup (process exit)
                var client = _client;
                if (client != null)
                {
                    foreach (var g in client.Guilds)
                    {
                        try
                        {
                            var me = g.CurrentUser;
                            if (me?.VoiceChannel != null)
                            {
                                try { me.VoiceChannel.DisconnectAsync().GetAwaiter().GetResult(); } catch { }
                                try { me.ModifyAsync(p => p.Channel = null).GetAwaiter().GetResult(); } catch { }
                            }
                        }
                        catch { }
                    }
                }
                var ac = _currentAudioClient;
                if (ac != null)
                {
                    try { ac.StopAsync().GetAwaiter().GetResult(); } catch { }
                    try { ac.Dispose(); } catch { }
                    _currentAudioClient = null;
                }
                if (client != null)
                {
                    try { client.StopAsync().GetAwaiter().GetResult(); } catch { }
                    try { client.LogoutAsync().GetAwaiter().GetResult(); } catch { }
                    try { client.Dispose(); } catch { }
                    _client = null;
                }
            }
            catch { }
        }

        private static string _lastDiscordTtsText; // debounce last spoken text
        private static DateTime _lastDiscordTtsTime = DateTime.MinValue;

        public static Task<bool> SendTtsToDiscordAsync(string text, string speakerRefId = null)
        {
            try
            {
                lock (_ttsCancelLock)
                {
                    if (!string.IsNullOrWhiteSpace(text) &&
                        _lastDiscordTtsText == text &&
                        (DateTime.UtcNow - _lastDiscordTtsTime).TotalMilliseconds < 1500)
                    {
                        try { Console.WriteLine("[TTS->Discord] Suppressed duplicate text (within 1500ms window)"); } catch { }
                        return Task.FromResult(false);
                    }
                    _lastDiscordTtsText = text;
                    _lastDiscordTtsTime = DateTime.UtcNow;
                }
            }
            catch { }
            if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(false);
            if (!IsInVoiceChannel) return Task.FromResult(false);
            if (!TtsService.IsEnabled()) return Task.FromResult(false);
            var speaker = speakerRefId ?? Kinectv1.App.SettingsProvider?.Current?.Tts?.Speaker;
            TtsPlaybackManager.Enqueue(text, Kinectv1.Tts.TtsOutputTarget.Discord, speaker, preempt: true);
            return Task.FromResult(true);
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

        // Resolve a human-friendly display name (Nickname > Username > fallback)
        private static string GetDisplayName(ulong userId)
        {
            try
            {
                var voice = (_currentChannelId.HasValue ? _client?.GetChannel(_currentChannelId.Value) as SocketVoiceChannel : null);
                var guildUser = voice?.Guild?.GetUser(userId);
                var name = guildUser?.Nickname;
                if (string.IsNullOrWhiteSpace(name)) name = guildUser?.Username;
                if (string.IsNullOrWhiteSpace(name)) name = _client?.GetUser(userId)?.Username;
                return string.IsNullOrWhiteSpace(name) ? $"User{userId}" : name;
            }
            catch { return $"User{userId}"; }
        }

        // Resolve a display name for a Discord user (nickname > username > fallback)
        private static string ResolveUserDisplayName(ulong userId)
        {
            try
            {
                // Try current voice channel context first
                if (_client != null && _currentChannelId.HasValue)
                {
                    var chan = _client.GetChannel(_currentChannelId.Value) as SocketVoiceChannel;
                    var gu = chan?.Guild?.GetUser(userId);
                    if (gu != null)
                    {
                        if (!string.IsNullOrWhiteSpace(gu.Nickname)) return gu.Nickname;
                        if (!string.IsNullOrWhiteSpace(gu.Username)) return gu.Username;
                        return gu.DisplayName;
                    }
                }

                // Fallback to global cache
                var user = _client?.GetUser(userId);
                if (user != null)
                {
                    var name = (user as SocketUser)?.Username;
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
            catch { }
            return $"User{userId}";
        }

        /// <summary>
        /// Get TTS backpressure metrics for monitoring queue health
        /// </summary>
        public static (int ttsQueueCount, long totalTtsDrops) GetTtsBackpressureMetrics()
        {
            try
            {
                // With single-item channel, no reliable count API here; return 0 or 1 heuristically
                var queueCount = 0;
                return (queueCount, _totalTtsDrops);
            }
            catch
            {
                return (0, _totalTtsDrops);
            }
        }

        /// <summary>
        /// Resample audio sample from source rate to target rate using linear interpolation
        /// </summary>
        private static float[] ResampleAudio(float[] source, int sourceRate, int targetRate)
        {
            if (source == null || source.Length == 0 || sourceRate == targetRate) return source?.ToArray();
            int sourceLength = source.Length; int targetLength = (int)((double)sourceLength * targetRate / sourceRate); float[] target = new float[targetLength];
            for (int n = 0; n < targetLength; n++) { double t = (double)n * sourceRate / targetRate; int t0 = (int)t; int t1 = Math.Min(t0 + 1, sourceLength - 1); double frac = t - t0; target[n] = (float)((1.0 - frac) * source[t0] + frac * source[t1]); }
            return target;
        }

        public static void CancelCurrentTts()
        {
            try
            {
                lock (_ttsCancelLock)
                {
                    if (_currentTtsCts != null)
                    {
                        try { Console.WriteLine($"[TTS->Discord] Cancel invoked at {DateTime.UtcNow:O}"); } catch { }
                        _currentTtsCts.Cancel();
                        try { Kinectv1.Tts.TtsService.MarkExternalCancel(); } catch { }
                    }
                }
            }
            catch { }
        }

        public static async Task<bool> ClearSessionAsync(bool restartGateway)
        {
            try
            {
                // Close any active voice pipes (readers + audio client)
                try { await ForceCloseAllVoicePipesAsync(); } catch { }
                // Close gateway (idempotent if already null)
                try { await CloseGatewayAsync(); } catch { }
                if (restartGateway)
                {
                    await Task.Delay(1500);
                    return await StartAsync();
                }
                return true;
            }
            catch { return false; }
        }

        static DiscordNetBotManager()
        {
            try
            {
                AppDomain.CurrentDomain.ProcessExit += (_, __) =>
                {
                    try { ClearSessionAsync(false).GetAwaiter().GetResult(); } catch { }
                };
            }
            catch { }
        }

        // Event handlers
        private static Task Log(LogMessage msg)
        {
            Console.WriteLine($"[{msg.Severity}] {msg.Source}: {msg.Message}");
            if (msg.Exception != null) Console.WriteLine(msg.Exception);
            return Task.CompletedTask;
        }

        private static Task Client_Ready()
        {
            Console.WriteLine($"?? Discord.Net bot ready as {_client.CurrentUser.Username}#{_client.CurrentUser.Discriminator}");
            
            // SANITY CHECK: Mark as initialized when Ready event fires
            _isInitialized = true;
            
            OnBotStatusChanged?.Invoke($"Ready as {_client.CurrentUser.Username}");
            return Task.CompletedTask;
        }

        private static async Task HandleCommandAsync(SocketMessage messageParam)
        {
            try
            {
                var message = messageParam as SocketUserMessage; if (message == null) return; int argPos = 0; var prefix = Kinectv1.App.SettingsProvider?.Current?.Discord?.Prefix;
                if (!(message.HasStringPrefix(prefix, ref argPos) || message.HasMentionPrefix(_client.CurrentUser, ref argPos)) || message.Author.IsBot) return;
                var context = new SocketCommandContext(_client, message);
                var result = await _commands.ExecuteAsync(context, argPos, null);
                if (!result.IsSuccess) await context.Channel.SendMessageAsync($"Command failed: {result.ErrorReason}");
            }
            catch (Exception ex) { Console.WriteLine($"HandleCommandAsync exception: {ex.Message}"); }
        }

        private static Task Client_UserVoiceStateUpdated(SocketUser user, SocketVoiceState before, SocketVoiceState after)
        {
            if (user.Id == _client.CurrentUser.Id)
            {
                var timingMs = DateTime.UtcNow.ToString("HH:mm:ss.fff");
                _lastObservedSessionId = after.VoiceSessionId; // capture latest
                Console.WriteLine($"[{timingMs}] SELF VoiceState: {before.VoiceChannel?.Name} -> {after.VoiceChannel?.Name} | session={after.VoiceSessionId ?? "None"}");
            }
            return Task.CompletedTask;
        }

        private static Task Client_VoiceServerUpdated(SocketVoiceServer voiceServer)
        {
            var timingMs = DateTime.UtcNow.ToString("HH:mm:ss.fff");
            Console.WriteLine($"[{timingMs}] VoiceServer: guild={voiceServer.Guild.Id} endpoint={MaskEndpoint(voiceServer.Endpoint)} token={MaskToken(voiceServer.Token)}");
            
            // Standard path: no custom handshake; keep log only
            
            return Task.CompletedTask;
        }

        // Helper methods for masking sensitive data in logs
        private static string MaskSessionId(string sessionId) 
            => string.IsNullOrEmpty(sessionId) ? "None" : sessionId.Substring(0, Math.Min(8, sessionId.Length)) + "***";
        
        private static string MaskToken(string token) 
            => string.IsNullOrEmpty(token) ? "None" : token.Substring(0, Math.Min(8, token.Length)) + "***";
        
        private static string MaskEndpoint(string endpoint) 
            => string.IsNullOrEmpty(endpoint) ? "None" : endpoint.Contains(".") ? endpoint.Split('.')[0] + ".***" : endpoint;
    }
}