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

namespace Kinectv1.Discord
{
    public static class DiscordNetBotManager
    {
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
        private static AudioOutStream _discordPcmStream; // persistent PCM stream for Discord TTS

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

        // Removed persistent output stream and 20ms manual framing
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
        private static int _ttsGeneration = 0; // increment per enqueue, used to avoid speaking state races

        // Backpressure configuration and metrics for TTS queue - length=1 with preemption
        private const int MAX_TTS_QUEUE_SIZE = 1; // TTS queue length=1 with preempt policy
        private static long _totalTtsDrops = 0;

        // Voice join handshake state management (4006 prevention)
        // Handshake cache removed; using standard Discord.Net connect flow

        /// <summary>
        /// Voice join handshake state for preventing 4006 errors
        /// </summary>
        private class VoiceJoinHandshake { }

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
                    var error = AppError.Discord("DISCORD_TOKEN_INVALID", 
                        "Discord bot token invalid or too short",
                        "Check DiscordBotToken in settings on Diagnostics page.");
                    OnErrorOccurred?.Invoke(error.GetDisplayString());
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                var error = AppError.Discord("DISCORD_CONFIG_TEST_ERROR", 
                    $"Config test failed: {ex.Message}",
                    "Check Discord configuration and network connectivity.", ex);
                OnErrorOccurred?.Invoke(error.GetDisplayString());
                return false;
            }
        }

        /// <summary>
        /// Start the Discord.Net bot with enhanced double registration prevention
        /// </summary>
        public static async Task<bool> StartAsync()
        {
            // ATOMIC CHECK: Prevent multiple startup attempts
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

                // ATOMIC OPERATIONS: Create Discord client and services with atomic protection
                DiscordSocketClient clientToUse = null;
                CommandService commandsToUse = null;

                // ATOMIC CLIENT CREATION - Prevent double creation
                if (Interlocked.CompareExchange(ref _clientCreated, 1, 0) == 0)
                {
                    _client = new DiscordSocketClient(new DiscordSocketConfig
                    {
                        // Specific intents required for message and voice functionality
                        GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates | GatewayIntents.MessageContent | GatewayIntents.GuildMessages,
                        LogLevel = LogSeverity.Debug,
                        ConnectionTimeout = 30000,
                        DefaultRetryMode = RetryMode.AlwaysRetry,
                        MessageCacheSize = 100
                    });

                    // Set up event handlers that should only be registered once
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

                var (processedAudio, processedLength, normalizedRms) = DiscordAudioProcessor.ProcessDiscordAudio(audioData, audioData.Length, username);
                VoiceRecognizer.OnDiscordRmsLevel?.Invoke(normalizedRms);

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

                // Create/recreate a persistent PCM stream for TTS output (reuse across utterances)
                try { _discordPcmStream?.Dispose(); } catch { }
                _discordPcmStream = _currentAudioClient?.CreatePCMStream(AudioApplication.Mixed, bitrate: 96000, bufferMillis: 200);

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

        /// <summary>
        /// Handle user audio stream: Read from AudioInStream to get 48 kHz PCM directly
        /// </summary>
        private static async Task HandleUserAudioStream(ulong userId, AudioInStream audioStream)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var buffer = new byte[3840]; // 20ms of 48kHz stereo
                    while (_currentAudioClient?.ConnectionState == ConnectionState.Connected)
                    {
                        try
                        {
                            // Read directly from AudioInStream - it's already decoded PCM
                            var bytesRead = await audioStream.ReadAsync(buffer, 0, buffer.Length);

                            if (bytesRead > 0)
                            {
                                // Resolve a friendly name for logs and speaker hint
                                var name = ResolveUserDisplayName(userId);

                                // Drop incomplete/short frames to avoid Decoder warnings
                                if (bytesRead != buffer.Length && bytesRead < 1920) { await Task.Delay(1); continue; }

                                ProcessVoiceData(buffer.Take(bytesRead).ToArray(), name);
                            }
                            else
                            {
                                await Task.Delay(1);
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
            });
        }

        /// <summary>
        /// Called when voice channel is left
        /// </summary>
        public static async Task OnVoiceChannelLeft()
        {
            try
            {
                if (_currentAudioClient != null)
                {
                    try { _discordPcmStream?.Dispose(); } catch { }
                    _discordPcmStream = null;
                    await _currentAudioClient.StopAsync();
                    _currentAudioClient = null;
                }
                
                var channelName = _currentChannelName ?? "voice channel";
                _currentChannelId = null;
                _currentChannelName = null;
                
                // Fix 4: Safety clear at gateway level if still in a voice channel
                try
                {
                    // Iterate guilds to find any lingering voice state for the bot and clear it
                    var meGlobal = _client?.CurrentUser;
                    foreach (var g in _client?.Guilds ?? Enumerable.Empty<SocketGuild>())
                    {
                        var me = g.CurrentUser;
                        if (me != null && meGlobal != null && me.Id == meGlobal.Id && me.VoiceChannel != null)
                        {
                            Console.WriteLine($"🧹 Safety clear: guild={g.Id} channel=null for lingering voice state ({me.VoiceChannel.Name})");
                            await me.ModifyAsync(p => p.Channel = null);
                            await Task.Delay(200);
                        }
                    }
                 }
                 catch (Exception clearEx)
                 {
                     Console.WriteLine($"⚠️ Safety clear warning: {clearEx.Message}");
                 }

                OnBotStatusChanged?.Invoke("Connected");
                Console.WriteLine($"?? Left voice channel: {channelName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error leaving voice channel: {ex.Message}");
                OnErrorOccurred?.Invoke($"Voice channel leave error: {ex.Message}");
            }
        }

        /// <summary>
        /// Get Discord.Net bot status summary
        /// </summary>
        public static string GetStatusSummary()
        {
            try
            {
                var enabled = Kinectv1.App.SettingsProvider?.Current?.Discord?.Enabled ?? false;
                var token = Kinectv1.App.SettingsProvider?.Current?.Discord?.Token;
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
            catch (Exception ex)
            {
                return $"Error getting status: {ex.Message}";
            }
        }

        /// <summary>
        /// Enhanced voice join with proper handshake state machine to prevent 4006 errors
        /// </summary>
        public static async Task<IAudioClient> JoinVoiceAsync(IVoiceChannel voiceChannel, int maxRetries = 3)
        {
            if (voiceChannel == null) throw new ArgumentNullException(nameof(voiceChannel));

            var guildId = voiceChannel.Guild.Id;
            var channelName = voiceChannel.Name;

            Console.WriteLine($"🎯 JoinVoiceAsync: Standard Discord.Net connect to {channelName} (guild {guildId})");
            var joinTimer = Stopwatch.StartNew();

            // Small pre-clean if we somehow still have an audio client
            try
            {
                if (_currentAudioClient != null)
                {
                    try { await _currentAudioClient.StopAsync(); } catch { }
                    try { _currentAudioClient.Dispose(); } catch { }
                    _currentAudioClient = null;
                }
            }
            catch { }

            for (int attempt = 1; attempt <= Math.Max(1, maxRetries); attempt++)
            {
                try
                {
                    Console.WriteLine($"🎯 ConnectAsync attempt {attempt}/{maxRetries}...");

                    var connectTask = voiceChannel.ConnectAsync(selfDeaf: false, selfMute: false);
                    var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(20)));
                    if (completed != connectTask)
                        throw new TimeoutException("ConnectAsync timed out after 20 seconds");

                    var audioClient = await connectTask;
                    if (audioClient == null)
                        throw new InvalidOperationException("ConnectAsync returned null audio client");

                    // Small settle delay
                    await Task.Delay(300);

                    Console.WriteLine($"✅ ConnectAsync succeeded in {joinTimer.ElapsedMilliseconds}ms, state: {audioClient.ConnectionState}");
                    Telemetry.Timer("timer.discord.voice.join.ms", joinTimer.ElapsedMilliseconds);
                    Telemetry.Gauge("gauge.discord.voice.connected", 1);
                    Telemetry.Counter("discord.voice.join.success");

                    return audioClient;
                }
                catch (HttpException httpEx) when (httpEx.DiscordCode.HasValue && (int)httpEx.DiscordCode.Value == 4006)
                {
                    Console.WriteLine($"❌ 4006 on ConnectAsync attempt {attempt}: {httpEx.Message}");
                    if (attempt == maxRetries) throw;
                    var backoffMs = 500 * attempt;
                    await Task.Delay(backoffMs);
                }
                catch (TimeoutException tex)
                {
                    Console.WriteLine($"⚠️ ConnectAsync timeout on attempt {attempt}: {tex.Message}");
                    if (attempt == maxRetries) throw;
                    var backoffMs = 500 * attempt;
                    await Task.Delay(backoffMs);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ ConnectAsync failed on attempt {attempt}: {ex.GetType().Name}: {ex.Message}");
                    if (attempt == maxRetries) throw;
                    var backoffMs = 500 * attempt;
                    await Task.Delay(backoffMs);
                }
            }

            joinTimer.Stop();
            Telemetry.Counter("discord.voice.join.failed");
            Telemetry.Gauge("gauge.discord.voice.connected", 0);
            throw new InvalidOperationException($"Voice join failed after {maxRetries} attempts");
        }

        /// <summary>
        /// Get voice connection status
        /// </summary>
        public static string GetVoiceConnectionStatus()
        {
            return (_currentAudioClient != null && _currentAudioClient.ConnectionState == ConnectionState.Connected)
                ? $"Connected to: {_currentChannelName}\nReady for voice processing"
                : "Not connected to voice channel";
        }

        /// <summary>
        /// Send TTS audio to Discord voice channel via PCM (Discord.Net handles Opus)
        /// </summary>
        public static Task<bool> SendTtsToDiscordAsync(string text, string speakerRefId = null)
        {
            // per-utterance CTS
            var linked = new CancellationTokenSource();
            var prev = Interlocked.Exchange(ref _currentSpeakCts, linked);
            if (prev != null) { try { prev.Cancel(); } catch (ObjectDisposedException) { } try { prev.Dispose(); } catch { } }

            var job = new TtsJob
            {
                Text = text,
                SpeakerRefId = speakerRefId,
                Tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                Cts = linked,
                Generation = Interlocked.Increment(ref _ttsGeneration)
            };
            
            // Backpressure: With length=1 and DropOldest, check if queue is full before writing
            bool wasQueueFull = false; // ChannelReader does not expose CanRead; treat as unknown
            if (!_ttsWriter.TryWrite(job))
            {
                // Channel is closed or writer is completed
                job.Tcs.SetException(new InvalidOperationException("TTS channel closed - service shutting down"));
            }
            else
            {
                // Successfully enqueued - if queue was full, count as drop (DropOldest policy)
                if (wasQueueFull)
                {
                    Interlocked.Increment(ref _totalTtsDrops);
                    Telemetry.Counter("counter.queue.drop.tts");
                    Console.WriteLine($"⚠️ TTS backpressure: Dropped previous job due to preemption (total drops: {_totalTtsDrops})");
                }
                Telemetry.Gauge("gauge.queue.depth.tts", 1); // Always 1 for single-item queue
            }
            StartTtsWorkerIfNeeded();
            return job.Tcs.Task;
        }

        // Interrupt current TTS when incoming Discord voice is detected (barge-in on VAD)
        private static void InterruptTtsForIncomingVoice()
        {
            // Barge-in removed; do nothing
        }

        // Ensure STT barge-in is disabled
        private static void EnsureSttBargeInHook()
        {
            if (Interlocked.CompareExchange(ref _sttBargeHooked, 1, 0) != 0) return;
            Console.WriteLine("[TTS] Barge-in disabled");
        }

        private static void StartTtsWorkerIfNeeded()
        {
            if (_ttsWorkerRunning) return;
            lock (_ttsWorkerLock)
            {
                if (_ttsWorkerRunning) return;
                _ttsWorkerRunning = true;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (true)
                        {
                            // Wait for next TTS job from channel
                            if (!await _ttsReader.WaitToReadAsync())
                            {
                                // Channel is closed
                                lock (_ttsWorkerLock) { _ttsWorkerRunning = false; }
                                return;
                            }

                            if (!_ttsReader.TryRead(out var job))
                            {
                                await Task.Delay(25);
                                continue;
                            }

                            Console.WriteLine($"[TTS] Dequeued job (len={job.Text?.Length ?? 0}). Queue size=1 (single item channel)");
                            lock (_ttsCancelLock) { _currentTtsCts = job.Cts; }

                            bool ok = false;
                            try { ok = await SendTtsToDiscordCoreAsync(job.Text, job.SpeakerRefId, job.Cts.Token, job.Generation); }
                            catch (OperationCanceledException) { ok = false; }
                            catch (Exception ex) { Console.WriteLine($"[TTS] Job failed: {ex.Message}"); }
                            finally { job.Tcs.TrySetResult(ok); Console.WriteLine($"[TTS] Job finished. Success={ok}"); }
                            await Task.Delay(1);

                        }
                    }
                    finally { lock (_ttsWorkerLock) { _ttsWorkerRunning = false; } }
                });
            }
        }

        private static async Task SpeakingIdleHoldAsync()
        {
            try { _speakHoldCts?.Cancel(); } catch { }
            var cts = new CancellationTokenSource();
            _speakHoldCts = cts;
            try { await Task.Delay(250, cts.Token); if (_currentAudioClient != null && _currentAudioClient.ConnectionState == ConnectionState.Connected) await _currentAudioClient.SetSpeakingAsync(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"[TTS] Speaking hold error: {ex.Message}"); }
            finally { try { cts.Dispose(); } catch { } }
        }

        private static async Task<bool> SendTtsToDiscordCoreAsync(string text, string speakerRefId, CancellationToken ct, int generation)
        {
            Console.WriteLine($"[TTS] Enter SendTtsToDiscordCoreAsync gen={generation}. Connected={( _currentAudioClient!=null ? _currentAudioClient.ConnectionState.ToString():"null")} SpeakerRef={speakerRefId ?? Kinectv1.App.SettingsProvider?.Current?.Tts?.Speaker} TextLen={text?.Length ?? 0}");
            ct.ThrowIfCancellationRequested();

            if (_currentAudioClient == null || _currentAudioClient.ConnectionState != ConnectionState.Connected)
            {
                Console.WriteLine("[TTS] No active Discord voice connection");
                return false;
            }
            if (!CoquiTtsService.IsEnabled())
            {
                Console.WriteLine("[TTS] TTS service not available");
                return false;
            }

            var currentSpeaker = speakerRefId ?? Kinectv1.App.SettingsProvider?.Current?.Tts?.Speaker;

            // 1) TTS: float[] at 24,000 Hz (Kokoro native), mono
            var audioFloatData = await CoquiTtsService.GenerateAudioDataAsync(text, currentSpeaker);
            ct.ThrowIfCancellationRequested();
            if (audioFloatData == null || audioFloatData.Length == 0)
            {
                Console.WriteLine("[TTS] Empty TTS audio");
                return false;
            }

            // 2) Convert float [-1..1] -> PCM16 bytes at Kokoro's native rate (mono)
            float gain = Math.Max(0f, (float)(Kinectv1.App.SettingsProvider?.Current?.Tts?.DiscordVolume ?? 1.0));
            byte[] pcm22050 = FloatsToPcm16(audioFloatData, gain);

            // 3) Resample to 48000 Hz, 2 channels (Discord.Net handles Opus)
            int totalWritten = 0;
            using (var srcStream = new MemoryStream(pcm22050, writable: false))
            using (var srcProvider = new RawSourceWaveStream(srcStream, new WaveFormat(KokoroTtsService.GetSampleRate(), 16, 1)))
            using (var resampler = new MediaFoundationResampler(srcProvider, new WaveFormat(48000, 16, 2)) { ResamplerQuality = 60 })
             {
                 // Avoid speaking state races across utterances
                 CancelSpeakingHoldSafe();

                 // Only set speaking if we are still the latest generation (next utterance not queued)
                 if (generation < Volatile.Read(ref _ttsGeneration))
                 {
                     Console.WriteLine($"[TTS] Gen {generation} canceled before start by newer gen {_ttsGeneration}");
                     return false;
                 }

                await _currentAudioClient.SetSpeakingAsync(true);
                await Task.Delay(60, ct); // allow state to propagate
                try
                {
                    const int frameBytes = 3840; // 20ms
                    byte[] resampleBuf = new byte[8192];
                    byte[] carry = new byte[frameBytes];
                    int carryLen = 0;
                    int n;

                    var sw = Stopwatch.StartNew();
                    int framesSent = 0;

                    while ((n = resampler.Read(resampleBuf, 0, resampleBuf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();

                        // If a newer generation has been queued mid-stream, we can choose to continue to completion
                        // but we must not allow a stale post-hold to flip speaking=false during our stream.

                        int offset = 0;
                        while (offset < n)
                        {
                            int toCopy = Math.Min(frameBytes - carryLen, n - offset);
                            Buffer.BlockCopy(resampleBuf, offset, carry, carryLen, toCopy);
                            carryLen += toCopy;
                            offset += toCopy;

                            if (carryLen == frameBytes)
                            {
                                long expectedMs = (long)(framesSent * 20);
                                long nowMs = sw.ElapsedMilliseconds;
                                if (nowMs < expectedMs)
                                {
                                    var delay = (int)(expectedMs - nowMs);
                                    if (delay > 0) await Task.Delay(delay, ct);
                                }

                                var stream = _discordPcmStream ?? _currentAudioClient.CreatePCMStream(AudioApplication.Mixed, bitrate: 96000, bufferMillis: 200);
                                if (_discordPcmStream == null) _discordPcmStream = stream; // cache for future
                                await stream.WriteAsync(carry, 0, frameBytes, ct).ConfigureAwait(false);
                                totalWritten += frameBytes;
                                carryLen = 0;
                                framesSent++;
                            }
                        }
                    }

                    if (carryLen > 0)
                    {
                        Array.Clear(carry, carryLen, frameBytes - carryLen);
                        long expectedMs = (long)(framesSent * 20);
                        long nowMs = sw.ElapsedMilliseconds;
                        if (nowMs < expectedMs)
                        {
                            var delay = (int)(expectedMs - nowMs);
                            if (delay > 0) await Task.Delay(delay, ct);
                        }
                        var stream = _discordPcmStream ?? _currentAudioClient.CreatePCMStream(AudioApplication.Mixed, bitrate: 96000, bufferMillis: 200);
                        if (_discordPcmStream == null) _discordPcmStream = stream;
                        await stream.WriteAsync(carry, 0, frameBytes, ct).ConfigureAwait(false);
                        totalWritten += frameBytes;
                        framesSent++;
                    }

                    try { await (_discordPcmStream?.FlushAsync(ct) ?? Task.CompletedTask).ConfigureAwait(false); } catch { }
                    Console.WriteLine($"[TTS] Sent {totalWritten} PCM bytes to Discord (48k/16-bit/2ch).\n");
                    return totalWritten > 0;
                }
                finally
                {
                    _ = SpeakingIdleHoldAsync();
                }
             }
        }
       

        private static void CancelSpeakingHoldSafe()
        {
            try { _speakHoldCts?.Cancel(); } catch { }
            try { _speakHoldCts?.Dispose(); } catch { }
            _speakHoldCts = null;
        }

        private static byte[] FloatsToPcm16(float[] src, float gain)
        {
            if (gain <= 0f) gain = 1f;
            byte[] dst = new byte[src.Length * 2];
            int b = 0;
            for (int i = 0; i < src.Length; i++)
            {
                float f = src[i] * gain;
                if (f > 0.98f) f = 0.98f; else if (f < -0.98f) f = -0.98f;
                short s = (short)Math.Round(f * 32767f);
                dst[b++] = (byte)(s & 0xFF);
                dst[b++] = (byte)((s >> 8) & 0xFF);
            }
            return dst;
        }

        // Event handlers
        private static Task Log(LogMessage msg)
        {
            Console.WriteLine($"[{msg.Severity}] {msg.Source}: {msg.Message}");
            if (msg.Exception != null) Console.WriteLine(msg.Exception);  // <-- SHOW IT
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
                Console.WriteLine($"[{timingMs}] SELF VoiceState: {before.VoiceChannel?.Name} -> {after.VoiceChannel?.Name} | session={after.VoiceSessionId ?? "None"}");
                
                // No handshake resolution needed in standard path
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
                    _currentTtsCts?.Cancel();
                }
            }
            catch { }
        }
    }
}