// Settings/AppSettings.cs
namespace Kinectv1.Settings
{
    // Root strongly-typed immutable settings snapshot
    public sealed record AppSettings(
        AudioSettings Audio,
        TtsSettings Tts,
        VadSettings Vad,
        OllamaSettings Ollama,
        DiscordSettings Discord,
        MumbleSettings Mumble
    );

    public sealed record AudioSettings(double VoiceThreshold, int VadThreshold, int BufferSize);

    public enum TtsExecution { GPU, CPU }

    public sealed record TtsSettings(
        bool Enabled,
        string Speaker,
        TtsExecution Execution,
        string ModelFolder,
        string ModelPath,
        string VocoderPath,
        // New unified fields
        string OutputDevice,
        double LocalVolume,
        double DiscordVolume,
        float Speed,
        double TrimThreshold,
        int TrimLeaveMs,
        int TrimMaxMs,
        int MinClausePaddingMs,
        int IpaServiceTimeoutMs,
        int IpaOneShotTimeoutMs
    );

    public sealed record VadSettings(int Threshold);

    // New: Ollama settings section persisted in JSON settings pipeline
    public sealed record OllamaSettings(
        string Provider, // "Ollama" | "LMStudio"
        bool Enabled,
        string Model,
        bool MemoryEnabled,
        int MaxMessagesPerSpeaker,
        int MaxSystemMessages,
        int ConversationTimeoutMinutes,
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
        string Host,
        int Port,
        string Username,
        string ServerPassword,
        string Channel,
        string ChannelPassword,
        bool ValidateTls,
        bool SelfMute,
        bool SelfDeaf,
        int OpusBitrate,
        int VadThreshold,
        int ReconnectBackoffMs,
        bool TextCommandsEnabled,
        MumbleTlsValidate TlsValidate = MumbleTlsValidate.Strict
    );
}
