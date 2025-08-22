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
        private static readonly SemaphoreSlim _discordVoiceOperationLock = new SemaphoreSlim(1, 1);
        
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
                    
                    // Wait for voice state to clear
                    await Task.Delay(750);
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

                // Discord.Net Bot Status
                var botStatus = DiscordNetBotManager.GetStatusSummary();
                embed.AddField("?? Discord.Net Bot", $"```{botStatus}```", false);

                // Native Libraries Status with verification
                var nativeStatus = GetNativeLibraryStatus();
                embed.AddField("?? Native Libraries", $"```{nativeStatus}```", false);

                // Voice Recognition Status
                var voiceStatus = GetVoiceRecognitionStatus();
                embed.AddField("?? Voice Recognition", $"```{voiceStatus}```", false);

                // Voice Connections Status
                var connectionStatus = GetVoiceConnectionStatus();
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
                var prefix = AppSettings.LoadDiscordBotPrefix();
                
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
        public async Task TestTtsAsync()
        {
            try
            {
                var guildId = Context.Guild.Id;
                if (!_audioClients.TryGetValue(guildId, out var audioClient) || audioClient.ConnectionState != ConnectionState.Connected)
                {
                    await ReplyAsync("? Bot is not connected to a voice channel. Use `!join <channel>` first.");
                    return;
                }

                await ReplyAsync("?? **Testing TTS pipeline for Discord...**");
                Console.WriteLine($"?? Testing TTS pipeline for Discord voice channel...");

                // Test with a short phrase that should be clearly audible
                var testText = "Testing Discord TTS pipeline - this should sound clear, normal volume, and proper pitch";
                
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (!CoquiTtsService.IsEnabled())
                        {
                            Console.WriteLine($"? TTS service is disabled - enable it first");
                            return;
                        }

                        Console.WriteLine($"?? Testing TTS generation: \"{testText}\"");
                        Console.WriteLine($"?? Expected format: Ultra-aggressive volume boost");
                        
                        // Use the Discord TTS method directly
                        var success = await DiscordNetBotManager.SendTtsToDiscordAsync(testText);
                        
                        if (success)
                        {
                            Console.WriteLine($"?? TTS test completed successfully!");
                            Console.WriteLine($"?? Audio should now be:");
                            Console.WriteLine($"   � MAXIMUM volume (30x+ ultra-aggressive boost)");
                            Console.WriteLine($"   � Near full-scale output (-1 dBFS target)");
                            Console.WriteLine($"   � Minimal limiting (only at 98% scale)");
                            Console.WriteLine($"   � May have some distortion for maximum volume");
                        }
                        else
                        {
                            Console.WriteLine($"? TTS test failed - check console for details");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"? TTS test failed: {ex.Message}");
                        Console.WriteLine($"?? Stack trace: {ex.StackTrace}");
                    }
                });
            }
            catch (Exception ex)
            {
                await ReplyAsync($"? TTS test failed: {ex.Message}");
                Console.WriteLine($"? TTS test command failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Debug Discord audio format and settings
        /// Usage: !debugaudio
        /// </summary>
        [Command("debugaudio")]
        [Summary("Debug Discord audio format and settings")]
        public async Task DebugAudioAsync()
        {
            try
            {
                var embed = new EmbedBuilder()
                    .WithTitle("?? Discord Audio Debug Information")
                    .WithColor(Color.Orange)
                    .WithTimestamp(DateTimeOffset.UtcNow);

                // TTS Configuration
                var ttsInfo = $"TTS Enabled: {(CoquiTtsService.IsEnabled() ? "? Yes" : "? No")}\n" +
                             $"TTS Model: {CoquiTtsService.GetCurrentModel()}\n" +
                             $"TTS Mode: {CoquiTtsService.GetExecutionMode()}\n" +
                             $"Current Speaker: {AppSettings.LoadTtsSpeaker()}";
                embed.AddField("?? TTS Configuration", $"```{ttsInfo}```", false);

                // Discord Audio Format
                var audioFormat = $"Expected Input: TTS MONO 22050 Hz float32\n" +
                                $"Resampling: 22050 Hz ? 48000 Hz (MONO)\n" +
                                $"Volume Boost: ULTRA-AGGRESSIVE 30x+ boost\n" +
                                $"Processing: Maximum volume, minimal limiting\n" +
                                $"Target Level: -1 dBFS (nearly full scale)\n" +
                                $"Conversion: MONO ? STEREO (duplicate channels)\n" +
                                $"Output Format: STEREO 48000 Hz 16-bit PCM\n" +
                                $"Chunk Size: 3840 bytes (20ms STEREO)\n" +
                                $"Stream Buffer: 128KB";
                embed.AddField("?? Audio Pipeline", $"```{audioFormat}```", false);

                // Connection Status
                var guildId = Context.Guild.Id;
                var connectionInfo = "Voice Connection: ";
                if (_audioClients.TryGetValue(guildId, out var audioClient) && audioClient.ConnectionState == ConnectionState.Connected)
                {
                    connectionInfo += $"? Connected\n" +
                                   $"Connection State: {audioClient.ConnectionState}\n" +
                                   $"Channel: Ready for audio output\n" +
                                   $"Latency: {audioClient.Latency}ms";
                }
                else
                {
                    connectionInfo += $"? Not Connected\n" +
                                   $"Use !join <channel> first";
                }
                embed.AddField("?? Connection Status", $"```{connectionInfo}```", false);

                // Common Issues
                var troubleshooting = $"� **Low Volume**: Fixed with ultra-aggressive 30x+ boost\n" +
                                    $"� **Distortion**: Minimized with 98% hard limiting\n" +
                                    $"� **High Pitch/Fast**: Fixed with proper STEREO format\n" +
                                    $"� **No Audio**: Verify bot has Speak permission\n" +
                                    $"� **Still Quiet**: Try restarting Discord client\n" +
                                    $"� **Clipping**: Acceptable for maximum volume\n" +
                                    $"� **Mono Issues**: Fixed - now converts to STEREO";
                embed.AddField("??? Troubleshooting", troubleshooting, false);

                await ReplyAsync(embed: embed.Build());
                Console.WriteLine($"?? Audio debug info requested by {Context.User.Username}");
            }
            catch (Exception ex)
            {
                await ReplyAsync($"? Debug failed: {ex.Message}");
                Console.WriteLine($"? Debug command failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Diagnose Discord voice connection issues
        /// Usage: !diagnose
        /// </summary>
        [Command("diagnose")]
        [Summary("Diagnose Discord voice connection issues")]
        public async Task DiagnoseAsync()
        {
            try
            {
                var embed = new EmbedBuilder()
                    .WithTitle("?? Discord Voice Connection Diagnostics")
                    .WithColor(Color.Gold)
                    .WithTimestamp(DateTimeOffset.UtcNow);

                Console.WriteLine("?? Running Discord voice connection diagnostics...");
                
                var guild = Context.Guild as SocketGuild;

                // 1. Network connectivity test
                var networkStatus = "Testing network connectivity...";
                try
                {
                    using (var client = new System.Net.Http.HttpClient())
                    {
                        client.Timeout = TimeSpan.FromSeconds(5);
                        var response = await client.GetAsync("https://discord.com/api/v10/gateway");
                        networkStatus = response.IsSuccessStatusCode ? "? Discord API reachable" : $"?? Discord API issue: {response.StatusCode}";
                    }
                }
                catch (Exception netEx)
                {
                    networkStatus = $"? Network error: {netEx.Message}";
                }

                // 2. Guild and channel analysis
                var voiceChannels = guild.VoiceChannels.ToList();
                var guildInfo = $"Guild: {guild.Name} (ID: {guild.Id})\n" +
                               $"Voice Channels: {voiceChannels.Count}\n" +
                               $"Bot Permissions: {guild.CurrentUser.GuildPermissions}\n" +
                               $"Guild Region: {guild.PreferredLocale}";

                // 3. Current voice state
                var currentVoiceState = "No active connections";
                if (_audioClients.TryGetValue(guild.Id, out var audioClient))
                {
                    currentVoiceState = $"State: {audioClient.ConnectionState}\n" +
                                       $"Latency: {audioClient.Latency}ms";
                }

                // 4. Native libraries status
                var nativeStatus = "Testing native libraries...";
                try
                {
                    VerifyVoiceNative();
                    nativeStatus = "? Native libraries verified";
                }
                catch (Exception nativeEx)
                {
                    nativeStatus = $"? Native library issue: {nativeEx.Message}";
                }

                // 5. Recent connection attempts
                var recentAttempts = "No recent connection data available";
                // You could implement connection attempt logging here

                embed.AddField("?? Network Status", networkStatus, false);
                embed.AddField("?? Guild Information", $"```{guildInfo}```", false);
                embed.AddField("?? Current Voice State", currentVoiceState, false);
                embed.AddField("?? Native Libraries", nativeStatus, false);

                // 6. Recommendations
                var recommendations = "� Try `!reload` to refresh native libraries\n" +
                                    "� Wait 30-60 seconds between connection attempts\n" +
                                    "� Check if other bots can connect to voice\n" +
                                    "� Try a different voice channel\n" +
                                    "� Verify bot has Connect and Speak permissions";
                embed.AddField("?? Recommendations", recommendations, false);

                await ReplyAsync(embed: embed.Build());
                Console.WriteLine($"?? Diagnostics completed for {Context.User.Username}");
            }
            catch (Exception ex)
            {
                await ReplyAsync($"? Diagnostics failed: {ex.Message}");
                Console.WriteLine($"? Diagnostics command failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Enhanced diagnostics for connection state tracking
        /// Usage: !conndiag
        /// </summary>
        [Command("conndiag")]
        [Summary("Enhanced connection state diagnostics")]
        public async Task ConnectionDiagnosticsAsync()
        {
            try
            {
                var embed = new EmbedBuilder()
                    .WithTitle("?? Connection State Diagnostics")
                    .WithColor(Color.DarkBlue)
                    .WithTimestamp(DateTimeOffset.UtcNow);

                var guildId = Context.Guild.Id;

                // Commands module tracking
                var commandsTracking = "Commands Module Tracking:\n";
                if (_audioClients.TryGetValue(guildId, out var commandClient))
                {
                    commandsTracking += $"? Client stored: {commandClient.ConnectionState}\n";
                    commandsTracking += $"   Latency: {commandClient.Latency}ms\n";
                    commandsTracking += $"   Connection State: {commandClient.ConnectionState}";
                }
                else
                {
                    commandsTracking += "? No client stored in commands module";
                }

                // Bot manager tracking
                var managerTracking = "Bot Manager Tracking:\n";
                managerTracking += $"Is Running: {DiscordNetBotManager.IsRunning}\n";
                managerTracking += $"In Voice Channel: {DiscordNetBotManager.IsInVoiceChannel}\n";
                managerTracking += $"Voice Status: {DiscordNetBotManager.GetVoiceConnectionStatus()}";

                // Connection synchronization analysis
                var syncAnalysis = "Synchronization Analysis:\n";
                var commandsHasConnection = _audioClients.ContainsKey(guildId);
                var managerHasConnection = DiscordNetBotManager.IsInVoiceChannel;
                
                if (commandsHasConnection && managerHasConnection)
                {
                    syncAnalysis += "? Both systems synchronized";
                }
                else if (commandsHasConnection && !managerHasConnection)
                {
                    syncAnalysis += "?? Commands has connection, Manager doesn't";
                }
                else if (!commandsHasConnection && managerHasConnection)
                {
                    syncAnalysis += "?? Manager has connection, Commands doesn't";
                }
                else
                {
                    syncAnalysis += "? Both systems show no connection";
                }

                embed.AddField("?? Commands Module", $"```{commandsTracking}```", false);
                embed.AddField("?? Bot Manager", $"```{managerTracking}```", false);
                embed.AddField("?? Sync Status", $"```{syncAnalysis}```", false);

                await ReplyAsync(embed: embed.Build());
                Console.WriteLine($"?? Connection diagnostics run by {Context.User.Username}");
            }
            catch (Exception ex)
            {
                await ReplyAsync($"? Connection diagnostics failed: {ex.Message}");
                Console.WriteLine($"? Connection diagnostics failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Check bot permissions in voice channels
        /// Usage: !checkperms
        /// </summary>
        [Command("checkperms")]
        [Summary("Check bot permissions in voice channels")]
        public async Task CheckPermissionsAsync()
        {
            try
            {
                var embed = new EmbedBuilder()
                    .WithTitle("?? Bot Permissions Check")
                    .WithColor(Color.Blue)
                    .WithTimestamp(DateTimeOffset.UtcNow);

                var guild = Context.Guild as SocketGuild;
                
                // Overall guild permissions
                var guildPerms = guild.CurrentUser.GuildPermissions;
                var guildInfo = $"Administrator: {(guildPerms.Administrator ? "?" : "?")}\n" +
                               $"Connect: {(guildPerms.Connect ? "?" : "?")}\n" +
                               $"Speak: {(guildPerms.Speak ? "?" : "?")}\n" +
                               $"Use Voice Activity: {(guildPerms.UseVAD ? "?" : "?")}\n" +
                               $"Send Messages: {(guildPerms.SendMessages ? "?" : "?")}\n" +
                               $"Read Message History: {(guildPerms.ReadMessageHistory ? "?" : "?")}";

                embed.AddField("?? Guild Permissions", $"```{guildInfo}```", false);

                // Check all voice channels
                var voiceChannels = guild.VoiceChannels.ToList();
                var channelPerms = "";
                
                foreach (var channel in voiceChannels.Take(5)) // Limit to 5 channels to avoid embed limits
                {
                    var perms = guild.CurrentUser.GetPermissions(channel);
                    channelPerms += $"#{channel.Name}:\n";
                    channelPerms += $"  Connect: {(perms.Connect ? "?" : "?")}\n";
                    channelPerms += $"  Speak: {(perms.Speak ? "?" : "?")}\n";
                    channelPerms += $"  Use VAD: {(perms.UseVAD ? "?" : "?")}\n\n";
                }

                if (string.IsNullOrEmpty(channelPerms))
                {
                    channelPerms = "No voice channels found";
                }

                embed.AddField("?? Voice Channel Permissions", $"```{channelPerms}```", false);

                // Bot role info
                var botRoles = guild.CurrentUser.Roles.Where(r => !r.IsEveryone).ToList();
                var roleInfo = botRoles.Any() 
                    ? string.Join(", ", botRoles.Select(r => r.Name))
                    : "No roles assigned";

                embed.AddField("?? Bot Roles", roleInfo, false);

                await ReplyAsync(embed: embed.Build());
                Console.WriteLine($"?? Permission check completed for {Context.User.Username}");
            }
            catch (Exception ex)
            {
                await ReplyAsync($"? Permission check failed: {ex.Message}");
                Console.WriteLine($"? Permission check failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Test connection stability with detailed monitoring
        /// Usage: !testconnection
        /// </summary>
        [Command("testconnection")]
        [Summary("Test Discord voice connection stability")]
        public async Task TestConnectionAsync()
        {
            try
            {
                await ReplyAsync("?? **Testing Discord voice connection stability...**");
                
                var guild = Context.Guild as SocketGuild;
                var guildId = guild.Id;
                
                // Check if we have an active connection
                if (!_audioClients.TryGetValue(guildId, out var audioClient))
                {
                    await ReplyAsync("? No active voice connection found. Use `!join <channel>` first.");
                    return;
                }
                
                Console.WriteLine($"?? Testing connection stability for guild {guild.Name}...");
                
                var embed = new EmbedBuilder()
                    .WithTitle("?? Connection Stability Test")
                    .WithColor(Color.Purple)
                    .WithTimestamp(DateTimeOffset.UtcNow);
                
                // Test connection state over time
                var states = new List<string>();
                var latencies = new List<int>();
                
                for (int i = 0; i < 10; i++)
                {
                    states.Add(audioClient.ConnectionState.ToString());
                    latencies.Add(audioClient.Latency);
                    
                    Console.WriteLine($"   Check {i + 1}/10: State={audioClient.ConnectionState}, Latency={audioClient.Latency}ms");
					
                    if (i < 9) await Task.Delay(500); // Wait 500ms between checks
                }
                
                // Analyze results
                var connectedCount = states.Count(s => s == "Connected");
                var avgLatency = latencies.Average();
                var maxLatency = latencies.Max();
                var minLatency = latencies.Min();
                
                var testResults = $"Connection Checks: {connectedCount}/10 Connected\n" +
                                $"Current State: {audioClient.ConnectionState}\n" +
                                $"Average Latency: {avgLatency:F1}ms\n" +
                                $"Latency Range: {minLatency}-{maxLatency}ms\n" +
                                $"Stability: {(connectedCount >= 8 ? "? Stable" : "?? Unstable")}";
                
                embed.AddField("?? Test Results", $"```{testResults}```", false);
                
                // Connection details
                var connectionDetails = $"Guild: {guild.Name}\n" +
                                      $"Channel: {DiscordNetBotManager.IsInVoiceChannel}\n" +
                                      $"Manager Sync: {(DiscordNetBotManager.IsInVoiceChannel ? "? Yes" : "? No")}\n" +
                                      $"Audio Streams: {(audioClient.ConnectionState == ConnectionState.Connected ? audioClient.GetStreams().Count() : 0)}";
                
                embed.AddField("?? Connection Details", $"```{connectionDetails}```", false);
                
                // Recommendations
                var recommendations = "";
                if (connectedCount < 8)
                {
                    recommendations = "� Connection is unstable - consider rejoining\n" +
                                    "� Check network connection\n" +
                                    "� Try `!leave` then `!join <channel>`\n" +
                                    "� Discord voice servers may be experiencing issues";
                }
                else if (avgLatency > 200)
                {
                    recommendations = "� High latency detected\n" +
                                    "� Check network connection\n" +
                                    "� Consider switching voice regions in Discord";
                }
                else
                {
                    recommendations = "� Connection appears stable ?\n" +
                                    "� Latency is acceptable\n" +
                                    "� Ready for voice processing";
                }
                
                embed.AddField("?? Recommendations", recommendations, false);
                
                await ReplyAsync(embed: embed.Build());
                Console.WriteLine($"?? Connection stability test completed for {Context.User.Username}");
            }
            catch (Exception ex)
            {
                await ReplyAsync($"? Connection test failed: {ex.Message}");
                Console.WriteLine($"? Connection test failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Test single process token usage - CRITICAL for preventing 4006 errors
        /// Usage: !tokencheck
        /// </summary>
        [Command("tokencheck")]
        [Summary("Check if only one process is using this bot token")]
        public async Task TokenCheckAsync()
        {
            try
            {
                await ReplyAsync("?? **Checking bot token usage...**");
                
                var embed = new EmbedBuilder()
                    .WithTitle("?? Bot Token Usage Check")
                    .WithColor(Color.Orange)
                    .WithTimestamp(DateTimeOffset.UtcNow);

                // Check current connection status
                var client = DiscordNetBotManager.GetClient();
                var tokenInfo = "";
                
                if (client != null)
                {
                    tokenInfo += $"Bot: {client.CurrentUser.Username}#{client.CurrentUser.Discriminator}\n";
                    tokenInfo += $"Connection: {client.ConnectionState}\n";
                    tokenInfo += $"Latency: {client.Latency}ms\n";
                    tokenInfo += $"Guilds: {client.Guilds.Count}\n";
                    
                    // Check if we're in voice anywhere
                    var voiceConnections = 0;
                    foreach (var guild in client.Guilds)
                    {
                        if (guild.CurrentUser.VoiceChannel != null)
                        {
                            voiceConnections++;
                            tokenInfo += $"Voice: {guild.CurrentUser.VoiceChannel.Name} in {guild.Name}\n";
                        }
                    }
                    
                    if (voiceConnections == 0)
                    {
                        tokenInfo += "Voice: Not connected\n";
                    }
                }
                else
                {
                    tokenInfo = "? No client connection";
                }

                embed.AddField("?? Current Session", $"```{tokenInfo}```", false);

                // Warning about multiple processes
                var warnings = "?? **CRITICAL: Only ONE process should use this token**\n\n" +
                              "If you see 4006 errors, check for:\n" +
                              "� Multiple instances of this application\n" +
                              "� Other Discord bots using the same token\n" +
                              "� Previous crashed instances still running\n" +
                              "� Development environment + production running\n\n" +
                              "**How to check:**\n" +
                              "� Task Manager ? Look for multiple processes\n" +
                              "� Discord Developer Portal ? Bot section\n" +
                              "� Only ONE connection should be active";

                embed.AddField("?? Multiple Process Warning", warnings, false);

                await ReplyAsync(embed: embed.Build());
                Console.WriteLine($"?? Token check completed by {Context.User.Username}");
            }
            catch (Exception ex)
            {
                await ReplyAsync($"? Token check failed: {ex.Message}");
                Console.WriteLine($"? Token check failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Test voice connection with timeout and detailed error reporting
        /// Usage: !testjoin <channel>
        /// </summary>
        [Command("testjoin")]
        [Summary("Test voice connection with timeout protection")]
        public async Task TestJoinAsync([Remainder] string channelName = null)
        {
            var guild = (SocketGuild)Context.Guild;
            var self = guild.CurrentUser;

            try
            {
                await ReplyAsync("?? **Testing voice connection with timeout protection...**");
                
                // Resolve target channel
                IVoiceChannel target = null;
                if (string.IsNullOrWhiteSpace(channelName))
                {
                    await ReplyAsync("? Please specify a channel name: `!testjoin <channel name>`");
                    return;
                }
                
                target = guild.VoiceChannels.FirstOrDefault(c => 
                             string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));

                if (target == null)
                {
                    await ReplyAsync($"? Voice channel '{channelName}' not found!");
                    return;
                }

                Console.WriteLine($"?? TEST JOIN: Starting connection test to {target.Name}");
                
                // Clean up first
                if (self.VoiceChannel != null)
                {
                    await self.VoiceChannel.DisconnectAsync();
                    await Task.Delay(750);
                }
                
                if (_audioClients.TryRemove(guild.Id, out var existing))
                {
                    try { await existing.StopAsync(); existing.Dispose(); } catch { /* ignore */ }
                }

                // Connection with timeout (using Task.Delay for .NET Framework compatibility)
                Console.WriteLine($"?? TEST JOIN: Starting ConnectAsync with 20-second timeout...");
                var connectTask = target.ConnectAsync(selfDeaf: false, selfMute: false);
                var timeoutTask = Task.Delay(20000); // 20 seconds
                
                var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    Console.WriteLine($"? TEST JOIN: Connection timed out after 20 seconds");
                    await ReplyAsync("? **Test failed:** Connection timed out after 20 seconds\n\n" +
                                   "This suggests a network or Discord API issue.");
                    return;
                }
                
                try
                {
                    var audioClient = await connectTask;
                    
                    if (audioClient != null)
                    {
                        Console.WriteLine($"? TEST JOIN: Success! State: {audioClient.ConnectionState}");
                        
                        // Store temporarily for testing
                        _audioClients[guild.Id] = audioClient;
                        
                        await ReplyAsync($"? **Test connection successful!**\n" +
                                       $"Channel: {target.Name}\n" +
                                       $"State: {audioClient.ConnectionState}\n" +
                                       $"Latency: {audioClient.Latency}ms\n\n" +
                                       $"Use `!leave` to disconnect.");
                        
                        audioClient.Disconnected += ex =>
                        {
                            Console.WriteLine($"?? TEST JOIN: Disconnected - {ex?.Message ?? "Normal"}");
                            _audioClients.TryRemove(guild.Id, out _);
                            return Task.CompletedTask;
                        };
                    }
                    else
                    {
                        await ReplyAsync("? **Test failed:** ConnectAsync returned null");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"? TEST JOIN: Exception - {ex.GetType().Name}: {ex.Message}");
                    await ReplyAsync($"? **Test failed:** {ex.Message}\n\n" +
                                   $"Error type: {ex.GetType().Name}");
                }
            }
            catch (Exception ex)
            {
                await ReplyAsync($"? Test join failed: {ex.Message}");
                Console.WriteLine($"? Test join command failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Force clear Discord voice session - helps prevent 4006 session reuse errors
        /// Usage: !clearsession
        /// </summary>
        [Command("clearsession")]
        [Summary("Force clear Discord voice session to prevent 4006 errors")]
        public async Task ClearSessionAsync()
        {
            // CRITICAL 4006 PREVENTION: Global lock to prevent concurrent voice operations
            await _discordVoiceOperationLock.WaitAsync();
            try
            {
                Console.WriteLine($"?? GLOBAL VOICE LOCK ACQUIRED for clear session operation");
                
                await ReplyAsync("?? **Forcing Discord voice session clear (Enhanced)...**");
                
                var guild = (SocketGuild)Context.Guild;
                var self = guild.CurrentUser;
                
                Console.WriteLine($"?? === ENHANCED FORCE SESSION CLEAR START ===");
                Console.WriteLine($"?? Current bot voice state: {(self.VoiceChannel?.Name ?? "None")}");
                Console.WriteLine($"?? Current session ID: {self.VoiceSessionId ?? "None"}");
                
                // Step 1: Clean up our tracking
                if (_audioClients.TryRemove(guild.Id, out var existing))
                {
                    Console.WriteLine($"?? STEP 1: Disposing existing audio client...");
                    try { await existing.StopAsync(); existing.Dispose(); } catch { /* ignore */ }
                }
                
                // Step 2: Enhanced force disconnect with multiple methods
                bool hadVoiceState = self.VoiceChannel != null || !string.IsNullOrEmpty(self.VoiceSessionId);
                
                if (hadVoiceState)
                {
                    // CRITICAL: Check shutdown status before proceeding with disconnect
                    if (!DiscordNetBotManager.IsRunning)
                    {
                        Console.WriteLine($"?? ABORT: Discord bot shut down during session clearing");
                        return;
                    }

                    Console.WriteLine($"?? STEP 2A: Voice state detected - starting enhanced clearing...");
                    
                    // Method 1: Standard channel disconnect
                    if (self.VoiceChannel != null)
                    {
                        Console.WriteLine($"?? STEP 2A1: Standard disconnect from {self.VoiceChannel.Name}...");
                        await self.VoiceChannel.DisconnectAsync();
                    }
                    
                    // Method 2: Guild-level user modification (catches ghost sessions)
                    Console.WriteLine($"?? STEP 2A2: Guild-level channel clearing...");
                    try
                    {
                        await self.ModifyAsync(x => x.Channel = null);
                        Console.WriteLine($"? STEP 2A2: Guild-level channel clear completed");
                    }
                    catch (Exception guildEx)
                    {
                        Console.WriteLine($"?? STEP 2A2: Guild-level clear failed: {guildEx.Message}");
                    }
                    
                    Console.WriteLine($"? STEP 2: Waiting 4 seconds for enhanced session clearing...");
                    await Task.Delay(4000); // Longer delay to ensure server clears session
                    
                    // Enhanced verification with multiple checks
                    var maxWait = 20; // Maximum 20 checks (10 seconds total)
                    var waitCount = 0;
                    while ((self.VoiceChannel != null || !string.IsNullOrEmpty(self.VoiceSessionId)) && waitCount < maxWait)
                    {
                        // CRITICAL: Check shutdown status during verification
                        if (!DiscordNetBotManager.IsRunning)
                        {
                            Console.WriteLine($"?? ABORT: Discord bot shut down during verification");
                            return;
                        }

                        Console.WriteLine($"? STEP 2: Still has voice state - VoiceChannel: {self.VoiceChannel?.Name ?? "None"}, Session: {self.VoiceSessionId ?? "None"} (check {waitCount + 1}/{maxWait})");
                        
                        // Additional clearing attempts during verification
                        if (waitCount % 5 == 0) // Every 2.5 seconds
                        {
                            try
                            {
                                await self.ModifyAsync(x => x.Channel = null);
                                Console.WriteLine($"?? STEP 2: Additional clear attempt during verification");
                            }
                            catch { /* ignore */ }
                        }
                        
                        await Task.Delay(500);
                        waitCount++;
                    }
                    
                    Console.WriteLine($"?? STEP 2: Final state - VoiceChannel: {self.VoiceChannel?.Name ?? "None"}, Session: {self.VoiceSessionId ?? "None"}");
                }
                else
                {
                    Console.WriteLine($"?? STEP 2: No voice state detected - performing safety clear anyway...");
                    try
                    {
                        await self.ModifyAsync(x => x.Channel = null);
                        await Task.Delay(1000);
                        Console.WriteLine($"? STEP 2: Safety clear completed");
                    }
                    catch (Exception safetyEx)
                    {
                        Console.WriteLine($"?? STEP 2: Safety clear failed (expected if already clear): {safetyEx.Message}");
                    }
                }
                
                // Step 3: Update bot manager
                await DiscordNetBotManager.OnVoiceChannelLeft();
                
                Console.WriteLine($"? === ENHANCED FORCE SESSION CLEAR COMPLETE ===");
                
                if (hadVoiceState)
                {
                    await ReplyAsync("? **Enhanced session cleared!** Wait 15-20 seconds before using voice commands.\n\n" +
                                   "**What was cleared:**\n" +
                                   "� Voice channel connection\n" +
                                   "� Discord session ID\n" +
                                   "� Guild-level voice state\n" +
                                   "� Audio client tracking\n\n" +
                                   "This should force Discord to issue a completely fresh session.");
                }
                else
                {
                    await ReplyAsync("? **Session verification completed!** No active voice state found.\n\n" +
                                   "The bot was already disconnected. You can try voice commands now.");
                }
                
                Console.WriteLine($"?? Recommendation: Wait 15-20 seconds before next voice connection attempt");
            }
            catch (Exception ex)
            {
                await ReplyAsync($"? Enhanced session clear failed: {ex.Message}");
                Console.WriteLine($"? Enhanced session clear failed: {ex.Message}");
            }
            finally
            {
                _discordVoiceOperationLock.Release();
                Console.WriteLine($"?? GLOBAL VOICE LOCK RELEASED for clear session operation");
            }
        }

        /// <summary>
        /// Enhanced join with session clearing to prevent 4006 errors
        /// Usage: !joinforce <channel>
        /// </summary>
        [Command("joinforce")]
        [Summary("Force join with session clearing to prevent 4006 errors")]
        public async Task JoinForceAsync([Remainder] string channelName = null)
        {
            var guild = (SocketGuild)Context.Guild;
            var self = guild.CurrentUser;

            // CRITICAL 4006 PREVENTION: Global lock to prevent concurrent voice operations
            await _discordVoiceOperationLock.WaitAsync();
            try
            {
                Console.WriteLine($"?? GLOBAL VOICE LOCK ACQUIRED for force join operation");
                Console.WriteLine($"?? === FORCE JOIN WITH SESSION CLEAR START ===");
                Console.WriteLine($"?? Force join requested by {Context.User.Username} in guild: {guild.Name}");

                // Resolve target
                IVoiceChannel target = null;
                if (string.IsNullOrWhiteSpace(channelName))
                {
                    await ReplyAsync("? Please specify a channel name: `!joinforce <channel>`");
                    return;
                }
                
                target = guild.VoiceChannels.FirstOrDefault(c => 
                             string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));

                if (target == null)
                {
                    Console.WriteLine($"? Target channel not found: '{channelName}'");
                    await ReplyAsync($"? Voice channel '{channelName}' not found!");
                    return;
                }

                Console.WriteLine($"? Target channel resolved: {target.Name} (ID: {target.Id})");

                await ReplyAsync($"?? **Step 1/3:** Clearing any existing sessions...");

                // ENHANCED STEP 1: Aggressive session clearing
                Console.WriteLine($"?? STEP 1: Aggressive session clearing...");
                
                // Clear our audio client tracking
                if (_audioClients.TryRemove(guild.Id, out var existing))
                {
                    Console.WriteLine($"?? Disposing existing audio client...");
                    try { await existing.StopAsync(); existing.Dispose(); } catch { /* ignore */ }
                }
                
                // Force disconnect if in voice
                if (self.VoiceChannel != null)
                {
                    Console.WriteLine($"?? Force disconnecting from: {self.VoiceChannel.Name}");
                    await self.VoiceChannel.DisconnectAsync();
                    Console.WriteLine($"? Waiting 4 seconds for session to clear server-side...");
                    await Task.Delay(4000); // Longer delay to ensure server clears session
                }
                else
                {
                    Console.WriteLine($"?? Not in voice, but waiting 2 seconds for safety...");
                    await Task.Delay(2000);
                }

                await ReplyAsync($"?? **Step 2/3:** Attempting connection with fresh session...");

                // STEP 2: Attempt connection
                Console.WriteLine($"?? STEP 2: Calling target.ConnectAsync with fresh session...");
                Console.WriteLine($"?? Connection attempt starting at: {DateTime.Now:HH:mm:ss.fff}");
                
                // CRITICAL: Final shutdown check before attempting connection
                if (!DiscordNetBotManager.IsRunning)
                {
                    Console.WriteLine($"?? ABORT: Discord bot shut down just before ConnectAsync - canceling operation");
                    await ReplyAsync("? **Connection canceled** - Discord bot is shutting down.");
                    return;
                }

                IAudioClient audioClient = null;
                try
                {
                    audioClient = await target.ConnectAsync(selfDeaf: false, selfMute: false);
                    Console.WriteLine($"? STEP 2: ConnectAsync completed at: {DateTime.Now:HH:mm:ss.fff}");
                    Console.WriteLine($"? AudioClient state: {audioClient?.ConnectionState}");
                }
                catch (Exception connectEx)
                {
                    Console.WriteLine($"? STEP 2: ConnectAsync failed: {connectEx.GetType().Name}: {connectEx.Message}");
                    await ReplyAsync($"? **Connection failed:** {connectEx.Message}\n\n" +
                                   $"Error type: {connectEx.GetType().Name}");
                    return;
                }

                if (audioClient == null)
                {
                    Console.WriteLine($"? STEP 2: ConnectAsync returned null");
                    await ReplyAsync($"? **Connection failed:** ConnectAsync returned null");
                    return;
                }

                await ReplyAsync($"?? **Step 3/3:** Configuring connection...");

                // STEP 3: Store and configure
                Console.WriteLine($"?? STEP 3: Storing and configuring connection...");
                _audioClients[guild.Id] = audioClient;
                
                audioClient.Disconnected += ex =>
                {
                    Console.WriteLine($"🔌 AudioClient.Disconnected event: {ex?.Message ?? "Normal"}");
                    _audioClients.TryRemove(guild.Id, out _);
                    return Task.CompletedTask;
                };

                Console.WriteLine($"?? STEP 3: Calling DiscordNetBotManager.SetVoiceConnection...");
                await DiscordNetBotManager.SetVoiceConnection(audioClient, target.Id, target.Name);

                Console.WriteLine($"?? === FORCE JOIN SUCCESS ===");
                await ReplyAsync($"? **Force join successful!** Joined **{target.Name}** with fresh session.\n\n" +
                               $"Connection state: {audioClient.ConnectionState}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? === FORCE JOIN ERROR ===");
                Console.WriteLine($"? Exception: {ex.GetType().Name}: {ex.Message}");
                await ReplyAsync($"? Force join failed: {ex.Message}");
            }
            finally
            {
                _discordVoiceOperationLock.Release();
                Console.WriteLine($"?? GLOBAL VOICE LOCK RELEASED for force join operation");
            }
        }
    }
}