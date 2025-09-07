// Defaults.cs
using System;
using System.Collections.Generic;
using Kinectv1.Settings; // new settings pipeline

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
        /// (Kept for UI hints only; persistence uses SettingsService defaults)
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
        /// Text-to-Speech defaults (UI hints only)
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
        /// Speech-to-Text defaults (UI hints only)
        /// </summary>
        public static class Stt
        {
            public const string SttModelPath = @"models\stt\vosk-model-en-us-0.22";
            public const string SttInputDevice = "Default";
            public const bool SttEnabled = true;
        }

        /// <summary>
        /// Discord Bot defaults (UI hints only)
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
        /// Ollama AI defaults (UI hints only)
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
        /// Kinect and Face Tracking defaults (UI hints only)
        /// </summary>
        public static class Kinect
        {
            public const string KinectMode = "Webcam";
            public const float FaceThreshold = 0.7f;
            public const bool EnhancedFaceTrackingEnabled = true;
            public const bool FaceTrackingDebugMode = false;
        }

        /// <summary>
        /// Audio Device and Processing defaults (UI hints only)
        /// </summary>
        public static class Audio
        {
            public const AudioInMode AudioInMode = Settings.AudioInMode.LocalMic;
            public const bool SystemAudioEnabled = false;
        }

        /// <summary>
        /// UI and Application defaults (UI hints only)
        /// </summary>
        public static class Ui
        {
            public const bool DarkMode = true;
            public const AppScenario AppScenario = Settings.AppScenario.Local;
        }

        /// <summary>
        /// Telemetry and Logging defaults (UI hints only)
        /// </summary>
        public static class Telemetry
        {
            public const bool TelemetryEnabled = false;
            public const int SamplingPctDefault = 100;
        }

        /// <summary>
        /// Window Settings defaults (UI hints only)
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
        /// Apply defaults for a specific category using SettingsService overlays
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
                    // No-op in new pipeline; telemetry not persisted via settings.json
                    break;
                case "window":
                    // No-op: window geometry not persisted via settings.json in new pipeline
                    break;
                case "all":
                    ApplyAllDefaults();
                    break;
                default:
                    throw new ArgumentException($"Unknown category: {category}");
            }
        }

        /// <summary>
        /// Apply all default settings: reset user overrides file and reload
        /// </summary>
        public static void ApplyAllDefaults()
        {
            try { App.SettingsProvider?.ResetToDefaults(); } catch { }
        }

        private static void ApplyVoiceDefaults()
        {
            var svc = App.SettingsProvider; var curr = svc?.Current; if (svc == null || curr == null) return;
            var defs = svc.GetDefaultsEffective();
            var next = curr with { Audio = defs.Audio, Vad = defs.Vad, Asr = defs.Asr };
            svc.Save(next);
        }

        private static void ApplyTtsDefaults()
        {
            var svc = App.SettingsProvider; var curr = svc?.Current; if (svc == null || curr == null) return;
            var defs = svc.GetDefaultsEffective();
            var next = curr with { Tts = defs.Tts };
            svc.Save(next);
        }

        private static void ApplySttDefaults()
        {
            var svc = App.SettingsProvider; var curr = svc?.Current; if (svc == null || curr == null) return;
            var defs = svc.GetDefaultsEffective();
            var next = curr with { Stt = defs.Stt };
            svc.Save(next);
        }

        private static void ApplyDiscordDefaults()
        {
            var svc = App.SettingsProvider; var curr = svc?.Current; if (svc == null || curr == null) return;
            var defs = svc.GetDefaultsEffective();
            var next = curr with { Discord = defs.Discord };
            svc.Save(next);
        }

        private static void ApplyOllamaDefaults()
        {
            var svc = App.SettingsProvider; var curr = svc?.Current; if (svc == null || curr == null) return;
            var defs = svc.GetDefaultsEffective();
            var next = curr with { Ollama = defs.Ollama };
            svc.Save(next);
        }

        private static void ApplyKinectDefaults()
        {
            var svc = App.SettingsProvider; var curr = svc?.Current; if (svc == null || curr == null) return;
            var defs = svc.GetDefaultsEffective();
            var next = curr with { Face = defs.Face };
            svc.Save(next);
        }

        private static void ApplyAudioDefaults()
        {
            var svc = App.SettingsProvider; var curr = svc?.Current; if (svc == null || curr == null) return;
            var defs = svc.GetDefaultsEffective();
            var next = curr with { App = defs.App };
            svc.Save(next);
        }

        private static void ApplyUiDefaults()
        {
            var svc = App.SettingsProvider; var curr = svc?.Current; if (svc == null || curr == null) return;
            var defs = svc.GetDefaultsEffective();
            var next = curr with { Ui = defs.Ui, App = curr.App with { Scenario = defs.App.Scenario } };
            svc.Save(next);
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
        /// Face Processing defaults (UI hints only)
        /// </summary>
        public static class Face
        {
            public const string ArcFaceModelPath = @"models\face\arcface_r100.onnx";
            public const string SpeakerEmbeddingModelPath = @"models\speaker\pyannote_embedding.onnx";
        }
    }
}