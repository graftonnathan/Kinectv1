using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;

namespace Kinectv1.Discord
{
    /// <summary>
    /// Simplified Discord commands for headless mode.
    /// </summary>
    public class DiscordHeadlessCommands : ModuleBase<SocketCommandContext>
    {
        [Command("join")]
        [Summary("Join a voice channel by name")]
        public async Task JoinAsync([Remainder] string channelName = null)
        {
            if (!DiscordNetBotManagerHeadless.IsRunning)
            {
                await ReplyAsync("❌ Discord bot is not running.");
                return;
            }

            if (string.IsNullOrWhiteSpace(channelName))
            {
                // List available channels
                var guild = Context.Guild;
                if (guild != null)
                {
                    var channels = guild.VoiceChannels.Select(vc => vc.Name).ToList();
                    if (channels.Any())
                    {
                        await ReplyAsync($"Available voice channels: {string.Join(", ", channels)}\nUse `!join <channel name>` to join.");
                    }
                    else
                    {
                        await ReplyAsync("No voice channels found.");
                    }
                }
                return;
            }

            var result = await DiscordNetBotManagerHeadless.JoinVoiceChannelAsync(channelName);
            if (result)
            {
                await ReplyAsync($"✅ Joined voice channel: {channelName}");
            }
            else
            {
                await ReplyAsync($"❌ Failed to join voice channel: {channelName}");
            }
        }

        [Command("leave")]
        [Summary("Leave the current voice channel")]
        public async Task LeaveAsync()
        {
            if (!DiscordNetBotManagerHeadless.IsInVoiceChannel)
            {
                await ReplyAsync("❌ Not currently in a voice channel.");
                return;
            }

            await DiscordNetBotManagerHeadless.LeaveVoiceChannelAsync();
            await ReplyAsync("✅ Left voice channel.");
        }

        [Command("status")]
        [Summary("Show bot status")]
        public async Task StatusAsync()
        {
            var status = $"**Maggie Discord Bot Status**\n" +
                        $"Bot: {(DiscordNetBotManagerHeadless.IsRunning ? "🟢 Connected" : "🔴 Disconnected")}\n" +
                        $"Voice: {(DiscordNetBotManagerHeadless.IsInVoiceChannel ? "🟢 In Channel" : "⚪ Not Connected")}";
            await ReplyAsync(status);
        }

        [Command("say")]
        [Summary("Make Maggie say something in voice channel")]
        public async Task SayAsync([Remainder] string text = null)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                await ReplyAsync("Usage: !say <message>");
                return;
            }

            if (!DiscordNetBotManagerHeadless.IsInVoiceChannel)
            {
                await ReplyAsync("❌ I'm not in a voice channel. Use `!join <channel>` first.");
                return;
            }

            DiscordNetBotManagerHeadless.QueueTts(text);
            await ReplyAsync($"🗣️ Speaking: {text.Substring(0, Math.Min(100, text.Length))}...");
        }

        [Command("help")]
        [Summary("Show available commands")]
        public async Task HelpAsync()
        {
            var prefix = App.SettingsProvider?.Current?.Discord?.Prefix ?? "!";
            var help = $"**Maggie Discord Commands**\n" +
                      $"`{prefix}join [channel]` - Join a voice channel\n" +
                      $"`{prefix}leave` - Leave voice channel\n" +
                      $"`{prefix}status` - Show bot status\n" +
                      $"`{prefix}say <message>` - Speak in voice channel\n" +
                      $"`{prefix}help` - Show this help";
            await ReplyAsync(help);
        }
    }
}
