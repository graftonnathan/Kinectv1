// Defaults.cs
using System;
using System.Collections.Generic;

namespace Kinectv1
{
    /// <summary>
    /// Default values for all application settings, organized by category
    /// Used by the Restore Defaults functionality in the settings UI
    /// </summary>
    public static class Defaults
    {
        /// <summary>
        /// Voice and Audio Processing defaults
        /// </summary>
        public static class Voice
        {
            public const float VoiceThreshold = 0.5f;
            public const float VoiceActivityThreshold = 300f;
            public const float DiscordVoiceActivityThreshold = 25f;
            public const int VadSilenceTimeoutMs = 1000;
            public const int VadDebounceTimeoutMs = 150;
            public const float VoiceConfidenceThreshold = 0.6f;
            public const float VoiceHighConfidenceThreshold = 0.8f;
            public const int VoiceConfidenceBufferSize = 3;
            public const bool BargeInEnabled = true;
        }

        /// <summary>
        /// Text-to-Speech defaults
        /// </summary>
        public static class Tts
        {
            public const bool TtsEnabled = true;
            public const bool TtsUseGpu = false;
            public const string TtsModelPath = @"models\tts\kokoro-82M\onnx\model_q8f16.onnx";
            public const string TtsModelFolder = @"models\tts\kokoro-82M";
            public const string TtsSymbolsPath = @"models\tts\kokoro-82M\symbols.txt";
            public const string TtsCmudictPath = "";
            public const string TtsOutputDevice = "Default";
            public const float TtsVolumeScale = 1.0f;
            public const float TtsSpeed = 1.0f;
        }

        /// <summary>
        /// Speech-to-Text defaults
        /// </summary>
        public static class Stt
        {
            public const string SttModelPath = @"models\stt\vosk-model-en-us-0.22";
            public const string SttInputDevice = "Default";
            public const bool SttEnabled = true;
        }

        /// <summary>
        /// Discord Bot defaults
        /// </summary>
        public static class Discord
        {
            public const bool DiscordBotEnabled = false;
            public const string DiscordBotToken = "";
            public const bool DiscordAutoJoinVoice = false;
            public const string DiscordGuildId = "";
            public const string DiscordChannelId = "";
        }

        /// <summary>
        /// Ollama AI defaults
        /// </summary>
        public static class Ollama
        {
            public const bool OllamaEnabled = false;
            public const string OllamaModel = "";
            public const string OllamaApiUrl = "http://localhost:11434";
            public const bool OllamaMemoryEnabled = true;
            public const int OllamaMaxMessagesPerSpeaker = 10;
            public const int OllamaMaxSystemMessages = 5;
            public const int OllamaConversationTimeoutMinutes = 30;
            public const string ConversationHistoryPath = "history";
            public const string SystemPromptPath = "";
        }

        /// <summary>
        /// Kinect and Face Tracking defaults
        /// </summary>
        public static class Kinect
        {
            public const string KinectMode = "Webcam";
            public const float FaceThreshold = 0.7f;
            public const bool EnhancedFaceTrackingEnabled = true;
            public const bool FaceTrackingDebugMode = false;
        }

        /// <summary>
        /// Audio Device and Processing defaults
        /// </summary>
        public static class Audio
        {
            public const AudioInMode AudioInMode = Kinectv1.AudioInMode.LocalMic;
            public const bool SystemAudioEnabled = false;
        }

        /// <summary>
        /// UI and Application defaults
        /// </summary>
        public static class Ui
        {
            public const bool DarkMode = true;
            public const AppScenario AppScenario = Kinectv1.AppScenario.Local;
        }

        /// <summary>
        /// Telemetry and Logging defaults
        /// </summary>
        public static class Telemetry
        {
            public const bool TelemetryEnabled = false;
            public const int SamplingPctDefault = 100;
        }

        /// <summary>
        /// Window Settings defaults
        /// </summary>
        public static class Window
        {
            public const double Width = 900;
            public const double Height = 750;
            public const double Left = 100;
            public const double Top = 100;
            public const string WindowState = "Normal";
            public static readonly string[] AllowedStates = new[] { "Normal", "Maximized", "Minimized" };
        }

        /// <summary>
        /// Validation ranges (min/max) used by AppSettings.ValidateAll()
        /// </summary>
        public static class Ranges
        {
            public const float VoiceThreshold_Min = 0.0f;
            public const float VoiceThreshold_Max = 1.0f;
            public const float VoiceHighThreshold_Min = 0.0f;
            public const float VoiceHighThreshold_Max = 1.0f;
            public const int VoiceConfidenceBuffer_Min = 1;
            public const int VoiceConfidenceBuffer_Max = 10;

            public const float MicVad_Min = 50f;
            public const float MicVad_Max = 5000f;
            public const float DiscordVad_Min = 10f;
            public const float DiscordVad_Max = 2000f;
            public const int VadSilence_Min = 100;
            public const int VadSilence_Max = 5000;
            public const int VadDebounce_Min = 50;
            public const int VadDebounce_Max = 1000;

            public const double Volume_Min = 0.0;
            public const double Volume_Max = 1.0;

            public const float FusionFace_Min = 0.0f;
            public const float FusionFace_Max = 1.0f;
            public const float FusionVoice_Min = 0.0f;
            public const float FusionVoice_Max = 1.0f;
            public const int FusionHalfLife_Min = 500;
            public const int FusionHalfLife_Max = 10000;
            public const float FusionUnknown_Min = 0.0f;
            public const float FusionUnknown_Max = 1.0f;

            public const int TelemetrySampling_Min = 0;
            public const int TelemetrySampling_Max = 100;

            public const double WindowWidth_Min = 300;
            public const double WindowHeight_Min = 200;
            public const double WindowLeft_Min = 0;
            public const double WindowTop_Min = 0;
        }

        /// <summary>
        /// Apply defaults for a specific category
        /// </summary>
        public static void ApplyCategory(string category)
        {
            switch (category.ToLower())
            {
                case "voice":
                    ApplyVoiceDefaults();
                    break;
                case "tts":
                    ApplyTtsDefaults();
                    break;
                case "stt":
                    ApplySttDefaults();
                    break;
                case "discord":
                    ApplyDiscordDefaults();
                    break;
                case "ollama":
                    ApplyOllamaDefaults();
                    break;
                case "kinect":
                    ApplyKinectDefaults();
                    break;
                case "audio":
                    ApplyAudioDefaults();
                    break;
                case "ui":
                    ApplyUiDefaults();
                    break;
                case "telemetry":
                    AppSettings.SaveTelemetryEnabled(Telemetry.TelemetryEnabled);
                    break;
                case "window":
                    AppSettings.SaveWindowSettings(Window.Width, Window.Height, Window.Left, Window.Top, Window.WindowState);
                    break;
                case "all":
                    ApplyAllDefaults();
                    break;
                default:
                    throw new ArgumentException($"Unknown category: {category}");
            }
        }

        /// <summary>
        /// Apply all default settings across all categories
        /// </summary>
        public static void ApplyAllDefaults()
        {
            ApplyVoiceDefaults();
            ApplyTtsDefaults();
            ApplySttDefaults();
            ApplyDiscordDefaults();
            ApplyOllamaDefaults();
            ApplyKinectDefaults();
            ApplyAudioDefaults();
            ApplyUiDefaults();
            AppSettings.SaveTelemetryEnabled(Telemetry.TelemetryEnabled);
            AppSettings.SaveWindowSettings(Window.Width, Window.Height, Window.Left, Window.Top, Window.WindowState);
        }

        private static void ApplyVoiceDefaults()
        {
            AppSettings.SaveVoiceThreshold(Voice.VoiceThreshold);
            AppSettings.SaveVoiceActivityThreshold(Voice.VoiceActivityThreshold);
            AppSettings.SaveDiscordVoiceActivityThreshold(Voice.DiscordVoiceActivityThreshold);
            AppSettings.SaveVadSilenceTimeoutMs(Voice.VadSilenceTimeoutMs);
            AppSettings.SaveVadDebounceTimeoutMs(Voice.VadDebounceTimeoutMs);
            AppSettings.SaveVoiceConfidenceThreshold(Voice.VoiceConfidenceThreshold);
            AppSettings.SaveVoiceHighConfidenceThreshold(Voice.VoiceHighConfidenceThreshold);
            AppSettings.SaveVoiceConfidenceBufferSize(Voice.VoiceConfidenceBufferSize);
            AppSettings.SaveBargeInEnabled(Voice.BargeInEnabled);
        }

        private static void ApplyTtsDefaults()
        {
            AppSettings.SaveTtsEnabled(Tts.TtsEnabled);
            AppSettings.SaveTtsUseGpu(Tts.TtsUseGpu);
            AppSettings.SaveTtsModelPath(Tts.TtsModelPath);
            AppSettings.SaveTtsModelFolder(Tts.TtsModelFolder);
            AppSettings.SaveTtsSymbolsPath(Tts.TtsSymbolsPath);
            AppSettings.SaveTtsCmudictPath(Tts.TtsCmudictPath);
            AppSettings.SaveTtsOutputDevice(Tts.TtsOutputDevice);
            AppSettings.SaveLocalTtsVolume(Tts.TtsVolumeScale);
        }

        private static void ApplySttDefaults()
        {
            AppSettings.SaveSttModelPath(Stt.SttModelPath);
            AppSettings.SaveSttInputDevice(Stt.SttInputDevice);
        }

        private static void ApplyDiscordDefaults()
        {
            AppSettings.SaveDiscordBotEnabled(Discord.DiscordBotEnabled);
            AppSettings.SaveDiscordBotToken(Discord.DiscordBotToken);
            AppSettings.SaveDiscordAutoJoinVoice(Discord.DiscordAutoJoinVoice);
        }

        private static void ApplyOllamaDefaults()
        {
            AppSettings.SaveOllamaEnabled(Ollama.OllamaEnabled);
            AppSettings.SaveOllamaModel(Ollama.OllamaModel);
            AppSettings.SaveOllamaMemoryEnabled(Ollama.OllamaMemoryEnabled);
            AppSettings.SaveConversationHistoryPath(Ollama.ConversationHistoryPath);
            AppSettings.SaveSystemPromptPath(Ollama.SystemPromptPath);
        }

        private static void ApplyKinectDefaults()
        {
            AppSettings.SaveFaceThreshold(Kinect.FaceThreshold);
        }

        private static void ApplyAudioDefaults()
        {
            AppSettings.SaveAudioInMode(Audio.AudioInMode);
        }

        private static void ApplyUiDefaults()
        {
            AppSettings.SaveDarkMode(Ui.DarkMode);
            AppSettings.SaveAppScenario(Ui.AppScenario);
        }

        /// <summary>
        /// Get list of available categories for the UI
        /// </summary>
        public static List<string> GetCategories()
        {
            return new List<string>
            {
                "Voice",
                "TTS", 
                "STT",
                "Discord",
                "Ollama",
                "Kinect",
                "Audio",
                "UI",
                "Telemetry",
                "Window",
                "All"
            };
        }
        
        /// <summary>
        /// Face Processing defaults
        /// </summary>
        public static class Face
        {
            public const string ArcFaceModelPath = @"models\face\arcface_r100.onnx";
            public const string SpeakerEmbeddingModelPath = @"models\speaker\pyannote_embedding.onnx";
        }
    }
}