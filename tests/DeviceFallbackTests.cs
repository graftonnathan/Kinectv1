using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kinectv1.Tests
{
    /// <summary>
    /// Tests for device-missing fallback scenarios
    /// Verifies that the system gracefully handles missing or invalid audio devices
    /// </summary>
    [TestClass]
    public class DeviceFallbackTests
    {
        private string _testFilePath;
        private JsonSettingsProvider _testProvider;
        private SettingsWindow _settingsWindow;

        [TestInitialize]
        public void TestInitialize()
        {
            _testFilePath = Path.Combine(Path.GetTempPath(), $"test_device_fallback_{Guid.NewGuid()}.json");
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
        public void DeviceFallback_InvalidInputDevice_ShouldFallbackToDefault()
        {
            // Arrange
            _settingsWindow.SetSetting("SttInputDevice", "NonExistent Microphone 9000");
            _settingsWindow.SetSetting("TtsOutputDevice", "Speakers"); // Valid device

            // Act
            var result = _settingsWindow.ValidateAndFixAudioDevices();

            // Assert
            Assert.IsFalse(result, "Should indicate that fallback was applied");
            Assert.AreEqual("Default", _settingsWindow.GetSetting<string>("SttInputDevice"), 
                "Invalid input device should fallback to Default");
            Assert.AreEqual("Speakers", _settingsWindow.GetSetting<string>("TtsOutputDevice"), 
                "Valid output device should remain unchanged");
        }

        [TestMethod]
        public void DeviceFallback_InvalidOutputDevice_ShouldFallbackToDefault()
        {
            // Arrange
            _settingsWindow.SetSetting("SttInputDevice", "Microphone"); // Valid device
            _settingsWindow.SetSetting("TtsOutputDevice", "Super Ultra Speakers XL"); // Invalid device

            // Act
            var result = _settingsWindow.ValidateAndFixAudioDevices();

            // Assert
            Assert.IsFalse(result, "Should indicate that fallback was applied");
            Assert.AreEqual("Microphone", _settingsWindow.GetSetting<string>("SttInputDevice"), 
                "Valid input device should remain unchanged");
            Assert.AreEqual("Default", _settingsWindow.GetSetting<string>("TtsOutputDevice"), 
                "Invalid output device should fallback to Default");
        }

        [TestMethod]
        public void DeviceFallback_BothDevicesInvalid_ShouldFallbackBoth()
        {
            // Arrange
            _settingsWindow.SetSetting("SttInputDevice", "Quantum Microphone Alpha");
            _settingsWindow.SetSetting("TtsOutputDevice", "Holographic Sound System");

            // Act
            var result = _settingsWindow.ValidateAndFixAudioDevices();

            // Assert
            Assert.IsFalse(result, "Should indicate that fallback was applied");
            Assert.AreEqual("Default", _settingsWindow.GetSetting<string>("SttInputDevice"), 
                "Invalid input device should fallback to Default");
            Assert.AreEqual("Default", _settingsWindow.GetSetting<string>("TtsOutputDevice"), 
                "Invalid output device should fallback to Default");
        }

        [TestMethod]
        public void DeviceFallback_ValidKnownDevices_ShouldNotFallback()
        {
            // Arrange - Use device names that our mock validation recognizes as valid
            _settingsWindow.SetSetting("SttInputDevice", "USB Microphone");
            _settingsWindow.SetSetting("TtsOutputDevice", "Headphones");

            // Act
            var result = _settingsWindow.ValidateAndFixAudioDevices();

            // Assert
            Assert.IsTrue(result, "Should indicate no fallback was needed");
            Assert.AreEqual("USB Microphone", _settingsWindow.GetSetting<string>("SttInputDevice"), 
                "Valid input device should remain unchanged");
            Assert.AreEqual("Headphones", _settingsWindow.GetSetting<string>("TtsOutputDevice"), 
                "Valid output device should remain unchanged");
        }

        [TestMethod]
        public void DeviceFallback_DefaultDevices_ShouldAlwaysBeValid()
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
        public void DeviceFallback_CaseInsensitiveDefault_ShouldBeValid()
        {
            // Arrange
            _settingsWindow.SetSetting("SttInputDevice", "default");
            _settingsWindow.SetSetting("TtsOutputDevice", "DEFAULT");

            // Act
            var result = _settingsWindow.ValidateAndFixAudioDevices();

            // Assert
            Assert.IsTrue(result, "Default devices with different casing should be valid");
            Assert.AreEqual("default", _settingsWindow.GetSetting<string>("SttInputDevice"));
            Assert.AreEqual("DEFAULT", _settingsWindow.GetSetting<string>("TtsOutputDevice"));
        }

        [TestMethod]
        public void DeviceFallback_EmptyOrNullDevices_ShouldBeHandledAsDefault()
        {
            // Arrange
            _settingsWindow.SetSetting("SttInputDevice", "");
            _settingsWindow.SetSetting("TtsOutputDevice", null);

            // Act
            var result = _settingsWindow.ValidateAndFixAudioDevices();

            // Assert
            Assert.IsTrue(result, "Empty/null devices should be treated as valid (Default)");
        }

        [TestMethod]
        public void DeviceFallback_JsonPersistence_ShouldMaintainFallbackValues()
        {
            // Arrange
            _settingsWindow.SetSetting("SttInputDevice", "Invalid Device Name");
            _settingsWindow.SetSetting("TtsOutputDevice", "Another Invalid Device");

            var exportPath = Path.Combine(Path.GetTempPath(), $"fallback_persistence_{Guid.NewGuid()}.json");

            try
            {
                // Act
                // Apply fallback
                var fallbackResult = _settingsWindow.ValidateAndFixAudioDevices();
                
                // Export the fixed settings
                _settingsWindow.ExportToJson(exportPath);
                
                // Import into new window
                var newWindow = new SettingsWindow(new JsonSettingsProvider());
                newWindow.ImportFromJson(exportPath);

                // Assert
                Assert.IsFalse(fallbackResult, "Initial fallback should have been applied");
                Assert.AreEqual("Default", newWindow.GetSetting<string>("SttInputDevice"), 
                    "Fallback values should persist through JSON export/import");
                Assert.AreEqual("Default", newWindow.GetSetting<string>("TtsOutputDevice"), 
                    "Fallback values should persist through JSON export/import");
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
        public void DeviceFallback_MultipleOperations_ShouldBeConsistent()
        {
            // Arrange
            var invalidInput = "Super Special Microphone";
            var invalidOutput = "Ultra Premium Speakers";
            var validInput = "Built-in Microphone";
            var validOutput = "Built-in Speakers";

            // Act & Assert - Multiple fallback operations should be consistent
            
            // Set invalid devices and fallback
            _settingsWindow.SetSetting("SttInputDevice", invalidInput);
            _settingsWindow.SetSetting("TtsOutputDevice", invalidOutput);
            var result1 = _settingsWindow.ValidateAndFixAudioDevices();
            
            Assert.IsFalse(result1, "Should apply fallback for invalid devices");
            Assert.AreEqual("Default", _settingsWindow.GetSetting<string>("SttInputDevice"));
            Assert.AreEqual("Default", _settingsWindow.GetSetting<string>("TtsOutputDevice"));

            // Set valid devices
            _settingsWindow.SetSetting("SttInputDevice", validInput);
            _settingsWindow.SetSetting("TtsOutputDevice", validOutput);
            var result2 = _settingsWindow.ValidateAndFixAudioDevices();
            
            Assert.IsTrue(result2, "Should not apply fallback for valid devices");
            Assert.AreEqual(validInput, _settingsWindow.GetSetting<string>("SttInputDevice"));
            Assert.AreEqual(validOutput, _settingsWindow.GetSetting<string>("TtsOutputDevice"));

            // Set mixed validity
            _settingsWindow.SetSetting("SttInputDevice", validInput);
            _settingsWindow.SetSetting("TtsOutputDevice", invalidOutput);
            var result3 = _settingsWindow.ValidateAndFixAudioDevices();
            
            Assert.IsFalse(result3, "Should apply fallback for invalid output only");
            Assert.AreEqual(validInput, _settingsWindow.GetSetting<string>("SttInputDevice"));
            Assert.AreEqual("Default", _settingsWindow.GetSetting<string>("TtsOutputDevice"));
        }

        [TestMethod]
        public void DeviceFallback_RoundTripWithInvalidDevices_ShouldApplyFallbackAfterImport()
        {
            // Arrange
            var exportPath = Path.Combine(Path.GetTempPath(), $"roundtrip_fallback_{Guid.NewGuid()}.json");

            try
            {
                // Create settings with invalid devices and export without applying fallback
                var originalSettings = new Dictionary<string, object>
                {
                    ["SttInputDevice"] = "Fictional Microphone Pro",
                    ["TtsOutputDevice"] = "Imaginary Sound System",
                    ["TtsEnabled"] = true,
                    ["VoiceConfidenceThreshold"] = 0.6f
                };

                _testProvider.SaveSettings(originalSettings);

                // Act - Import settings and then apply fallback
                var newWindow = new SettingsWindow(new JsonSettingsProvider());
                newWindow.ImportFromJson(_testFilePath);
                
                // Verify the invalid devices were imported
                Assert.AreEqual("Fictional Microphone Pro", newWindow.GetSetting<string>("SttInputDevice"));
                Assert.AreEqual("Imaginary Sound System", newWindow.GetSetting<string>("TtsOutputDevice"));

                // Apply device fallback
                var fallbackResult = newWindow.ValidateAndFixAudioDevices();

                // Assert
                Assert.IsFalse(fallbackResult, "Should detect invalid devices and apply fallback");
                Assert.AreEqual("Default", newWindow.GetSetting<string>("SttInputDevice"), 
                    "Should fallback invalid input device");
                Assert.AreEqual("Default", newWindow.GetSetting<string>("TtsOutputDevice"), 
                    "Should fallback invalid output device");
                
                // Other settings should remain unchanged
                Assert.AreEqual(true, newWindow.GetSetting<bool>("TtsEnabled"));
                Assert.AreEqual(0.6f, newWindow.GetSetting<float>("VoiceConfidenceThreshold"), 0.01f);
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
        public void DeviceFallback_ValidDeviceList_ShouldCoverExpectedDevices()
        {
            // This test verifies our mock device validation covers reasonable device names
            
            // Test valid input devices
            var validInputDevices = new[]
            {
                "Microphone",
                "USB Microphone", 
                "Built-in Microphone",
                "Default",
                "default",
                ""
            };

            foreach (var device in validInputDevices)
            {
                _settingsWindow.SetSetting("SttInputDevice", device);
                _settingsWindow.SetSetting("TtsOutputDevice", "Speakers"); // Known valid
                var result = _settingsWindow.ValidateAndFixAudioDevices();
                
                Assert.IsTrue(result, $"Device '{device}' should be valid for input");
                Assert.AreEqual(device, _settingsWindow.GetSetting<string>("SttInputDevice"), 
                    $"Valid input device '{device}' should not be changed");
            }

            // Test valid output devices
            var validOutputDevices = new[]
            {
                "Speakers",
                "Headphones",
                "USB Speakers",
                "Built-in Speakers",
                "Default",
                "default", 
                ""
            };

            foreach (var device in validOutputDevices)
            {
                _settingsWindow.SetSetting("SttInputDevice", "Microphone"); // Known valid
                _settingsWindow.SetSetting("TtsOutputDevice", device);
                var result = _settingsWindow.ValidateAndFixAudioDevices();
                
                Assert.IsTrue(result, $"Device '{device}' should be valid for output");
                Assert.AreEqual(device, _settingsWindow.GetSetting<string>("TtsOutputDevice"), 
                    $"Valid output device '{device}' should not be changed");
            }
        }
    }
}