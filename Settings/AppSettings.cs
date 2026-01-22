// Settings/AppSettings.cs
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Kinectv1.Settings
{
    // Root strongly-typed immutable settings snapshot
    public sealed record AppSettings(
        [property: Required] AudioSettings Audio,
        [property: Required] TtsSettings Tts,
        [property: Required] OllamaSettings Ollama,
        [property: Required] DiscordSettings Discord,
        [property: Required] WebRtcSettings WebRtc,
        [property: Required] UiSettings Ui,
        [property: Required] AsrSettings Asr,
        [property: Required] SttSettings Stt,
        [property: Required] FaceSettings Face,
        [property: Required] AppConfig App
    );

    // Unified audio settings – removed legacy VadThreshold (RMS gating now derived from VoiceThreshold)
    public sealed record AudioSettings(
        [property: Range(0, 1)] double VoiceThreshold,
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
        // Vocoder is optional for some models
        string VocoderPath,
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

    public enum AudioInMode
    {
        [EnumMember(Value = "mic")]
        LocalMic,
        [EnumMember(Value = "discord")]
        DiscordVoice,
        [EnumMember(Value = "loopback")]
        SystemLoopback,
        [EnumMember(Value = "webrtc")]
        WebRtcVoice
    }

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
        // Conversation history persistence: cap by approximate tokens in conversation.json
        [property: Range(0, int.MaxValue)] int ConversationMaxTokens,
        string ConversationHistoryPath,
        string SystemPromptPath,
        bool OutputThink, // when false, <think>..</think> is removed
        // New: provider endpoints + API key (optional)
        string BaseUrl,
        string LmStudioBaseUrl,
        string ApiKey,
        // New: force speaker override for conversation history and prompts
        bool ForceSpeakerOverrideEnabled,
        string ForcedSpeakerId,
        // Vector memory (3-layer: hot/warm/cold)
        bool VectorMemoryEnabled,
        [property: Range(1000, 128000)] int HotContextTokenLimit,
        [property: Range(100, 4000)] int WarmSummaryTokenLimit,
        [property: Range(200, 4000)] int MemoryContextBudget, // Total tokens allowed for all memory injection
        string VectorDbPath,
        string EmbeddingsModel,
        [property: Range(50, 1000)] int ChunkSizeTokens, // Smaller chunks for finer granularity
        [property: Range(0, 200)] int ChunkOverlapTokens,
        [property: Range(1, 20)] int VectorSearchTopK, // Reduced max to prevent context bloat
        [property: Range(0.0, 1.0)] double RecencyBoostFactor,
        [property: Range(0.0, 1.0)] double MinRetrievalScore, // Minimum similarity to include a chunk
        // Tool use (web search, etc.)
        bool ToolsEnabled
    );

    // New: Discord settings for bot configuration
    public sealed record DiscordSettings(
        bool Enabled,
        string Prefix,
        bool AutoJoinVoice,
        string Token
    );

    // WebRTC settings for LAN voice transport (replaces TeamTalk)
    public sealed record WebRtcSettings(
        bool Enabled,
        [property: Range(1, 65535)] int Port,
        bool HttpsEnabled,
        int HttpsPort
    );

    // New: UI settings
    public sealed record UiSettings(
        bool DarkMode
    );
}
