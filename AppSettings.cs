// AppSettings.cs
using System;
using System.Configuration;
using System.Globalization;
using System.Threading;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Kinectv1
{
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

    public static partial class AppSettings
    {
        // Thread-safe configuration access
        private static readonly object _configLock = new object();

        private static void LogSettingError(string name, string detail)
        {
            var error = AppError.Config("CONFIG_SETTING_ERROR", 
                $"Setting '{name}': {detail}",
                "Check configuration values on Diagnostics page.");
            Console.WriteLine(error.GetDisplayString());
        }

        // Helpers to read/write settings without Properties.Settings.Default
        private static ClientSettingsSection GetClientSettingsSection(Configuration config, string groupName)
        {
            // groupName expected: "applicationSettings" or "userSettings"
            var sectionName = typeof(Kinectv1.Properties.Settings).FullName;
            var group = config.SectionGroups[groupName];
            if (group == null) return null;
            return (group.Sections[sectionName] as ClientSettingsSection);
        }

        private static string ReadSettingRaw(string name)
        {
            try
            {
                lock (_configLock)
                {
                    var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                    // Prefer applicationSettings only. If not found, migrate from userSettings/appSettings.
                    var appSection = GetClientSettingsSection(config, "applicationSettings");
                    var userSection = GetClientSettingsSection(config, "userSettings");

                    string fromSection(ClientSettingsSection section)
                    {
                        if (section == null) return null;
                        foreach (SettingElement element in section.Settings)
                        {
                            if (string.Equals(element.Name, name, StringComparison.Ordinal))
                            {
                                return element.Value?.ValueXml?.InnerText ?? string.Empty;
                            }
                        }
                        return null;
                    }

                    var val = fromSection(appSection);
                    if (val != null) return val;

                    // Try userSettings (migrate if found)
                    var userVal = fromSection(userSection);
                    if (userVal != null)
                    {
                        // Migrate to applicationSettings for single source of truth
                        WriteSettingRaw(name, userVal);
                        return userVal;
                    }

                    // Fallback to appSettings (migrate if found)
                    var appValue = ConfigurationManager.AppSettings[name];
                    if (appValue != null)
                    {
                        WriteSettingRaw(name, appValue);
                        return appValue;
                    }

                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static void WriteSettingRaw(string name, string value)
        {
            lock (_configLock)
            {
                var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                // Write into applicationSettings section
                var groupName = "applicationSettings";
                var section = GetClientSettingsSection(config, groupName);
                if (section == null)
                {
                    // Create group/section if missing
                    var appGroup = new ApplicationSettingsGroup();
                    config.SectionGroups.Add(groupName, appGroup);
                    var sectionName = typeof(Kinectv1.Properties.Settings).FullName;
                    section = new ClientSettingsSection();
                    appGroup.Sections.Add(sectionName, section);
                }

                SettingElement target = null;
                foreach (SettingElement element in section.Settings)
                {
                    if (string.Equals(element.Name, name, StringComparison.Ordinal))
                    {
                        target = element;
                        break;
                    }
                }
                if (target == null)
                {
                    target = new SettingElement(name, SettingsSerializeAs.String);
                    section.Settings.Add(target);
                }
                if (target.Value.ValueXml == null)
                {
                    var doc = new System.Xml.XmlDocument();
                    target.Value.ValueXml = doc.CreateElement("value");
                }
                target.Value.ValueXml.InnerText = value ?? string.Empty;

                config.Save(ConfigurationSaveMode.Modified);
                ConfigurationManager.RefreshSection($"{groupName}/{typeof(Kinectv1.Properties.Settings).FullName}");
            }
        }

        // Application-level wrappers used by some settings helpers
        private static string GetApplicationSetting(string name)
        {
            return ReadSettingRaw(name);
        }
        private static void SetApplicationSetting(string name, string value)
        {
            WriteSettingRaw(name, value);
        }

        private static string GetString(string name)
        {
            return ReadSettingRaw(name);
        }

        private static string GetString(string name, string fallback)
        {
            var s = ReadSettingRaw(name);
            return string.IsNullOrEmpty(s) ? fallback : s;
        }

        private static bool GetBool(string name, bool fallback = false)
        {
            var s = ReadSettingRaw(name);
            if (bool.TryParse(s, out var v)) return v;
            return fallback;
        }

        private static int GetInt(string name, int fallback = 0)
        {
            var s = ReadSettingRaw(name);
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            return fallback;
        }

        private static double GetDouble(string name, double fallback = 0)
        {
            var s = ReadSettingRaw(name);
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            return fallback;
        }

        private static float GetFloat(string name, float fallback = 0)
        {
            var s = ReadSettingRaw(name);
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            return fallback;
        }

        private static void SetString(string name, string value) => WriteSettingRaw(name, value);
        private static void SetBool(string name, bool value) => WriteSettingRaw(name, value ? "True" : "False");
        private static void SetInt(string name, int value) => WriteSettingRaw(name, value.ToString(CultureInfo.InvariantCulture));
        private static void SetDouble(string name, double value) => WriteSettingRaw(name, value.ToString(CultureInfo.InvariantCulture));
        private static void SetFloat(string name, float value) => WriteSettingRaw(name, value.ToString(CultureInfo.InvariantCulture));

        // Public save helpers needed by UI (ints)
        public static void SaveOllamaMaxMessagesPerSpeaker(int value) => SetInt("OllamaMaxMessagesPerSpeaker", value);
        public static void SaveOllamaMaxSystemMessages(int value) => SetInt("OllamaMaxSystemMessages", value);
        public static void SaveOllamaConversationTimeoutMinutes(int value) => SetInt("OllamaConversationTimeoutMinutes", value);

        // Validation clamp helpers and full validation pass
        private static float ClampAndPersist(string key, float value, float min, float max, Action<float> saver)
        {
            var clamped = Math.Max(min, Math.Min(max, value));
            if (Math.Abs(clamped - value) > 0.0001f)
            {
                saver(clamped);
                LogSettingError(key, $"OUT OF RANGE -> clamped to {clamped}");
            }
            return clamped;
        }

        private static int ClampAndPersist(string key, int value, int min, int max, Action<int> saver)
        {
            var clamped = Math.Max(min, Math.Min(max, value));
            if (clamped != value)
            {
                saver(clamped);
                LogSettingError(key, $"OUT OF RANGE -> clamped to {clamped}");
            }
            return clamped;
        }

        private static double ClampAndPersist(string key, double value, double min, double max, Action<double> saver)
        {
            var clamped = Math.Max(min, Math.Min(max, value));
            if (Math.Abs(clamped - value) > 0.0000001)
            {
                saver(clamped);
                LogSettingError(key, $"OUT OF RANGE -> clamped to {clamped:F3}");
            }
            return clamped;
        }

        /// <summary>
        /// Validate and normalize all persisted settings at startup. Logs once when corrections are applied.
        /// </summary>
        public static void ValidateAll()
        {
            try
            {
                // Voice thresholds
                var vThresh = LoadVoiceThreshold();
                ClampAndPersist("VoiceThreshold", vThresh, Defaults.Ranges.VoiceThreshold_Min, Defaults.Ranges.VoiceThreshold_Max, SaveVoiceThreshold);

                var vHiThresh = LoadVoiceHighConfidenceThreshold();
                vHiThresh = ClampAndPersist("VoiceHighConfidenceThreshold", vHiThresh, Defaults.Ranges.VoiceHighThreshold_Min, Defaults.Ranges.VoiceHighThreshold_Max, SaveVoiceHighConfidenceThreshold);
                var vLow = LoadVoiceConfidenceThreshold();
                if (vHiThresh < vLow)
                {
                    SaveVoiceHighConfidenceThreshold(vLow);
                    LogSettingError("VoiceHighConfidenceThreshold", $"LESS THAN VoiceConfidenceThreshold -> raised to {vLow:F2}");
                }

                var vBuf = LoadVoiceConfidenceBufferSize();
                ClampAndPersist("VoiceConfidenceBufferSize", vBuf, Defaults.Ranges.VoiceConfidenceBuffer_Min, Defaults.Ranges.VoiceConfidenceBuffer_Max, SaveVoiceConfidenceBufferSize);

                // VAD
                var micVad = LoadVoiceActivityThreshold();
                ClampAndPersist("VoiceActivityThreshold", micVad, Defaults.Ranges.MicVad_Min, Defaults.Ranges.MicVad_Max, x => SaveVoiceActivityThreshold(x));

                var discVad = LoadDiscordVoiceActivityThreshold();
                ClampAndPersist("DiscordVoiceActivityThreshold", discVad, Defaults.Ranges.DiscordVad_Min, Defaults.Ranges.DiscordVad_Max, x => SaveDiscordVoiceActivityThreshold(x));

                var silenceMs = LoadVadSilenceTimeoutMs();
                ClampAndPersist("VadSilenceTimeoutMs", silenceMs, Defaults.Ranges.VadSilence_Min, Defaults.Ranges.VadSilence_Max, SaveVadSilenceTimeoutMs);

                var debounceMs = LoadVadDebounceTimeoutMs();
                ClampAndPersist("VadDebounceTimeoutMs", debounceMs, Defaults.Ranges.VadDebounce_Min, Defaults.Ranges.VadDebounce_Max, SaveVadDebounceTimeoutMs);

                // Ensure cached VAD threshold reflects any clamped/updated values
                try { AudioUtils.RefreshVadThreshold(); } catch { }

                // Volumes
                var localVol = LoadLocalTtsVolume();
                ClampAndPersist("LocalTtsVolume", localVol, Defaults.Ranges.Volume_Min, Defaults.Ranges.Volume_Max, SaveLocalTtsVolume);
                var discVol = LoadDiscordTtsVolume();
                ClampAndPersist("DiscordTtsVolume", discVol, Defaults.Ranges.Volume_Min, Defaults.Ranges.Volume_Max, SaveDiscordTtsVolume);

                // Fusion
                var fFace = LoadFusionFaceWeight();
                ClampAndPersist("FusionFaceWeight", fFace, Defaults.Ranges.FusionFace_Min, Defaults.Ranges.FusionFace_Max, SaveFusionFaceWeight);
                var fVoice = LoadFusionVoiceWeight();
                ClampAndPersist("FusionVoiceWeight", fVoice, Defaults.Ranges.FusionVoice_Min, Defaults.Ranges.FusionVoice_Max, SaveFusionVoiceWeight);
                var fHalf = LoadFusionDecayHalfLifeMs();
                ClampAndPersist("FusionDecayHalfLifeMs", fHalf, Defaults.Ranges.FusionHalfLife_Min, Defaults.Ranges.FusionHalfLife_Max, SaveFusionDecayHalfLifeMs);
                var fUnknown = LoadFusionUnknownThreshold();
                ClampAndPersist("FusionUnknownThreshold", fUnknown, Defaults.Ranges.FusionUnknown_Min, Defaults.Ranges.FusionUnknown_Max, SaveFusionUnknownThreshold);

                // Telemetry
                var sampling = LoadTelemetrySamplingPct();
                ClampAndPersist("TelemetrySamplingPct", sampling, Defaults.Ranges.TelemetrySampling_Min, Defaults.Ranges.TelemetrySampling_Max, SaveTelemetrySamplingPct);

                // Window
                var (w, h, l, t, state) = LoadWindowSettings();
                if (w < Defaults.Ranges.WindowWidth_Min) { SaveWindowSettings(Defaults.Ranges.WindowWidth_Min, h, l, t, state); LogSettingError("WindowWidth", $"OUT OF RANGE -> set to {Defaults.Ranges.WindowWidth_Min:F0}"); }
                if (h < Defaults.Ranges.WindowHeight_Min) { SaveWindowSettings(w, Defaults.Ranges.WindowHeight_Min, l, t, state); LogSettingError("WindowHeight", $"OUT OF RANGE -> set to {Defaults.Ranges.WindowHeight_Min:F0}"); }
                if (l < Defaults.Ranges.WindowLeft_Min) { SaveWindowSettings(w, h, Defaults.Ranges.WindowLeft_Min, t, state); LogSettingError("WindowLeft", $"OUT OF RANGE -> set to {Defaults.Ranges.WindowLeft_Min:F0}"); }
                if (t < Defaults.Ranges.WindowTop_Min) { SaveWindowSettings(w, h, l, Defaults.Ranges.WindowTop_Min, state); LogSettingError("WindowTop", $"OUT OF RANGE -> set to {Defaults.Ranges.WindowTop_Min:F0}"); }
                if (string.IsNullOrWhiteSpace(state) || !Defaults.Window.AllowedStates.Contains(state))
                {
                    SaveWindowSettings(w, h, l, t, Defaults.Window.WindowState);
                    LogSettingError("WindowState", $"INVALID -> set to {Defaults.Window.WindowState}");
                }

                // Enums
                var scen = LoadAppScenario();
                if (!Enum.IsDefined(typeof(AppScenario), scen))
                {
                    SaveAppScenario(Defaults.Ui.AppScenario);
                    LogSettingError("AppScenario", $"INVALID -> set to {Defaults.Ui.AppScenario}");
                }
                var audioMode = LoadAudioInMode();
                if (!Enum.IsDefined(typeof(AudioInMode), audioMode))
                {
                    SaveAudioInMode(Defaults.Audio.AudioInMode);
                    LogSettingError("AudioInMode", $"INVALID -> set to {Defaults.Audio.AudioInMode}");
                }

                // Strings with reasonable defaults
                var lang = LoadLanguage();
                if (string.IsNullOrWhiteSpace(lang)) { SaveLanguage("en-US"); }

                var conv = LoadConversationHistoryPath();
                if (string.IsNullOrWhiteSpace(conv)) { SaveConversationHistoryPath(Defaults.Ollama.ConversationHistoryPath); }

                var speaker = LoadTtsSpeaker();
                if (string.IsNullOrWhiteSpace(speaker)) { SaveTtsSpeaker("em_alex"); }

                var ttsFolder = LoadTtsModelFolder();
                if (string.IsNullOrWhiteSpace(ttsFolder)) { SaveTtsModelFolder(Defaults.Tts.TtsModelFolder); }

                var ttsModel = LoadTtsModelPath();
                if (string.IsNullOrWhiteSpace(ttsModel)) { SaveTtsModelPath(Defaults.Tts.TtsModelPath); }

                var vocModel = LoadTtsVocoderModelPath();
                if (string.IsNullOrWhiteSpace(vocModel)) { SaveTtsVocoderModelPath(Defaults.Tts.TtsModelPath); }

                var arcModel = LoadArcFaceModelPath();
                if (string.IsNullOrWhiteSpace(arcModel)) { SaveArcFaceModelPath(Defaults.Tts.TtsModelPath); }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ERROR: ValidateAll failed: {ex.Message}");
            }
        }

        // Scenario Configuration Methods

        /// <summary>
        /// Load current application scenario
        /// </summary>
        public static AppScenario LoadAppScenario()
        {
            try
            {
                var scenarioStr = GetString("AppScenario", "Local");
                if (Enum.TryParse<AppScenario>(scenarioStr, true, out var scenario))
                {
                    return scenario;
                }
                return AppScenario.Local; // Default fallback
            }
            catch (Exception ex)
            {
                LogSettingError("AppScenario", $"read FAILED: {ex.Message}");
                return AppScenario.Local;
            }
        }

        /// <summary>
        /// Save application scenario
        /// </summary>
        public static void SaveAppScenario(AppScenario scenario)
        {
            try
            {
                SetString("AppScenario", scenario.ToString());
                Console.WriteLine($"📋 App scenario set to: {scenario}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving app scenario: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply scenario defaults to all relevant settings
        /// </summary>
        public static void ApplyScenarioDefaults(AppScenario scenario)
        {
            try
            {
                Console.WriteLine($"📋 Applying {scenario} scenario defaults...");

                switch (scenario)
                {
                    case AppScenario.Local:
                        // Local scenario: Focus on TTS and local voice processing
                        SaveTtsEnabled(true);
                        SaveDiscordBotEnabled(false);
                        SaveOllamaEnabled(true);
                        SaveTelemetryEnabled(false);
                        SaveVoiceConfidenceThreshold(0.5f);
                        SaveVoiceActivityThreshold(300);
                        SaveTtsUseGpu(false); // Conservative for local use
                        break;

                    case AppScenario.Discord:
                        // Discord scenario: Enable bot integration and optimize for voice commands
                        SaveTtsEnabled(true);
                        SaveDiscordBotEnabled(true);
                        SaveOllamaEnabled(true);
                        SaveTelemetryEnabled(true);
                        SaveVoiceConfidenceThreshold(0.7f); // Higher threshold for Discord
                        SaveDiscordVoiceActivityThreshold(25f);
                        SaveVadDebounceTimeoutMs(200);
                        SaveDiscordAutoJoinVoice(true);
                        break;

                    case AppScenario.Kiosk:
                        // Kiosk scenario: Public-facing, stable settings
                        SaveTtsEnabled(true);
                        SaveDiscordBotEnabled(false);
                        SaveOllamaEnabled(false); // Disable AI for public use
                        SaveTelemetryEnabled(false);
                        SaveVoiceConfidenceThreshold(0.8f); // High threshold for accuracy
                        SaveVoiceActivityThreshold(400); // Higher threshold to avoid noise
                        SaveTtsUseGpu(false); // Stable CPU processing
                        break;
                }

                Console.WriteLine($"✅ {scenario} scenario defaults applied successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error applying {scenario} scenario defaults: {ex.Message}");
            }
        }

        /// <summary>
        /// Validate current configuration and return list of issues
        /// </summary>
        public static List<string> Validate()
        {
            var issues = new List<string>();

            try
            {
                // TTS Validation
                if (LoadTtsEnabled())
                {
                    var ttsModelPath = LoadTtsModelPath();
                    if (string.IsNullOrEmpty(ttsModelPath) || !File.Exists(ttsModelPath))
                    {
                        issues.Add("❌ TTS: Model file not found or not configured");
                    }

                    var ttsModelFolder = LoadTtsModelFolder();
                    if (string.IsNullOrEmpty(ttsModelFolder) || !Directory.Exists(ttsModelFolder))
                    {
                        issues.Add("❌ TTS: Model folder not found or not configured");
                    }
                }

                // STT Validation
                var sttModelPath = LoadSttModelPath();
                if (string.IsNullOrEmpty(sttModelPath) || !Directory.Exists(sttModelPath))
                {
                    issues.Add("❌ STT: Vosk model directory not found");
                }

                // Face (ArcFace) Validation
                var arc = LoadArcFaceModelPath();
                if (string.IsNullOrWhiteSpace(arc) || !File.Exists(arc))
                {
                    issues.Add("⚠️ Face: ArcFace ONNX not configured or missing — face recognition will be limited. Configure in Settings → Models.");
                }

                // Discord Validation
                if (LoadDiscordBotEnabled())
                {
                    var discordToken = LoadDiscordBotToken();
                    if (string.IsNullOrEmpty(discordToken))
                    {
                        issues.Add("❌ Discord: Bot token not configured");
                    }
                    else if (discordToken.Length < 50) // Rough check for valid token length
                    {
                        issues.Add("⚠️ Discord: Bot token appears invalid (too short)");
                    }
                }

                // Ollama Validation
                if (LoadOllamaEnabled())
                {
                    var ollamaModel = LoadOllamaModel();
                    if (string.IsNullOrEmpty(ollamaModel))
                    {
                        issues.Add("❌ Ollama: No model selected");
                    }

                    var systemPromptPath = LoadSystemPromptPath();
                    if (!string.IsNullOrEmpty(systemPromptPath) && !File.Exists(systemPromptPath))
                    {
                        issues.Add("⚠️ Ollama: System prompt file not found");
                    }
                }

                // Audio Device Validation
                var sttInputDevice = LoadSttInputDevice();
                var ttsOutputDevice = LoadTtsOutputDevice();
                
                // Basic device name validation
                if (string.IsNullOrEmpty(sttInputDevice))
                {
                    issues.Add("⚠️ Audio: STT input device not configured");
                }
                if (string.IsNullOrEmpty(ttsOutputDevice))
                {
                    issues.Add("⚠️ Audio: TTS output device not configured");
                }

                // Scenario-specific validation
                var currentScenario = LoadAppScenario();
                switch (currentScenario)
                {
                    case AppScenario.Discord:
                        if (!LoadDiscordBotEnabled())
                        {
                            issues.Add("⚠️ Scenario: Discord scenario but Discord bot is disabled");
                        }
                        break;

                    case AppScenario.Kiosk:
                        if (LoadOllamaEnabled())
                        {
                            issues.Add("⚠️ Scenario: Kiosk scenario should disable Ollama for public use");
                        }
                        if (LoadTelemetryEnabled())
                        {
                            issues.Add("⚠️ Scenario: Kiosk scenario should disable telemetry for privacy");
                        }
                        break;
                }

                if (issues.Count == 0)
                {
                    issues.Add("✅ All configuration checks passed");
                }
            }
            catch (Exception ex)
            {
                issues.Add($"❌ Validation failed: {ex.Message}");
            }

            return issues;
        }

        /// <summary>
        /// Get comprehensive diagnostics report including scenario and validation
        /// </summary>
        public static string GetDiagnosticsReport()
        {
            try
            {
                var report = new StringBuilder();
                report.AppendLine("🔍 Configuration Diagnostics Report");
                report.AppendLine("=".PadRight(50, '='));
                report.AppendLine();

                // Current scenario
                var currentScenario = LoadAppScenario();
                report.AppendLine($"📋 Current Scenario: {currentScenario}");
                report.AppendLine();

                // Validation results
                report.AppendLine("🔍 Validation Results:");
                var validationIssues = Validate();
                foreach (var issue in validationIssues)
                {
                    report.AppendLine($"   {issue}");
                }
                report.AppendLine();

                // Grouped settings summaries
                report.AppendLine(GetTtsSettingsSummary());
                report.AppendLine();
                report.AppendLine(GetSttSettingsSummary());
                report.AppendLine();
                report.AppendLine(GetDiscordBotSettingsSummary());
                report.AppendLine();
                report.AppendLine(GetAudioDeviceSettingsSummary());
                report.AppendLine();
                report.AppendLine(GetVoiceConfidenceSettingsSummary());
                report.AppendLine();
                report.AppendLine(GetTelemetrySettingsSummary());
                report.AppendLine();
                report.AppendLine(GetFusionSettingsSummary());

                return report.ToString();
            }
            catch (Exception ex)
            {
                return $"❌ ERROR: Could not generate diagnostics report: {ex.Message}";
            }
        }

        /// <summary>
        /// Initialize settings on application startup - loads from App.config (no Settings.settings)
        /// </summary>
        public static void InitializeSettingsOnStartup()
        {
            try
            {
                Console.WriteLine("📋 Initializing settings from App.config (Settings.settings-backed, no Settings.Default)...");

                // Organized grouped summaries
                Console.WriteLine(GetTtsSettingsSummary());
                Console.WriteLine(GetSttSettingsSummary());
                Console.WriteLine(GetVoiceConfidenceSettingsSummary());
                Console.WriteLine(GetDiscordBotSettingsSummary());
                Console.WriteLine(GetAudioDeviceSettingsSummary());
                Console.WriteLine(GetTelemetrySettingsSummary());
                Console.WriteLine(GetFusionSettingsSummary());

                // Other settings remain grouped by feature
                Console.WriteLine($"👤 Face Settings:\n   Face Threshold: {LoadFaceThreshold():F2}");

                Console.WriteLine($"🧠 Ollama Settings:\n   Model: {LoadOllamaModel()}\n   Enabled: {LoadOllamaEnabled()}\n   Memory Enabled: {LoadOllamaMemoryEnabled()}\n   Max Messages Per Speaker: {LoadOllamaMaxMessagesPerSpeaker()}\n   Max System Messages: {LoadOllamaMaxSystemMessages()}\n   Conversation Timeout: {LoadOllamaConversationTimeoutMinutes()} minutes\n   Conversation History Path: {LoadConversationHistoryPath()}");

                Console.WriteLine($"🎨 UI Settings:\n   Dark Mode: {LoadDarkMode()}\n   Language: {LoadLanguage()}\n   Telemetry Level: {LoadTelemetryLevel()}");

                var (width, height, left, top, state) = LoadWindowSettings();
                Console.WriteLine($"🪟 Window Settings:\n   Size: {width:F0}x{height:F0}\n   Position: ({left:F0}, {top:F0})\n   State: {state}");

                Console.WriteLine($"📁 System Settings:\n   Kinect Mode: {LoadKinectMode()}\n   System Prompt Path: {LoadSystemPromptPath()}");

                // Initialize and log audio devices
                InitializeAudioDevices();

                // Preflight validation with friendly hints
                var issues = Validate();
                foreach (var msg in issues)
                {
                    Console.WriteLine($"[Config] {msg}");
                }

                Console.WriteLine("✅ Settings initialization complete!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ERROR: Error loading settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Save all settings - retained for compatibility, but not required since each setter persists.
        /// </summary>
        public static void SaveAllSettings()
        {
            // No-op: individual setters persist directly to App.config
        }

        // Kinect Settings
        public static string LoadKinectMode()
        {
            try
            {
                var mode = GetString("KinectMode");
                if (string.IsNullOrWhiteSpace(mode))
                    LogSettingError("KinectMode", "EMPTY");
                return mode;
            }
            catch (Exception ex)
            {
                LogSettingError("KinectMode", $"READ FAILED: {ex.Message}");
                return null;
            }
        }

        // Voice Recognition Settings
        public static float LoadVoiceThreshold()
        {
            try
            {
                var value = GetFloat("VoiceThreshold");
                if (value < 0.0f || value > 1.0f)
                    LogSettingError("VoiceThreshold", $"OUT OF RANGE (expected 0.0-1.0, got {value:F3})");
                return value;
            }
            catch (Exception ex)
            {
                LogSettingError("VoiceThreshold", $"READ FAILED: {ex.Message}");
                return 0f;
            }
        }

        public static void SaveVoiceThreshold(float threshold)
        {
            try
            {
                SetFloat("VoiceThreshold", threshold);
                Console.WriteLine($"Saved voice threshold: {threshold:F3}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving voice threshold: {ex.Message}");
            }
        }

        /// <summary>
        /// Load Voice Activity Detection (VAD) RMS threshold for microphone input
        /// </summary>
        public static float LoadVoiceActivityThreshold()
        {
            try
            {
                var value = GetFloat("VoiceActivityThreshold");
                if (value < 50f || value > 5000f)
                    LogSettingError("VoiceActivityThreshold", $"OUT OF RANGE (expected 50-5000, got {value:F0})");
                return value;
            }
            catch (Exception ex)
            {
                LogSettingError("VoiceActivityThreshold", $"READ FAILED: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// Save Voice Activity Detection (VAD) RMS threshold
        /// </summary>
        public static void SaveVoiceActivityThreshold(float threshold)
        {
            try
            {
                SetFloat("VoiceActivityThreshold", threshold);
                Console.WriteLine($"Voice: Saved VAD threshold: {threshold:F0}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving voice activity threshold: {ex.Message}");
            }
        }

        /// <summary>
        /// Load Discord Voice Activity Detection (VAD) RMS threshold for Discord audio input
        /// </summary>
        public static float LoadDiscordVoiceActivityThreshold()
        {
            try
            {
                var value = GetFloat("DiscordVoiceActivityThreshold");
                if (value < 10f || value > 2000f)
                    LogSettingError("DiscordVoiceActivityThreshold", $"OUT OF RANGE (expected 10-2000, got {value:F0})");
                return value;
            }
            catch (Exception ex)
            {
                LogSettingError("DiscordVoiceActivityThreshold", $"READ FAILED: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// Save Discord Voice Activity Detection (VAD) RMS threshold
        /// </summary>
        public static void SaveDiscordVoiceActivityThreshold(float threshold)
        {
            try
            {
                SetFloat("DiscordVoiceActivityThreshold", threshold);
                Console.WriteLine($"Discord: Saved VAD threshold: {threshold:F0}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving Discord voice activity threshold: {ex.Message}");
            }
        }

        /// <summary>
        /// Load VAD silence timeout in milliseconds (prevents double FinalResult flush)
        /// </summary>
        public static int LoadVadSilenceTimeoutMs()
        {
            try
            {
                var value = GetInt("VadSilenceTimeoutMs");
                if (value < 100 || value > 5000)
                    LogSettingError("VadSilenceTimeoutMs", $"OUT OF RANGE (expected 100-5000, got {value})");
                return value;
            }
            catch (Exception ex)
            {
                LogSettingError("VadSilenceTimeoutMs", $"READ FAILED: {ex.Message}");
                return 150; // Default 150ms
            }
        }

        /// <summary>
        /// Save VAD silence timeout in milliseconds
        /// </summary>
        public static void SaveVadSilenceTimeoutMs(int timeoutMs)
        {
            try
            {
                SetInt("VadSilenceTimeoutMs", timeoutMs);
                Console.WriteLine($"VAD: Saved silence timeout: {timeoutMs}ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving VAD silence timeout: {ex.Message}");
            }
        }

        /// <summary>
        /// Load VAD debounce timeout in milliseconds (prevents rapid consecutive FinalResults)
        /// </summary>
        public static int LoadVadDebounceTimeoutMs()
        {
            try
            {
                var value = GetInt("VadDebounceTimeoutMs");
                if (value < 50 || value > 1000)
                    LogSettingError("VadDebounceTimeoutMs", $"OUT OF RANGE (expected 50-1000, got {value})");
                return value;
            }
            catch (Exception ex)
            {
                LogSettingError("VadDebounceTimeoutMs", $"READ FAILED: {ex.Message}");
                return 200; // Default 200ms debounce
            }
        }

        /// <summary>
        /// Save VAD debounce timeout in milliseconds
        /// </summary>
        public static void SaveVadDebounceTimeoutMs(int timeoutMs)
        {
            try
            {
                SetInt("VadDebounceTimeoutMs", timeoutMs);
                Console.WriteLine($"VAD: Saved debounce timeout: {timeoutMs}ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving VAD debounce timeout: {ex.Message}");
            }
        }

        public static float LoadVoiceConfidenceThreshold()
        {
            try
            {
                var value = GetFloat("VoiceConfidenceThreshold");
                if (value < 0f || value > 1f)
                    LogSettingError("VoiceConfidenceThreshold", $"OUT OF RANGE (expected 0.0-1.0, got {value:F2})");
                return value;
            }
            catch (Exception ex)
            {
                LogSettingError("VoiceConfidenceThreshold", $"READ FAILED: {ex.Message}");
                return 0f;
            }
        }

        public static void SaveVoiceConfidenceThreshold(float threshold)
        {
            try
            {
                SetFloat("VoiceConfidenceThreshold", threshold);
                Console.WriteLine($"Voice: Saved confidence threshold: {threshold:F2}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving voice confidence threshold: {ex.Message}");
            }
        }

        public static float LoadVoiceHighConfidenceThreshold()
        {
            try
            {
                var value = GetFloat("VoiceHighConfidenceThreshold");
                if (value < 0f || value > 1f)
                    LogSettingError("VoiceHighConfidenceThreshold", $"OUT OF RANGE (expected 0.0-1.0, got {value:F2})");
                var min = GetFloat("VoiceConfidenceThreshold");
                if (value < min)
                    LogSettingError("VoiceHighConfidenceThreshold", $"LESS THAN VoiceConfidenceThreshold (high={value:F2}, low={min:F2})");
                return value;
            }
            catch (Exception ex)
            {
                LogSettingError("VoiceHighConfidenceThreshold", $"READ FAILED: {ex.Message}");
                return 0f;
            }
        }

        public static void SaveVoiceHighConfidenceThreshold(float threshold)
        {
            try
            {
                SetFloat("VoiceHighConfidenceThreshold", threshold);
                Console.WriteLine($"Voice: Saved high confidence threshold: {threshold:F2}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving voice high confidence threshold: {ex.Message}");
            }
        }

        public static int LoadVoiceConfidenceBufferSize()
        {
            try
            {
                var size = GetInt("VoiceConfidenceBufferSize");
                if (size < 1 || size > 10)
                    LogSettingError("VoiceConfidenceBufferSize", $"OUT OF RANGE (expected 1-10, got {size})");
                return size;
            }
            catch (Exception ex)
            {
                LogSettingError("VoiceConfidenceBufferSize", $"READ FAILED: {ex.Message}");
                return 0;
            }
        }

        public static void SaveVoiceConfidenceBufferSize(int bufferSize)
        {
            try
            {
                SetInt("VoiceConfidenceBufferSize", bufferSize);
                Console.WriteLine($"Voice: Saved confidence buffer size: {bufferSize}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving voice confidence buffer size: {ex.Message}");
            }
        }

        public static bool LoadVoiceConfidenceLoggingEnabled()
        {
            try
            {
                return GetBool("VoiceConfidenceLoggingEnabled");
            }
            catch (Exception ex)
            {
                LogSettingError("VoiceConfidenceLoggingEnabled", $"READ FAILED: {ex.Message}");
                return false;
            }
        }

        public static void SaveVoiceConfidenceLoggingEnabled(bool enabled)
        {
            try
            {
                SetBool("VoiceConfidenceLoggingEnabled", enabled);
                Console.WriteLine($"Voice: Saved confidence logging enabled: {enabled}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving voice confidence logging enabled: {ex.Message}");
            }
        }

        // Audio Device Settings
        /// <summary>
        /// Load STT microphone input device setting
        /// </summary>
        public static string LoadSttInputDevice()
        {
            try
            {
                var deviceName = GetString("SttInputDevice");
                if (string.IsNullOrWhiteSpace(deviceName))
                {
                    LogSettingError("SttInputDevice", "EMPTY");
                }
                else if (!string.Equals(deviceName, "Default", StringComparison.OrdinalIgnoreCase))
                {
                    var exists = AudioDeviceManager.GetInputDevices().Any(d => string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
                    if (!exists)
                        LogSettingError("SttInputDevice", $"NOT FOUND '{deviceName}'");
                }
                return deviceName;
            }
            catch (Exception ex)
            {
                LogSettingError("SttInputDevice", $"READ FAILED: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Save STT microphone input device setting
        /// </summary>
        public static void SaveSttInputDevice(string deviceName)
        {
            try
            {
                SetString("SttInputDevice", deviceName);
                Console.WriteLine($"STT: Saved input device: {deviceName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving STT input device: {ex.Message}");
            }
        }

        /// <summary>
        /// Load TTS audio output device setting
        /// </summary>
        public static string LoadTtsOutputDevice()
        {
            try
            {
                var deviceName = GetString("TtsOutputDevice");
                if (string.IsNullOrWhiteSpace(deviceName))
                {
                    LogSettingError("TtsOutputDevice", "EMPTY");
                }
                else if (!string.Equals(deviceName, "Default", StringComparison.OrdinalIgnoreCase))
                {
                    var exists = AudioDeviceManager.GetOutputDevices().Any(d => string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
                    if (!exists)
                        LogSettingError("TtsOutputDevice", $"NOT FOUND '{deviceName}'");
                }
                return deviceName;
            }
            catch (Exception ex)
            {
                LogSettingError("TtsOutputDevice", $"READ FAILED: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Save TTS audio output device setting
        /// </summary>
        public static void SaveTtsOutputDevice(string deviceName)
        {
            try
            {
                SetString("TtsOutputDevice", deviceName);
                Console.WriteLine($"TTS: Saved output device: {deviceName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving TTS output device: {ex.Message}");
            }
        }

        // Discord Bot Settings
        /// <summary>
        /// Load Discord bot token
        /// </summary>
        public static string LoadDiscordBotToken()
        {
            try
            {
                var token = GetString("DiscordBotToken");
                if (string.IsNullOrWhiteSpace(token))
                    LogSettingError("DiscordBotToken", "EMPTY");
                return token;
            }
            catch (Exception ex)
            {
                LogSettingError("DiscordBotToken", $"READ FAILED: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Save Discord bot token
        /// </summary>
        public static void SaveDiscordBotToken(string token)
        {
            try
            {
                SetString("DiscordBotToken", token);
                Console.WriteLine($"Discord: Saved bot token (length: {(token?.Length ?? 0)} characters)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving Discord bot token: {ex.Message}");
            }
        }

        /// <summary>
        /// Load Discord bot enabled status
        /// </summary>
        public static bool LoadDiscordBotEnabled()
        {
            try
            {
                return GetBool("DiscordBotEnabled");
            }
            catch (Exception ex)
            {
                LogSettingError("DiscordBotEnabled", $"READ FAILED: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Save Discord bot enabled status
        /// </summary>
        public static void SaveDiscordBotEnabled(bool enabled)
        {
            try
            {
                SetBool("DiscordBotEnabled", enabled);
                Console.WriteLine($"Discord: Saved bot enabled: {enabled}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving Discord bot enabled: {ex.Message}");
            }
        }

        /// <summary>
        /// Load Discord bot command prefix
        /// </summary>
        public static string LoadDiscordBotPrefix()
        {
            try
            {
                var prefix = GetString("DiscordBotPrefix");
                if (string.IsNullOrWhiteSpace(prefix))
                    LogSettingError("DiscordBotPrefix", "EMPTY");
                return prefix;
            }
            catch (Exception ex)
            {
                LogSettingError("DiscordBotPrefix", $"READ FAILED: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Save Discord bot command prefix
        /// </summary>
        public static void SaveDiscordBotPrefix(string prefix)
        {
            try
            {
                SetString("DiscordBotPrefix", prefix);
                Console.WriteLine($"Discord: Saved bot prefix: {prefix}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving Discord bot prefix: {ex.Message}");
            }
        }

        /// <summary>
        /// Load Discord auto-join voice channels setting
        /// </summary>
        public static bool LoadDiscordAutoJoinVoice()
        {
            try
            {
                return GetBool("DiscordAutoJoinVoice");
            }
            catch (Exception ex)
            {
                LogSettingError("DiscordAutoJoinVoice", $"READ FAILED: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Save Discord auto-join voice channels setting
        /// </summary>
        public static void SaveDiscordAutoJoinVoice(bool autoJoin)
        {
            try
            {
                SetBool("DiscordAutoJoinVoice", autoJoin);
                Console.WriteLine($"Discord: Saved auto-join voice: {autoJoin}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving Discord auto-join voice: {ex.Message}");
            }
        }

        // Face Recognition Settings
        public static float LoadFaceThreshold()
        {
            try
            {
                var value = GetFloat("FaceThreshold");
                if (value < 0f || value > 1f)
                    LogSettingError("FaceThreshold", $"OUT OF RANGE (expected 0.0-1.0, got {value:F2})");
                return value;
            }
            catch (Exception ex)
            {
                LogSettingError("FaceThreshold", $"READ FAILED: {ex.Message}");
                return 0f;
            }
        }

        public static void SaveFaceThreshold(float threshold)
        {
            try
            {
                SetFloat("FaceThreshold", threshold);
                Console.WriteLine($"Saved face threshold: {threshold:F2}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving face threshold: {ex.Message}");
            }
        }

        // Ollama AI Settings
        public static string LoadOllamaModel()
        {
            try
            {
                var model = GetString("OllamaModel");
                if (string.IsNullOrWhiteSpace(model))
                    LogSettingError("OllamaModel", "EMPTY");
                return model;
            }
            catch (Exception ex)
            {
                LogSettingError("OllamaModel", $"READ FAILED: {ex.Message}");
                return null;
            }
        }

        public static void SaveOllamaModel(string model)
        {
            try
            {
                SetString("OllamaModel", model);
                Console.WriteLine($"Saved Ollama model: {model}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving Ollama model: {ex.Message}");
            }
        }

        public static bool LoadOllamaEnabled()
        {
            try
            {
                return GetBool("OllamaEnabled");
            }
            catch (Exception ex)
            {
                LogSettingError("OllamaEnabled", $"READ FAILED: {ex.Message}");
                return false;
            }
        }

        public static void SaveOllamaEnabled(bool enabled)
        {
            try
            {
                SetBool("OllamaEnabled", enabled);
                Console.WriteLine($"Saved Ollama enabled: {enabled}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving Ollama enabled: {ex.Message}");
            }
        }

        /// <summary>
        /// Load Ollama maximum messages per speaker
        /// </summary>
        public static int LoadOllamaMaxMessagesPerSpeaker()
        {
            try
            {
                var value = GetInt("OllamaMaxMessagesPerSpeaker");
                if (value <= 0 || value > 100)
                    LogSettingError("OllamaMaxMessagesPerSpeaker", $"OUT OF RANGE (expected 1-100, got {value})");
                return value > 0 ? value : 20; // Default to 20 if invalid
            }
            catch (Exception ex)
            {
                LogSettingError("OllamaMaxMessagesPerSpeaker", $"READ FAILED: {ex.Message}");
                return 20; // Default value
            }
        }

        /// <summary>
        /// Load Ollama maximum system messages
        /// </summary>
        public static int LoadOllamaMaxSystemMessages()
        {
            try
            {
                var value = GetInt("OllamaMaxSystemMessages");
                if (value <= 0 || value > 10)
                    LogSettingError("OllamaMaxSystemMessages", $"OUT OF RANGE (expected 1-10, got {value})");
                return value > 0 ? value : 3; // Default to 3 if invalid
            }
            catch (Exception ex)
            {
                LogSettingError("OllamaMaxSystemMessages", $"READ FAILED: {ex.Message}");
                return 3; // Default value
            }
        }

        /// <summary>
        /// Load Ollama conversation timeout in minutes
        /// </summary>
        public static int LoadOllamaConversationTimeoutMinutes()
        {
            try
            {
                var value = GetInt("OllamaConversationTimeoutMinutes");
                if (value <= 0 || value > 1440)
                    LogSettingError("OllamaConversationTimeoutMinutes", $"OUT OF RANGE (expected 1-1440, got {value})");
                return value > 0 ? value : 30; // Default to 30 minutes if invalid
            }
            catch (Exception ex)
            {
                LogSettingError("OllamaConversationTimeoutMinutes", $"READ FAILED: {ex.Message}");
                return 30; // Default value
            }
        }

        public static bool LoadOllamaMemoryEnabled()
        {
            try
            {
                return GetBool("OllamaMemoryEnabled");
            }
            catch (Exception ex)
            {
                LogSettingError("OllamaMemoryEnabled", $"READ FAILED: {ex.Message}");
                return false;
            }
        }

        public static void SaveOllamaMemoryEnabled(bool enabled)
        {
            try
            {
                SetBool("OllamaMemoryEnabled", enabled);
                Console.WriteLine($"Ollama: Saved memory enabled: {enabled}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving Ollama memory enabled: {ex.Message}");
            }
        }

        // Conversation History Settings
        public static string LoadConversationHistoryPath()
        {
            try
            {
                var path = GetString("ConversationHistoryPath");
                if (string.IsNullOrWhiteSpace(path))
                {
                    LogSettingError("ConversationHistoryPath", "EMPTY");
                    return "history"; // Default fallback
                }
                else
                {
                    if (!Directory.Exists(path))
                        LogSettingError("ConversationHistoryPath", $"NOT FOUND '{path}'");
                }
                return path;
            }
            catch (Exception ex)
            {
                LogSettingError("ConversationHistoryPath", $"READ FAILED: {ex.Message}");
                return "history"; // Default fallback
            }
        }

        public static void SaveConversationHistoryPath(string path)
        {
            try
            {
                SetString("ConversationHistoryPath", path);
                Console.WriteLine($"Saved conversation history path: {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving conversation history path: {ex.Message}");
            }
        }

        // System Prompt Settings
        public static string LoadSystemPromptPath()
        {
            try
            {
                var path = GetString("SystemPromptPath");
                if (string.IsNullOrWhiteSpace(path))
                {
                    LogSettingError("SystemPromptPath", "EMPTY");
                }
                else if (!File.Exists(path))
                {
                    LogSettingError("SystemPromptPath", "NOT FOUND");
                }
                return path;
            }
            catch (Exception ex)
            {
                LogSettingError("SystemPromptPath", $"READ FAILED: {ex.Message}");
                return null;
            }
        }

        public static void SaveSystemPromptPath(string path)
        {
            try
            {
                SetString("SystemPromptPath", path);
                Console.WriteLine($"Saved system prompt path: {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving system prompt path: {ex.Message}");
            }
        }

        // TTS Settings
        public static bool LoadTtsEnabled()
        {
            try
            {
                return GetBool("TtsEnabled");
            }
            catch (Exception ex)
            {
                LogSettingError("TtsEnabled", $"READ FAILED: {ex.Message}");
                return false;
            }
        }

        public static void SaveTtsEnabled(bool enabled)
        {
            try
            {
                SetBool("TtsEnabled", enabled);
                Console.WriteLine($"TTS: Saved TTS enabled: {enabled}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving TTS enabled: {ex.Message}");
            }
        }

        public static string LoadTtsModelFolder()
        {
            try
            {
                var folder = GetString("ttsmodelfolder");
                if (string.IsNullOrWhiteSpace(folder))
                {
                    // Default folder for Kokoro
                    folder = Path.Combine("models", "tts", "kokoro");
                }
                return folder;
            }
            catch (Exception ex)
            {
                LogSettingError("ttsmodelfolder", $"READ FAILED: {ex.Message}");
                return null;
            }
        }

        public static void SaveTtsModelFolder(string folder)
        {
            try
            {
                SetString("ttsmodelfolder", folder);
                Console.WriteLine($"TTS: Saved TTS model folder: {folder}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving TTS model folder: {ex.Message}");
            }
        }

        public static string LoadTtsModelPath()
        {
            try
            {
                var path = GetString("TtsModelPath");
                if (string.IsNullOrWhiteSpace(path))
                {
                    // Default to Kokoro-82M ONNX path without error noise
                    path = Path.Combine("models", "tts", "kokoro-82M", "onnx", "model_q8f16.onnx");
                }
                // Do not log errors if file doesn't exist; Kokoro service handles this gracefully
                return path;
            }
            catch (Exception ex)
            {
                LogSettingError("TtsModelPath", $"READ FAILED: {ex.Message}");
                return null;
            }
        }

        public static void SaveTtsModelPath(string path)
        {
            try
            {
                SetString("TtsModelPath", path);
                Console.WriteLine($"TTS: Saved TTS model path: {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving TTS model path: {ex.Message}");
            }
        }

        public static string LoadTtsCmudictPath()
        {
            try
            {
                // Kokoro doesn't require CMU dict; return as-is without error logging
                return GetString("TtsCmudictPath");
            }
            catch (Exception ex)
            {
                LogSettingError("TtsCmudictPath", $"READ FAILED: {ex.Message}");
                return null;
            }
        }

        public static void SaveTtsCmudictPath(string path)
        {
            try
            {
                SetString("TtsCmudictPath", path);
                Console.WriteLine($"TTS: Saved CMU dict path: {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving CMU dict path: {ex.Message}");
            }
        }

        public static string LoadTtsSymbolsPath()
        {
            try
            {
                // Kokoro doesn't require symbols; return as-is without error logging
                return GetString("TtsSymbolsPath");
            }
            catch (Exception ex)
            {
                LogSettingError("TtsSymbolsPath", $"READ FAILED: {ex.Message}");
                return null;
            }
        }

        public static void SaveTtsSymbolsPath(string path)
        {
            try
            {
                SetString("TtsSymbolsPath", path);
                Console.WriteLine($"TTS: Saved symbols path: {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving symbols path: {ex.Message}");
            }
        }

        public static string LoadTtsVocoderModelPath()
        {
            try
            {
                var path = GetString("TtsVocoderModelPath");
                if (string.IsNullOrWhiteSpace(path))
                {
                    // Default to Kokoro-82M ONNX path without error noise
                    path = Path.Combine("models", "tts", "kokoro-82M", "onnx", "model_q8f16.onnx");
                }
                // Do not log errors if file doesn't exist; Kokoro service handles this gracefully
                return path;
            }
            catch (Exception ex)
            {
                LogSettingError("TtsVocoderModelPath", $"READ FAILED: {ex.Message}");
                return null;
            }
        }

        public static void SaveTtsVocoderModelPath(string path)
        {
            try
            {
                SetString("TtsVocoderModelPath", path);
                Console.WriteLine($"TTS: Saved vocoder model path: {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving vocoder model path: {ex.Message}");
            }
        }

        public static string LoadTtsSpeaker()
        {
            try
            {
                var speaker = GetString("TtsSpeaker");
                if (string.IsNullOrWhiteSpace(speaker))
                {
                    // Default to Kokoro's English male voice if not set
                    speaker = "em_alex";
                    SetString("TtsSpeaker", speaker);
                }
                // No numeric validation: Kokoro uses named voice keys like 'em_alex'
                return speaker;
            }
            catch (Exception ex)
            {
                LogSettingError("TtsSpeaker", $"READ FAILED: {ex.Message}");
                return "em_alex";
            }
        }

        public static void SaveTtsSpeaker(string speaker)
        {
            try
            {
                SetString("TtsSpeaker", speaker);
                Console.WriteLine($"TTS: Saved TTS speaker: {speaker}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving TTS speaker: {ex.Message}");
            }
        }

        public static bool LoadTtsUseGpu()
        {
            try
            {
                // Prefer exact settings.settings key: 'ttsusegpu'
                var v = GetBool("ttsusegpu");
                if (v) return true; // if true, short-circuit
                if (!v)
                {
                    // If false could be default; try legacy key as fallback
                    var legacy = GetBool("TtsUseGpu");
                    return legacy;
                }
                return v;
            }
            catch (Exception ex)
            {
                LogSettingError("ttsusegpu", $"READ FAILED: {ex.Message}");
                return false;
            }
        }

        public static void SaveTtsUseGpu(bool useGpu)
        {
            try
            {
                // Write primary key as requested
                SetBool("ttsusegpu", useGpu);
                // Also write legacy key for backward compatibility
                SetBool("TtsUseGpu", useGpu);
                Console.WriteLine($"TTS: Saved TTS GPU preference: {useGpu}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving TTS GPU preference: {ex.Message}");
            }
        }

        // GPU device selection (CUDA)
        public static int LoadTtsGpuDeviceId()
        {
            try
            {
                // Prefer exact key 'ttsgpudeviceid', fallback to 'TtsGpuDeviceId'
                var id = GetInt("ttsgpudeviceid", 0);
                if (id == 0)
                {
                    var legacy = GetInt("TtsGpuDeviceId", 0);
                    if (legacy != 0) id = legacy;
                }
                if (id < 0) id = 0;
                return id;
            }
            catch (Exception ex)
            {
                LogSettingError("ttsgpudeviceid", $"READ FAILED: {ex.Message}");
                return 0;
            }
        }

        public static void SaveTtsGpuDeviceId(int deviceId)
        {
            try
            {
                if (deviceId < 0) deviceId = 0;
                SetInt("ttsgpudeviceid", deviceId);
                // Also write legacy for compatibility
                SetInt("TtsGpuDeviceId", deviceId);
                Console.WriteLine($"TTS: Saved CUDA GPU device id: {deviceId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving CUDA GPU device id: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Load local TTS volume setting (0.0 to 1.0)
        /// </summary>
        public static double LoadLocalTtsVolume()
        {
            try
            {
                var value = GetDouble("LocalTtsVolume");
                if (value < 0.0 || value > 1.0)
                    LogSettingError("LocalTtsVolume", $"OUT OF RANGE (expected 0.0-1.0, got {value:F3})");
                return value;
            }
            catch (Exception ex)
            {
                LogSettingError("LocalTtsVolume", $"READ FAILED: {ex.Message}");
                return 0.0;
            }
        }

        /// <summary>
        /// Save local TTS volume setting (0.0 to 1.0)
        /// </summary>
        public static void SaveLocalTtsVolume(double volume)
        {
            try
            {
                SetDouble("LocalTtsVolume", volume);
                Console.WriteLine($"TTS: Saved local volume: {volume * 100:F0}%");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving local TTS volume: {ex.Message}");
            }
        }

        /// <summary>
        /// Load Discord TTS volume setting (0.0 to 1.0)
        /// </summary>
        public static double LoadDiscordTtsVolume()
        {
            try
            {
                var value = GetDouble("DiscordTtsVolume");
                if (value < 0.0 || value > 1.0)
                    LogSettingError("DiscordTtsVolume", $"OUT OF RANGE (expected 0.0-1.0, got {value:F3})");
                return value;
            }
            catch (Exception ex)
            {
                LogSettingError("DiscordTtsVolume", $"READ FAILED: {ex.Message}");
                return 0.0;
            }
        }

        /// <summary>
        /// Save Discord TTS volume setting (0.0 to 1.0)
        /// </summary>
        public static void SaveDiscordTtsVolume(double volume)
        {
            try
            {
                SetDouble("DiscordTtsVolume", volume);
                Console.WriteLine($"TTS: Saved Discord volume: {volume * 100:F0}%");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving Discord TTS volume: {ex.Message}");
            }
        }

        /// <summary>
        /// Load barge-in setting (allows ASR to interrupt TTS when enabled)
        /// </summary>
        public static bool LoadBargeInEnabled()
        {
            try
            {
                // Default to true unless explicitly disabled in settings
                return GetBool("BargeInEnabled", true);
            }
            catch (Exception ex)
            {
                LogSettingError("BargeInEnabled", $"READ FAILED: {ex.Message}");
                return true; // Default: barge-in enabled if read fails
            }
        }

        /// <summary>
        /// Save barge-in setting
        /// </summary>
        public static void SaveBargeInEnabled(bool enabled)
        {
            try
            {
                SetBool("BargeInEnabled", enabled);
                Console.WriteLine($"ASR: Saved barge-in enabled: {enabled}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving barge-in setting: {ex.Message}");
            }
        }

        /// <summary>
        /// Load wake word requirement setting (when true, require wake word to dispatch)
        /// </summary>
        public static bool LoadWakeWordRequired()
        {
            try
            {
                return GetBool("WakeWordRequired");
            }
            catch (Exception ex)
            {
                LogSettingError("WakeWordRequired", $"READ FAILED: {ex.Message}");
                return false; // default disabled
            }
        }

        /// <summary>
        /// Save wake word requirement setting
        /// </summary>
        public static void SaveWakeWordRequired(bool required)
        {
            try
            {
                SetBool("WakeWordRequired", required);
                Console.WriteLine($"ASR: Saved wake word required: {required}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving wake word requirement: {ex.Message}");
            }
        }

        // UI Theme Settings
        public static bool LoadDarkMode()
        {
            try
            {
                return GetBool("DarkMode");
            }
            catch (Exception ex)
            {
                LogSettingError("DarkMode", $"READ FAILED: {ex.Message}");
                return false;
            }
        }

        public static void SaveDarkMode(bool darkMode)
        {
            try
            {
                SetBool("DarkMode", darkMode);
                Console.WriteLine($"Saved dark mode: {darkMode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving dark mode: {ex.Message}");
            }
        }

        // Language Settings
        public static string LoadLanguage()
        {
            try
            {
                var language = GetString("Language", "en-US");
                if (string.IsNullOrWhiteSpace(language))
                {
                    language = "en-US";
                    SetString("Language", language);
                }
                return language;
            }
            catch (Exception ex)
            {
                LogSettingError("Language", $"READ FAILED: {ex.Message}");
                return "en-US";
            }
        }

        public static void SaveLanguage(string language)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(language))
                    language = "en-US";
                
                SetString("Language", language);
                Console.WriteLine($"Saved language: {language}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving language: {ex.Message}");
            }
        }

        // Telemetry Level Settings
        public static TelemetryLevel LoadTelemetryLevel()
        {
            try
            {
                var levelStr = GetString("TelemetryLevel", "Info");
                if (Enum.TryParse<TelemetryLevel>(levelStr, true, out var level))
                {
                    return level;
                }
                return TelemetryLevel.Info;
            }
            catch (Exception ex)
            {
                LogSettingError("TelemetryLevel", $"READ FAILED: {ex.Message}");
                return TelemetryLevel.Info;
            }
        }

        public static void SaveTelemetryLevel(TelemetryLevel level)
        {
            try
            {
                SetString("TelemetryLevel", level.ToString());
                Console.WriteLine($"Saved telemetry level: {level}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving telemetry level: {ex.Message}");
            }
        }

        // Window Settings
        public static (double width, double height, double left, double top, string state) LoadWindowSettings()
        {
            try
            {
                double width = GetDouble("WindowWidth");
                double height = GetDouble("WindowHeight");
                double left = GetDouble("WindowLeft");
                double top = GetDouble("WindowTop");
                string state = GetString("WindowState");

                if (width < 300) LogSettingError("WindowWidth", $"OUT OF RANGE (value {width:F0} < 300)");
                if (height < 200) LogSettingError("WindowHeight", $"OUT OF RANGE (value {height:F0} < 200)");
                if (left < 0) LogSettingError("WindowLeft", $"OUT OF RANGE (value {left:F0} < 0)");
                if (top < 0) LogSettingError("WindowTop", $"OUT OF RANGE (value {top:F0} < 0)");

                if (string.IsNullOrWhiteSpace(state))
                {
                    LogSettingError("WindowState", "EMPTY");
                }
                else
                {
                    var valid = new[] { "Normal", "Maximized", "Minimized" };
                    if (!valid.Contains(state))
                        LogSettingError("WindowState", $"INVALID '{state}' (expected Normal/Maximized/Minimized)");
                }

                return (width, height, left, top, state);
            }
            catch (Exception ex)
            {
                LogSettingError("WindowSettings", $"READ FAILED: {ex.Message}");
                return (0, 0, 0, 0, null);
            }
        }

        public static void SaveWindowSettings(double width, double height, double left, double top, string state)
        {
            try
            {
                SetDouble("WindowWidth", width);
                SetDouble("WindowHeight", height);
                SetDouble("WindowLeft", left);
                SetDouble("WindowTop", top);
                SetString("WindowState", state);
                Console.WriteLine($"Saved window settings: {width:F0}x{height:F0} at ({left:F0},{top:F0}) state={state}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving window settings: {ex.Message}");
            }
        }

        public static (double width, double height) GetDefaultWindowSize()
        {
            return (900, 900);
        }

        // Legacy compatibility methods
        [Obsolete("Use InitializeSettingsOnStartup() instead")]
        public static void InitializeDefaultSettings()
        {
            InitializeSettingsOnStartup();
        }

        // Voice confidence helper methods
        public static void ConfigureVoiceConfidenceSettings(float confidenceThreshold = 0.5f, float highConfidenceThreshold = 0.8f, int bufferSize = 3, bool enableLogging = true)
        {
            try
            {
                SaveVoiceConfidenceThreshold(confidenceThreshold);
                SaveVoiceHighConfidenceThreshold(highConfidenceThreshold);
                SaveVoiceConfidenceBufferSize(bufferSize);
                SaveVoiceConfidenceLoggingEnabled(enableLogging);

                Console.WriteLine($"🎤 Voice confidence settings configured:");
                Console.WriteLine($"   Confidence threshold: {confidenceThreshold:F2} (min quality level)");
                Console.WriteLine($"   High confidence threshold: {highConfidenceThreshold:F2} (high quality level)");
                Console.WriteLine($"   Buffer size: {bufferSize} (low confidence recovery)");
                Console.WriteLine($"   Logging enabled: {enableLogging}");
                Console.WriteLine($"   💡 Higher thresholds = better quality but may miss some speech");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error configuring voice confidence settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Configure Voice Activity Detection settings for improved speech onset detection
        /// </summary>
        public static void ConfigureVoiceActivitySettings(float vadThreshold = 300f, float discordVadThreshold = 25f, int silenceTimeoutMs = 150, int debounceTimeoutMs = 200)
        {
            try
            {
                SaveVoiceActivityThreshold(vadThreshold);
                SaveDiscordVoiceActivityThreshold(discordVadThreshold);
                SaveVadSilenceTimeoutMs(silenceTimeoutMs);
                SaveVadDebounceTimeoutMs(debounceTimeoutMs);
                AudioUtils.RefreshVadThreshold(); // Update cached value immediately

                Console.WriteLine($"🎙️ Voice Activity Detection configured:");
                Console.WriteLine($"   Microphone VAD threshold: {vadThreshold:F0} (RMS level for voice detection)");
                Console.WriteLine($"   Discord VAD threshold: {discordVadThreshold:F0} (RMS level for Discord audio)");
                Console.WriteLine($"   Silence timeout: {silenceTimeoutMs}ms (time before finalizing transcription)");
                Console.WriteLine($"   Debounce timeout: {debounceTimeoutMs}ms (prevents double-finalization)");
                Console.WriteLine($"   💡 Lower VAD values = more sensitive (catches quiet speech start)");
                Console.WriteLine($"   💡 Higher VAD values = less sensitive (reduces false positives)");
                Console.WriteLine($"   💡 Discord threshold should be lower due to audio compression");
                Console.WriteLine($"   💡 Recommended ranges: Microphone 200-500, Discord 15-50");
                Console.WriteLine($"   💡 Recommended timeouts: Silence 100-300ms, Debounce 100-500ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error configuring VAD settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Configure audio device settings for STT and TTS
        /// </summary>
        public static void ConfigureAudioDeviceSettings(string sttInputDevice = "Default", string ttsOutputDevice = "Default")
        {
            try
            {
                SaveSttInputDevice(sttInputDevice);
                SaveTtsOutputDevice(ttsOutputDevice);

                Console.WriteLine($"🎧 Audio device settings configured:");
                Console.WriteLine($"   STT input device: {sttInputDevice}");
                Console.WriteLine($"   TTS output device: {ttsOutputDevice}");
                Console.WriteLine($"   💡 Use device names from the Audio Device Manager");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error configuring audio device settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Configure Discord bot settings all at once
        /// </summary>
        public static void ConfigureDiscordBotSettings(string token, bool enabled = true, string prefix = "!", bool autoJoinVoice = false)
        {
            // Backwards compatibility - call the new method
            ConfigureDiscordSettings(token, enabled, prefix, autoJoinVoice, 25f);
        }

        /// <summary>
        /// Configure Discord-specific settings all at once
        /// </summary>
        public static void ConfigureDiscordSettings(string token, bool enabled = true, string prefix = "!", bool autoJoinVoice = false, float discordVadThreshold = 150f)
        {
            try
            {
                SaveDiscordBotToken(token);
                SaveDiscordBotEnabled(enabled);
                SaveDiscordBotPrefix(prefix);
                SaveDiscordAutoJoinVoice(autoJoinVoice);
                SaveDiscordVoiceActivityThreshold(discordVadThreshold);

                Console.WriteLine($"🤖 Discord settings configured:");
                Console.WriteLine($"   Bot enabled: {enabled}");
                Console.WriteLine($"   Token configured: {!string.IsNullOrEmpty(token)}");
                Console.WriteLine($"   Command prefix: {prefix}");
                Console.WriteLine($"   Auto-join voice: {autoJoinVoice}");
                Console.WriteLine($"   Discord VAD threshold: {discordVadThreshold:F0} (voice activity detection for normalized audio)");
                Console.WriteLine($"   💡 Discord audio is normalized to microphone levels for better STT");
                Console.WriteLine($"   💡 Lower VAD threshold = more sensitive Discord voice detection");
                Console.WriteLine($"   💡 Recommended range: 100-300 for normalized Discord audio");
                Console.WriteLine($"   💡 Use DiscordBotManager.StartAsync() to start the bot");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error configuring Discord settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Get Discord bot settings summary
        /// </summary>
        public static string GetDiscordBotSettingsSummary()
        {
            try
            {
                var enabled = LoadDiscordBotEnabled();
                var token = LoadDiscordBotToken();
                var prefix = LoadDiscordBotPrefix();
                var autoJoin = LoadDiscordAutoJoinVoice();
                var discordVadThreshold = LoadDiscordVoiceActivityThreshold();

                return $"🤖 Discord Bot Settings:\n" +
                       $"   Enabled: {enabled}\n" +
                       $"   Token configured: {(string.IsNullOrEmpty(token) ? "No" : "Yes")}\n" +
                       $"   Command prefix: {prefix}\n" +
                       $"   Auto-join voice: {autoJoin}\n" +
                       $"   Discord VAD threshold: {discordVadThreshold:F0} (voice activity detection)\n" +
                       $"   Discord VAD sensitivity: {(discordVadThreshold <= 15f ? "High" : discordVadThreshold <= 35f ? "Medium" : "Low")}\n" +
                       $"   Integration: {(enabled && !string.IsNullOrEmpty(token) ? "Ready" : "Not configured")}";
            }
            catch (Exception ex)
            {
                return $"ERROR: Could not load Discord bot settings: {ex.Message}";
            }
        }

        public static string GetVoiceConfidenceSettingsSummary()
        {
            try
            {
                var threshold = LoadVoiceConfidenceThreshold();
                var highThreshold = LoadVoiceHighConfidenceThreshold();
                var bufferSize = LoadVoiceConfidenceBufferSize();
                var loggingEnabled = LoadVoiceConfidenceLoggingEnabled();
                var vadThreshold = LoadVoiceActivityThreshold();
                var discordVadThreshold = LoadDiscordVoiceActivityThreshold();
                var silenceTimeoutMs = LoadVadSilenceTimeoutMs();
                var debounceTimeoutMs = LoadVadDebounceTimeoutMs();
                var telemetryEnabled = LoadTelemetryEnabled();
                var telemetryFile = LoadTelemetryFile();

                return $"🎤 Voice Recognition Settings:\n" +
                       $"   Microphone VAD threshold: {vadThreshold:F0} (voice activity detection)\n" +
                       $"   Discord VAD threshold: {discordVadThreshold:F0} (Discord voice activity detection)\n" +
                       $"   VAD silence timeout: {silenceTimeoutMs}ms (finalization delay)\n" +
                       $"   VAD debounce timeout: {debounceTimeoutMs}ms (double-finalization prevention)\n" +
                       $"   Confidence threshold: {threshold:F2} (minimum quality)\n" +
                       $"   High confidence threshold: {highThreshold:F2} (high quality)\n" +
                       $"   Buffer size: {bufferSize} (recovery attempts)\n" +
                       $"   Logging enabled: {loggingEnabled}\n" +
                       $"   Quality level: {(threshold >= 0.5f ? "High" : threshold >= 0.3f ? "Medium" : "Low")}\n" +
                       $"   Microphone VAD sensitivity: {(vadThreshold <= 200f ? "High" : vadThreshold <= 400f ? "Medium" : "Low")}\n" +
                       $"   Discord VAD sensitivity: {(discordVadThreshold <= 15f ? "High" : discordVadThreshold <= 35f ? "Medium" : "Low")}\n" +
                       $"   Telemetry enabled: {telemetryEnabled}\n" +
                       $"   Telemetry file: {telemetryFile}";
            }
            catch (Exception ex)
            {
                return $"ERROR: Could not load voice settings: {ex.Message}";
            }
        }

        /// <summary>
        /// Get audio device settings summary
        /// </summary>
        public static string GetAudioDeviceSettingsSummary()
        {
            try
            {
                var sttInputDevice = LoadSttInputDevice();
                var ttsOutputDevice = LoadTtsOutputDevice();
                var audioMode = LoadAudioInMode();

                return $"🎧 Audio Device Settings:\n" +
                       $"   STT Input Device: {sttInputDevice}\n" +
                       $"   TTS Output Device: {ttsOutputDevice}\n" +
                       $"   Audio Input Mode: {audioMode}";
            }
            catch (Exception ex)
            {
                return $"ERROR: Could not load audio device settings: {ex.Message}";
            }
        }

        /// <summary>
        /// Demo/test method to show available audio devices and current configuration
        /// </summary>
        public static void ShowAudioDeviceDemo()
        {
            try
            {
                Console.WriteLine("🎧🎤🔊 === AUDIO DEVICE CONFIGURATION DEMO ===");

                // Show all available input devices
                Console.WriteLine("\n🎤 Available STT Input Devices (Microphones):");
                var inputDevices = AudioDeviceManager.GetInputDevices();
                if (inputDevices.Any())
                {
                    for (int i = 0; i < inputDevices.Count; i++)
                    {
                        var device = inputDevices[i];
                        var testResult = AudioDeviceManager.TestInputDevice(device.DeviceNumber);
                        var statusIcon = testResult ? "✅" : "❌";
                        var defaultIcon = device.IsDefault ? " 🌟" : "";

                        Console.WriteLine($"   [{device.DeviceNumber}] {device.DeviceName}{defaultIcon} {statusIcon}");
                        Console.WriteLine($"       Channels: {device.Channels}, Product: {device.ProductName}");
                    }
                }
                else
                {
                    Console.WriteLine("   ❌ No input devices found");
                }

                // Show all available output devices
                Console.WriteLine("\n🔊 Available TTS Output Devices (Speakers/Headphones):");
                var outputDevices = AudioDeviceManager.GetOutputDevices();
                if (outputDevices.Any())
                {
                    for (int i = 0; i < outputDevices.Count; i++)
                    {
                        var device = outputDevices[i];
                        var testResult = AudioDeviceManager.TestOutputDevice(device.DeviceNumber);
                        var statusIcon = testResult ? "✅" : "❌";
                        var defaultIcon = device.IsDefault ? " 🌟" : "";

                        Console.WriteLine($"   [{device.DeviceNumber}] {device.DeviceName}{defaultIcon} {statusIcon}");
                        Console.WriteLine($"       Channels: {device.Channels}, Product: {device.ProductName}");
                    }
                }
                else
                {
                    Console.WriteLine("   ❌ No output devices found");
                }

                // Show current configuration
                Console.WriteLine("\n⚙️ Current Configuration:");
                Console.WriteLine($"   STT Input Device Setting: '{LoadSttInputDevice()}'");
                Console.WriteLine($"   TTS Output Device Setting: '{LoadTtsOutputDevice()}'");

                var currentInput = AudioDeviceManager.GetConfiguredInputDevice();
                var currentOutput = AudioDeviceManager.GetConfiguredOutputDevice();

                if (currentInput != null)
                {
                    Console.WriteLine($"   ✅ Configured STT Input: {currentInput.DeviceName} (Device #{currentInput.DeviceNumber})");
                }
                else
                {
                    Console.WriteLine($"   ⚠️ STT Input: Using system default");
                }

                if (currentOutput != null)
                {
                    Console.WriteLine($"   ✅ Configured TTS Output: {currentOutput.DeviceName} (Device #{currentOutput.DeviceNumber})");
                }
                else
                {
                    Console.WriteLine($"   ⚠️ TTS Output: Using system default");
                }

                // Demo configuration changes
                Console.WriteLine("\n🔧 Configuration Examples:");
                Console.WriteLine("   To set STT input device:");
                Console.WriteLine("   AppSettings.SaveSttInputDevice(\"Microphone Name\");");
                Console.WriteLine("   AppSettings.SaveSttInputDevice(\"Default\"); // Use system default");

                Console.WriteLine("\n   To set TTS output device:");
                Console.WriteLine("   AppSettings.SaveTtsOutputDevice(\"Speaker Name\");");
                Console.WriteLine("   AppSettings.SaveTtsOutputDevice(\"Default\"); // Use system default");

                Console.WriteLine("\n   To configure both devices:");
                Console.WriteLine("   AppSettings.ConfigureAudioDeviceSettings(\"Mic Name\", \"Speaker Name\");");

                Console.WriteLine("\n🎧🎤🔊 === END AUDIO DEVICE DEMO ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ERROR in ShowAudioDeviceDemo: {ex.Message}");
            }
        }

        // New model path loaders/savers for STT model and speaker embedding
        public static string LoadSttModelPath()
        {
            try
            {
                var path = GetString("SttModelPath");
                if (string.IsNullOrWhiteSpace(path)) LogSettingError("SttModelPath", "EMPTY");
                else if (!Directory.Exists(path)) LogSettingError("SttModelPath", $"NOT FOUND '{path}'");
                return path;
            }
            catch (Exception ex)
            {
                LogSettingError("SttModelPath", $"READ FAILED: {ex.Message}");
                return null;
            }
        }

        public static void SaveSttModelPath(string path)
        {
            try { SetString("SttModelPath", path); Console.WriteLine($"STT: Saved model path: {path}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving STT model path: {ex.Message}"); }
        }

        public static string LoadSpeakerEmbeddingModelPath()
        {
            try
            {
                var path = GetString("SpeakerEmbeddingModelPath");
                if (string.IsNullOrWhiteSpace(path)) LogSettingError("SpeakerEmbeddingModelPath", "EMPTY");
                else if (!File.Exists(path)) LogSettingError("SpeakerEmbeddingModelPath", "NOT FOUND");
                return path;
            }
            catch (Exception ex)
            {
                LogSettingError("SpeakerEmbeddingModelPath", $"READ FAILED: {ex.Message}");
                return null;
            }
        }

        public static void SaveSpeakerEmbeddingModelPath(string path)
        {
            try { SetString("SpeakerEmbeddingModelPath", path); Console.WriteLine($"Saved speaker embedding model path: {path}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving speaker embedding model path: {ex.Message}"); }
        }

        // Face model (ArcFace) settings
        public static string LoadArcFaceModelPath()
        {
            try
            {
                var path = GetString("ArcFaceModelPath");
                if (string.IsNullOrWhiteSpace(path)) LogSettingError("ArcFaceModelPath", "EMPTY");
                else if (!File.Exists(path)) LogSettingError("ArcFaceModelPath", "NOT FOUND");
                return path;
            }
            catch (Exception ex)
            {
                LogSettingError("ArcFaceModelPath", $"READ FAILED: {ex.Message}");
                return null;
            }
        }

        public static void SaveArcFaceModelPath(string path)
        {
            try { SetString("ArcFaceModelPath", path); Console.WriteLine($"Saved ArcFace model path: {path}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving ArcFace model path: {ex.Message}"); }
        }

        // Telemetry Settings
        public static bool LoadTelemetryEnabled()
        {
            try { return GetBool("TelemetryEnabled"); }
            catch (Exception ex) { LogSettingError("TelemetryEnabled", $"READ FAILED: {ex.Message}"); return false; }
        }
        public static void SaveTelemetryEnabled(bool enabled)
        {
            try { SetBool("TelemetryEnabled", enabled); Console.WriteLine($"Telemetry: Enabled: {enabled}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving telemetry enabled: {ex.Message}"); }
        }
        public static string LoadTelemetryFile()
        {
            try { var path = GetString("TelemetryFile"); return string.IsNullOrWhiteSpace(path) ? "logs/telemetry.ndjson" : path; }
            catch (Exception ex) { LogSettingError("TelemetryFile", $"READ FAILED: {ex.Message}"); return "logs/telemetry.ndjson"; }
        }
        public static void SaveTelemetryFile(string filePath)
        {
            try { SetString("TelemetryFile", filePath); Console.WriteLine($"Telemetry: File path: {filePath}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving telemetry file path: {ex.Message}"); }
        }
        public static int LoadTelemetrySamplingPct()
        {
            try { var pct = GetInt("TelemetrySamplingPct"); return Math.Max(0, Math.Min(100, pct)); }
            catch (Exception ex) { LogSettingError("TelemetrySamplingPct", $"READ FAILED: {ex.Message}"); return 100; }
        }
        public static void SaveTelemetrySamplingPct(int samplingPct)
        {
            try { var clamped = Math.Max(0, Math.Min(100, samplingPct)); SetInt("TelemetrySamplingPct", clamped); Console.WriteLine($"Telemetry: Sampling percentage: {clamped}%"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving telemetry sampling percentage: {ex.Message}"); }
        }
        public static void ConfigureTelemetrySettings(bool enabled = true, string filePath = "logs/telemetry.ndjson", int samplingPct = 100)
        {
            try { SaveTelemetryEnabled(enabled); SaveTelemetryFile(filePath); SaveTelemetrySamplingPct(samplingPct); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error configuring telemetry settings: {ex.Message}"); }
        }
        public static string GetTelemetrySettingsSummary()
        {
            try
            {
                var enabled = LoadTelemetryEnabled(); var filePath = LoadTelemetryFile(); var samplingPct = LoadTelemetrySamplingPct();
                return $"📊 Telemetry Settings:\n" +
                       $"   Enabled: {enabled}\n" +
                       $"   File path: {filePath}\n" +
                       $"   Sampling: {samplingPct}%\n" +
                       $"   Format: NDJSON (Newline Delimited JSON)\n" +
                       $"   Console: Warnings+ and summaries only\n" +
                       $"   Rotation: ~5MB file size limit";
            }
            catch (Exception ex) { return $"ERROR: Could not load telemetry settings: {ex.Message}"; }
        }

        // ====== IDENTITY FUSION SETTINGS ======
        public static float LoadFusionFaceWeight()
        {
            try { var v = GetFloat("FusionFaceWeight", 0.6f); if (v <= 0.0f || v > 1.0f) { LogSettingError("FusionFaceWeight", $"OUT OF RANGE (expected 0.0-1.0, got {v})"); return 0.6f; } return v; }
            catch (Exception ex) { LogSettingError("FusionFaceWeight", $"read FAILED: {ex.Message}"); return 0.6f; }
        }
        public static void SaveFusionFaceWeight(float weight)
        {
            try { var c = Math.Max(0.0f, Math.Min(1.0f, weight)); SetFloat("FusionFaceWeight", c); Console.WriteLine($"Fusion: Face weight set to {c:F2}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving fusion face weight: {ex.Message}"); }
        }
        public static float LoadFusionVoiceWeight()
        {
            try { var v = GetFloat("FusionVoiceWeight", 0.4f); if (v <= 0.0f || v > 1.0f) { LogSettingError("FusionVoiceWeight", $"OUT OF RANGE (expected 0.0-1.0, got {v})"); return 0.4f; } return v; }
            catch (Exception ex) { LogSettingError("FusionVoiceWeight", $"read FAILED: {ex.Message}"); return 0.4f; }
        }
        public static void SaveFusionVoiceWeight(float weight)
        {
            try { var c = Math.Max(0.0f, Math.Min(1.0f, weight)); SetFloat("FusionVoiceWeight", c); Console.WriteLine($"Fusion: Voice weight set to {c:F2}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving fusion voice weight: {ex.Message}"); }
        }
        public static int LoadFusionDecayHalfLifeMs()
        {
            try { var v = GetInt("FusionDecayHalfLifeMs", 2000); if (v < 500 || v > 10000) { LogSettingError("FusionDecayHalfLifeMs", $"OUT OF RANGE (expected 500-10000ms, got {v})"); return 2000; } return v; }
            catch (Exception ex) { LogSettingError("FusionDecayHalfLifeMs", $"read FAILED: {ex.Message}"); return 2000; }
        }
        public static void SaveFusionDecayHalfLifeMs(int halfLifeMs)
        {
            try { var c = Math.Max(500, Math.Min(10000, halfLifeMs)); SetInt("FusionDecayHalfLifeMs", c); Console.WriteLine($"Fusion: Decay half-life set to {c}ms"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving fusion decay half-life: {ex.Message}"); }
        }
        public static float LoadFusionUnknownThreshold()
        {
            try { var v = GetFloat("FusionUnknownThreshold", 0.3f); if (v < 0.0f || v > 1.0f) { LogSettingError("FusionUnknownThreshold", $"OUT OF RANGE (expected 0.0-1.0, got {v})"); return 0.3f; } return v; }
            catch (Exception ex) { LogSettingError("FusionUnknownThreshold", $"read FAILED: {ex.Message}"); return 0.3f; }
        }
        public static void SaveFusionUnknownThreshold(float threshold)
        {
            try { var c = Math.Max(0.0f, Math.Min(1.0f, threshold)); SetFloat("FusionUnknownThreshold", c); Console.WriteLine($"Fusion: Unknown threshold set to {c:F2}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving fusion unknown threshold: {ex.Message}"); }
        }
        public static void ConfigureFusionSettings(float faceWeight = 0.6f, float voiceWeight = 0.4f, int halfLifeMs = 2000, float unknownThreshold = 0.3f)
        {
            try { SaveFusionFaceWeight(faceWeight); SaveFusionVoiceWeight(voiceWeight); SaveFusionDecayHalfLifeMs(halfLifeMs); SaveFusionUnknownThreshold(unknownThreshold); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error configuring fusion settings: {ex.Message}"); }
        }
        public static string GetFusionSettingsSummary()
        {
            try
            {
                var faceWeight = LoadFusionFaceWeight(); var voiceWeight = LoadFusionVoiceWeight(); var halfLifeMs = LoadFusionDecayHalfLifeMs(); var unknownThreshold = LoadFusionUnknownThreshold();
                return $"🔀 Identity Fusion Settings:\n" +
                       $"   Face weight: {faceWeight:F2}\n" +
                       $"   Voice weight: {voiceWeight:F2}\n" +
                       $"   Decay half-life: {halfLifeMs}ms\n" +
                       $"   Unknown threshold: {unknownThreshold:F2}\n" +
                       $"   Fusion formula: (face*{faceWeight:F1} + voice*{voiceWeight:F1}) / {faceWeight + voiceWeight:F1}\n" +
                       $"   Time decay: score *= exp(-dt/{halfLifeMs}ms)";
            }
            catch (Exception ex) { return $"ERROR: Could not load fusion settings: {ex.Message}"; }
        }

        // Audio input mode
        public static AudioInMode LoadAudioInMode()
        {
            try
            {
                var modeStr = GetString("AudioInMode", "LocalMic");
                if (Enum.TryParse<AudioInMode>(modeStr, true, out var mode))
                {
                    string telemetryMode = mode switch { AudioInMode.LocalMic => "local_mic", AudioInMode.DiscordVoice => "discord_voice", AudioInMode.SystemLoopback => "system_loopback", _ => "unknown" };
                    Telemetry.Counter($"audio.ingest.mode.{telemetryMode}");
                    return mode;
                }
                else
                {
                    LogSettingError("AudioInMode", $"INVALID VALUE (expected LocalMic|DiscordVoice|SystemLoopback, got '{modeStr}')");
                    Telemetry.Counter("audio.ingest.mode.local_mic");
                    return AudioInMode.LocalMic;
                }
            }
            catch (Exception ex)
            {
                LogSettingError("AudioInMode", $"READ FAILED: {ex.Message}");
                Telemetry.Counter("audio.ingest.mode.local_mic");
                return AudioInMode.LocalMic;
            }
        }
        public static void SaveAudioInMode(AudioInMode mode)
        {
            try
            {
                WriteSettingRaw("AudioInMode", mode.ToString());
                Console.WriteLine($"🎧 Audio input mode saved: {mode}");
                string telemetryMode = mode switch { AudioInMode.LocalMic => "local_mic", AudioInMode.DiscordVoice => "discord_voice", AudioInMode.SystemLoopback => "system_loopback", _ => "unknown" };
                Telemetry.Counter($"audio.ingest.mode.{telemetryMode}");
            }
            catch (Exception ex) { LogSettingError("AudioInMode", $"SAVE FAILED: {ex.Message}"); }
        }

        // Summaries
        public static string GetTtsSettingsSummary()
        {
            try
            {
                return "🔊 TTS Pipeline Settings:\n" +
                       $"   Enabled: {LoadTtsEnabled()}\n" +
                       $"   Speaker: {LoadTtsSpeaker()}\n" +
                       $"   Execution: {(LoadTtsUseGpu() ? "GPU" : "CPU")}\n" +
                       $"   Model Folder: {LoadTtsModelFolder()}\n" +
                       $"   Model: {LoadTtsModelPath()}\n" +
                       $"   Vocoder: {LoadTtsVocoderModelPath()}\n" +
                       $"   CMU Dict: {LoadTtsCmudictPath()}\n" +
                       $"   Symbols: {LoadTtsSymbolsPath()}\n" +
                       $"   Output Device: {LoadTtsOutputDevice()}\n" +
                       $"   Local Volume: {LoadLocalTtsVolume() * 100:F0}%\n" +
                       $"   Discord Volume: {LoadDiscordTtsVolume() * 100:F0}%\n" +
                       $"   Speed: {LoadTtsSpeed():F2}x\n" +
                       $"   Trim Threshold: {LoadTtsTrimThreshold():F4}\n" +
                       $"   Trim Leave Ms: {LoadTtsTrimLeaveMs()}ms\n" +
                       $"   Trim Max Ms: {LoadTtsTrimMaxMs()}ms\n" +
                       $"   Min Clause Padding: {LoadTtsMinClausePaddingMs()}ms\n" +
                       $"   IPA Service Timeout: {LoadTtsIpaServiceTimeoutMs()}ms\n" +
                       $"   IPA One-Shot Timeout: {LoadTtsIpaOneShotTimeoutMs()}ms";
            }
            catch (Exception ex) { return $"ERROR: Could not load TTS settings: {ex.Message}"; }
        }
        public static string GetSttSettingsSummary()
        {
            try
            {
                return "🎙️ STT Pipeline Settings:\n" +
                       $"   Input Device: {LoadSttInputDevice()}\n" +
                       $"   Vosk Model: {LoadSttModelPath()}\n" +
                       $"   Speaker Embedding: {LoadSpeakerEmbeddingModelPath()}\n" +
                       $"   Speaker Match Threshold: {LoadSpeakerMatchMinScore():F2}\n" +
                       $"   Mic VAD Threshold: {LoadVoiceActivityThreshold():F0}\n" +
                       $"   Discord VAD Threshold: {LoadDiscordVoiceActivityThreshold():F0}\n" +
                       $"   VAD Silence Timeout: {LoadVadSilenceTimeoutMs()}ms\n" +
                       $"   VAD Debounce Timeout: {LoadVadDebounceTimeoutMs()}ms";
            }
            catch (Exception ex) { return $"ERROR: Could not load STT settings: {ex.Message}"; }
        }
        public static void InitializeAudioDevices()
        {
            try
            {
                Console.WriteLine("🎧 Initializing audio devices...");
                var audioMode = LoadAudioInMode();
                var modeDescription = audioMode switch { AudioInMode.LocalMic => "Local Microphone", AudioInMode.DiscordVoice => "Discord Voice (Opus→PCM)", AudioInMode.SystemLoopback => "System Loopback Capture", _ => "Unknown" };
                Console.WriteLine($"🎛️ Audio Input Mode: {modeDescription}");
                var input = AudioDeviceManager.GetConfiguredInputDevice();
                Console.WriteLine(input != null ? $"🎤 Using STT input device: {input.DeviceName} (Device #{input.DeviceNumber})" : "🎤 STT input device: Default");
                var outputDevice = AudioDeviceManager.GetConfiguredOutputDevice();
                Console.WriteLine(outputDevice != null ? $"🔊 Using TTS output device: {outputDevice.DeviceName} (Device #{outputDevice.DeviceNumber})" : "🔊 TTS output device: Default");
                Console.WriteLine("🎧 Audio device initialization complete!");
            }
            catch (Exception ex) { Console.WriteLine($"❌ ERROR: Audio device initialization failed: {ex.Message}"); }
        }

        /// <summary>
        /// Load speaker match minimum score threshold (default: 0.6)
        /// </summary>
        public static float LoadSpeakerMatchMinScore()
        {
            try
            {
                var value = GetFloat("SpeakerMatchMinScore", 0.6f);
                if (value < 0.0f || value > 1.0f)
                {
                    LogSettingError("SpeakerMatchMinScore", $"OUT OF RANGE (expected 0.0-1.0, got {value})");
                    return 0.6f; // Default threshold
                }
                return value;
            }
            catch (Exception ex)
            {
                LogSettingError("SpeakerMatchMinScore", $"read FAILED: {ex.Message}");
                return 0.6f; // Default threshold
            }
        }

        /// <summary>
        /// Save speaker match minimum score threshold
        /// </summary>
        public static void SaveSpeakerMatchMinScore(float threshold)
        {
            try
            {
                var clampedThreshold = Math.Max(0.0f, Math.Min(1.0f, threshold));
                SetFloat("SpeakerMatchMinScore", clampedThreshold);
                Console.WriteLine($"Speaker: Match threshold set to {clampedThreshold:F2}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving speaker match threshold: {ex.Message}");
            }
        }

        // ===== TTS Latency/Pacing Settings =====
        public static float LoadTtsSpeed()
        {
            try
            {
                var v = GetFloat("TtsSpeed", 1.05f);
                if (v < 0.5f || v > 2.0f) LogSettingError("TtsSpeed", $"OUT OF RANGE (0.5-2.0, got {v:F2})");
                return v;
            }
            catch (Exception ex) { LogSettingError("TtsSpeed", $"READ FAILED: {ex.Message}"); return 1.05f; }
        }
        public static void SaveTtsSpeed(float speed)
        {
            try { var c = Math.Max(0.5f, Math.Min(2.0f, speed)); SetFloat("TtsSpeed", c); Console.WriteLine($"TTS: Saved speed: {c:F2}x"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving TTS speed: {ex.Message}"); }
        }

        public static double LoadTtsTrimThreshold()
        {
            try
            {
                var v = GetDouble("TtsTrimThreshold");
                if (v <= 0) v = 0.003; // default
                if (v < 0.0005 || v > 0.05) LogSettingError("TtsTrimThreshold", $"OUT OF RANGE (0.0005-0.05, got {v:F4})");
                return v;
            }
            catch (Exception ex) { LogSettingError("TtsTrimThreshold", $"READ FAILED: {ex.Message}"); return 0.003; }
        }
        public static void SaveTtsTrimThreshold(double threshold)
        {
            try { var c = Math.Max(0.0005, Math.Min(0.05, threshold)); SetDouble("TtsTrimThreshold", c); Console.WriteLine($"TTS: Saved trim threshold: {c:F4}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving TTS trim threshold: {ex.Message}"); }
        }

        public static int LoadTtsTrimLeaveMs()
        {
            try
            {
                var v = GetInt("TtsTrimLeaveMs");
                if (v <= 0) v = 6; // default
                if (v < 0 || v > 100) LogSettingError("TtsTrimLeaveMs", $"OUT OF RANGE (0-100, got {v})");
                return v;
            }
            catch (Exception ex) { LogSettingError("TtsTrimLeaveMs", $"READ FAILED: {ex.Message}"); return 6; }
        }
        public static void SaveTtsTrimLeaveMs(int ms)
        {
            try { var c = Math.Max(0, Math.Min(100, ms)); SetInt("TtsTrimLeaveMs", c); Console.WriteLine($"TTS: Saved trim leave ms: {c}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving TTS trim leave ms: {ex.Message}"); }
        }

        public static int LoadTtsTrimMaxMs()
        {
            try
            {
                var v = GetInt("TtsTrimMaxMs");
                if (v <= 0) v = 800; // default
                if (v < 50 || v > 3000) LogSettingError("TtsTrimMaxMs", $"OUT OF RANGE (50-3000, got {v})");
                return v;
            }
            catch (Exception ex) { LogSettingError("TtsTrimMaxMs", $"READ FAILED: {ex.Message}"); return 800; }
        }
        public static void SaveTtsTrimMaxMs(int ms)
        {
            try { var c = Math.Max(50, Math.Min(3000, ms)); SetInt("TtsTrimMaxMs", c); Console.WriteLine($"TTS: Saved trim max ms: {c}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving TTS trim max ms: {ex.Message}"); }
        }

        public static int LoadTtsMinClausePaddingMs()
        {
            try
            {
                var v = GetInt("TtsMinClausePaddingMs");
                if (v <= 0) v = 10; // default 10ms
                if (v < 0 || v > 200) LogSettingError("TtsMinClausePaddingMs", $"OUT OF RANGE (0-200, got {v})");
                return v;
            }
            catch (Exception ex) { LogSettingError("TtsMinClausePaddingMs", $"READ FAILED: {ex.Message}"); return 10; }
        }
        public static void SaveTtsMinClausePaddingMs(int ms)
        {
            try { var c = Math.Max(0, Math.Min(200, ms)); SetInt("TtsMinClausePaddingMs", c); Console.WriteLine($"TTS: Saved min clause padding: {c}ms"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving TTS min clause padding: {ex.Message}"); }
        }

        public static int LoadTtsIpaServiceTimeoutMs()
        {
            try
            {
                var v = GetInt("TtsIpaServiceTimeoutMs");
                if (v <= 0) v = 1500; // default
                if (v < 200 || v > 5000) LogSettingError("TtsIpaServiceTimeoutMs", $"OUT OF RANGE (200-5000, got {v})");
                return v;
            }
            catch (Exception ex) { LogSettingError("TtsIpaServiceTimeoutMs", $"READ FAILED: {ex.Message}"); return 1500; }
        }
        public static void SaveTtsIpaServiceTimeoutMs(int ms)
        {
            try { var c = Math.Max(200, Math.Min(5000, ms)); SetInt("TtsIpaServiceTimeoutMs", c); Console.WriteLine($"TTS: Saved IPA service timeout: {c}ms"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving IPA service timeout: {ex.Message}"); }
        }

        public static int LoadTtsIpaOneShotTimeoutMs()
        {
            try
            {
                var v = GetInt("TtsIpaOneShotTimeoutMs");
                if (v <= 0) v = 1500; // default
                if (v < 200 || v > 5000) LogSettingError("TtsIpaOneShotTimeoutMs", $"OUT OF RANGE (200-5000, got {v})");
                return v;
            }
            catch (Exception ex) { LogSettingError("TtsIpaOneShotTimeoutMs", $"READ FAILED: {ex.Message}"); return 1500; }
        }
        public static void SaveTtsIpaOneShotTimeoutMs(int ms)
        {
            try { var c = Math.Max(200, Math.Min(5000, ms)); SetInt("TtsIpaOneShotTimeoutMs", c); Console.WriteLine($"TTS: Saved IPA one-shot timeout: {c}ms"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving TTS IPA one-shot timeout: {ex.Message}"); }
        }

        // ===== New: OutputThink policy (persisted in App.config for legacy callers) =====
        public static bool LoadOllamaOutputThink()
        {
            try { return GetBool("OllamaOutputThink", false); }
            catch (Exception ex) { LogSettingError("OllamaOutputThink", $"READ FAILED: {ex.Message}"); return false; }
        }
        public static void SaveOllamaOutputThink(bool outputThink)
        {
            try { SetBool("OllamaOutputThink", outputThink); Console.WriteLine($"Ollama: OutputThink = {outputThink}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving Ollama OutputThink: {ex.Message}"); }
        }
    }
}