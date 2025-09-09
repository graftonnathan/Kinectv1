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
    /// (Simplified join logic – removed aggressive manual voice state manipulation to avoid 4006)
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
                Console.WriteLine("[VoiceJoin] start");
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

                // If we are in some other channel, leave it cleanly (lightweight)
                if (self.VoiceChannel != null && self.VoiceChannel.Id != target.Id)
                {
                    await SafeLeaveAsync(self);
                }

                await ReplyAsync($"🎯 Connecting to **{target.Name}**...");

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
                int tries = 0;
                while (tries < 2)
                {
                    try
                    {
                        // Use the new enhanced join method with proper handshake
                        audioClient = await DiscordNetBotManager.JoinVoiceAsync(target, maxRetries: 3);
                        break; // success
                    }
                    catch (HttpException httpEx) when (httpEx.DiscordCode.HasValue && (int)httpEx.DiscordCode.Value == 4006)
                    {
                        Console.WriteLine("4006 detected — auto-clearing session and retrying once");

                        // 1) Kill local voice (readers + audio client)
                        await DiscordNetBotManager.LeaveAllVoiceAsync();

                        // 2) Restart the gateway (break stale/competing session)
                        await DiscordNetBotManager.CloseGatewayAsync();
                        await Task.Delay(1500);
                        var restarted = await DiscordNetBotManager.StartAsync();
                        if (!restarted)
                        {
                            await ReplyAsync("❌ Gateway restart failed; cannot recover from 4006.");
                            return;
                        }

                        // 3) Small cooldown so the prior voice session fully retires
                        await Task.Delay(1000);

                        // 4) One clean retry
                        var retryClient = await DiscordNetBotManager.JoinVoiceAsync(target, maxRetries: 1);
                        if (retryClient == null)
                        {
                            await ReplyAsync("❌ 4006 recovery retry failed.");
                            return;
                        }

                        _audioClients[guild.Id] = retryClient;
                        await DiscordNetBotManager.SetVoiceConnection(retryClient, target.Id, target.Name);
                        await ReplyAsync($"✅ Recovered from 4006 and joined **{target.Name}**.");
                        return;
                    }
                    catch (TimeoutException tex)
                    {
                        await ReplyAsync($"❌ **Voice handshake timed out:** {tex.Message}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        await ReplyAsync($"❌ **Enhanced voice join failed:** {ex.Message}");
                        return;
                    }
                }

                if (audioClient == null)
                {
                    await ReplyAsync("❌ **Connection failed:** no audio client");
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

        private async Task SafeLeaveAsync(SocketGuildUser self)
        {
            try
            {
                if (self?.VoiceChannel == null) return;
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task Handler(SocketUser u, SocketVoiceState before, SocketVoiceState after)
                {
                    if (u.Id == self.Id && after.VoiceChannel == null)
                        tcs.TrySetResult(true);
                    return Task.CompletedTask;
                }
                Context.Client.UserVoiceStateUpdated += Handler;
                try
                {
                    await self.VoiceChannel.DisconnectAsync();
                    await Task.WhenAny(tcs.Task, Task.Delay(1500));
                }
                finally { Context.Client.UserVoiceStateUpdated -= Handler; }
            }
            catch { }
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

                // Disconnect first (gateway state null)
                if (self.VoiceChannel != null)
                {
                    Console.WriteLine($"?? Disconnecting from voice channel: {self.VoiceChannel.Name}");
                    try { await self.VoiceChannel.DisconnectAsync(); Console.WriteLine("? DisconnectAsync completed"); }
                    catch (InvalidOperationException opEx) when (opEx.Message.Contains("Client is not logged in"))
                    { await ReplyAsync("? Leave canceled - Discord bot is shutting down."); return; }

                    var clearedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task Handler(SocketUser u, SocketVoiceState before, SocketVoiceState after)
                    { if (u.Id == self.Id && after.VoiceChannel == null) clearedTcs.TrySetResult(true); return Task.CompletedTask; }
                    Context.Client.UserVoiceStateUpdated += Handler;
                    try { await Task.WhenAny(clearedTcs.Task, Task.Delay(3000)); }
                    finally { Context.Client.UserVoiceStateUpdated -= Handler; }
                    try { await self.ModifyAsync(x => x.Channel = null); } catch { }
                }

                // Dispose tracked audio client after gateway disconnect
                if (_audioClients.TryRemove(guild.Id, out var ac))
                {
                    try { await ac.StopAsync(); } catch { }
                    try { ac.Dispose(); } catch { }
                    Console.WriteLine($"?? Disposed audio client for guild {guild.Name}");
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
                embed.AddField($"`{prefix}voicereset`", "Hard reset voice session (force close pipes)", true);
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

        /// <summary>
        /// Force close all voice pipes and reset the Discord voice session
        /// Usage: !voicereset
        /// </summary>
        [Command("voicereset")]
        [Summary("Force close all voice pipes and reset the Discord voice session")] 
        public async Task VoiceResetAsync()
        {
            var guild = (SocketGuild)Context.Guild;
            var self = guild.CurrentUser;
            if (!DiscordNetBotManager.IsRunning)
            {
                await ReplyAsync("? Bot not running.");
                return;
            }
            await _discordVoiceOperationLock.WaitAsync();
            try
            {
                await ReplyAsync("🔧 Hard voice reset in progress...");

                // 1. Force close all audio pipes (in/out)
                await DiscordNetBotManager.ForceCloseAllVoicePipesAsync();

                // 2. Gateway disconnect (if still latched)
                if (self?.VoiceChannel != null)
                {
                    try { await self.VoiceChannel.DisconnectAsync(); } catch { }
                    var clearedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task Handler(SocketUser u, SocketVoiceState before, SocketVoiceState after)
                    { if (u.Id == self.Id && after.VoiceChannel == null) clearedTcs.TrySetResult(true); return Task.CompletedTask; }
                    Context.Client.UserVoiceStateUpdated += Handler;
                    try { await Task.WhenAny(clearedTcs.Task, Task.Delay(3000)); }
                    finally { Context.Client.UserVoiceStateUpdated -= Handler; }
                    try { await self.ModifyAsync(x => x.Channel = null); } catch { }
                }

                // 3. Cooldown to allow region to retire old session
                await Task.Delay(2000);

                await ReplyAsync("✅ Voice session hard reset complete. Use !join to reconnect.");
            }
            catch (Exception ex)
            {
                await ReplyAsync($"❌ Reset failed: {ex.Message}");
            }
            finally
            {
                _discordVoiceOperationLock.Release();
            }
        }

        /// <summary>
        /// Force-close all voice readers/clients and restart the gateway
        /// Usage: !clearsession
        /// </summary>
        [Command("clearsession")]
        [Summary("Force-close all voice readers/clients and restart the gateway")] 
        public async Task ClearSessionAsync()
        {
            await _discordVoiceOperationLock.WaitAsync();
            try
            {
                await ReplyAsync("🔧 Clearing voice session and restarting gateway…");
                await DiscordNetBotManager.ForceCloseAllVoicePipesAsync();
                await DiscordNetBotManager.CloseGatewayAsync();
                await Task.Delay(1500);
                var ok = await DiscordNetBotManager.StartAsync();
                await ReplyAsync(ok ? "✅ Gateway restarted." : "❌ Failed to restart gateway.");
            }
            catch (Exception ex)
            {
                await ReplyAsync($"❌ Clear session error: {ex.Message}");
            }
            finally { _discordVoiceOperationLock.Release(); }
        }

        [Command("tokencheck")]
        [Summary("Detect common multi-process token conflicts")] 
        public async Task TokenCheckAsync()
        {
            var client = DiscordNetBotManager.GetClient();
            var info = client != null ? $"Gateway state: {client.ConnectionState}" : "No client";
            await ReplyAsync($"🔎 {info}\nIf another process uses this token, 4006 loops can occur. Ensure only one instance runs.");
        }
    }
}