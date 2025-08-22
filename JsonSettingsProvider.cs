// JsonSettingsProvider.cs
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kinectv1
{
    /// <summary>
    /// JSON-based settings provider for serializing and deserializing settings
    /// Provides alternative to XML config file storage for settings persistence
    /// </summary>
    public class JsonSettingsProvider
    {
        private readonly string _filePath;

        public JsonSettingsProvider(string filePath = "settings.json")
        {
            _filePath = filePath;
        }

        /// <summary>
        /// Save settings to JSON file
        /// </summary>
        public void SaveSettings(Dictionary<string, object> settings)
        {
            try
            {
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to save settings to JSON: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Load settings from JSON file
        /// </summary>
        public Dictionary<string, object> LoadSettings()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return new Dictionary<string, object>();
                }

                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new Dictionary<string, object>();
                }

                var result = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                return result ?? new Dictionary<string, object>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to load settings from JSON: {ex.Message}");
                return new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// Get a specific setting value with type conversion
        /// </summary>
        public T GetSetting<T>(string key, T defaultValue = default(T))
        {
            try
            {
                var settings = LoadSettings();
                if (!settings.ContainsKey(key))
                {
                    return defaultValue;
                }

                var value = settings[key];
                if (value == null)
                {
                    return defaultValue;
                }

                // Handle JToken conversion from Newtonsoft.Json
                if (value is JToken jToken)
                {
                    return jToken.ToObject<T>();
                }

                // Direct type conversion
                if (value is T directValue)
                {
                    return directValue;
                }

                // String conversion for primitives
                if (typeof(T) == typeof(bool) && value is string boolStr)
                {
                    return (T)(object)bool.Parse(boolStr);
                }
                if (typeof(T) == typeof(int) && value is string intStr)
                {
                    return (T)(object)int.Parse(intStr);
                }
                if (typeof(T) == typeof(float) && value is string floatStr)
                {
                    return (T)(object)float.Parse(floatStr);
                }
                if (typeof(T) == typeof(double) && value is string doubleStr)
                {
                    return (T)(object)double.Parse(doubleStr);
                }

                // Convert via string representation
                return (T)Convert.ChangeType(value.ToString(), typeof(T));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to get setting '{key}': {ex.Message}");
                return defaultValue;
            }
        }

        /// <summary>
        /// Set a specific setting value
        /// </summary>
        public void SetSetting<T>(string key, T value)
        {
            try
            {
                var settings = LoadSettings();
                settings[key] = value;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to set setting '{key}': {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Export current AppSettings to JSON
        /// </summary>
        public Dictionary<string, object> ExportFromAppSettings()
        {
            var settings = new Dictionary<string, object>();

            try
            {
                // Core settings
                settings["AppScenario"] = AppSettings.LoadAppScenario().ToString();
                settings["TtsEnabled"] = AppSettings.LoadTtsEnabled();
                settings["DiscordBotEnabled"] = AppSettings.LoadDiscordBotEnabled();
                settings["OllamaEnabled"] = AppSettings.LoadOllamaEnabled();
                settings["TelemetryEnabled"] = AppSettings.LoadTelemetryEnabled();

                // Voice settings
                settings["VoiceConfidenceThreshold"] = AppSettings.LoadVoiceConfidenceThreshold();
                settings["VoiceActivityThreshold"] = AppSettings.LoadVoiceActivityThreshold();
                settings["DiscordVoiceActivityThreshold"] = AppSettings.LoadDiscordVoiceActivityThreshold();

                // Audio devices
                settings["SttInputDevice"] = AppSettings.LoadSttInputDevice();
                settings["TtsOutputDevice"] = AppSettings.LoadTtsOutputDevice();

                // TTS settings
                settings["TtsSpeaker"] = AppSettings.LoadTtsSpeaker();
                settings["TtsUseGpu"] = AppSettings.LoadTtsUseGpu();
                settings["LocalTtsVolume"] = AppSettings.LoadLocalTtsVolume();
                settings["DiscordTtsVolume"] = AppSettings.LoadDiscordTtsVolume();

                // Model paths
                settings["TtsModelPath"] = AppSettings.LoadTtsModelPath();
                settings["SttModelPath"] = AppSettings.LoadSttModelPath();
                settings["SpeakerEmbeddingModelPath"] = AppSettings.LoadSpeakerEmbeddingModelPath();

                // Ollama settings
                settings["OllamaModel"] = AppSettings.LoadOllamaModel();
                settings["SystemPromptPath"] = AppSettings.LoadSystemPromptPath();

                // Window settings
                var (width, height, left, top, state) = AppSettings.LoadWindowSettings();
                settings["WindowWidth"] = width;
                settings["WindowHeight"] = height;
                settings["WindowLeft"] = left;
                settings["WindowTop"] = top;
                settings["WindowState"] = state;

                // UI settings
                settings["DarkMode"] = AppSettings.LoadDarkMode();

                // Face settings
                settings["FaceThreshold"] = AppSettings.LoadFaceThreshold();

                // Fusion settings
                settings["FusionFaceWeight"] = AppSettings.LoadFusionFaceWeight();
                settings["FusionVoiceWeight"] = AppSettings.LoadFusionVoiceWeight();
                settings["FusionDecayHalfLifeMs"] = AppSettings.LoadFusionDecayHalfLifeMs();
                settings["FusionUnknownThreshold"] = AppSettings.LoadFusionUnknownThreshold();

                // Audio mode
                settings["AudioInMode"] = AppSettings.LoadAudioInMode().ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to export settings from AppSettings: {ex.Message}");
            }

            return settings;
        }

        /// <summary>
        /// Import settings to AppSettings from JSON
        /// </summary>
        public void ImportToAppSettings(Dictionary<string, object> settings)
        {
            try
            {
                foreach (var kvp in settings)
                {
                    var key = kvp.Key;
                    var value = kvp.Value;

                    switch (key)
                    {
                        case "AppScenario":
                            if (Enum.TryParse<AppScenario>(value?.ToString(), out var scenario))
                                AppSettings.SaveAppScenario(scenario);
                            break;
                        case "TtsEnabled":
                            AppSettings.SaveTtsEnabled(GetValueAsBool(value));
                            break;
                        case "DiscordBotEnabled":
                            AppSettings.SaveDiscordBotEnabled(GetValueAsBool(value));
                            break;
                        case "OllamaEnabled":
                            AppSettings.SaveOllamaEnabled(GetValueAsBool(value));
                            break;
                        case "TelemetryEnabled":
                            AppSettings.SaveTelemetryEnabled(GetValueAsBool(value));
                            break;
                        case "VoiceConfidenceThreshold":
                            AppSettings.SaveVoiceConfidenceThreshold(GetValueAsFloat(value));
                            break;
                        case "VoiceActivityThreshold":
                            AppSettings.SaveVoiceActivityThreshold(GetValueAsFloat(value));
                            break;
                        case "DiscordVoiceActivityThreshold":
                            AppSettings.SaveDiscordVoiceActivityThreshold(GetValueAsFloat(value));
                            break;
                        case "SttInputDevice":
                            AppSettings.SaveSttInputDevice(value?.ToString());
                            break;
                        case "TtsOutputDevice":
                            AppSettings.SaveTtsOutputDevice(value?.ToString());
                            break;
                        case "TtsSpeaker":
                            AppSettings.SaveTtsSpeaker(value?.ToString());
                            break;
                        case "TtsUseGpu":
                            AppSettings.SaveTtsUseGpu(GetValueAsBool(value));
                            break;
                        case "LocalTtsVolume":
                            AppSettings.SaveLocalTtsVolume(GetValueAsDouble(value));
                            break;
                        case "DiscordTtsVolume":
                            AppSettings.SaveDiscordTtsVolume(GetValueAsDouble(value));
                            break;
                        case "DarkMode":
                            AppSettings.SaveDarkMode(GetValueAsBool(value));
                            break;
                        case "FaceThreshold":
                            AppSettings.SaveFaceThreshold(GetValueAsFloat(value));
                            break;
                        case "AudioInMode":
                            if (Enum.TryParse<AudioInMode>(value?.ToString(), out var audioMode))
                                AppSettings.SaveAudioInMode(audioMode);
                            break;
                        // Note: Model paths and other read-only settings are not imported for safety
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to import settings to AppSettings: {ex.Message}");
                throw;
            }
        }

        private bool GetValueAsBool(object value)
        {
            if (value is bool b) return b;
            if (value is JToken jToken) return jToken.ToObject<bool>();
            return bool.Parse(value?.ToString() ?? "false");
        }

        private float GetValueAsFloat(object value)
        {
            if (value is float f) return f;
            if (value is double d) return (float)d;
            if (value is JToken jToken) return jToken.ToObject<float>();
            return float.Parse(value?.ToString() ?? "0");
        }

        private double GetValueAsDouble(object value)
        {
            if (value is double d) return d;
            if (value is float f) return f;
            if (value is JToken jToken) return jToken.ToObject<double>();
            return double.Parse(value?.ToString() ?? "0");
        }

        /// <summary>
        /// Check if settings file exists
        /// </summary>
        public bool SettingsFileExists()
        {
            return File.Exists(_filePath);
        }

        /// <summary>
        /// Delete settings file
        /// </summary>
        public void DeleteSettingsFile()
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
    }
}