// SettingsWindow.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace Kinectv1
{
    /// <summary>
    /// Minimal settings window implementation for testing purposes
    /// In a full WPF implementation, this would be a Window class with UI elements
    /// </summary>
    public class SettingsWindow
    {
        private JsonSettingsProvider _jsonProvider;
        private Dictionary<string, object> _currentSettings;

        public SettingsWindow(JsonSettingsProvider jsonProvider = null)
        {
            _jsonProvider = jsonProvider ?? new JsonSettingsProvider();
            _currentSettings = new Dictionary<string, object>();
            LoadCurrentSettings();
        }

        /// <summary>
        /// Load current settings from AppSettings
        /// </summary>
        public void LoadCurrentSettings()
        {
            try
            {
                _currentSettings = _jsonProvider.ExportFromAppSettings();
                Console.WriteLine($"Settings loaded: {_currentSettings.Count} items");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to load current settings: {ex.Message}");
                _currentSettings = new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// Save current settings to AppSettings
        /// </summary>
        public void SaveCurrentSettings()
        {
            try
            {
                _jsonProvider.ImportToAppSettings(_currentSettings);
                Console.WriteLine("Settings saved successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to save current settings: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Export settings to JSON file
        /// </summary>
        public void ExportToJson(string filePath = null)
        {
            try
            {
                if (filePath != null)
                {
                    var provider = new JsonSettingsProvider(filePath);
                    provider.SaveSettings(_currentSettings);
                }
                else
                {
                    _jsonProvider.SaveSettings(_currentSettings);
                }
                Console.WriteLine($"Settings exported to JSON: {filePath ?? "default location"}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to export to JSON: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Import settings from JSON file
        /// </summary>
        public void ImportFromJson(string filePath = null)
        {
            try
            {
                if (filePath != null)
                {
                    var provider = new JsonSettingsProvider(filePath);
                    _currentSettings = provider.LoadSettings();
                }
                else
                {
                    _currentSettings = _jsonProvider.LoadSettings();
                }
                Console.WriteLine($"Settings imported from JSON: {filePath ?? "default location"}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to import from JSON: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Get a setting value
        /// </summary>
        public T GetSetting<T>(string key, T defaultValue = default(T))
        {
            if (!_currentSettings.ContainsKey(key))
            {
                return defaultValue;
            }

            try
            {
                var value = _currentSettings[key];
                if (value is T directValue)
                {
                    return directValue;
                }

                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to get setting '{key}': {ex.Message}");
                return defaultValue;
            }
        }

        /// <summary>
        /// Set a setting value
        /// </summary>
        public void SetSetting<T>(string key, T value)
        {
            _currentSettings[key] = value;
        }

        /// <summary>
        /// Validate current audio device settings and handle fallback
        /// </summary>
        public bool ValidateAndFixAudioDevices()
        {
            bool hadIssues = false;

            try
            {
                // Check STT input device
                var sttDevice = GetSetting<string>("SttInputDevice", "Default");
                if (!IsValidInputDevice(sttDevice))
                {
                    Console.WriteLine($"WARNING: STT input device '{sttDevice}' not found, falling back to Default");
                    SetSetting("SttInputDevice", "Default");
                    hadIssues = true;
                }

                // Check TTS output device
                var ttsDevice = GetSetting<string>("TtsOutputDevice", "Default");
                if (!IsValidOutputDevice(ttsDevice))
                {
                    Console.WriteLine($"WARNING: TTS output device '{ttsDevice}' not found, falling back to Default");
                    SetSetting("TtsOutputDevice", "Default");
                    hadIssues = true;
                }

                if (hadIssues)
                {
                    Console.WriteLine("Audio device fallback applied - save settings to persist changes");
                }

                return !hadIssues;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to validate audio devices: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if an input device name is valid (mock implementation for testing)
        /// </summary>
        private bool IsValidInputDevice(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName) || deviceName.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                return true; // Default is always valid
            }

            // Mock device validation - in real implementation would check AudioDeviceManager
            var validInputDevices = new[] { "Microphone", "USB Microphone", "Built-in Microphone" };
            return validInputDevices.Contains(deviceName, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Check if an output device name is valid (mock implementation for testing)
        /// </summary>
        private bool IsValidOutputDevice(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName) || deviceName.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                return true; // Default is always valid
            }

            // Mock device validation - in real implementation would check AudioDeviceManager
            var validOutputDevices = new[] { "Speakers", "Headphones", "USB Speakers", "Built-in Speakers" };
            return validOutputDevices.Contains(deviceName, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reset settings to defaults
        /// </summary>
        public void ResetToDefaults()
        {
            _currentSettings.Clear();
            
            // Set reasonable defaults
            SetSetting("TtsEnabled", true);
            SetSetting("DiscordBotEnabled", false);
            SetSetting("OllamaEnabled", true);
            SetSetting("TelemetryEnabled", false);
            SetSetting("VoiceConfidenceThreshold", 0.5f);
            SetSetting("VoiceActivityThreshold", 300f);
            SetSetting("DiscordVoiceActivityThreshold", 25f);
            SetSetting("SttInputDevice", "Default");
            SetSetting("TtsOutputDevice", "Default");
            SetSetting("TtsSpeaker", "em_alex");
            SetSetting("TtsUseGpu", false);
            SetSetting("LocalTtsVolume", 0.8);
            SetSetting("DiscordTtsVolume", 0.8);
            SetSetting("DarkMode", false);
            SetSetting("FaceThreshold", 0.6f);
            SetSetting("AudioInMode", "LocalMic");
            SetSetting("AppScenario", "Local");

            Console.WriteLine("Settings reset to defaults");
        }

        /// <summary>
        /// Get count of current settings
        /// </summary>
        public int GetSettingsCount()
        {
            return _currentSettings.Count;
        }

        /// <summary>
        /// Get all setting keys
        /// </summary>
        public string[] GetSettingKeys()
        {
            return _currentSettings.Keys.ToArray();
        }

        /// <summary>
        /// Check if a setting exists
        /// </summary>
        public bool HasSetting(string key)
        {
            return _currentSettings.ContainsKey(key);
        }
    }
}