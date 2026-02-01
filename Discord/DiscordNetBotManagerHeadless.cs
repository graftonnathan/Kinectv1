using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Audio;
using Discord.Commands;
using Discord.WebSocket;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;

namespace Kinectv1.Discord
{
    /// <summary>
    /// Simplified Discord.Net bot manager for headless mode.
    /// Provides text chat integration and voice channel TTS without WPF dependencies.
    /// </summary>
    public static class DiscordNetBotManagerHeadless
    {
        private static readonly SemaphoreSlim _voiceOpLock = new SemaphoreSlim(1, 1);
        public static SemaphoreSlim VoiceOpLock => _voiceOpLock;
        
        public static event Action<string> OnBotStatusChanged;
        public static event Action<string, string> OnMessageReceived;
        public static event Action<string> OnErrorOccurred;
        
        private static DiscordSocketClient _client;
        private static CommandService _commands;
        private static IAudioClient _currentAudioClient;
        private static AudioOutStream _discordPcmStream;
        private static bool _isRunning = false;
        private static ulong? _currentChannelId = null;
        private static string _currentChannelName = null;
        private static CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        
        // TTS queue for Discord voice
        private class TtsJob { public string Text; public CancellationTokenSource Cts; }
        private static readonly Channel<TtsJob> _ttsChannel = Channel.CreateBounded<TtsJob>(new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        private static CancellationTokenSource _currentTtsCts;
        private static volatile bool _ttsWorkerRunning = false;

        public static bool IsRunning => _isRunning;
        public static bool IsInVoiceChannel => _currentAudioClient != null && _currentAudioClient.ConnectionState == ConnectionState.Connected;
        public static DiscordSocketClient GetClient() => _client;

        /// <summary>
        /// Start the Discord bot with the configured token.
        /// </summary>
        public static async Task<bool> StartAsync()
        {
            if (_isRunning) return true;
            
            try
            {
                var settings = App.SettingsProvider?.Current?.Discord;
                if (settings == null || !settings.Enabled)
                {
                    Console.WriteLine("[Discord] Bot is disabled in settings");
                    return false;
                }
                
                var token = settings.Token;
                if (string.IsNullOrEmpty(token) || token.Length < 50)
                {
                    OnErrorOccurred?.Invoke("Discord bot token invalid or too short. Check Discord token in settings.");
                    return false;
                }

                // Load native libraries for voice support
                var nativesLoaded = DiscordNativeLoader.LoadNativeLibraries();
                if (!nativesLoaded)
                    Console.WriteLine("[Discord] Warning: Native voice libraries not fully loaded");

                // Create Discord client
                _client = new DiscordSocketClient(new DiscordSocketConfig 
                { 
                    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates | GatewayIntents.MessageContent | GatewayIntents.GuildMessages,
                    LogLevel = LogSeverity.Info,
                    ConnectionTimeout = 30000,
                    DefaultRetryMode = RetryMode.AlwaysRetry,
                    MessageCacheSize = 100
                });
                
                _client.Log += Log;
                _client.Ready += Client_Ready;
                _client.MessageReceived += HandleCommandAsync;

                // Create command service
                _commands = new CommandService(new CommandServiceConfig 
                { 
                    DefaultRunMode = RunMode.Async, 
                    LogLevel = LogSeverity.Info 
                });
                
                await _commands.AddModuleAsync<DiscordHeadlessCommands>(null);

                // Login and start
                await _client.LoginAsync(TokenType.Bot, token);
                await _client.StartAsync();

                // Wait for connection
                var readyTimeout = DateTime.UtcNow.AddSeconds(30);
                while (_client.ConnectionState != ConnectionState.Connected && DateTime.UtcNow < readyTimeout)
                    await Task.Delay(250);

                _isRunning = _client.ConnectionState == ConnectionState.Connected;
                
                if (_isRunning)
                {
                    OnBotStatusChanged?.Invoke("Connected");
                    Console.WriteLine($"[Discord] Bot connected as {_client.CurrentUser?.Username}");
                    
                    // Start TTS worker
                    _ = Task.Run(TtsWorkerAsync);
                }
                else
                {
                    OnBotStatusChanged?.Invoke("Failed");
                    Console.WriteLine("[Discord] Bot failed to connect");
                }
                
                return _isRunning;
            }
            catch (Exception ex)
            {
                OnErrorOccurred?.Invoke($"Failed to start Discord bot: {ex.Message}");
                Console.WriteLine($"[Discord] Start error: {ex.Message}");
                _isRunning = false;
                return false;
            }
        }

        /// <summary>
        /// Shutdown the Discord bot gracefully.
        /// </summary>
        public static async Task ShutdownAsync()
        {
            if (!_isRunning && _client == null) return;
            
            _isRunning = false;
            
            try
            {
                CancelCurrentTts();
                _ttsChannel.Writer.TryComplete();
                
                await _voiceOpLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_currentAudioClient != null)
                    {
                        try { await _currentAudioClient.StopAsync().ConfigureAwait(false); } catch { }
                        try { _currentAudioClient.Dispose(); } catch { }
                        _currentAudioClient = null;
                        _currentChannelId = null;
                        _currentChannelName = null;
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

                try { _discordPcmStream?.Dispose(); } catch { }
                _discordPcmStream = null;
                _commands = null;
                
                OnBotStatusChanged?.Invoke("Disconnected");
                Console.WriteLine("[Discord] Bot disconnected");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Discord] Shutdown error: {ex.Message}");
            }
        }

        /// <summary>
        /// Join a voice channel by name.
        /// </summary>
        public static async Task<bool> JoinVoiceChannelAsync(string channelName)
        {
            if (!_isRunning || _client == null) return false;
            
            await _voiceOpLock.WaitAsync();
            try
            {
                // Find the guild and channel
                var guild = _client.Guilds.FirstOrDefault();
                if (guild == null)
                {
                    Console.WriteLine("[Discord] No guilds found");
                    return false;
                }

                var channel = guild.VoiceChannels.FirstOrDefault(vc => 
                    vc.Name.Equals(channelName, StringComparison.OrdinalIgnoreCase));
                
                if (channel == null)
                {
                    Console.WriteLine($"[Discord] Voice channel '{channelName}' not found");
                    return false;
                }

                // Leave current channel if in one
                if (_currentAudioClient != null)
                {
                    try { await _currentAudioClient.StopAsync(); } catch { }
                    try { _currentAudioClient.Dispose(); } catch { }
                    _currentAudioClient = null;
                }

                // Join the new channel
                _currentAudioClient = await channel.ConnectAsync();
                _currentChannelId = channel.Id;
                _currentChannelName = channel.Name;
                
                Console.WriteLine($"[Discord] Joined voice channel: {channel.Name}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Discord] Join error: {ex.Message}");
                return false;
            }
            finally
            {
                _voiceOpLock.Release();
            }
        }

        /// <summary>
        /// Leave the current voice channel.
        /// </summary>
        public static async Task LeaveVoiceChannelAsync()
        {
            await _voiceOpLock.WaitAsync();
            try
            {
                if (_currentAudioClient != null)
                {
                    try { await _currentAudioClient.StopAsync(); } catch { }
                    try { _currentAudioClient.Dispose(); } catch { }
                    _currentAudioClient = null;
                    _currentChannelId = null;
                    _currentChannelName = null;
                    Console.WriteLine("[Discord] Left voice channel");
                }
            }
            finally
            {
                _voiceOpLock.Release();
            }
        }

        /// <summary>
        /// Send a text message to the default text channel.
        /// </summary>
        public static async Task SendMessageAsync(string message)
        {
            if (!_isRunning || _client == null) return;
            
            try
            {
                var guild = _client.Guilds.FirstOrDefault();
                var channel = guild?.TextChannels.FirstOrDefault();
                
                if (channel != null)
                {
                    await channel.SendMessageAsync(message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Discord] Send message error: {ex.Message}");
            }
        }

        /// <summary>
        /// Queue text for TTS in the current voice channel.
        /// </summary>
        public static void QueueTts(string text)
        {
            if (!IsInVoiceChannel) return;
            
            var job = new TtsJob 
            { 
                Text = text, 
                Cts = new CancellationTokenSource() 
            };
            
            _ttsChannel.Writer.TryWrite(job);
        }

        /// <summary>
        /// Cancel the current TTS playback.
        /// </summary>
        public static void CancelCurrentTts()
        {
            try
            {
                var cts = Interlocked.Exchange(ref _currentTtsCts, null);
                if (cts != null)
                {
                    cts.Cancel();
                    cts.Dispose();
                }
            }
            catch { }
        }

        /// <summary>
        /// Get PCM audio stream for Discord voice output.
        /// </summary>
        public static AudioOutStream GetPcmStream()
        {
            if (_currentAudioClient == null) return null;
            
            try
            {
                if (_discordPcmStream == null)
                {
                    _discordPcmStream = _currentAudioClient.CreatePCMStream(AudioApplication.Mixed, bitrate: 128000);
                }
                return _discordPcmStream;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Discord] Get PCM stream error: {ex.Message}");
                return null;
            }
        }

        // === Private Methods ===

        private static Task Client_Ready()
        {
            Console.WriteLine($"[Discord] Ready! Logged in as {_client.CurrentUser.Username}");
            return Task.CompletedTask;
        }

        private static async Task HandleCommandAsync(SocketMessage messageParam)
        {
            var message = messageParam as SocketUserMessage;
            if (message == null) return;
            if (message.Author.IsBot) return;

            int argPos = 0;
            var prefix = App.SettingsProvider?.Current?.Discord?.Prefix ?? "!";
            
            if (!message.HasStringPrefix(prefix, ref argPos)) return;

            var context = new SocketCommandContext(_client, message);
            await _commands.ExecuteAsync(context, argPos, null);
            
            // Also raise event for processing by Maggie's brain
            OnMessageReceived?.Invoke(message.Author.Username, message.Content[argPos..].Trim());
        }

        private static Task Log(LogMessage msg)
        {
            Console.WriteLine($"[Discord] {msg.Message}");
            return Task.CompletedTask;
        }

        private static async Task TtsWorkerAsync()
        {
            if (_ttsWorkerRunning) return;
            _ttsWorkerRunning = true;
            
            try
            {
                await foreach (var job in _ttsChannel.Reader.ReadAllAsync())
                {
                    if (string.IsNullOrWhiteSpace(job.Text)) continue;
                    if (job.Cts.IsCancellationRequested) continue;
                    if (!IsInVoiceChannel) continue;
                    
                    try
                    {
                        Interlocked.Exchange(ref _currentTtsCts, job.Cts);
                        
                        // TTS audio will be sent via the Qwen3TtsService.OnTtsAudioChunk event
                        // which is hooked up in HeadlessMaggie.cs
                        Console.WriteLine($"[Discord TTS] Speaking: {job.Text.Substring(0, Math.Min(50, job.Text.Length))}...");
                        
                        // Wait for completion or cancellation
                        await Task.Delay(100, job.Cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // TTS was cancelled
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Discord TTS] Error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Discord TTS] Worker error: {ex.Message}");
            }
            finally
            {
                _ttsWorkerRunning = false;
            }
        }
    }
}
