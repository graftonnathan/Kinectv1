using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Kinectv1.Config
{
    /// <summary>
    /// Strongly-typed application settings POCO that can be serialized to JSON.
    /// Supports versioning and migration between different settings versions.
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// Version of the settings schema for migration purposes
        /// </summary>
        [JsonProperty("settingsVersion")]
        public int SettingsVersion { get; set; } = 1;

        /// <summary>
        /// Timestamp when settings were last updated
        /// </summary>
        [JsonProperty("lastUpdated")]
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        #region Application Paths
        /// <summary>
        /// Path to Vosk speech recognition model directory
        /// </summary>
        [JsonProperty("voskModelPath")]
        public string VoskModelPath { get; set; } = string.Empty;

        /// <summary>
        /// Path to conversation history storage directory
        /// </summary>
        [JsonProperty("conversationHistoryPath")]
        public string ConversationHistoryPath { get; set; } = string.Empty;

        /// <summary>
        /// Path to user data and profiles directory
        /// </summary>
        [JsonProperty("userDataPath")]
        public string UserDataPath { get; set; } = string.Empty;
        #endregion

        #region Audio Device Selections
        /// <summary>
        /// Selected audio input device for speech-to-text
        /// </summary>
        [JsonProperty("sttInputDevice")]
        public string SttInputDevice { get; set; } = "Default";

        /// <summary>
        /// Selected audio output device for text-to-speech
        /// </summary>
        [JsonProperty("ttsOutputDevice")]
        public string TtsOutputDevice { get; set; } = "Default";

        /// <summary>
        /// Audio input mode selection
        /// </summary>
        [JsonProperty("audioInputMode")]
        public string AudioInputMode { get; set; } = "LocalMic";
        #endregion

        #region Model File Paths
        /// <summary>
        /// Path to TTS model files
        /// </summary>
        [JsonProperty("ttsModelPath")]
        public string TtsModelPath { get; set; } = string.Empty;

        /// <summary>
        /// Path to face recognition model files
        /// </summary>
        [JsonProperty("faceModelPath")]
        public string FaceModelPath { get; set; } = string.Empty;

        /// <summary>
        /// Path to speaker identification model files
        /// </summary>
        [JsonProperty("speakerModelPath")]
        public string SpeakerModelPath { get; set; } = string.Empty;
        #endregion

        #region Logging Configuration
        /// <summary>
        /// Application logging level
        /// </summary>
        [JsonProperty("loggingLevel")]
        public string LoggingLevel { get; set; } = "Info";

        /// <summary>
        /// Whether to enable telemetry collection
        /// </summary>
        [JsonProperty("telemetryEnabled")]
        public bool TelemetryEnabled { get; set; } = false;

        /// <summary>
        /// Whether to enable verbose logging for debugging
        /// </summary>
        [JsonProperty("verboseLogging")]
        public bool VerboseLogging { get; set; } = false;
        #endregion

        #region Feature Toggles
        /// <summary>
        /// Whether text-to-speech is enabled
        /// </summary>
        [JsonProperty("ttsEnabled")]
        public bool TtsEnabled { get; set; } = true;

        /// <summary>
        /// Whether speech-to-text is enabled
        /// </summary>
        [JsonProperty("sttEnabled")]
        public bool SttEnabled { get; set; } = true;

        /// <summary>
        /// Whether Discord bot integration is enabled
        /// </summary>
        [JsonProperty("discordBotEnabled")]
        public bool DiscordBotEnabled { get; set; } = false;

        /// <summary>
        /// Whether Ollama AI integration is enabled
        /// </summary>
        [JsonProperty("ollamaEnabled")]
        public bool OllamaEnabled { get; set; } = false;

        /// <summary>
        /// Whether Kinect face tracking is enabled
        /// </summary>
        [JsonProperty("faceTrackingEnabled")]
        public bool FaceTrackingEnabled { get; set; } = true;

        /// <summary>
        /// Whether speaker identification is enabled
        /// </summary>
        [JsonProperty("speakerIdentificationEnabled")]
        public bool SpeakerIdentificationEnabled { get; set; } = false;

        /// <summary>
        /// Whether to use GPU acceleration for TTS
        /// </summary>
        [JsonProperty("ttsUseGpu")]
        public bool TtsUseGpu { get; set; } = false;
        #endregion

        #region Voice Processing Settings
        /// <summary>
        /// Voice confidence threshold for speech recognition
        /// </summary>
        [JsonProperty("voiceConfidenceThreshold")]
        public float VoiceConfidenceThreshold { get; set; } = 0.5f;

        /// <summary>
        /// Voice activity detection threshold
        /// </summary>
        [JsonProperty("voiceActivityThreshold")]
        public int VoiceActivityThreshold { get; set; } = 300;

        /// <summary>
        /// Face detection confidence threshold
        /// </summary>
        [JsonProperty("faceThreshold")]
        public float FaceThreshold { get; set; } = 0.7f;
        #endregion

        #region Network Configuration
        /// <summary>
        /// Discord bot token for integration
        /// </summary>
        [JsonProperty("discordBotToken")]
        public string DiscordBotToken { get; set; } = string.Empty;

        /// <summary>
        /// Ollama API endpoint URL
        /// </summary>
        [JsonProperty("ollamaApiUrl")]
        public string OllamaApiUrl { get; set; } = "http://localhost:11434";

        /// <summary>
        /// Ollama model name to use
        /// </summary>
        [JsonProperty("ollamaModel")]
        public string OllamaModel { get; set; } = string.Empty;
        #endregion

        /// <summary>
        /// Creates a deep copy of the current settings
        /// </summary>
        public AppSettings Clone()
        {
            var json = JsonConvert.SerializeObject(this);
            return JsonConvert.DeserializeObject<AppSettings>(json);
        }

        /// <summary>
        /// Validates the current settings and returns a list of validation issues
        /// </summary>
        public List<string> Validate()
        {
            var issues = new List<string>();

            // Validate required paths
            if (string.IsNullOrWhiteSpace(VoskModelPath))
                issues.Add("Vosk model path is required for speech recognition");

            if (SttEnabled && string.IsNullOrWhiteSpace(VoskModelPath))
                issues.Add("STT is enabled but Vosk model path is not configured");

            if (DiscordBotEnabled && string.IsNullOrWhiteSpace(DiscordBotToken))
                issues.Add("Discord bot is enabled but token is not configured");

            if (OllamaEnabled && string.IsNullOrWhiteSpace(OllamaModel))
                issues.Add("Ollama is enabled but model is not selected");

            // Validate thresholds
            if (VoiceConfidenceThreshold < 0.0f || VoiceConfidenceThreshold > 1.0f)
                issues.Add("Voice confidence threshold must be between 0.0 and 1.0");

            if (FaceThreshold < 0.0f || FaceThreshold > 1.0f)
                issues.Add("Face threshold must be between 0.0 and 1.0");

            if (VoiceActivityThreshold < 0)
                issues.Add("Voice activity threshold must be non-negative");

            return issues;
        }
    }
}