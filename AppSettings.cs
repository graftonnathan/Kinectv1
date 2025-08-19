// AppSettings.cs
using System;
using System.Configuration;
using System.Globalization;
using System.Threading;
using System.IO;
using System.Linq;
using Kinectv1.Properties;

namespace Kinectv1
{
    public static class AppSettings
    {
        // Thread-safe configuration access
        private static readonly object _configLock = new object();

        private static void LogSettingError(string name, string detail)
        {
            Console.WriteLine($"Error: {name}: {detail}");
        }
        
        /// <summary>
        /// Initialize settings on application startup - loads from Settings.settings
        /// Defaults-only mode: ignores user.config and uses design-time defaults.
        /// </summary>
        public static void InitializeSettingsOnStartup()
        {
            try
            {
                Console.WriteLine("📋 Initializing settings from Settings.settings...");

                // Force runtime to use design-time defaults only (disable user-scoped overrides)
                try
                {
                    Settings.Default.Reset(); // Reset in-memory values to defaults from Settings.settings
                    Console.WriteLine("🔒 User settings ignored: using Settings.settings defaults only (no persistence)");
                }
                catch (Exception resetEx)
                {
                    Console.WriteLine($"⚠️ Unable to reset user settings: {resetEx.Message}");
                }
                
                // Organized grouped summaries
                Console.WriteLine(GetTtsSettingsSummary());
                Console.WriteLine(GetSttSettingsSummary());
                Console.WriteLine(GetVoiceConfidenceSettingsSummary());
                Console.WriteLine(GetDiscordBotSettingsSummary());
                Console.WriteLine(GetAudioDeviceSettingsSummary());

                // Other settings remain grouped by feature
                Console.WriteLine($"👤 Face Settings:\n   Face Threshold: {LoadFaceThreshold():F2}");
                
                Console.WriteLine($"🧠 Ollama Settings:\n   Model: {LoadOllamaModel()}\n   Enabled: {LoadOllamaEnabled()}\n   Memory Enabled: {LoadOllamaMemoryEnabled()}\n   Max Messages Per Speaker: {LoadOllamaMaxMessagesPerSpeaker()}\n   Max System Messages: {LoadOllamaMaxSystemMessages()}\n   Conversation Timeout: {LoadOllamaConversationTimeoutMinutes()} minutes\n   Conversation History Path: {LoadConversationHistoryPath()}");
                
                Console.WriteLine($"🎨 UI Settings:\n   Dark Mode: {LoadDarkMode()}");
                
                var (width, height, left, top, state) = LoadWindowSettings();
                Console.WriteLine($"🪟 Window Settings:\n   Size: {width:F0}x{height:F0}\n   Position: ({left:F0}, {top:F0})\n   State: {state}");
                
                Console.WriteLine($"📁 System Settings:\n   Kinect Mode: {LoadKinectMode()}\n   System Prompt Path: {LoadSystemPromptPath()}");
                
                // Initialize and log audio devices
                InitializeAudioDevices();
                
                Console.WriteLine("✅ Settings initialization complete!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ERROR: Error loading settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Save all settings - writes to application config (Settings.settings/app.config)
        /// </summary>
        public static void SaveAllSettings()
        {
            try
            {
                lock (_configLock)
                {
                    var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                    var group = config.SectionGroups["applicationSettings"] as ApplicationSettingsGroup;
                    var sectionName = typeof(Settings).FullName; // e.g., Kinectv1.Properties.Settings
                    var clientSection = group?.Sections[sectionName] as ClientSettingsSection;

                    if (clientSection == null)
                    {
                        Console.WriteLine($"❌ ERROR: applicationSettings section '{sectionName}' not found in app.config");
                        return;
                    }

                    foreach (SettingElement element in clientSection.Settings)
                    {
                        var name = element.Name;
                        var valueObj = Settings.Default[name];
                        string textValue;

                        if (valueObj == null)
                        {
                            textValue = string.Empty;
                        }
                        else if (valueObj is bool b)
                        {
                            textValue = b ? "True" : "False";
                        }
                        else if (valueObj is IFormattable formattable)
                        {
                            textValue = formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
                        }
                        else
                        {
                            textValue = valueObj.ToString();
                        }

                        if (element.Value.ValueXml == null)
                        {
                            var doc = new System.Xml.XmlDocument();
                            element.Value.ValueXml = doc.CreateElement("value");
                        }

                        element.Value.ValueXml.InnerText = textValue ?? string.Empty;
                    }

                    config.Save(ConfigurationSaveMode.Modified);
                    ConfigurationManager.RefreshSection($"applicationSettings/{sectionName}");
                    Console.WriteLine("💾 Settings saved to application configuration (Settings.settings)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ERROR: Error saving settings: {ex.Message}");
            }
        }

        // Kinect Settings
        public static string LoadKinectMode()
        {
            try
            {
                var mode = Settings.Default.KinectMode;
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
                var value = Settings.Default.VoiceThreshold;
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
                Settings.Default.VoiceThreshold = threshold;
                SaveAllSettings();
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
                var value = Settings.Default.VoiceActivityThreshold;
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
                Settings.Default.VoiceActivityThreshold = threshold;
                SaveAllSettings();
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
                var value = Settings.Default.DiscordVoiceActivityThreshold;
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
                Settings.Default.DiscordVoiceActivityThreshold = threshold;
                SaveAllSettings();
                Console.WriteLine($"Discord: Saved VAD threshold: {threshold:F0}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving Discord voice activity threshold: {ex.Message}");
            }
        }

        public static float LoadVoiceConfidenceThreshold()
        {
            try
            {
                var value = Settings.Default.VoiceConfidenceThreshold;
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
                Settings.Default.VoiceConfidenceThreshold = threshold;
                SaveAllSettings();
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
                var value = Settings.Default.VoiceHighConfidenceThreshold;
                if (value < 0f || value > 1f)
                    LogSettingError("VoiceHighConfidenceThreshold", $"OUT OF RANGE (expected 0.0-1.0, got {value:F2})");
                var min = Settings.Default.VoiceConfidenceThreshold;
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
                Settings.Default.VoiceHighConfidenceThreshold = threshold;
                SaveAllSettings();
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
                var size = Settings.Default.VoiceConfidenceBufferSize;
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
                Settings.Default.VoiceConfidenceBufferSize = bufferSize;
                SaveAllSettings();
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
                return Settings.Default.VoiceConfidenceLoggingEnabled;
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
                Settings.Default.VoiceConfidenceLoggingEnabled = enabled;
                SaveAllSettings();
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
                var deviceName = Settings.Default.SttInputDevice;
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
                Settings.Default.SttInputDevice = deviceName;
                SaveAllSettings();
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
                var deviceName = Settings.Default.TtsOutputDevice;
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
                Settings.Default.TtsOutputDevice = deviceName;
                SaveAllSettings();
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
                var token = Settings.Default.DiscordBotToken;
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
                Settings.Default.DiscordBotToken = token;
                SaveAllSettings();
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
                return Settings.Default.DiscordBotEnabled;
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
                Settings.Default.DiscordBotEnabled = enabled;
                SaveAllSettings();
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
                var prefix = Settings.Default.DiscordBotPrefix;
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
                Settings.Default.DiscordBotPrefix = prefix;
                SaveAllSettings();
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
                return Settings.Default.DiscordAutoJoinVoice;
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
                Settings.Default.DiscordAutoJoinVoice = autoJoin;
                SaveAllSettings();
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
                var value = Settings.Default.FaceThreshold;
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
                Settings.Default.FaceThreshold = threshold;
                SaveAllSettings();
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
                var model = Settings.Default.OllamaModel;
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
                Settings.Default.OllamaModel = model;
                SaveAllSettings();
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
                return Settings.Default.OllamaEnabled;
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
                Settings.Default.OllamaEnabled = enabled;
                SaveAllSettings();
                Console.WriteLine($"Saved Ollama enabled: {enabled}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving Ollama enabled: {ex.Message}");
            }
        }

        public static int LoadOllamaMaxMessagesPerSpeaker()
        {
            try
            {
                var value = Settings.Default.OllamaMaxMessagesPerSpeaker;
                if (value <= 0 || value > 100)
                    LogSettingError("OllamaMaxMessagesPerSpeaker", $"OUT OF RANGE (expected 1-100, got {value})");
                return value;
            }
            catch (Exception ex)
            {
                LogSettingError("OllamaMaxMessagesPerSpeaker", $"READ FAILED: {ex.Message}");
                return 0;
            }
        }

        public static int LoadOllamaMaxSystemMessages()
        {
            try
            {
                var value = Settings.Default.OllamaMaxSystemMessages;
                if (value <= 0 || value > 10)
                    LogSettingError("OllamaMaxSystemMessages", $"OUT OF RANGE (expected 1-10, got {value})");
                return value;
            }
            catch (Exception ex)
            {
                LogSettingError("OllamaMaxSystemMessages", $"READ FAILED: {ex.Message}");
                return 0;
            }
        }

        public static int LoadOllamaConversationTimeoutMinutes()
        {
            try
            {
                var value = Settings.Default.OllamaConversationTimeoutMinutes;
                if (value <= 0 || value > 1440)
                    LogSettingError("OllamaConversationTimeoutMinutes", $"OUT OF RANGE (expected 1-1440, got {value})");
                return value;
            }
            catch (Exception ex)
            {
                LogSettingError("OllamaConversationTimeoutMinutes", $"READ FAILED: {ex.Message}");
                return 0;
            }
        }

        public static bool LoadOllamaMemoryEnabled()
        {
            try
            {
                return Settings.Default.OllamaMemoryEnabled;
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
                Settings.Default.OllamaMemoryEnabled = enabled;
                SaveAllSettings();
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
                var path = Settings.Default.ConversationHistoryPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    LogSettingError("ConversationHistoryPath", "EMPTY");
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
                return null;
            }
        }

        public static void SaveConversationHistoryPath(string path)
        {
            try
            {
                Settings.Default.ConversationHistoryPath = path;
                SaveAllSettings();
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
                var path = Settings.Default.SystemPromptPath;
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
                Settings.Default.SystemPromptPath = path;
                SaveAllSettings();
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
                return Settings.Default.TtsEnabled;
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
                Settings.Default.TtsEnabled = enabled;
                SaveAllSettings();
                Console.WriteLine($"TTS: Saved TTS enabled: {enabled}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving TTS enabled: {ex.Message}");
            }
        }

        public static string LoadTtsModelPath()
        {
            try
            {
                var path = Settings.Default.TtsModelPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    LogSettingError("TtsModelPath", "EMPTY");
                }
                else if (!File.Exists(path))
                {
                    LogSettingError("TtsModelPath", "NOT FOUND");
                }
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
                Settings.Default.TtsModelPath = path;
                SaveAllSettings();
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
                var path = Settings.Default.TtsCmudictPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    LogSettingError("TtsCmudictPath", "EMPTY");
                }
                else if (!File.Exists(path))
                {
                    LogSettingError("TtsCmudictPath", "NOT FOUND");
                }
                return path;
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
                Settings.Default.TtsCmudictPath = path;
                SaveAllSettings();
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
                var path = Settings.Default.TtsSymbolsPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    LogSettingError("TtsSymbolsPath", "EMPTY");
                }
                else if (!File.Exists(path))
                {
                    LogSettingError("TtsSymbolsPath", "NOT FOUND");
                }
                return path;
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
                Settings.Default.TtsSymbolsPath = path;
                SaveAllSettings();
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
                var path = Settings.Default.TtsVocoderModelPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    LogSettingError("TtsVocoderModelPath", "EMPTY");
                }
                else if (!File.Exists(path))
                {
                    LogSettingError("TtsVocoderModelPath", "NOT FOUND");
                }
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
                Settings.Default.TtsVocoderModelPath = path;
                SaveAllSettings();
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
                var speaker = Settings.Default.TtsSpeaker;
                if (string.IsNullOrWhiteSpace(speaker))
                {
                    LogSettingError("TtsSpeaker", "EMPTY");
                }
                else
                {
                    int dummy;
                    if (!int.TryParse(speaker, out dummy))
                        LogSettingError("TtsSpeaker", $"INVALID '{speaker}' (expected numeric id)");
                }
                return speaker;
            }
            catch (Exception ex)
            {
                LogSettingError("TtsSpeaker", $"READ FAILED: {ex.Message}");
                return null;
            }
        }

        public static void SaveTtsSpeaker(string speaker)
        {
            try
            {
                Settings.Default.TtsSpeaker = speaker;
                SaveAllSettings();
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
                return Settings.Default.TtsUseGpu;
            }
            catch (Exception ex)
            {
                LogSettingError("TtsUseGpu", $"READ FAILED: {ex.Message}");
                return false;
            }
        }

        public static void SaveTtsUseGpu(bool useGpu)
        {
            try
            {
                Settings.Default.TtsUseGpu = useGpu;
                SaveAllSettings();
                Console.WriteLine($"TTS: Saved TTS GPU preference: {useGpu}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving TTS GPU preference: {ex.Message}");
            }
        }

        /// <summary>
        /// Load local TTS volume setting (0.0 to 1.0)
        /// </summary>
        public static double LoadLocalTtsVolume()
        {
            try
            {
                var value = Settings.Default.LocalTtsVolume;
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
                Settings.Default.LocalTtsVolume = volume;
                SaveAllSettings();
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
                var value = Settings.Default.DiscordTtsVolume;
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
                Settings.Default.DiscordTtsVolume = volume;
                SaveAllSettings();
                Console.WriteLine($"TTS: Saved Discord volume: {volume * 100:F0}%");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving Discord TTS volume: {ex.Message}");
            }
        }

        // UI Theme Settings
        public static bool LoadDarkMode()
        {
            try
            {
                return Settings.Default.DarkMode;
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
                Settings.Default.DarkMode = darkMode;
                SaveAllSettings();
                Console.WriteLine($"Saved dark mode: {darkMode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Error saving dark mode: {ex.Message}");
            }
        }

        // Window Settings
        public static (double width, double height, double left, double top, string state) LoadWindowSettings()
        {
            try
            {
                double width = Settings.Default.WindowWidth;
                double height = Settings.Default.WindowHeight;
                double left = Settings.Default.WindowLeft;
                double top = Settings.Default.WindowTop;
                string state = Settings.Default.WindowState;

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
                Settings.Default.WindowWidth = width;
                Settings.Default.WindowHeight = height;
                Settings.Default.WindowLeft = left;
                Settings.Default.WindowTop = top;
                Settings.Default.WindowState = state;
                SaveAllSettings();
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
        public static void ConfigureVoiceActivitySettings(float vadThreshold = 300f, float discordVadThreshold = 25f)
        {
            try
            {
                SaveVoiceActivityThreshold(vadThreshold);
                SaveDiscordVoiceActivityThreshold(discordVadThreshold);
                AudioUtils.RefreshVadThreshold(); // Update cached value immediately
                
                Console.WriteLine($"🎙️ Voice Activity Detection configured:");
                Console.WriteLine($"   Microphone VAD threshold: {vadThreshold:F0} (RMS level for voice detection)");
                Console.WriteLine($"   Discord VAD threshold: {discordVadThreshold:F0} (RMS level for Discord audio)");
                Console.WriteLine($"   💡 Lower values = more sensitive (catches quiet speech start)");
                Console.WriteLine($"   💡 Higher values = less sensitive (reduces false positives)");
                Console.WriteLine($"   💡 Discord threshold should be lower due to audio compression");
                Console.WriteLine($"   💡 Recommended ranges: Microphone 200-500, Discord 15-50");
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
                       $"   Token configured: {(!string.IsNullOrEmpty(token) ? "Yes" : "No")}\n" +
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
                
                return $"🎤 Voice Recognition Settings:\n" +
                       $"   Microphone VAD threshold: {vadThreshold:F0} (voice activity detection)\n" +
                       $"   Discord VAD threshold: {discordVadThreshold:F0} (Discord voice activity detection)\n" +
                       $"   Confidence threshold: {threshold:F2} (minimum quality)\n" +
                       $"   High confidence threshold: {highThreshold:F2} (high quality)\n" +
                       $"   Buffer size: {bufferSize} (recovery attempts)\n" +
                       $"   Logging enabled: {loggingEnabled}\n" +
                       $"   Quality level: {(threshold >= 0.5f ? "High" : threshold >= 0.3f ? "Medium" : "Low")}\n" +
                       $"   Microphone VAD sensitivity: {(vadThreshold <= 200f ? "High" : vadThreshold <= 400f ? "Medium" : "Low")}\n" +
                       $"   Discord VAD sensitivity: {(discordVadThreshold <= 15f ? "High" : discordVadThreshold <= 35f ? "Medium" : "Low")}";
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
                
                return $"🎧 Audio Device Settings:\n" +
                       $"   STT Input Device: {sttInputDevice}\n" +
                       $"   TTS Output Device: {ttsOutputDevice}";
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
                var path = Settings.Default.SttModelPath;
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
            try { Settings.Default.SttModelPath = path; SaveAllSettings(); Console.WriteLine($"STT: Saved model path: {path}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving STT model path: {ex.Message}"); }
        }

        public static string LoadSpeakerEmbeddingModelPath()
        {
            try
            {
                var path = Settings.Default.SpeakerEmbeddingModelPath;
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
            try { Settings.Default.SpeakerEmbeddingModelPath = path; SaveAllSettings(); Console.WriteLine($"Saved speaker embedding model path: {path}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving speaker embedding model path: {ex.Message}"); }
        }

        /// <summary>
        /// Grouped summary of TTS pipeline settings
        /// </summary>
        public static string GetTtsSettingsSummary()
        {
            try
            {
                return "🔊 TTS Pipeline Settings:\n" +
                       $"   Enabled: {LoadTtsEnabled()}\n" +
                       $"   Speaker: {LoadTtsSpeaker()}\n" +
                       $"   Execution: {(LoadTtsUseGpu() ? "GPU" : "CPU")}\n" +
                       $"   Model: {LoadTtsModelPath()}\n" +
                       $"   Vocoder: {LoadTtsVocoderModelPath()}\n" +
                       $"   CMU Dict: {LoadTtsCmudictPath()}\n" +
                       $"   Symbols: {LoadTtsSymbolsPath()}\n" +
                       $"   Output Device: {LoadTtsOutputDevice()}\n" +
                       $"   Local Volume: {LoadLocalTtsVolume() * 100:F0}%\n" +
                       $"   Discord Volume: {LoadDiscordTtsVolume() * 100:F0}%";
            }
            catch (Exception ex)
            {
                return $"ERROR: Could not load TTS settings: {ex.Message}";
            }
        }

        /// <summary>
        /// Grouped summary of STT pipeline settings
        /// </summary>
        public static string GetSttSettingsSummary()
        {
            try
            {
                return "🎙️ STT Pipeline Settings:\n" +
                       $"   Input Device: {LoadSttInputDevice()}\n" +
                       $"   Vosk Model: {LoadSttModelPath()}\n" +
                       $"   Speaker Embedding: {LoadSpeakerEmbeddingModelPath()}\n" +
                       $"   Mic VAD Threshold: {LoadVoiceActivityThreshold():F0}\n" +
                       $"   Discord VAD Threshold: {LoadDiscordVoiceActivityThreshold():F0}";
            }
            catch (Exception ex)
            {
                return $"ERROR: Could not load STT settings: {ex.Message}";
            }
        }

        /// <summary>
        /// Initialize audio devices and log their status
        /// </summary>
        public static void InitializeAudioDevices()
        {
            try
            {
                Console.WriteLine("🎧 Initializing audio devices...");
                
                // Log all available devices
                AudioDeviceManager.LogAllDevices();
                
                // Log current STT configuration
                AudioInputHelper.LogCurrentConfiguration();
                
                // Log current TTS output device
                var outputDevice = AudioDeviceManager.GetConfiguredOutputDevice();
                if (outputDevice != null)
                {
                    Console.WriteLine($"🔊 Current TTS Output: {outputDevice.DeviceName} (Device #{outputDevice.DeviceNumber})");
                }
                else
                {
                    Console.WriteLine("🔊 Current TTS Output: Default device");
                }
                
                Console.WriteLine("🎧 Audio device initialization complete!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ERROR: Audio device initialization failed: {ex.Message}");
            }
        }
    }
}