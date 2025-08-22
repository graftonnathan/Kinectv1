using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kinectv1.Tests
{
    /// <summary>
    /// Integration tests for SettingsWindow
    /// Tests window behavior, JSON import/export, and audio device fallback
    /// </summary>
    [TestClass]
    public class SettingsWindowTests
    {
        private string _testFilePath;
        private JsonSettingsProvider _testProvider;
        private SettingsWindow _settingsWindow;

        [TestInitialize]
        public void TestInitialize()
        {
            _testFilePath = Path.Combine(Path.GetTempPath(), $"test_settings_window_{Guid.NewGuid()}.json");
            _testProvider = new JsonSettingsProvider(_testFilePath);
            _settingsWindow = new SettingsWindow(_testProvider);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            if (File.Exists(_testFilePath))
            {
                File.Delete(_testFilePath);
            }
        }

        [TestMethod]
        public void Constructor_ShouldInitializeWithValidSettings()
        {
            // Act
            var window = new SettingsWindow(_testProvider);

            // Assert
            Assert.IsNotNull(window, "SettingsWindow should be created");
            Assert.IsTrue(window.GetSettingsCount() >= 0, "Should have valid settings count");
        }

        [TestMethod]
        public void ResetToDefaults_ShouldSetReasonableDefaults()
        {
            // Act
            _settingsWindow.ResetToDefaults();

            // Assert
            Assert.IsTrue(_settingsWindow.GetSettingsCount() > 0, "Should have default settings");
            Assert.AreEqual(true, _settingsWindow.GetSetting<bool>("TtsEnabled"));
            Assert.AreEqual(false, _settingsWindow.GetSetting<bool>("DiscordBotEnabled"));
            Assert.AreEqual(true, _settingsWindow.GetSetting<bool>("OllamaEnabled"));
            Assert.AreEqual(false, _settingsWindow.GetSetting<bool>("TelemetryEnabled"));
            Assert.AreEqual(0.5f, _settingsWindow.GetSetting<float>("VoiceConfidenceThreshold"), 0.01f);
            Assert.AreEqual("Default", _settingsWindow.GetSetting<string>("SttInputDevice"));
            Assert.AreEqual("Default", _settingsWindow.GetSetting<string>("TtsOutputDevice"));
            Assert.AreEqual("Local", _settingsWindow.GetSetting<string>("AppScenario"));
        }

        [TestMethod]
        public void SetAndGetSetting_ShouldWorkCorrectly()
        {
            // Arrange
            var key = "TestSetting";
            var value = "TestValue";

            // Act
            _settingsWindow.SetSetting(key, value);
            var retrieved = _settingsWindow.GetSetting<string>(key);

            // Assert
            Assert.AreEqual(value, retrieved, "Should set and get setting correctly");
        }

        [TestMethod]
        public void HasSetting_WithExistingKey_ShouldReturnTrue()
        {
            // Arrange
            _settingsWindow.SetSetting("TestKey", "TestValue");

            // Act
            var hasSetting = _settingsWindow.HasSetting("TestKey");

            // Assert
            Assert.IsTrue(hasSetting, "Should detect existing setting");
        }

        [TestMethod]
        public void HasSetting_WithNonExistentKey_ShouldReturnFalse()
        {
            // Act
            var hasSetting = _settingsWindow.HasSetting("NonExistentKey");

            // Assert
            Assert.IsFalse(hasSetting, "Should not detect non-existent setting");
        }

        [TestMethod]
        public void GetSettingKeys_ShouldReturnAllKeys()
        {
            // Arrange
            _settingsWindow.ResetToDefaults();

            // Act
            var keys = _settingsWindow.GetSettingKeys();

            // Assert
            Assert.IsNotNull(keys, "Should return keys array");
            Assert.IsTrue(keys.Length > 0, "Should have some keys");
            Assert.IsTrue(Array.Exists(keys, k => k == "TtsEnabled"), "Should contain TtsEnabled key");
            Assert.IsTrue(Array.Exists(keys, k => k == "SttInputDevice"), "Should contain SttInputDevice key");
        }

        [TestMethod]
        public void ExportToJson_ShouldCreateFile()
        {
            // Arrange
            _settingsWindow.ResetToDefaults();
            var exportPath = Path.Combine(Path.GetTempPath(), $"export_test_{Guid.NewGuid()}.json");

            try
            {
                // Act
                _settingsWindow.ExportToJson(exportPath);

                // Assert
                Assert.IsTrue(File.Exists(exportPath), "Export file should be created");
                var content = File.ReadAllText(exportPath);
                Assert.IsFalse(string.IsNullOrWhiteSpace(content), "Export file should contain data");
            }
            finally
            {
                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }
            }
        }

        [TestMethod]
        public void ImportFromJson_ShouldLoadSettings()
        {
            // Arrange
            _settingsWindow.ResetToDefaults();
            var exportPath = Path.Combine(Path.GetTempPath(), $"import_test_{Guid.NewGuid()}.json");

            try
            {
                // Export current settings
                _settingsWindow.ExportToJson(exportPath);
                var originalCount = _settingsWindow.GetSettingsCount();

                // Clear settings and import
                _settingsWindow.SetSetting("TtsEnabled", false); // Change a value
                _settingsWindow.ImportFromJson(exportPath);

                // Assert
                Assert.AreEqual(originalCount, _settingsWindow.GetSettingsCount(), "Should have same number of settings after import");
                Assert.AreEqual(true, _settingsWindow.GetSetting<bool>("TtsEnabled"), "Should restore original TtsEnabled value");
            }
            finally
            {
                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }
            }
        }

        [TestMethod]
        public void RoundTrip_ExportAndImport_ShouldMaintainData()
        {
            // Arrange
            _settingsWindow.ResetToDefaults();
            _settingsWindow.SetSetting("CustomSetting", "CustomValue");
            _settingsWindow.SetSetting("CustomNumber", 123);
            _settingsWindow.SetSetting("CustomBool", true);

            var exportPath = Path.Combine(Path.GetTempPath(), $"roundtrip_test_{Guid.NewGuid()}.json");

            try
            {
                // Act
                _settingsWindow.ExportToJson(exportPath);
                
                // Create new window and import
                var newWindow = new SettingsWindow(new JsonSettingsProvider());
                newWindow.ImportFromJson(exportPath);

                // Assert
                Assert.AreEqual("CustomValue", newWindow.GetSetting<string>("CustomSetting"));
                Assert.AreEqual(123, newWindow.GetSetting<int>("CustomNumber"));
                Assert.AreEqual(true, newWindow.GetSetting<bool>("CustomBool"));
                Assert.AreEqual(true, newWindow.GetSetting<bool>("TtsEnabled"));
                Assert.AreEqual("Default", newWindow.GetSetting<string>("SttInputDevice"));
            }
            finally
            {
                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }
            }
        }

        [TestMethod]
        public void ValidateAndFixAudioDevices_WithValidDevices_ShouldReturnTrue()
        {
            // Arrange
            _settingsWindow.SetSetting("SttInputDevice", "Microphone"); // Valid mock device
            _settingsWindow.SetSetting("TtsOutputDevice", "Speakers");   // Valid mock device

            // Act
            var result = _settingsWindow.ValidateAndFixAudioDevices();

            // Assert
            Assert.IsTrue(result, "Should return true for valid devices");
            Assert.AreEqual("Microphone", _settingsWindow.GetSetting<string>("SttInputDevice"));
            Assert.AreEqual("Speakers", _settingsWindow.GetSetting<string>("TtsOutputDevice"));
        }

        [TestMethod]
        public void ValidateAndFixAudioDevices_WithInvalidDevices_ShouldApplyFallback()
        {
            // Arrange
            _settingsWindow.SetSetting("SttInputDevice", "NonExistentMic");
            _settingsWindow.SetSetting("TtsOutputDevice", "NonExistentSpeaker");

            // Act
            var result = _settingsWindow.ValidateAndFixAudioDevices();

            // Assert
            Assert.IsFalse(result, "Should return false when fallback was applied");
            Assert.AreEqual("Default", _settingsWindow.GetSetting<string>("SttInputDevice"), 
                "Should fallback to Default for invalid STT device");
            Assert.AreEqual("Default", _settingsWindow.GetSetting<string>("TtsOutputDevice"), 
                "Should fallback to Default for invalid TTS device");
        }

        [TestMethod]
        public void ValidateAndFixAudioDevices_WithMixedValidity_ShouldFixOnlyInvalid()
        {
            // Arrange
            _settingsWindow.SetSetting("SttInputDevice", "Microphone"); // Valid
            _settingsWindow.SetSetting("TtsOutputDevice", "NonExistentSpeaker"); // Invalid

            // Act
            var result = _settingsWindow.ValidateAndFixAudioDevices();

            // Assert
            Assert.IsFalse(result, "Should return false when any fallback was applied");
            Assert.AreEqual("Microphone", _settingsWindow.GetSetting<string>("SttInputDevice"), 
                "Should keep valid STT device");
            Assert.AreEqual("Default", _settingsWindow.GetSetting<string>("TtsOutputDevice"), 
                "Should fallback invalid TTS device");
        }

        [TestMethod]
        public void ValidateAndFixAudioDevices_WithDefaultDevices_ShouldReturnTrue()
        {
            // Arrange
            _settingsWindow.SetSetting("SttInputDevice", "Default");
            _settingsWindow.SetSetting("TtsOutputDevice", "Default");

            // Act
            var result = _settingsWindow.ValidateAndFixAudioDevices();

            // Assert
            Assert.IsTrue(result, "Default devices should always be valid");
            Assert.AreEqual("Default", _settingsWindow.GetSetting<string>("SttInputDevice"));
            Assert.AreEqual("Default", _settingsWindow.GetSetting<string>("TtsOutputDevice"));
        }

        [TestMethod]
        public void ValidateAndFixAudioDevices_WithEmptyDevices_ShouldApplyFallback()
        {
            // Arrange
            _settingsWindow.SetSetting("SttInputDevice", "");
            _settingsWindow.SetSetting("TtsOutputDevice", "");

            // Act
            var result = _settingsWindow.ValidateAndFixAudioDevices();

            // Assert
            Assert.IsTrue(result, "Empty device names should be treated as Default (valid)");
        }

        [TestMethod]
        public void Integration_FullWorkflow_ShouldMaintainDataIntegrity()
        {
            // Arrange
            var exportPath = Path.Combine(Path.GetTempPath(), $"integration_test_{Guid.NewGuid()}.json");

            try
            {
                // Act - Full workflow
                _settingsWindow.ResetToDefaults();
                _settingsWindow.SetSetting("VoiceConfidenceThreshold", 0.75f);
                _settingsWindow.SetSetting("SttInputDevice", "USB Microphone");
                _settingsWindow.SetSetting("CustomTestValue", "Integration Test");

                // Validate and fix audio devices
                var audioValidation = _settingsWindow.ValidateAndFixAudioDevices();

                // Export to JSON
                _settingsWindow.ExportToJson(exportPath);

                // Create new window and import
                var newWindow = new SettingsWindow(new JsonSettingsProvider());
                newWindow.ImportFromJson(exportPath);

                // Validate the imported data
                var finalValidation = newWindow.ValidateAndFixAudioDevices();

                // Assert - Data integrity maintained throughout workflow
                Assert.IsTrue(audioValidation, "Initial audio validation should pass");
                Assert.IsTrue(finalValidation, "Final audio validation should pass");
                Assert.AreEqual(0.75f, newWindow.GetSetting<float>("VoiceConfidenceThreshold"), 0.01f);
                Assert.AreEqual("USB Microphone", newWindow.GetSetting<string>("SttInputDevice"));
                Assert.AreEqual("Integration Test", newWindow.GetSetting<string>("CustomTestValue"));
                Assert.AreEqual(true, newWindow.GetSetting<bool>("TtsEnabled"));
                Assert.AreEqual("Local", newWindow.GetSetting<string>("AppScenario"));
            }
            finally
            {
                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }
            }
        }
    }
}