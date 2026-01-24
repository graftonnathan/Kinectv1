using System.Runtime.Serialization;

namespace Kinectv1.Settings
{
    /// <summary>
    /// High-level usage scenario for the assistant (kept for future expansion).
    /// </summary>
    public enum AppScenario
    {
        [EnumMember(Value = "Local")]
        Local,

        [EnumMember(Value = "DiscordBridge")]
        DiscordBridge,

        [EnumMember(Value = "HandsFree")]
        HandsFree
    }

    /// <summary>
    /// Primary audio ingress selection persisted in settings and mirrored in UI.
    /// EnumMember values stay lowercase to match historical JSON (see docs/settings.md).
    /// </summary>
    public enum AudioInMode
    {
        [EnumMember(Value = "mic")]
        LocalMic,

        [EnumMember(Value = "discordvoice")]
        DiscordVoice,

        [EnumMember(Value = "webrtc")]
        WebRtcVoice
    }
}
