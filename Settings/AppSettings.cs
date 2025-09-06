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
        [property: Required] MumbleSettings Mumble,
        [property: Required] UiSettings Ui,
        [property: Required] AsrSettings Asr,
        [property: Required] SttSettings Stt,
        [property: Required] FaceSettings Face,
        [property: Required] AppConfig App
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

    // New: ASR/Discord VAD and confidence controls
    public sealed record AsrSettings(
        [property: Range(0.0, 1.0)] double VoiceConfidenceThreshold,
        [property: Range(0.0, 1.0)] double VoiceHighConfidenceThreshold,
        [property: Range(1, int.MaxValue)] int VoiceConfidenceBufferSize,
        bool VoiceConfidenceLoggingEnabled,
        [property: Range(50, 5000)] int VadSilenceTimeoutMs,
        [property: Range(10, 2000)] int VadDebounceTimeoutMs,
        [property: Range(0.0, 1000.0)] double DiscordVadThreshold,
        bool BargeInEnabled
    );

    // STT configuration
    public sealed record SttSettings(
        [property: Required] string ModelPath,
        [property: Required] string InputDevice
    );

    // Face recognition and fusion settings
    public sealed record FaceSettings(
        [property: Range(0.0, 1.0)] double Threshold,
        [property: Range(0.0, 1.0)] double FusionFaceWeight,
        [property: Range(0.0, 1.0)] double FusionVoiceWeight,
        [property: Range(100, 60000)] int FusionDecayHalfLifeMs,
        [property: Range(0.0, 1.0)] double FusionUnknownThreshold,
        string ArcFaceModelPath,
        string SpeakerEmbeddingModelPath
    );

    public enum AppScenario { Local, Remote }

    public enum AudioInMode { LocalMic, DiscordVoice, SystemLoopback, MumbleVoice }

    // App-level behavior
    public sealed record AppConfig(
        bool RequireWakeWord,
        AppScenario Scenario,
        AudioInMode InputMode
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
        bool OutputThink // when false, <think>..</think> is removed
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

    // New: UI settings
    public sealed record UiSettings(
        bool DarkMode
    );
}
