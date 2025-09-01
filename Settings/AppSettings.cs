// Settings/AppSettings.cs
namespace Kinectv1.Settings
{
    // Root strongly-typed immutable settings snapshot
    public sealed record AppSettings(
        AudioSettings Audio,
        TtsSettings Tts,
        VadSettings Vad,
        OllamaSettings Ollama
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
        bool Enabled,
        string Model,
        bool MemoryEnabled,
        int MaxMessagesPerSpeaker,
        int MaxSystemMessages,
        int ConversationTimeoutMinutes,
        string ConversationHistoryPath,
        string SystemPromptPath
    );
}
