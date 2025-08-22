// MockAppSettings.cs - Mock implementation for testing
using System;

namespace Kinectv1
{
    /// <summary>
    /// Mock AppSettings for testing JsonSettingsProvider and SettingsWindow
    /// Provides in-memory storage without dependencies on XML config or WPF
    /// </summary>
    public static class AppSettings
    {
        private static AppScenario _appScenario = AppScenario.Local;
        private static bool _ttsEnabled = true;
        private static bool _discordBotEnabled = false;
        private static bool _ollamaEnabled = true;
        private static bool _telemetryEnabled = false;
        private static float _voiceConfidenceThreshold = 0.5f;
        private static float _voiceActivityThreshold = 300f;
        private static float _discordVoiceActivityThreshold = 25f;
        private static string _sttInputDevice = "Default";
        private static string _ttsOutputDevice = "Default";
        private static string _ttsSpeaker = "em_alex";
        private static bool _ttsUseGpu = false;
        private static double _localTtsVolume = 0.8;
        private static double _discordTtsVolume = 0.8;
        private static string _ttsModelPath = "models/tts/kokoro/model.onnx";
        private static string _sttModelPath = "models/stt/vosk";
        private static string _speakerEmbeddingModelPath = "models/speaker/embedding.onnx";
        private static string _ollamaModel = "llama2";
        private static string _systemPromptPath = "prompts/system.txt";
        private static double _windowWidth = 900;
        private static double _windowHeight = 900;
        private static double _windowLeft = 100;
        private static double _windowTop = 100;
        private static string _windowState = "Normal";
        private static bool _darkMode = false;
        private static float _faceThreshold = 0.6f;
        private static float _fusionFaceWeight = 0.6f;
        private static float _fusionVoiceWeight = 0.4f;
        private static int _fusionDecayHalfLifeMs = 2000;
        private static float _fusionUnknownThreshold = 0.3f;
        private static AudioInMode _audioInMode = AudioInMode.LocalMic;

        // Load methods
        public static AppScenario LoadAppScenario() => _appScenario;
        public static bool LoadTtsEnabled() => _ttsEnabled;
        public static bool LoadDiscordBotEnabled() => _discordBotEnabled;
        public static bool LoadOllamaEnabled() => _ollamaEnabled;
        public static bool LoadTelemetryEnabled() => _telemetryEnabled;
        public static float LoadVoiceConfidenceThreshold() => _voiceConfidenceThreshold;
        public static float LoadVoiceActivityThreshold() => _voiceActivityThreshold;
        public static float LoadDiscordVoiceActivityThreshold() => _discordVoiceActivityThreshold;
        public static string LoadSttInputDevice() => _sttInputDevice;
        public static string LoadTtsOutputDevice() => _ttsOutputDevice;
        public static string LoadTtsSpeaker() => _ttsSpeaker;
        public static bool LoadTtsUseGpu() => _ttsUseGpu;
        public static double LoadLocalTtsVolume() => _localTtsVolume;
        public static double LoadDiscordTtsVolume() => _discordTtsVolume;
        public static string LoadTtsModelPath() => _ttsModelPath;
        public static string LoadSttModelPath() => _sttModelPath;
        public static string LoadSpeakerEmbeddingModelPath() => _speakerEmbeddingModelPath;
        public static string LoadOllamaModel() => _ollamaModel;
        public static string LoadSystemPromptPath() => _systemPromptPath;
        public static (double width, double height, double left, double top, string state) LoadWindowSettings() 
            => (_windowWidth, _windowHeight, _windowLeft, _windowTop, _windowState);
        public static bool LoadDarkMode() => _darkMode;
        public static float LoadFaceThreshold() => _faceThreshold;
        public static float LoadFusionFaceWeight() => _fusionFaceWeight;
        public static float LoadFusionVoiceWeight() => _fusionVoiceWeight;
        public static int LoadFusionDecayHalfLifeMs() => _fusionDecayHalfLifeMs;
        public static float LoadFusionUnknownThreshold() => _fusionUnknownThreshold;
        public static AudioInMode LoadAudioInMode() => _audioInMode;

        // Save methods
        public static void SaveAppScenario(AppScenario scenario) => _appScenario = scenario;
        public static void SaveTtsEnabled(bool enabled) => _ttsEnabled = enabled;
        public static void SaveDiscordBotEnabled(bool enabled) => _discordBotEnabled = enabled;
        public static void SaveOllamaEnabled(bool enabled) => _ollamaEnabled = enabled;
        public static void SaveTelemetryEnabled(bool enabled) => _telemetryEnabled = enabled;
        public static void SaveVoiceConfidenceThreshold(float threshold) => _voiceConfidenceThreshold = threshold;
        public static void SaveVoiceActivityThreshold(float threshold) => _voiceActivityThreshold = threshold;
        public static void SaveDiscordVoiceActivityThreshold(float threshold) => _discordVoiceActivityThreshold = threshold;
        public static void SaveSttInputDevice(string device) => _sttInputDevice = device;
        public static void SaveTtsOutputDevice(string device) => _ttsOutputDevice = device;
        public static void SaveTtsSpeaker(string speaker) => _ttsSpeaker = speaker;
        public static void SaveTtsUseGpu(bool useGpu) => _ttsUseGpu = useGpu;
        public static void SaveLocalTtsVolume(double volume) => _localTtsVolume = volume;
        public static void SaveDiscordTtsVolume(double volume) => _discordTtsVolume = volume;
        public static void SaveDarkMode(bool darkMode) => _darkMode = darkMode;
        public static void SaveFaceThreshold(float threshold) => _faceThreshold = threshold;
        public static void SaveAudioInMode(AudioInMode mode) => _audioInMode = mode;
    }

    /// <summary>
    /// Application scenario presets for configuration
    /// </summary>
    public enum AppScenario
    {
        Local,   // Local-only TTS and voice processing
        Discord, // Discord bot integration with voice commands
        Kiosk    // Public kiosk mode with restricted settings
    }

    /// <summary>
    /// Audio input mode selection to prevent conflicting audio pipelines
    /// </summary>
    public enum AudioInMode
    {
        LocalMic,       // Local microphone input only
        DiscordVoice,   // Discord voice channel input (Opus→PCM from voice receiver)
        SystemLoopback  // System audio loopback capture
    }
}