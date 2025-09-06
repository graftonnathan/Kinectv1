// Settings/AppSettings.cs
using System.ComponentModel.DataAnnotations;

namespace Kinectv1.Settings
{
    // Root strongly-typed immutable settings snapshot
    public sealed record AppSettings(
        [property: Required] AudioSettings Audio,
        [property: Required] TtsSettings Tts,
        [property: Required] VadSettings Vad,
        [property: Required] OllamaSettings Ollama,
        [property: Required] DiscordSettings Discord,
        [property: Required] MumbleSettings Mumble
    );

    public sealed record AudioSettings(
        [property: Range(0, 1)] double VoiceThreshold,
        [property: Range(0, int.MaxValue)] int VadThreshold,
        [property: Range(1, int.MaxValue)] int BufferSize,
        [property: Range(0, 1)] double SpeakerMatchMinScore
    );

    public enum TtsExecution { GPU, CPU }

    public sealed record TtsSettings(
        bool Enabled,
        [property: Required] string Speaker,
        TtsExecution Execution,
        [property: Required] string ModelFolder,
        [property: Required] string ModelPath,
        [property: Required] string VocoderPath,
        // New unified fields
        [property: Required] string OutputDevice,
        [property: Range(0, 1)] double LocalVolume,
        [property: Range(0, 1)] double DiscordVolume,
        [property: Range(0.5, 2.0)] float Speed,
        [property: Range(0.0005, 0.05)] double TrimThreshold,
        [property: Range(0, 100)] int TrimLeaveMs,
        [property: Range(50, 3000)] int TrimMaxMs,
        [property: Range(0, 200)] int MinClausePaddingMs,
        [property: Range(200, 5000)] int IpaServiceTimeoutMs,
        [property: Range(200, 5000)] int IpaOneShotTimeoutMs
    );

    public sealed record VadSettings(
        [property: Range(0, int.MaxValue)] int Threshold
    );

    // New: Ollama settings section persisted in JSON settings pipeline
    public sealed record OllamaSettings(
        [property: Required] string Provider, // "Ollama" | "LMStudio"
        bool Enabled,
        string Model,
        bool MemoryEnabled,
        [property: Range(0, int.MaxValue)] int MaxMessagesPerSpeaker,
        [property: Range(0, int.MaxValue)] int MaxSystemMessages,
        [property: Range(0, int.MaxValue)] int ConversationTimeoutMinutes,
        string ConversationHistoryPath,
        string SystemPromptPath,
        bool OutputThink // when false, <think>..</think> is replaced with "thinking"
    );

    // New: Discord settings for bot configuration
    public sealed record DiscordSettings(
        bool Enabled,
        string Prefix,
        bool AutoJoinVoice,
        string Token
    );

    public enum MumbleTlsValidate { Strict, AcceptSelfSigned, Off }

    // New: Mumble settings for client configuration
    public sealed record MumbleSettings(
        bool Enabled,
        bool AutoConnect,
        [property: Required] string Host,
        [property: Range(1, 65535)] int Port,
        [property: Required] string Username,
        string ServerPassword,
        string Channel,
        string ChannelPassword,
        bool ValidateTls,
        bool SelfMute,
        bool SelfDeaf,
        [property: Range(6000, 96000)] int OpusBitrate,
        [property: Range(1, 10000)] int VadThreshold,
        [property: Range(0, 60000)] int ReconnectBackoffMs,
        bool TextCommandsEnabled,
        MumbleTlsValidate TlsValidate = MumbleTlsValidate.Strict
    );
}
