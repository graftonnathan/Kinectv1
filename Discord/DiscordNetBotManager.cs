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

namespace Kinectv1.Discord
{
    /// <summary>
    /// Discord.Net Bot Manager for Kinect Voice Bot
    /// 
    /// ENHANCED DOUBLE REGISTRATION PREVENTION:
    /// 1. Atomic initialization flags with Interlocked operations
    /// 2. Early return guards for all registration methods
    /// 3. Comprehensive logging for debugging registration issues
    /// 4. Single point of control for all Discord operations
    /// 5. Thread-safe startup/shutdown sequences
    /// 
    /// SANITY CHECKS FOR DEBUGGING:
    /// 1. VoiceServerUpdated subscription removed - reduces noise, logging via UserVoiceStateUpdated (self only)
    /// 2. Ensure only ONE process using this bot token is running - second process will nuke first session (instant 4006)
    /// 3. Wait for _client.Ready event before considering startup complete - prevents premature !join commands
    /// 4. Windows firewall: outbound UDP must be allowed (no inbound/port-forward needed)
    ///    - UDP blocked = fail after SessionDescription, not at Identify (different from current symptom)
    /// 5. Commands & handlers registered exactly ONCE - prevents double registration
    /// 
    /// CLEAN IMPLEMENTATION: All complex workarounds removed, using standard Discord.Net patterns
    /// </summary>
    public static class DiscordNetBotManager
    {
        // Events
        public static event Action<string> OnBotStatusChanged;
        public static event Action<string, string> OnVoiceMessageReceived;
        public static event Action<string> OnErrorOccurred;

        // Discord.Net client and services
        private static DiscordSocketClient _client;
        private static CommandService _commands;
        private static IAudioClient _currentAudioClient;

        // State tracking
        private static bool _isRunning = false;
        private static bool _isInitialized = false;
        private static ulong? _currentChannelId = null;
        private static string _currentChannelName = null;

        // Cancellation token for background operations
        private static CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        // ENHANCED DOUBLE REGISTRATION PREVENTION - Use atomic operations for thread safety
        private static int _messageHandlerHooked = 0; // 0 = not hooked, 1 = hooked
        private static int _modulesRegistered = 0;    // 0 = not registered, 1 = registered
        private static int _clientCreated = 0;        // 0 = not created, 1 = created
        private static int _commandServiceCreated = 0; // 0 = not created, 1 = created
        private static int _startupInProgress = 0;    // 0 = not in progress, 1 = in progress
        private static int _shutdownInProgress = 0;   // 0 = not in progress, 1 = in progress

        // Thread safety lock for critical operations
        private static readonly object _initializationLock = new object();

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
                var token = AppSettings.LoadDiscordBotToken();
                var enabled = AppSettings.LoadDiscordBotEnabled();

                if (!enabled)
                {
                    Console.WriteLine("?? Discord bot is disabled in settings");
                    return false;
                }

                if (string.IsNullOrEmpty(token))
                {
                    Console.WriteLine("? Discord bot token is not configured");
                    OnErrorOccurred?.Invoke("Discord bot token is not configured");
                    return false;
                }

                // Basic token format validation
                if (token.Length < 50)
                {
                    Console.WriteLine("? Discord bot token appears to be invalid (too short)");
                    OnErrorOccurred?.Invoke("Discord bot token appears to be invalid");
                    return false;
                }

                Console.WriteLine("? Discord.Net bot configuration is valid");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error testing Discord.Net bot configuration: {ex.Message}");
                OnErrorOccurred?.Invoke($"Configuration test failed: {ex.Message}");
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
            {
                Console.WriteLine("?? StartAsync already in progress - EARLY RETURN");
                return _isRunning; // Return current status
            }

            try
            {
                Console.WriteLine($"?? === StartAsync ENTRY POINT (Thread-Safe) ===");
                Console.WriteLine($"?? _isRunning: {_isRunning}");
                Console.WriteLine($"?? _messageHandlerHooked: {(_messageHandlerHooked == 1)}");
                Console.WriteLine($"?? _modulesRegistered: {(_modulesRegistered == 1)}");
                Console.WriteLine($"?? _clientCreated: {(_clientCreated == 1)}");
                Console.WriteLine($"?? _commandServiceCreated: {(_commandServiceCreated == 1)}");
                Console.WriteLine($"?? Thread ID: {Thread.CurrentThread.ManagedThreadId}");
                
                if (_isRunning)
                {
                    Console.WriteLine("? Discord.Net bot is already running - EARLY RETURN");
                    return true;
                }

                // Check for already running without lock first
                if (_isRunning)
                {
                    Console.WriteLine("? Discord.Net bot started while checking - EARLY RETURN");
                    return true;
                }

                Console.WriteLine("?? Starting Discord.Net bot...");
                OnBotStatusChanged?.Invoke("Starting...");

                // Load native libraries first
                Console.WriteLine("?? Loading Discord.Net native audio libraries...");
                var nativesLoaded = DiscordNativeLoader.LoadNativeLibraries();
                if (!nativesLoaded)
                {
                    Console.WriteLine("?? Native libraries not fully loaded - voice features may be limited");
                }
                else
                {
                    Console.WriteLine("? Native libraries loaded successfully - voice features should work");
                }

                // Test configuration first
                var configValid = await TestConfigurationAsync();
                if (!configValid)
                {
                    OnBotStatusChanged?.Invoke("Configuration invalid");
                    return false;
                }

                var token = AppSettings.LoadDiscordBotToken();
                var prefix = AppSettings.LoadDiscordBotPrefix();

                // ATOMIC OPERATIONS: Create Discord client and services with atomic protection
                DiscordSocketClient clientToUse = null;
                CommandService commandsToUse = null;

                // ATOMIC CLIENT CREATION - Prevent double creation
                if (Interlocked.CompareExchange(ref _clientCreated, 1, 0) == 0)
                {
                    Console.WriteLine("?? Creating new Discord client (ATOMIC)...");
                    var config = new DiscordSocketConfig()
                    {
                        // Specific intents required for message and voice functionality
                        GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates | GatewayIntents.MessageContent | GatewayIntents.GuildMessages,
                        LogLevel = LogSeverity.Debug, // Changed to Debug for more detailed logging
                        ConnectionTimeout = 30000, // 30 seconds
                        DefaultRetryMode = RetryMode.AlwaysRetry,
                        MessageCacheSize = 100
                    };

                    _client = new DiscordSocketClient(config);
                    
                    // Set up event handlers that should only be registered once
                    _client.Log += Log;
                    _client.Ready += Client_Ready;
                    _client.UserVoiceStateUpdated += Client_UserVoiceStateUpdated;
                    
                    Console.WriteLine("? Discord client created with event handlers (ATOMIC)");
                }
                else
                {
                    Console.WriteLine("?? Discord client already exists - using existing instance");
                }

                clientToUse = _client; // Get reference for use

                // ATOMIC MESSAGE HANDLER HOOK - Prevent double hooking
                if (Interlocked.CompareExchange(ref _messageHandlerHooked, 1, 0) == 0)
                {
                    Console.WriteLine("?? Hooking MessageReceived handler (ATOMIC)...");
                    clientToUse.MessageReceived += HandleCommandAsync;
                    Console.WriteLine("? MessageReceived handler hooked (ATOMIC)");
                }
                else
                {
                    Console.WriteLine("?? MessageReceived handler already hooked - SKIPPING");
                }

                // ATOMIC COMMAND SERVICE CREATION - Prevent double creation
                if (Interlocked.CompareExchange(ref _commandServiceCreated, 1, 0) == 0)
                {
                    Console.WriteLine("?? Creating command service (ATOMIC)...");
                    var commandConfig = new CommandServiceConfig()
                    {
                        DefaultRunMode = RunMode.Async,
                        LogLevel = LogSeverity.Info,
                        CaseSensitiveCommands = false
                    };
                    
                    _commands = new CommandService(commandConfig);
                    Console.WriteLine("? Command service created (ATOMIC)");
                }
                else
                {
                    Console.WriteLine("?? Command service already exists - using existing instance");
                }

                commandsToUse = _commands; // Get reference for use

                // ATOMIC MODULE REGISTRATION - Prevent double registration
                if (Interlocked.CompareExchange(ref _modulesRegistered, 1, 0) == 0)
                {
                    Console.WriteLine("?? Registering Discord command modules (ATOMIC)...");
                    try
                    {
                        var moduleAddResult = await commandsToUse.AddModuleAsync<DiscordNetVoiceCommands>(null);
                        Console.WriteLine($"?? Module addition result: {moduleAddResult?.Name ?? "Success"}");
                        
                        var commandCount = commandsToUse.Commands.Count();
                        Console.WriteLine($"? Registered {commandCount} commands successfully (ATOMIC)");
                        
                        if (commandCount == 0)
                        {
                            Console.WriteLine("?? WARNING: No commands were registered! Commands may not work.");
                            
                            // FALLBACK: Try alternative registration method
                            Console.WriteLine("?? Attempting fallback command registration...");
                            try
                            {
                                var assembly = Assembly.GetExecutingAssembly();
                                var moduleTypes = assembly.GetTypes()
                                    .Where(t => t.IsSubclassOf(typeof(ModuleBase<SocketCommandContext>)))
                                    .ToList();
                                
                                Console.WriteLine($"?? Found {moduleTypes.Count} potential command modules in assembly");
                                
                                foreach (var moduleType in moduleTypes)
                                {
                                    Console.WriteLine($"?? Attempting to register module: {moduleType.Name}");
                                    await commandsToUse.AddModuleAsync(moduleType, null);
                                }
                                
                                var fallbackCommandCount = commandsToUse.Commands.Count();
                                Console.WriteLine($"?? After fallback registration: {fallbackCommandCount} commands");
                            }
                            catch (Exception fallbackEx)
                            {
                                Console.WriteLine($"? Fallback registration failed: {fallbackEx.Message}");
                            }
                        }
                        else
                        {
                            Console.WriteLine("?? Registered commands:");
                            foreach (var command in commandsToUse.Commands)
                            {
                                Console.WriteLine($"   - {command.Name} (Module: {command.Module.Name})");
                            }
                        }
                    }
                    catch (Exception moduleEx)
                    {
                        Console.WriteLine($"? Module registration failed: {moduleEx.Message}");
                        Console.WriteLine($"?? Stack trace: {moduleEx.StackTrace}");
                        
                        // Reset the atomic flag if registration failed
                        Interlocked.Exchange(ref _modulesRegistered, 0);
                        throw;
                    }
                }
                else
                {
                    var existingCount = commandsToUse?.Commands?.Count() ?? 0;
                    Console.WriteLine($"?? Command modules already registered - SKIPPING (existing: {existingCount} commands)");
                }

                Console.WriteLine("?? Connecting to Discord...");
                await clientToUse.LoginAsync(TokenType.Bot, token);
                await clientToUse.StartAsync();

                // SANITY CHECK: Wait for _client.Ready to ensure proper startup before processing commands
                var readyTimeout = DateTime.UtcNow.AddSeconds(30);
                while (clientToUse.ConnectionState != ConnectionState.Connected && DateTime.UtcNow < readyTimeout)
                {
                    await Task.Delay(500);
                }

                if (clientToUse.ConnectionState != ConnectionState.Connected)
                {
                    throw new Exception("Failed to connect to Discord within 30 seconds");
                }

                // IMPORTANT: Ensure Ready event has fired before considering startup complete
                // This prevents !join commands from running before the bot is fully ready
                Console.WriteLine("? Waiting for Discord.Net Ready event...");
                var readyEventTimeout = DateTime.UtcNow.AddSeconds(15);
                while (!_isInitialized && DateTime.UtcNow < readyEventTimeout)
                {
                    await Task.Delay(250);
                }

                if (!_isInitialized)
                {
                    Console.WriteLine("?? Ready event didn't fire within 15 seconds, but connection is established");
                }

                _isRunning = true;
                _isInitialized = true;

                Console.WriteLine("? Discord.Net bot started successfully");
                OnBotStatusChanged?.Invoke("Connected");

                // Hook STT barge-in to interrupt TTS only when STT is resolved
                EnsureSttBargeInHook();

                // Show bot information
                Console.WriteLine($"?? Discord.Net Bot Settings:");
                Console.WriteLine($"   Command Prefix: {prefix}");
                Console.WriteLine($"   Registered Commands: {commandsToUse.Commands.Count()}");
                Console.WriteLine($"   Message Handler: {(_messageHandlerHooked == 1 ? "Hooked" : "Not Hooked")}");
                Console.WriteLine($"   Voice Support: Native Discord.Net audio");
                Console.WriteLine($"   Gateway Intents: Guild Voice States enabled");
                Console.WriteLine($"   Audio Providers: Built-in native libraries");
                Console.WriteLine($"   Thread ID: {Thread.CurrentThread.ManagedThreadId}");

                Console.WriteLine($"? === StartAsync EXIT - SUCCESS ===");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Failed to start Discord.Net bot: {ex.Message}");
                Console.WriteLine($"?? Stack trace: {ex.StackTrace}");
                OnErrorOccurred?.Invoke($"Failed to start: {ex.Message}");
                OnBotStatusChanged?.Invoke("Failed");
                _isRunning = false;
                
                // Reset atomic flags on failure
                Interlocked.Exchange(ref _clientCreated, 0);
                Interlocked.Exchange(ref _messageHandlerHooked, 0);
                Interlocked.Exchange(ref _commandServiceCreated, 0);
                Interlocked.Exchange(ref _modulesRegistered, 0);
                
                Console.WriteLine($"? === StartAsync EXIT - FAILURE ===");
                return false;
            }
            finally
            {
                // Always reset startup progress flag
                Interlocked.Exchange(ref _startupInProgress, 0);
            }
        }

        /// <summary>
        /// Enhanced Discord.Net bot shutdown with proper blocking pattern for application exit
        /// Implements the 3-step clean exit pattern:
        /// 1. Leave voice & stop audio
        /// 2. Stop & logout Discord client  
        /// 3. Block until completion (exit handlers aren't async-friendly)
        /// </summary>
        public static async Task ShutdownAsync()
        {
            // ATOMIC CHECK: Prevent multiple shutdown attempts
            if (Interlocked.CompareExchange(ref _shutdownInProgress, 1, 0) != 0)
            {
                Console.WriteLine("?? Discord shutdown already in progress - EARLY RETURN");
                return;
            }

            try
            {
                if (!_isRunning)
                {
                    Console.WriteLine("?? Discord.Net bot is not running - skipping shutdown");
                    return;
                }

                Console.WriteLine("?? === DISCORD BOT SHUTDOWN INITIATED ===");
                Console.WriteLine("?? Implementing clean Discord shutdown pattern...");
                OnBotStatusChanged?.Invoke("Shutting down...");

                // STEP 1: Stop receiving new background tasks and prevent reconnects
                Console.WriteLine("?? STEP 1: Setting shutdown flags to prevent new operations...");
                _isRunning = false; // Prevent background loops from starting new reconnects
                _isInitialized = false;

                // Cancel all background tasks
                try
                {
                    Console.WriteLine("?? Canceling background Discord operations...");
                    _cancellationTokenSource?.Cancel();
                    Console.WriteLine("? Background operations canceled");
                }
                catch (Exception cancelEx)
                {
                    Console.WriteLine($"?? Error canceling background operations: {cancelEx.Message}");
                }

                // STEP 2: Leave voice & stop audio (highest priority for clean exit)
                Console.WriteLine("?? STEP 2: Disconnecting from voice channels...");
                if (_currentAudioClient != null)
                {
                    try
                    {
                        Console.WriteLine("?? Stopping voice audio client...");
                        await _currentAudioClient.StopAsync();
                        _currentAudioClient.Dispose();
                        _currentAudioClient = null;
                        Console.WriteLine("? Voice audio client stopped and disposed");
                    }
                    catch (Exception voiceEx)
                    {
                        Console.WriteLine($"?? Error stopping voice audio client: {voiceEx.Message}");
                    }

                    // Clear voice connection tracking
                    _currentChannelId = null;
                    _currentChannelName = null;
                    Console.WriteLine("? Voice connection tracking cleared");
                }
                else
                {
                    Console.WriteLine("?? No active voice connection to stop");
                }

                // STEP 3: Stop & logout Discord client (gateway connection)
                Console.WriteLine("?? STEP 3: Disconnecting Discord gateway client...");
                if (_client != null)
                {
                    try
                    {
                        // Stop all Discord operations first
                        Console.WriteLine("?? Stopping Discord client...");
                        await _client.StopAsync();
                        Console.WriteLine("? Discord client stopped");

                        // Logout from Discord gateway
                        Console.WriteLine("?? Logging out from Discord...");
                        await _client.LogoutAsync();
                        Console.WriteLine("? Discord logout completed");

                        // Dispose Discord client resources
                        Console.WriteLine("?? Disposing Discord client...");
                        _client.Dispose();
                        _client = null;
                        Console.WriteLine("? Discord client disposed");
                    }
                    catch (Exception clientEx)
                    {
                        Console.WriteLine($"?? Error stopping Discord client: {clientEx.Message}");
                        // Continue with cleanup even if client stop fails
                    }
                }
                else
                {
                    Console.WriteLine("?? No Discord client to stop");
                }

                // STEP 4: Reset ALL state flags atomically (cleanup)
                Console.WriteLine("?? STEP 4: Resetting all state flags...");
                _commands = null;
                Interlocked.Exchange(ref _messageHandlerHooked, 0);
                Interlocked.Exchange(ref _modulesRegistered, 0);
                Interlocked.Exchange(ref _clientCreated, 0);
                Interlocked.Exchange(ref _commandServiceCreated, 0);
                Console.WriteLine("? All state flags reset");

                Console.WriteLine("?? === DISCORD BOT SHUTDOWN COMPLETED ===");
                Console.WriteLine("?? All Discord voice + gateway sessions closed cleanly");
                OnBotStatusChanged?.Invoke("Disconnected");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error during Discord.Net bot shutdown: {ex.Message}");
                Console.WriteLine($"?? Stack trace: {ex.StackTrace}");
                OnErrorOccurred?.Invoke($"Shutdown error: {ex.Message}");
                OnBotStatusChanged?.Invoke("Error");
                
                // Don't let shutdown errors prevent application exit
                // Force reset state flags even if cleanup failed
                try
                {
                    _isRunning = false;
                    _isInitialized = false;
                    _commands = null;
                    _client = null;
                    _currentAudioClient = null;
                    Interlocked.Exchange(ref _messageHandlerHooked, 0);
                    Interlocked.Exchange(ref _modulesRegistered, 0);
                    Interlocked.Exchange(ref _clientCreated, 0);
                    Interlocked.Exchange(ref _commandServiceCreated, 0);
                    Console.WriteLine("?? Force reset all state flags after error");
                }
                catch (Exception forceEx)
                {
                    Console.WriteLine($"? Error during force reset: {forceEx.Message}");
                }
            }
            finally
            {
                // Always reset shutdown progress flag
                Interlocked.Exchange(ref _shutdownInProgress, 0);
                
                // Dispose cancellation token
                try
                {
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;
                    Console.WriteLine("?? Cancellation token disposed");
                }
                catch (Exception tokenEx)
                {
                    Console.WriteLine($"?? Error disposing cancellation token: {tokenEx.Message}");
                }
                
                Console.WriteLine("?? Discord shutdown flag reset - ready for application exit");
            }
        }

        /// <summary>
        /// Process Discord voice audio data using Discord.Net
        /// </summary>
        public static void ProcessVoiceData(byte[] audioData, string username)
        {
            try
            {
                if (!_isRunning || !AppSettings.LoadDiscordBotEnabled())
                {
                    return;
                }

                // Check if Discord input is enabled
                if (!VoiceRecognizer.IsDiscordInputEnabled())
                {
                    return;
                }

                // Skip very small audio chunks
                if (audioData?.Length < 100)
                {
                    return;
                }

                // Process through the existing audio pipeline
                var (processedAudio, processedLength, normalizedRms) = DiscordAudioProcessor.ProcessDiscordAudio(
                    audioData, 
                    audioData.Length, 
                    username);

                // NOTE: Removed RMS-based TTS interruption.
                // Barge-in now occurs only when STT resolves (hooked via VoiceRecognizer.OnTranscription).

                // Fire Discord RMS event for UI updates
                VoiceRecognizer.OnDiscordRmsLevel?.Invoke(normalizedRms);

                // Send to STT if we have substantial processed audio
                if (processedAudio != null && processedLength > 320)
                {
                    if (VoiceRecognizer.IsReady())
                    {
                        // Set Discord speaker hint for identification
                        SpeakerIdentifier.SetDiscordSpeakerHint(username);
                        
                        // Process the audio
                        VoiceRecognizer.ProcessExternalAudio(processedAudio, processedLength, $"Discord:{username}");
                        
                        // Removed verbose voice message event that was showing processed audio details
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error processing Discord.Net voice data: {ex.Message}");
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
                
                Console.WriteLine($"?? Discord.Net voice connection stored: {channelName} (ID: {channelId})");
                
                if (_currentAudioClient != null)
                {
                    Console.WriteLine($"?? Setting up Discord.Net audio Receiving using EXACT specified pattern...");
                    
                    // 1. Subscribe to audio.StreamCreated (new talkers)
                    _currentAudioClient.StreamCreated += async (userId, audioStream) =>
                    {
                        Console.WriteLine($"?? StreamCreated: New talker {userId}");
                        await HandleUserAudioStream(userId, audioStream);
                    };
                    
                    // 2. Enumerate audio.GetStreams() once (talkers that already started before we subscribed)
                    var existingStreams = _currentAudioClient.GetStreams();
                    Console.WriteLine($"?? GetStreams() found {existingStreams.Count()} existing talkers");
                    
                    foreach (var stream in existingStreams)
                    {
                        Console.WriteLine($"?? Existing talker: User {stream.Key}");
                        await HandleUserAudioStream(stream.Key, stream.Value);
                    }
                    
                    Console.WriteLine($"? Discord.Net audio receiving configured using EXACT pattern");
                }
                
                OnBotStatusChanged?.Invoke($"In voice channel: {channelName}");
                Console.WriteLine($"?? Voice reception enabled for channel: {channelName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error setting Discord.Net voice connection: {ex.Message}");
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
                    Console.WriteLine($"?? Audio reception started: User{userId}");
                    
                    // AudioInStream from Discord.Net already provides decoded 48kHz PCM - no OpusDecodeStream needed
                    var buffer = new byte[3840]; // 20ms of 48kHz stereo
                    
                    while (_currentAudioClient?.ConnectionState == ConnectionState.Connected)
                    {
                        try
                        {
                            // Read directly from AudioInStream - it's already decoded PCM
                            var bytesRead = await audioStream.ReadAsync(buffer, 0, buffer.Length);
                            
                            if (bytesRead > 0)
                            {
                                // Process the PCM audio through existing pipeline - removed verbose logging
                                ProcessVoiceData(buffer.Take(bytesRead).ToArray(), $"User{userId}");
                            }
                            else
                            {
                                // No data available, yield control briefly
                                await Task.Delay(1);
                            }
                        }
                        catch (Exception streamEx)
                        {
                            Console.WriteLine($"?? Stream error for user {userId}: {streamEx.Message}");
                            break;
                        }
                    }
                    
                    Console.WriteLine($"?? Audio reception ended: User{userId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"? Error in audio handler for user {userId}: {ex.Message}");
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
                    await _currentAudioClient.StopAsync();
                    _currentAudioClient = null;
                }
                
                var channelName = _currentChannelName ?? "voice channel";
                _currentChannelId = null;
                _currentChannelName = null;
                
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
                var enabled = AppSettings.LoadDiscordBotEnabled();
                var token = AppSettings.LoadDiscordBotToken();
                var prefix = AppSettings.LoadDiscordBotPrefix();
                
                var connectionState = _client?.ConnectionState.ToString() ?? "Disconnected";
                var guildCount = _client?.Guilds?.Count ?? 0;
                var latency = _client?.Latency ?? 0;

                return $"Bot enabled: {enabled}\n" +
                       $"Running: {_isRunning}\n" +
                       $"Token configured: {(!string.IsNullOrEmpty(token) ? "Yes" : "No")}\n" +
                       $"Command prefix: {prefix}\n" +
                       $"Connection state: {connectionState}\n" +
                       $"Guilds: {guildCount}\n" +
                       $"Latency: {latency}ms\n" +
                       $"Voice connection: {(_currentAudioClient != null ? $"In {_currentChannelName}" : "Not connected")}\n" +
                       $"Discord input enabled: {VoiceRecognizer.IsDiscordInputEnabled()}\n" +
                       $"Library: Discord.Net {typeof(DiscordSocketClient).Assembly.GetName().Version}\n" +
                       $"Registration Status: Client={(_clientCreated == 1)}, Commands={(_commandServiceCreated == 1)}, Modules={(_modulesRegistered == 1)}, Handler={(_messageHandlerHooked == 1)}";
            }
            catch (Exception ex)
            {
                return $"? Error getting Discord.Net bot status: {ex.Message}";
            }
        }

        /// <summary>
        /// Get voice connection status
        /// </summary>
        public static string GetVoiceConnectionStatus()
        {
            if (_currentAudioClient != null && _currentAudioClient.ConnectionState == ConnectionState.Connected)
            {
                return $"? Connected to: {_currentChannelName}\n?? Ready for voice processing (Discord.Net)";
            }
            else
            {
                return "? Not connected to voice channel";
            }
        }

        /// <summary>
        /// Calculate RMS level for adaptive gain control
        /// </summary>
        private static float CalculateRmsLevel(float[] buffer, int length)
        {
            if (buffer == null || length <= 0) return 0f;
            
            double sum = 0.0;
            for (int i = 0; i < length; i++)
            {
                sum += buffer[i] * buffer[i];
            }
            
            return (float)Math.Sqrt(sum / length);
        }

        /// <summary>
        /// Apply ultra-aggressive volume boost with minimal limiting
        /// Prioritizes maximum volume over audio quality
        /// </summary>
        private static float[] ApplyUltraVolumeBoost(float[] audioBuffer, float targetVolumeBoost)
        {
            // Calculate current RMS level
            float currentRms = CalculateRmsLevel(audioBuffer, audioBuffer.Length);
            
            // Ultra-aggressive target RMS for maximum volume (-1 dBFS - nearly full scale)
            float targetRms = 0.89f; // Nearly maximum possible without clipping
            
            // Calculate ultra-aggressive gain
            float ultraGain;
            if (currentRms > 0.0001f) // Very low threshold to avoid division by zero
            {
                // Apply much more aggressive gain calculation
                ultraGain = Math.Min(targetVolumeBoost * 2.0f, targetRms / currentRms); // Allow up to 30x boost
            }
            else
            {
                ultraGain = targetVolumeBoost * 2.0f; // Maximum boost for silent audio
            }
            
            // Ensure minimum aggressive boost
            ultraGain = Math.Max(ultraGain, 20.0f); // At least 20x boost always
            
            Console.WriteLine($"?? Ultra-aggressive gain: {ultraGain:F1}x, Target RMS: {targetRms:F3}, Current RMS: {currentRms:F3}");
            
            // Apply ultra-aggressive gain with minimal limiting
            var processedBuffer = new float[audioBuffer.Length];
            for (int i = 0; i < audioBuffer.Length; i++)
            {
                // Apply ultra-aggressive gain
                float boostedSample = audioBuffer[i] * ultraGain;
                
                // Only apply hard limiting at the very edge (98% full scale)
                if (Math.Abs(boostedSample) > 0.98f)
                {
                    processedBuffer[i] = Math.Sign(boostedSample) * 0.98f;
                }
                else
                {
                    processedBuffer[i] = boostedSample;
                }
            }
            
            return processedBuffer;
        }

        // TTS Queueing with interruption (barge-in)
        private class TtsJob
        {
            public string Text { get; set; }
            public String SpeakerRefId { get; set; }
            public TaskCompletionSource<bool> Tcs { get; set; }
            public CancellationTokenSource Cts { get; set; }
        }
        private static readonly ConcurrentQueue<TtsJob> _ttsQueue = new ConcurrentQueue<TtsJob>();
        private static volatile bool _ttsWorkerRunning = false;
        private static readonly object _ttsWorkerLock = new object();
        private static readonly object _ttsCancelLock = new object();
        private static CancellationTokenSource _currentTtsCts;
        private static volatile bool _ttsPlaying = false; // track active TTS playback
        private static DateTime _lastTtsInterrupt = DateTime.MinValue;
        private static int _sttBargeHooked = 0; // ensure we hook STT event only once

        /// <summary>
        /// Send TTS audio to Discord voice channel (INTERRUPTIBLE)
        /// New requests cancel current playback and flush the queue.
        /// </summary>
        public static Task<bool> SendTtsToDiscordAsync(string text, string speakerRefId = null)
        {
            // Cancel any current playback and clear pending items
            var newCts = new CancellationTokenSource();
            lock (_ttsCancelLock)
            {
                try
                {
                    _currentTtsCts?.Cancel();
                    _currentTtsCts?.Dispose();
                }
                catch { }
                _currentTtsCts = newCts;

                // Drain queue (drop older responses)
                while (_ttsQueue.TryDequeue(out var oldJob))
                {
                    try { oldJob.Cts?.Cancel(); oldJob.Tcs?.TrySetResult(false); } catch { }
                }
            }

            var job = new TtsJob
            {
                Text = text,
                SpeakerRefId = speakerRefId,
                Tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                Cts = newCts
            };

            _ttsQueue.Enqueue(job);
            StartTtsWorkerIfNeeded();
            return job.Tcs.Task;
        }

        // Interrupt current TTS when incoming Discord voice is detected (barge-in on VAD)
        private static void InterruptTtsForIncomingVoice()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastTtsInterrupt).TotalMilliseconds < 150)
                return; // cooldown to avoid thrash

            _lastTtsInterrupt = now;
            lock (_ttsCancelLock)
            {
                try { _currentTtsCts?.Cancel(); } catch { }
                // Clear any pending TTS jobs so only the next response plays
                while (_ttsQueue.TryDequeue(out var oldJob))
                {
                    try { oldJob.Cts?.Cancel(); oldJob.Tcs?.TrySetResult(false); } catch { }
                }
            }
        }

        // Ensure STT barge-in is tied to resolved transcription
        private static void EnsureSttBargeInHook()
        {
            if (Interlocked.CompareExchange(ref _sttBargeHooked, 1, 0) != 0) return;
            try
            {
                VoiceRecognizer.OnTranscription += transcript =>
                {
                    // Only interrupt when a non-empty transcription is produced (resolved STT)
                    if (!string.IsNullOrWhiteSpace(transcript) && _ttsPlaying)
                    {
                        InterruptTtsForIncomingVoice();
                    }
                };
                Console.WriteLine("?? STT barge-in hook attached (interrupt TTS on resolved STT)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"?? Failed to attach STT barge-in hook: {ex.Message}");
            }
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
                            if (!_ttsQueue.TryDequeue(out var job))
                            {
                                // Idle briefly to avoid race with enqueuer, then re-check
                                await Task.Delay(25);
                                if (!_ttsQueue.IsEmpty)
                                {
                                    continue; // a job arrived; loop to dequeue it
                                }
                                lock (_ttsWorkerLock)
                                {
                                    if (_ttsQueue.IsEmpty)
                                    {
                                        _ttsWorkerRunning = false;
                                        return;
                                    }
                                }
                                // If we get here, a job was enqueued between checks; continue loop
                                continue;
                            }

                            // Set as current CTS for interruption by new requests
                            lock (_ttsCancelLock)
                            {
                                _currentTtsCts = job.Cts;
                            }

                            bool ok = false;
                            try
                            {
                                ok = await SendTtsToDiscordCoreAsync(job.Text, job.SpeakerRefId, job.Cts.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                ok = false;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"? TTS job failed: {ex.Message}");
                            }
                            finally
                            {
                                job.Tcs.TrySetResult(ok);
                            }

                            // Small yield to let a new job enqueue and cancel promptly
                            await Task.Delay(1);
                        }
                    }
                    finally
                    {
                        lock (_ttsWorkerLock)
                        {
                            _ttsWorkerRunning = false;
                        }
                    }
                });
            }
        }

        /// <summary>
        /// Core implementation that actually generates and streams TTS to Discord (cancellable)
        /// </summary>
        private static async Task<bool> SendTtsToDiscordCoreAsync(string text, string speakerRefId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (_currentAudioClient == null || _currentAudioClient.ConnectionState != ConnectionState.Connected)
            {
                Console.WriteLine("? No active Discord voice connection for TTS");
                return false;
            }

            if (!CoquiTtsService.IsEnabled())
            {
                Console.WriteLine("? TTS service not available for Discord output");
                return false;
            }

            Console.WriteLine($"?? Generating TTS for Discord: \"{text}\"");
            
            // Use provided speaker or get current speaker from settings
            var currentSpeaker = speakerRefId ?? AppSettings.LoadTtsSpeaker();

            // Generate audio data (returns float[] MONO at 22050 Hz)
            ct.ThrowIfCancellationRequested();
            var audioFloatData = await CoquiTtsService.GenerateAudioDataAsync(text, currentSpeaker);
            ct.ThrowIfCancellationRequested();
            
            if (audioFloatData == null || audioFloatData.Length == 0)
            {
                Console.WriteLine($"? Failed to generate TTS audio for Discord");
                return false;
            }

            Console.WriteLine($"? Generated TTS audio: {audioFloatData.Length} samples MONO at 22050 Hz");

            // Resample MONO audio from 22050 Hz to 48000 Hz for Discord
            var resampledAudio = ResampleAudio(audioFloatData, 22050, 48000);
            Console.WriteLine($"?? Resampled to 48000 Hz MONO: {resampledAudio.Length} samples");

            // Use the resampled audio directly to maintain natural dynamics
            var processedAudio = resampledAudio;

            // Convert MONO to STEREO for Discord (duplicate mono to both channels)
            var stereoAudio = new float[processedAudio.Length * 2];
            for (int i = 0; i < processedAudio.Length; i++)
            {
                stereoAudio[i * 2] = processedAudio[i];     // Left channel
                stereoAudio[i * 2 + 1] = processedAudio[i]; // Right channel (same as left)
            }
            Console.WriteLine($"?? Converted to STEREO: {stereoAudio.Length} samples (duplicated mono to both channels)");

            // Send audio to Discord voice channel with proper stream handling
            try
            {
                _ttsPlaying = true;
                using var audioOutStream = _currentAudioClient.CreatePCMStream(AudioApplication.Voice, 128 * 1024); // 128KB buffer
                
                // Use correct chunk size for STEREO 48kHz PCM
                const int chunkSize = 3840; // 20ms of 48kHz STEREO PCM
                var totalSamples = stereoAudio.Length; // float samples (stereo)
                var bytesPerSample = 2; // 16-bit

                // PRIME: Send a brief silence pre-roll so Discord reliably opens the gate
                var silence = new byte[chunkSize]; // 20ms silence
                if (ct.IsCancellationRequested) return false;
                await audioOutStream.WriteAsync(silence, 0, silence.Length); // no ct to avoid TaskCanceledException

                // Stream in chunks, applying latest Discord volume per chunk
                for (int floatOffset = 0; floatOffset < totalSamples; floatOffset += (chunkSize / bytesPerSample))
                {
                    if (ct.IsCancellationRequested) return false;

                    // Compute how many float samples fit in this chunk
                    int floatsPerChunk = chunkSize / bytesPerSample; // 1920 float samples (stereo) = 20ms
                    int remainingFloats = Math.Min(floatsPerChunk, totalSamples - floatOffset);

                    // Read latest volume each chunk for real-time updates
                    var discordVolume = AppSettings.LoadDiscordTtsVolume();

                    // Apply fixed pre-gain to raw output before user volume (do not touch settings)
                    const float preOutputGain = 0.3f; // set output to 30%

                    // Convert this chunk of float stereo to PCM bytes with current volume
                    var pcmBytes = new byte[remainingFloats * bytesPerSample];
                    int byteIndex = 0;
                    for (int i = 0; i < remainingFloats; i++)
                    {
                        var scaled = stereoAudio[floatOffset + i] * preOutputGain * (float)discordVolume;
                        var clampedSample = Math.Max(-0.98f, Math.Min(0.98f, scaled));
                        var sample = (short)(clampedSample * 32767);
                        pcmBytes[byteIndex++] = (byte)(sample & 0xFF);
                        pcmBytes[byteIndex++] = (byte)((sample >> 8) & 0xFF);
                    }

                    await audioOutStream.WriteAsync(pcmBytes, 0, pcmBytes.Length); // no ct to avoid TaskCanceledException
                    // Let PCM stream pacing manage timing
                }

                // TRAILER: Send one more silence frame to ensure tail is not clipped
                if (ct.IsCancellationRequested) return false;
                await audioOutStream.WriteAsync(silence, 0, silence.Length); // no ct
                
                await audioOutStream.FlushAsync(); // no ct
                
                Console.WriteLine($"? TTS audio sent to Discord voice channel with real-time volume control");
                return true;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("?? TTS playback canceled (barge-in)");
                return false;
            }
            catch (Exception streamEx)
            {
                Console.WriteLine($"? Error writing TTS to Discord audio stream: {streamEx.Message}");
                return false;
            }
            finally
            {
                _ttsPlaying = false;
            }
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
                var message = messageParam as SocketUserMessage;
                if (message == null) 
                {
                    return;
                }

                int argPos = 0;
                var prefix = AppSettings.LoadDiscordBotPrefix();

                Console.WriteLine($"?? Message received: '{message.Content}' from {message.Author.Username} (Bot: {message.Author.IsBot})");

                if (!(message.HasStringPrefix(prefix, ref argPos) || 
                    message.HasMentionPrefix(_client.CurrentUser, ref argPos)) ||
                    message.Author.IsBot)
                {
                    Console.WriteLine($"?? Message ignored - No prefix match or from bot (prefix: '{prefix}', argPos: {argPos})");
                    return;
                }

                Console.WriteLine($"?? Discord.Net command received: '{message.Content}' from {message.Author.Username}");
                Console.WriteLine($"?? Command details - Prefix: '{prefix}', ArgPos: {argPos}, Content after prefix: '{message.Content.Substring(argPos)}'");

                var context = new SocketCommandContext(_client, message);
                
                Console.WriteLine($"? Executing command: '{message.Content.Substring(argPos)}'");
                
                var result = await _commands.ExecuteAsync(
                    context: context,
                    argPos: argPos,
                    services: null);
                    
                if (!result.IsSuccess)
                {
                    Console.WriteLine($"? Command error: {result.ErrorReason}");
                    Console.WriteLine($"? Error type: {result.Error}");
                    
                    if (result.Error == CommandError.UnknownCommand)
                    {
                        await context.Channel.SendMessageAsync($"? Unknown command. Use `{prefix}help` to see available commands.");
                    }
                    else
                    {
                        await context.Channel.SendMessageAsync($"? Command execution failed: {result.ErrorReason}");
                    }
                }
                else
                {
                    Console.WriteLine($"? Command executed successfully: '{message.Content}'");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? HandleCommandAsync exception: {ex.Message}");
                Console.WriteLine($"?? Stack trace: {ex.StackTrace}");
            }
        }

        private static Task Client_UserVoiceStateUpdated(SocketUser user, SocketVoiceState before, SocketVoiceState after)
        {
            try
            {
                // DEBUGGING: Only track our own voice state changes (reduces noise)
                if (user.Id == _client.CurrentUser.Id)
                {
                    Console.WriteLine($"SELF VoiceState: {before.VoiceChannel?.Name} -> {after.VoiceChannel?.Name} | session={after.VoiceSessionId}");
                    
                    if (after.VoiceChannel != null)
                    {
                        Console.WriteLine($"?? Discord.Net bot joined voice channel: {after.VoiceChannel.Name}");
                    }
                    else if (before.VoiceChannel != null)
                    {
                        Console.WriteLine($"?? Discord.Net bot left voice channel: {before.VoiceChannel.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? VoiceStateUpdated handling error: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Resample audio sample from source rate to target rate using linear interpolation
        /// </summary>
        private static float[] ResampleAudio(float[] source, int sourceRate, int targetRate)
        {
            if (source == null || source.Length == 0)
                return source;

            if (sourceRate == targetRate)
                return source.ToArray(); // No resampling needed

            int sourceLength = source.Length;
            int targetLength = (int)((double)sourceLength * targetRate / sourceRate);
            float[] target = new float[targetLength];

            // Linear interpolation
            for (int n = 0; n < targetLength; n++)
            {
                double t = (double)n * sourceRate / targetRate;
                int t0 = (int)t;
                int t1 = Math.Min(t0 + 1, sourceLength - 1);
                double frac = t - t0;

                // Perform linear interpolation between samples t0 and t1
                target[n] = (float)((1.0 - frac) * source[t0] + frac * source[t1]);
            }

            return target;
        }
    }
}