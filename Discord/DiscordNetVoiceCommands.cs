using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Audio;
using Discord.Commands;
using Discord.Net;
using Discord.WebSocket;
using Kinectv1.Tts;

namespace Kinectv1.Discord
{
    /// <summary>
    /// Discord.Net bot commands for voice channel management and system status
    /// Uses the prefix from Settings.settings (default: "!")
    /// ENHANCED WITH DOUBLE REGISTRATION PREVENTION:
    /// - Atomic protection for ConnectAsync calls
    /// - Per-guild join attempt tracking
    /// - Enhanced session management
    /// - Comprehensive error handling with recovery advice
    /// - GLOBAL VOICE OPERATION LOCK to prevent concurrent Discord operations
    /// CLEAN IMPLEMENTATION - All complex workarounds removed, using standard Discord.Net ConnectAsync
    /// DON'T REUSE/LINGER IAudioClient - Always StopAsync() + Dispose() old clients
    /// </summary>
    public class DiscordNetVoiceCommands : ModuleBase<SocketCommandContext>
    {
        // Track audio clients per guild - SIMPLE TRACKING ONLY
        private static readonly ConcurrentDictionary<ulong, IAudioClient> _audioClients = new();
        
        // ENHANCED: Track active join attempts to prevent double ConnectAsync calls
        private static readonly ConcurrentDictionary<ulong, Task> _activeJoinAttempts = new();
        
        // CRITICAL 4006 PREVENTION: Global lock for Discord voice operations
        private static readonly SemaphoreSlim _discordVoiceOperationLock = Kinectv1.Discord.DiscordNetBotManager.VoiceOpLock;

        #region Native Library Verification

        [DllImport("opus", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr opus_get_version_string();

        [DllImport("libsodium", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sodium_init();

        /// <summary>
        /// Verify native DLLs can actually be loaded (bitness matters!)
        /// </summary>
        public static void VerifyVoiceNative()
        {
            try
            {
                Console.WriteLine($"?? Process bitness: {(Environment.Is64BitProcess ? "x64" : "x86")}");
                
                // Test libsodium
                var sodiumResult = sodium_init(); // >=0 means loaded
                Console.WriteLine($"?? libsodium init: {sodiumResult} {(sodiumResult >= 0 ? "? SUCCESS" : "? FAILED")}");
                
                // Test opus
                var versionPtr = opus_get_version_string();
                var version = Marshal.PtrToStringAnsi(versionPtr);
                Console.WriteLine($"?? opus version: {version ?? "? FAILED"}");
                
                if (sodiumResult >= 0 && !string.IsNullOrEmpty(version))
                {
                    Console.WriteLine("?? Native voice libraries verification PASSED");
                }
                else
                {
                    Console.WriteLine("? Native voice libraries verification FAILED");
                }
            }
            catch (DllNotFoundException ex)
            {
                Console.WriteLine($"? Native DLL not found: {ex.Message}");
                Console.WriteLine("?? Run download-discord-natives.ps1 to install native libraries");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Native verification error: {ex.Message}");
            }
        }

        #endregion

        /// <summary>
        /// Join a specific voice channel by name - ENHANCED WITH ATOMIC SESSION MANAGEMENT
        /// Enhanced with 4006 debugging logging and double connection prevention
        /// Usage: !join <channel name>
        /// </summary>
        [Command("join")]
        [Summary("Join a voice channel by name")]
        public async Task JoinAsync([Remainder] string channelName = null)
        {
            var guild = (SocketGuild)Context.Guild;
            var self = guild.CurrentUser;

            // CRITICAL: Check if Discord bot is shutting down before acquiring lock
            if (!DiscordNetBotManager.IsRunning)
            {
                Console.WriteLine($"?? JOIN ABORT: Discord bot is shutting down - early return");
                await ReplyAsync("? **Voice join unavailable** - Discord bot is shutting down.");
                return;
            }

            // CRITICAL 4006 PREVENTION: Global lock to prevent concurrent voice operations
            await _discordVoiceOperationLock.WaitAsync();
            try
            {
                Console.WriteLine($"?? GLOBAL VOICE LOCK ACQUIRED for guild {guild.Name}");

                // CRITICAL: Re-check shutdown status after acquiring lock
                if (!DiscordNetBotManager.IsRunning)
                {
                    Console.WriteLine($"?? JOIN ABORT: Discord bot shut down while waiting for lock");
                    await ReplyAsync("? **Voice join canceled** - Discord bot is shutting down.");
                    return;
                }

                // ATOMIC PROTECTION: Prevent multiple join attempts for the same guild
                if (_activeJoinAttempts.TryGetValue(guild.Id, out var existingAttempt) && !existingAttempt.IsCompleted)
                {
                    Console.WriteLine($"?? Join already in progress for guild {guild.Name} - EARLY RETURN");
                    await ReplyAsync("?? Voice connection already in progress for this server. Please wait...");
                    return;
                }

                // Register this join attempt
                var joinTask = PerformJoinAsync(guild, self, channelName);
                _activeJoinAttempts[guild.Id] = joinTask;

                try
                {
                    await joinTask;
                }
                finally
                {
                    // Always clean up the tracking
                    _activeJoinAttempts.TryRemove(guild.Id, out _);
                }
            }
            finally
            {
                _discordVoiceOperationLock.Release();
                Console.WriteLine($"?? GLOBAL VOICE LOCK RELEASED for guild {guild.Name}");
            }
        }

        /// <summary>
        /// Perform the actual join operation with enhanced handshake state machine
        /// </summary>
        private async Task PerformJoinAsync(SocketGuild guild, SocketGuildUser self, string channelName)
        {
            try
            {
                Console.WriteLine($"🎯 === ENHANCED VOICE JOIN START (4006 Prevention v3) ===");
                Console.WriteLine($"🎯 Join requested by {Context.User.Username} in guild: {guild.Name}");
                Console.WriteLine($"🎯 Current bot voice state: {(self.VoiceChannel?.Name ?? "None")}");
                Console.WriteLine($"🎯 Current session ID: {self.VoiceSessionId ?? "None"}");
                Console.WriteLine($"🎯 Thread ID: {Thread.CurrentThread.ManagedThreadId}");

                // CRITICAL: Check if Discord bot is shutting down - abort if so
                if (!DiscordNetBotManager.IsRunning)
                {
                    Console.WriteLine($"❌ ABORT: Discord bot is shutting down - canceling voice join operation");
                    await ReplyAsync("❌ **Voice join canceled** - Discord bot is shutting down.");
                    return;
                }

                // Resolve target
                IVoiceChannel target = null;
                if (string.IsNullOrWhiteSpace(channelName))
                {
                    // Try to get user's current channel
                    var user = Context.User as SocketGuildUser;
                    target = user?.VoiceChannel;
                    if (target == null)
                    {
                        Console.WriteLine($"? No channel specified and user not in voice channel");
                        await ReplyAsync("? Please specify a channel name or join a voice channel first: `!join <channel name>`");
                        return;
                    }
                }
                else
                {
                    target = guild.VoiceChannels.FirstOrDefault(c => 
                                 string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));
                }

                if (target == null)
                {
                    Console.WriteLine($"? Target channel not found: '{channelName}'");
                    await ReplyAsync($"? Voice channel '{channelName}' not found!");
                    return;
                }

                Console.WriteLine($"? Target channel resolved: {target.Name} (ID: {target.Id})");

                // CRITICAL 4006 PREVENTION: Check if already in target channel BEFORE any operations
                if (self.VoiceChannel != null && self.VoiceChannel.Id == target.Id)
                {
                    Console.WriteLine($"? Already in target channel: {self.VoiceChannel.Name} - EARLY RETURN");
                    await ReplyAsync($"? Already connected to **{target.Name}**!");
                    return;
                }

                // ENHANCED STEP 1: Clean up any existing voice connection
                if (self.VoiceChannel != null || !string.IsNullOrEmpty(self.VoiceSessionId))
                {
                    // CRITICAL: Check shutdown status before proceeding with disconnect
                    if (!DiscordNetBotManager.IsRunning)
                    {
                        Console.WriteLine($"❌ ABORT: Discord bot shut down during session clearing");
                        return;
                    }

                    Console.WriteLine($"🧹 STEP 1: Cleaning up existing voice state...");
                    Console.WriteLine($"🧹 STEP 1: PRE-DISCONNECT - VoiceChannel: {self.VoiceChannel?.Name ?? "None"}, Session: {self.VoiceSessionId ?? "None"}");
                    
                    // Clean up our tracking first
                    if (_audioClients.TryRemove(guild.Id, out var existing))
                    {
                        Console.WriteLine($"🧹 Disposing existing audio client...");
                        try { await existing.StopAsync(); existing.Dispose(); } catch { /* ignore */ }
                    }
                    
                    // Force disconnect if in voice
                    if (self.VoiceChannel != null)
                    {
                        Console.WriteLine($"🧹 STEP 1A: Disconnecting from voice channel: {self.VoiceChannel.Name}");
                        await self.VoiceChannel.DisconnectAsync();
                    }
                    
                    // Clear any ghost sessions
                    if (!string.IsNullOrEmpty(self.VoiceSessionId))
                    {
                        Console.WriteLine($"🧹 STEP 1B: Clearing ghost session via guild modification");
                        try
                        {
                            await guild.CurrentUser.ModifyAsync(x => x.Channel = null);
                        }
                        catch (Exception ghostEx)
                        {
                            Console.WriteLine($"⚠️ STEP 1B: Ghost session clear failed: {ghostEx.Message}");
                        }
                    }
                    
                    Console.WriteLine($"⏱️ STEP 1: Waiting 2000ms for voice state to clear...");
                    await Task.Delay(2000); // Give time for server-side clearing
                    
                    // Verification that state is clear
                    var maxWait = 10; // Maximum 10 checks (5 seconds total)
                    var waitCount = 0;
                    while ((self.VoiceChannel != null || !string.IsNullOrEmpty(self.VoiceSessionId)) && waitCount < maxWait)
                    {
                        if (!DiscordNetBotManager.IsRunning) return; // Check shutdown
                        
                        Console.WriteLine($"⏱️ STEP 1: Still has voice state - VoiceChannel: {self.VoiceChannel?.Name ?? "None"}, Session: {self.VoiceSessionId ?? "None"} (check {waitCount + 1}/{maxWait})");
                        await Task.Delay(500);
                        waitCount++;
                    }
                    
                    Console.WriteLine($"✅ STEP 1: Voice state cleanup completed");
                }
                else
                {
                    Console.WriteLine($"ℹ️ No existing voice state to clean up");
                }

                await ReplyAsync($"🎯 Connecting to **{target.Name}** with enhanced handshake...");

                // STEP 2: Use the enhanced voice join method with handshake state machine
                Console.WriteLine($"🎯 STEP 2: Starting enhanced voice join with handshake state machine...");
                
                // CRITICAL: Final shutdown check before attempting connection
                if (!DiscordNetBotManager.IsRunning)
                {
                    Console.WriteLine($"❌ ABORT: Discord bot shut down just before enhanced join - canceling operation");
                    await ReplyAsync("❌ **Connection canceled** - Discord bot is shutting down.");
                    return;
                }

                IAudioClient audioClient = null;
                try
                {
                    // Use the new enhanced join method with proper handshake
                    audioClient = await DiscordNetBotManager.JoinVoiceAsync(target, maxRetries: 3);
                    Console.WriteLine($"✅ STEP 2: Enhanced voice join completed");
                    Console.WriteLine($"✅ AudioClient state: {audioClient?.ConnectionState}");
                }
                catch (HttpException httpEx) when (httpEx.DiscordCode.HasValue && (int)httpEx.DiscordCode.Value == 4006)
                {
                    Console.WriteLine($"❌ STEP 2: 4006 error after all retries: {httpEx.Message}");
                    await ReplyAsync($"❌ **Voice join failed with 4006 'Session is no longer valid'**\n\n" +
                                   "This indicates persistent session conflicts. Try:\n" +
                                   "• `!clearsession` and wait 15 seconds\n" +
                                   "• Check for multiple bot instances with `!tokencheck`\n" +
                                   "• Restart the application if issues persist");
                    return;
                }
                catch (TimeoutException timeoutEx)
                {
                    Console.WriteLine($"❌ STEP 2: Voice handshake timeout: {timeoutEx.Message}");
                    await ReplyAsync($"❌ **Voice handshake timed out**\n\n" +
                                   $"The bot didn't receive session/server info from Discord.\n" +
                                   $"This may indicate Discord voice server issues.");
                    return;
                }
                catch (Exception joinEx)
                {
                    Console.WriteLine($"❌ STEP 2: Enhanced voice join failed: {joinEx.GetType().Name}: {joinEx.Message}");
                    await ReplyAsync($"❌ **Enhanced voice join failed:** {joinEx.Message}\n\n" +
                                   $"Error type: {joinEx.GetType().Name}");
                    return;
                }

                if (audioClient == null)
                {
                    Console.WriteLine($"❌ STEP 2: Enhanced voice join returned null");
                    await ReplyAsync($"❌ **Connection failed:** Enhanced join returned null audio client");
                    return;
                }

                // STEP 3: Store and configure the connection
                Console.WriteLine($"🎯 STEP 3: Storing and configuring connection...");
                _audioClients[guild.Id] = audioClient;
                
                audioClient.Disconnected += ex =>
                {
                    Console.WriteLine($"🔌 AudioClient.Disconnected event: {ex?.Message ?? "Normal"}");
                    _audioClients.TryRemove(guild.Id, out _);
                    return Task.CompletedTask;
                };

                Console.WriteLine($"🎯 STEP 3: Calling DiscordNetBotManager.SetVoiceConnection...");
                await DiscordNetBotManager.SetVoiceConnection(audioClient, target.Id, target.Name);

                Console.WriteLine($"🎉 === ENHANCED VOICE JOIN SUCCESS ===");
                await ReplyAsync($"🎉 Joined **{target.Name}** successfully with enhanced handshake! (Connection state: {audioClient.ConnectionState})");

            }
            catch (InvalidOperationException opEx) when (opEx.Message.Contains("Client is not logged in"))
            {
                Console.WriteLine($"?? === VOICE JOIN ABORTED - CLIENT LOGGED OUT ===");
                Console.WriteLine($"?? Discord client was logged out during voice join operation");
                Console.WriteLine($"?? This is normal during application shutdown");
                
                // Don't show error to user during shutdown - it's expected
                try
                {
                    await ReplyAsync($"? Voice join canceled - Discord bot is shutting down.");
                }
                catch
                {
                    // Ignore reply errors during shutdown
                    Console.WriteLine($"?? Could not send shutdown message - channel likely unavailable");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? === VOICE JOIN DEBUG ERROR ===");
                Console.WriteLine($"? Exception type: {ex.GetType().Name}");
                Console.WriteLine($"? Exception message: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"? Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
                Console.WriteLine($"?? Stack trace: {ex.StackTrace}");
                
                await ReplyAsync($"? Join failed: {ex.Message}\n\n**Error Type:** {ex.GetType().Name}\n\n" +
                               $"**Recovery options:**\n" +
                               $"� Try `!clearsession` then wait 15 seconds\n" +
                               $"� Use `!testjoin {channelName ?? "channel"}` for isolated testing\n" +
                               $"� Check `!tokencheck` for multiple process conflicts\n" +
                               $"� Restart application if session issues persist");
            }
        }

        /// <summary>
        /// Leave the current voice channel - TRULY CLEAR STATE PATTERN
        /// Usage: !leave
        /// </summary>
        [Command("leave")]
        [Summary("Leave the current voice channel")]
        public async Task LeaveAsync()
        {
            var guild = (SocketGuild)Context.Guild;
            var self = guild.CurrentUser;

            // CRITICAL: Check if Discord bot is shutting down
            if (!DiscordNetBotManager.IsRunning)
            {
                Console.WriteLine($"?? LEAVE ABORT: Discord bot is shutting down - early return");
                await ReplyAsync("? **Voice leave unavailable** - Discord bot is shutting down.");
                return;
            }

            // CRITICAL 4006 PREVENTION: Global lock to prevent concurrent voice operations
            await _discordVoiceOperationLock.WaitAsync();
            try
            {
                Console.WriteLine($"?? GLOBAL VOICE LOCK ACQUIRED for leave operation in guild {guild.Name}");

                // CRITICAL: Re-check shutdown status after acquiring lock
                if (!DiscordNetBotManager.IsRunning)
                {
                    Console.WriteLine($"?? LEAVE ABORT: Discord bot shut down while waiting for lock");
                    await ReplyAsync("? **Voice leave canceled** - Discord bot is shutting down.");
                    return;
                }

                // DON'T REUSE/LINGER: Stop/Dispose audio client if we have it
                if (_audioClients.TryRemove(guild.Id, out var ac))
                {
                    try { await ac.StopAsync(); ac.Dispose(); } catch { /* ignore */ }
                    Console.WriteLine($"?? Disposed audio client for guild {guild.Name}");
                }

                // Tell Discord to move the bot out of voice (clears session server-side)
                if (self.VoiceChannel != null)
                {
                    Console.WriteLine($"?? Disconnecting from voice channel: {self.VoiceChannel.Name}");
                    
                    try
                    {
                        await self.VoiceChannel.DisconnectAsync();
                        Console.WriteLine($"? DisconnectAsync completed");
                    }
                    catch (InvalidOperationException opEx) when (opEx.Message.Contains("Client is not logged in"))
                    {
                        Console.WriteLine($"?? LEAVE: Discord client was logged out during disconnect - this is normal during shutdown");
                        await ReplyAsync("? Leave canceled - Discord bot is shutting down.");
                        return;
                    }
                    
                    // Fix 3: Confirm null-state via event (up to 3s)
                    var clearedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task Handler(SocketUser u, SocketVoiceState before, SocketVoiceState after)
                    {
                        if (u.Id == self.Id && after.VoiceChannel == null)
                            clearedTcs.TrySetResult(true);
                        return Task.CompletedTask;
                    }
                    Context.Client.UserVoiceStateUpdated += Handler;
                    try { await Task.WhenAny(clearedTcs.Task, Task.Delay(3000)); }
                    finally { Context.Client.UserVoiceStateUpdated -= Handler; }
                    // Guild-level safety null
                    try { await self.ModifyAsync(x => x.Channel = null); } catch { }
                    await Task.Delay(250);
                }

                await DiscordNetBotManager.OnVoiceChannelLeft();
                await ReplyAsync("? Left voice channel.");
                Console.WriteLine($"? Leave operation completed for guild {guild.Name}");
            }
            catch (InvalidOperationException opEx) when (opEx.Message.Contains("Client is not logged in"))
            {
                Console.WriteLine($"?? LEAVE: Discord client logged out during leave operation - normal during shutdown");
                try
                {
                    await ReplyAsync("? Leave operation canceled - Discord bot is shutting down.");
                }
                catch
                {
                    Console.WriteLine($"?? Could not send leave message - channel unavailable during shutdown");
                }
            }
            finally
            {
                _discordVoiceOperationLock.Release();
                Console.WriteLine($"?? GLOBAL VOICE LOCK RELEASED for leave operation in guild {guild.Name}");
            }
        }

        /// <summary>
        /// Show comprehensive system status
        /// Usage: !status
        /// </summary>
        [Command("status")]
        [Summary("Show comprehensive system status")]
        public async Task StatusAsync()
        {
            try
            {
                var embed = new EmbedBuilder()
                    .WithTitle("?? Kinect Voice Bot Status (Discord.Net)")
                    .WithColor(Color.Blue)
                    .WithTimestamp(DateTimeOffset.UtcNow);

                // Bot status
                var botStatus = DiscordNetBotManager.GetStatusSummary();
                embed.AddField("?? Discord.Net Bot", $"```{botStatus}```", false);

                // Inline native library status
                string nativeStatus;
                try
                {
                    var loaded = DiscordNativeLoader.AreLibrariesLoaded;
                    var sodium = sodium_init();
                    var opusPtr = opus_get_version_string();
                    var opusVer = Marshal.PtrToStringAnsi(opusPtr);
                    nativeStatus = $"Libraries Loaded: {(loaded ? "? Yes" : "? No")}\n" +
                                   $"Sodium Init: {(sodium >= 0 ? "? OK" : "? Failed")}\n" +
                                   $"Opus Version: {(string.IsNullOrEmpty(opusVer) ? "? Failed" : "? " + opusVer)}\n" +
                                   $"Process: {(Environment.Is64BitProcess ? "x64 ?" : "x86 ??")}";
                }
                catch (Exception ex)
                {
                    nativeStatus = $"Status: ? Error\n{ex.Message}";
                }
                embed.AddField("?? Native Libraries", $"```{nativeStatus}```", false);

                // Inline voice recognition status
                string voiceStatus;
                try
                {
                    if (VoiceRecognizer.IsReady())
                    {
                        var micEnabled = VoiceRecognizer.IsMicrophoneInputEnabled();
                        var discordEnabled = VoiceRecognizer.IsDiscordInputEnabled();
                        var (queueSize, isProcessing) = VoiceRecognizer.GetExternalAudioStats();
                        voiceStatus = $"Status: ? Ready\n" +
                                      $"Microphone: {(micEnabled ? "? Enabled" : "? Disabled")}\n" +
                                      $"Discord: {(discordEnabled ? "? Enabled" : "? Disabled")}\n" +
                                      $"Queue size: {queueSize}\n" +
                                      $"Processing: {(isProcessing ? "?? Active" : "?? Idle")}";
                    }
                    else
                    {
                        voiceStatus = "Status: ? Not Ready\nVoice recognition not initialized";
                    }
                }
                catch (Exception ex)
                {
                    voiceStatus = $"Status: ? Error\n{ex.Message}";
                }
                embed.AddField("?? Voice Recognition", $"```{voiceStatus}```", false);

                // Inline voice connections status
                string connectionStatus;
                try
                {
                    var status = $"Active Connections: {_audioClients.Count}\n";
                    if (_audioClients.Any())
                    {
                        foreach (var kvp in _audioClients)
                        {
                            var gid = kvp.Key; var client = kvp.Value;
                            status += $"Guild {gid}: {client.ConnectionState}\n";
                        }
                    }
                    else status += "No active voice connections";
                    connectionStatus = status;
                }
                catch (Exception ex)
                {
                    connectionStatus = $"Error: {ex.Message}";
                }
                embed.AddField("?? Voice Connections", connectionStatus, false);

                await ReplyAsync(embed: embed.Build());
                Console.WriteLine($"?? Status command executed by {Context.User.Username}");
            }
            catch (Exception ex)
            {
                await ReplyAsync($"? Error getting status: {ex.Message}");
                Console.WriteLine($"? Discord.Net status command failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Test native libraries and voice connection
        /// Usage: !testvoice
        /// </summary>
        [Command("testvoice")]
        [Summary("Test Discord.Net voice connection and native libraries")]
        public async Task TestVoiceAsync()
        {
            try
            {
                await ReplyAsync("?? **Testing Discord.Net voice system...**");
                
                var embed = new EmbedBuilder()
                    .WithTitle("?? Discord.Net Voice Test")
                    .WithColor(Color.Purple)
                    .WithTimestamp(DateTimeOffset.UtcNow);

                // Perform native library verification
                Console.WriteLine("?? Running native library verification test...");
                VerifyVoiceNative();

                var testResult = $"Discord.Net Client: {(DiscordNetBotManager.IsRunning ? "? Ready" : "? Not Ready")}\n" +
                               $"Voice Connection: {(DiscordNetBotManager.IsInVoiceChannel ? "? Connected" : "? Not Connected")}\n" +
                               $"Native Libraries: {(DiscordNativeLoader.AreLibrariesLoaded ? "? Loaded" : "? Not Loaded")}\n" +
                               $"Active Connections: {_audioClients.Count}\n" +
                               $"Process Bitness: {(Environment.Is64BitProcess ? "x64 ?" : "x86 ??")}\n" +
                               $"Discord Input: {(VoiceRecognizer.IsDiscordInputEnabled() ? "? Enabled" : "? Disabled")}\n" +
                               $"VoiceRecognizer: {(VoiceRecognizer.IsReady() ? "? Ready" : "? Not Ready")}";
                
                embed.AddField("?? Test Results", $"```{testResult}```", false);
                
                var instructions = "**Testing Steps:**\n" +
                                 "1. Use `!join <channel>` to connect to voice\n" +
                                 "2. Join the same voice channel as the bot\n" +
                                 "3. Speak clearly in the voice channel\n" +
                                 "4. Watch console for voice processing messages\n" +
                                 "5. Use `!status` for detailed diagnostics\n" +
                                 "6. Check console for native library verification results";
                
                embed.AddField("?? Instructions", instructions, false);
                
                await ReplyAsync(embed: embed.Build());
                
                Console.WriteLine($"?? Voice test command executed by {Context.User.Username}");
            }
            catch (Exception ex)
            {
                await ReplyAsync($"? Voice test failed: {ex.Message}");
                Console.WriteLine($"? Voice test command failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reload native libraries and verify functionality
        /// Usage: !reload
        /// </summary>
        [Command("reload")]
        [Summary("Reload Discord.Net native libraries")]
        public async Task ReloadAsync()
        {
            try
            {
                await ReplyAsync("?? **Reloading Discord.Net native libraries...**");
                
                Console.WriteLine("?? Manual reload of native libraries requested...");
                
                // Attempt to reload libraries
                var success = DiscordNativeLoader.ReloadNativeLibraries();
                
                // Verify they actually work
                if (success)
                {
                    Console.WriteLine("?? Verifying reloaded libraries...");
                    VerifyVoiceNative();
                    
                    await ReplyAsync("? **Native libraries reloaded successfully!**\n\n" +
                                   "Libraries verified and functional.\n" +
                                   "Try using `!join <channel>` to test the connection.");
                    Console.WriteLine("? Native libraries manually reloaded and verified");
                }
                else
                {
                    await ReplyAsync("? **Failed to reload native libraries**\n\n" +
                                   "**To fix this:**\n" +
                                   "1. Run: `download-discord-natives.ps1`\n" +
                                   "2. Restart the application\n" +
                                   "3. Try `!reload` again");
                    Console.WriteLine("? Native library reload failed");
                }
            }
            catch (Exception ex)
            {
                await ReplyAsync($"? Reload failed: {ex.Message}");
                Console.WriteLine($"? Reload command failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Show help for available commands
        /// Usage: !help
        /// </summary>
        [Command("help")]
        [Summary("Show available commands")]
        public async Task HelpAsync()
        {
            try
            {
                var prefix = Kinectv1.App.SettingsProvider?.Current?.Discord?.Prefix;
                
                var embed = new EmbedBuilder()
                    .WithTitle("?? Kinect Voice Bot Commands (Enhanced)")
                    .WithDescription($"Command prefix: `{prefix}`")
                    .WithColor(Color.Green)
                    .WithTimestamp(DateTimeOffset.UtcNow);

                embed.AddField($"`{prefix}join <channel>`", "Join voice channel with native audio receiving", true);
                embed.AddField($"`{prefix}leave`", "Leave current voice channel", true);
                embed.AddField($"`{prefix}speak <text>`", "Speak text using TTS in voice channel", true);
                embed.AddField($"`{prefix}testtts`", "Test TTS audio generation and Discord streaming", true);
                embed.AddField($"`{prefix}testconnection`", "Test Discord voice connection stability", true);
                embed.AddField($"`{prefix}debugaudio`", "Debug Discord audio format and settings", true);
                embed.AddField($"`{prefix}diagnose`", "Diagnose Discord voice connection issues", true);
                embed.AddField($"`{prefix}checkperms`", "Check bot permissions in voice channels", true);
                embed.AddField($"`{prefix}conndiag`", "Enhanced connection state diagnostics", true);
                embed.AddField($"`{prefix}status`", "Show comprehensive system status", true);
                embed.AddField($"`{prefix}testvoice`", "Test voice system & native libraries", true);
                embed.AddField($"`{prefix}testaudio`", "Test Discord audio processing pipeline", true);
                embed.AddField($"`{prefix}reload`", "Reload & verify native libraries", true);
                embed.AddField($"`{prefix}help`", "Show this help message", true);
                embed.AddField($"`{prefix}ping`", "Test bot responsiveness", true);

                embed.WithFooter("Enhanced Voice Bot - Native Audio with TTS Support");

                await ReplyAsync(embed: embed.Build());
                Console.WriteLine($"? Help command executed by {Context.User.Username}");
            }
            catch (Exception ex)
            {
                await ReplyAsync($"? Error showing help: {ex.Message}");
                Console.WriteLine($"? Discord.Net help command failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Simple ping command to test if bot is responding
        /// Usage: !ping
        /// </summary>
        [Command("ping")]
        [Summary("Test if the bot is responding")]
        public async Task PingAsync()
        {
            try
            {
                Console.WriteLine($"?? Ping command received from {Context.User.Username}");
                await ReplyAsync("?? Pong! Bot is responding correctly.");
                Console.WriteLine($"? Ping command executed successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Ping command failed: {ex.Message}");
                await ReplyAsync($"? Error: {ex.Message}");
            }
        }

        #region Helper Methods

        private string GetNativeLibraryStatus()
        {
            try
            {
                var status = $"Libraries Loaded: {(DiscordNativeLoader.AreLibrariesLoaded ? "? Yes" : "? No")}\n" +
                           $"opus.dll: {(System.IO.File.Exists("libs/opus.dll") ? "? Available" : "? Missing")}\n" +
                           $"libsodium.dll: {(System.IO.File.Exists("libs/libsodium.dll") ? "? Available" : "? Missing")}\n" +
                           $"Process: {(Environment.Is64BitProcess ? "x64 ?" : "x86 ??")}";

                // Try to get version info
                try
                {
                    var sodiumResult = sodium_init();
                    var versionPtr = opus_get_version_string();
                    var version = Marshal.PtrToStringAnsi(versionPtr);
                    
                    status += $"\nSodium Init: {(sodiumResult >= 0 ? "? OK" : "? Failed")}";
                    status += $"\nOpus Version: {(string.IsNullOrEmpty(version) ? "? Failed" : "? " + version)}";
                }
                catch (Exception ex)
                {
                    status += $"\nVerification: ? {ex.Message}";
                }

                return status;
            }
            catch (Exception ex)
            {
                return $"Status: ? Error\n{ex.Message}";
            }
        }

        private string GetVoiceConnectionStatus()
        {
            try
            {
                var status = $"Active Connections: {_audioClients.Count}\n";
                
                if (_audioClients.Any())
                {
                    foreach (var kvp in _audioClients)
                    {
                        var guildId = kvp.Key;
                        var client = kvp.Value;
                        status += $"Guild {guildId}: {client.ConnectionState}\n";
                    }
                }
                else
                {
                    status += "No active voice connections";
                }

                return status;
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private string GetVoiceRecognitionStatus()
        {
            try
            {
                if (VoiceRecognizer.IsReady())
                {
                    var micEnabled = VoiceRecognizer.IsMicrophoneInputEnabled();
                    var discordEnabled = VoiceRecognizer.IsDiscordInputEnabled();
                    var (queueSize, isProcessing) = VoiceRecognizer.GetExternalAudioStats();
                    
                    return $"Status: ? Ready\n" +
                           $"Microphone: {(micEnabled ? "? Enabled" : "? Disabled")}\n" +
                           $"Discord: {(discordEnabled ? "? Enabled" : "? Disabled")}\n" +
                           $"Queue size: {queueSize}\n" +
                           $"Processing: {(isProcessing ? "?? Active" : "?? Idle")}";
                }
                else
                {
                    return "Status: ? Not Ready\nVoice recognition not initialized";
                }
            }
            catch (Exception ex)
            {
                return $"Status: ? Error\n{ex.Message}";
            }
        }

        #endregion

        /// <summary>
        /// Speak text using TTS in the voice channel
        /// Usage: !speak <text>
        /// </summary>
        [Command("speak")]
        [Summary("Speak text using TTS in the voice channel")]
        public async Task SpeakAsync([Remainder] string text = null)
        {
            try
            {
                if (string.IsNullOrEmpty(text))
                {
                    await ReplyAsync("? Please provide text to speak: `!speak Hello world`");
                    return;
                }

                var guildId = Context.Guild.Id;
                if (!_audioClients.TryGetValue(guildId, out var audioClient) || audioClient.ConnectionState != ConnectionState.Connected)
                {
                    await ReplyAsync("? Bot is not connected to a voice channel. Use `!join <channel>` first.");
                    return;
                }

                await ReplyAsync($"?? Speaking: \"{text}\"");
                Console.WriteLine($"?? TTS request from {Context.User.Username}: \"{text}\"");

                // Use the centralized Discord TTS method for consistency
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var success = await DiscordNetBotManager.SendTtsToDiscordAsync(text);
                        
                        if (success)
                        {
                            Console.WriteLine($"? TTS command completed successfully");
                        }
                        else
                        {
                            Console.WriteLine($"? TTS command failed - check console for details");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"? Error in Discord TTS command: {ex.Message}");
                        Console.WriteLine($"?? Stack trace: {ex.StackTrace}");
                    }
                });
            }
            catch (Exception ex)
            {
                await ReplyAsync($"? Failed to speak: {ex.Message}");
                Console.WriteLine($"? Speak command failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Test Discord audio processing pipeline
        /// Usage: !testaudio
        /// </summary>
        [Command("testaudio")]
        [Summary("Test Discord audio processing pipeline")]
        public async Task TestAudioAsync()
        {
            try
            {
                await ReplyAsync("?? **Testing Discord audio processing pipeline...**");
                
                Console.WriteLine($"?? Testing Discord audio pipeline...");
                
                // Generate test audio data (simulated PCM)
                var testAudio = new byte[3840]; // 20ms of 48kHz stereo
                var random = new Random();
                
                // Fill with realistic audio-like data (not pure noise)
                for (int i = 0; i < testAudio.Length; i += 2)
                {
                    // Generate a sine wave pattern for test audio
                    var sample = (short)(Math.Sin(i * 0.1) * 1000 + random.Next(-100, 100));
                    testAudio[i] = (byte)(sample & 0xFF);
                    testAudio[i + 1] = (byte)((sample >> 8) & 0xFF);
                }
                
                Console.WriteLine($"?? Generated {testAudio.Length} bytes of test audio data");
                
                // Test the processing pipeline
                Console.WriteLine($"?? Testing ProcessVoiceData...");
                DiscordNetBotManager.ProcessVoiceData(testAudio, "TestUser");
                
                await ReplyAsync("? **Test completed!** Check console for detailed results.\n\n" +
                               "**What was tested:**\n" +
                               "� Audio data processing\n" +
                               "� RMS calculation and callback\n" +
                               "� Speech recognition pipeline\n" +
                               "� Error handling\n\n" +
                               "**Check console for:**\n" +
                               "� ?? ProcessVoiceData logs\n" +
                               "� ?? Audio processing results\n" +
                               "� ?? RMS callback status");
                
                Console.WriteLine($"? Audio pipeline test completed");
            }
            catch (Exception ex)
            {
                await ReplyAsync($"? Audio test failed: {ex.Message}");
                Console.WriteLine($"? Audio test command failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Test TTS audio generation and Discord streaming
        /// Usage: !testtts
        /// </summary>
        [Command("testtts")]
        [Summary("Test TTS audio generation and Discord streaming")]
        public async Task TestTtsAsync([Remainder] string text = "This is a test of the TTS system.")
        {
            try
            {
                if (!TtsService.IsEnabled())
                {
                    await ReplyAsync("TTS service not enabled in settings.");
                    return;
                }

                var ok = await DiscordNetBotManager.SendTtsToDiscordAsync(text);
                await ReplyAsync(ok ? "✅ TTS sent to Discord." : "❌ Failed to send TTS.");
            }
            catch (Exception ex)
            {
                await ReplyAsync($"Error: {ex.Message}");
            }
        }
    }
}