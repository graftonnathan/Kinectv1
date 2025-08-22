using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kinectv1.Tests
{
    /// <summary>
    /// Unit tests for JsonSettingsProvider
    /// Tests round-trip serialization, type conversion, and persistence
    /// </summary>
    [TestClass]
    public class JsonSettingsProviderTests
    {
        private string _testFilePath;
        private JsonSettingsProvider _provider;

        [TestInitialize]
        public void TestInitialize()
        {
            _testFilePath = Path.Combine(Path.GetTempPath(), $"test_settings_{Guid.NewGuid()}.json");
            _provider = new JsonSettingsProvider(_testFilePath);
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
        public void SaveSettings_WithValidData_ShouldCreateFile()
        {
            // Arrange
            var settings = new Dictionary<string, object>
            {
                ["TestString"] = "Hello World",
                ["TestInt"] = 42,
                ["TestBool"] = true,
                ["TestFloat"] = 3.14f
            };

            // Act
            _provider.SaveSettings(settings);

            // Assert
            Assert.IsTrue(File.Exists(_testFilePath), "Settings file should be created");
            var fileContent = File.ReadAllText(_testFilePath);
            Assert.IsFalse(string.IsNullOrWhiteSpace(fileContent), "File should contain JSON data");
        }

        [TestMethod]
        public void LoadSettings_FromExistingFile_ShouldReturnCorrectData()
        {
            // Arrange
            var originalSettings = new Dictionary<string, object>
            {
                ["TestString"] = "Hello World",
                ["TestInt"] = 42,
                ["TestBool"] = true,
                ["TestFloat"] = 3.14f
            };
            _provider.SaveSettings(originalSettings);

            // Act
            var loadedSettings = _provider.LoadSettings();

            // Assert
            Assert.IsNotNull(loadedSettings, "Loaded settings should not be null");
            Assert.AreEqual(4, loadedSettings.Count, "Should load all 4 settings");
            Assert.AreEqual("Hello World", loadedSettings["TestString"].ToString());
            Assert.AreEqual(42L, loadedSettings["TestInt"]); // JSON deserializes ints as long
            Assert.AreEqual(true, loadedSettings["TestBool"]);
        }

        [TestMethod]
        public void LoadSettings_FromNonExistentFile_ShouldReturnEmptyDictionary()
        {
            // Arrange
            var nonExistentProvider = new JsonSettingsProvider("non_existent_file.json");

            // Act
            var loadedSettings = nonExistentProvider.LoadSettings();

            // Assert
            Assert.IsNotNull(loadedSettings, "Should return empty dictionary, not null");
            Assert.AreEqual(0, loadedSettings.Count, "Should return empty dictionary");
        }

        [TestMethod]
        public void GetSetting_WithExistingKey_ShouldReturnCorrectValue()
        {
            // Arrange
            var settings = new Dictionary<string, object>
            {
                ["TestString"] = "Hello World",
                ["TestInt"] = 42,
                ["TestBool"] = true,
                ["TestFloat"] = 3.14f
            };
            _provider.SaveSettings(settings);

            // Act & Assert
            Assert.AreEqual("Hello World", _provider.GetSetting<string>("TestString"));
            Assert.AreEqual(42, _provider.GetSetting<int>("TestInt"));
            Assert.AreEqual(true, _provider.GetSetting<bool>("TestBool"));
            Assert.AreEqual(3.14f, _provider.GetSetting<float>("TestFloat"), 0.01f);
        }

        [TestMethod]
        public void GetSetting_WithNonExistentKey_ShouldReturnDefault()
        {
            // Arrange
            _provider.SaveSettings(new Dictionary<string, object>());

            // Act & Assert
            Assert.AreEqual("default", _provider.GetSetting<string>("NonExistent", "default"));
            Assert.AreEqual(99, _provider.GetSetting<int>("NonExistent", 99));
            Assert.AreEqual(false, _provider.GetSetting<bool>("NonExistent", false));
        }

        [TestMethod]
        public void SetSetting_WithNewValue_ShouldPersistCorrectly()
        {
            // Arrange
            var key = "TestKey";
            var value = "TestValue";

            // Act
            _provider.SetSetting(key, value);

            // Assert
            var loadedValue = _provider.GetSetting<string>(key);
            Assert.AreEqual(value, loadedValue, "Setting should persist correctly");
        }

        [TestMethod]
        public void RoundTrip_StringValues_ShouldMaintainEquality()
        {
            // Arrange
            var testValues = new[]
            {
                "Simple string",
                "",
                "String with spaces and special chars: !@#$%^&*()",
                "Unicode: éññø 中文 🎵"
            };

            // Act & Assert
            foreach (var value in testValues)
            {
                _provider.SetSetting("test", value);
                var retrieved = _provider.GetSetting<string>("test");
                Assert.AreEqual(value, retrieved, $"String round-trip failed for: {value}");
            }
        }

        [TestMethod]
        public void RoundTrip_BooleanValues_ShouldMaintainEquality()
        {
            // Arrange
            var testValues = new[] { true, false };

            // Act & Assert
            foreach (var value in testValues)
            {
                _provider.SetSetting("test", value);
                var retrieved = _provider.GetSetting<bool>("test");
                Assert.AreEqual(value, retrieved, $"Boolean round-trip failed for: {value}");
            }
        }

        [TestMethod]
        public void RoundTrip_IntegerValues_ShouldMaintainEquality()
        {
            // Arrange
            var testValues = new[] { 0, 1, -1, 42, int.MaxValue, int.MinValue };

            // Act & Assert
            foreach (var value in testValues)
            {
                _provider.SetSetting("test", value);
                var retrieved = _provider.GetSetting<int>("test");
                Assert.AreEqual(value, retrieved, $"Integer round-trip failed for: {value}");
            }
        }

        [TestMethod]
        public void RoundTrip_FloatValues_ShouldMaintainEquality()
        {
            // Arrange
            var testValues = new[] { 0.0f, 1.0f, -1.0f, 3.14f, 0.5f, 0.123456f };

            // Act & Assert
            foreach (var value in testValues)
            {
                _provider.SetSetting("test", value);
                var retrieved = _provider.GetSetting<float>("test");
                Assert.AreEqual(value, retrieved, 0.0001f, $"Float round-trip failed for: {value}");
            }
        }

        [TestMethod]
        public void RoundTrip_DoubleValues_ShouldMaintainEquality()
        {
            // Arrange
            var testValues = new[] { 0.0, 1.0, -1.0, 3.14159265359, 0.5, 0.123456789 };

            // Act & Assert
            foreach (var value in testValues)
            {
                _provider.SetSetting("test", value);
                var retrieved = _provider.GetSetting<double>("test");
                Assert.AreEqual(value, retrieved, 0.0000001, $"Double round-trip failed for: {value}");
            }
        }

        [TestMethod]
        public void RoundTrip_ComplexSettingsSet_ShouldMaintainAllValues()
        {
            // Arrange
            var originalSettings = new Dictionary<string, object>
            {
                ["AppScenario"] = "Discord",
                ["TtsEnabled"] = true,
                ["DiscordBotEnabled"] = false,
                ["VoiceConfidenceThreshold"] = 0.75f,
                ["VoiceActivityThreshold"] = 250.0f,
                ["SttInputDevice"] = "USB Microphone",
                ["TtsOutputDevice"] = "Speakers",
                ["LocalTtsVolume"] = 0.8,
                ["WindowWidth"] = 1200.0,
                ["WindowHeight"] = 800.0,
                ["DarkMode"] = true
            };

            // Act
            _provider.SaveSettings(originalSettings);
            var loadedSettings = _provider.LoadSettings();

            // Assert
            Assert.AreEqual(originalSettings.Count, loadedSettings.Count, "Should load same number of settings");
            
            foreach (var kvp in originalSettings)
            {
                Assert.IsTrue(loadedSettings.ContainsKey(kvp.Key), $"Should contain key: {kvp.Key}");
                
                // Special handling for numeric types that may be deserialized differently
                if (kvp.Value is float floatVal)
                {
                    var retrieved = _provider.GetSetting<float>(kvp.Key);
                    Assert.AreEqual(floatVal, retrieved, 0.0001f, $"Float value mismatch for {kvp.Key}");
                }
                else if (kvp.Value is double doubleVal)
                {
                    var retrieved = _provider.GetSetting<double>(kvp.Key);
                    Assert.AreEqual(doubleVal, retrieved, 0.0000001, $"Double value mismatch for {kvp.Key}");
                }
                else if (kvp.Value is int intVal)
                {
                    var retrieved = _provider.GetSetting<int>(kvp.Key);
                    Assert.AreEqual(intVal, retrieved, $"Int value mismatch for {kvp.Key}");
                }
                else
                {
                    Assert.AreEqual(kvp.Value.ToString(), loadedSettings[kvp.Key].ToString(), 
                        $"Value mismatch for {kvp.Key}");
                }
            }
        }

        [TestMethod]
        public void SettingsFileExists_WithExistingFile_ShouldReturnTrue()
        {
            // Arrange
            _provider.SaveSettings(new Dictionary<string, object> { ["test"] = "value" });

            // Act
            var exists = _provider.SettingsFileExists();

            // Assert
            Assert.IsTrue(exists, "Should detect existing settings file");
        }

        [TestMethod]
        public void SettingsFileExists_WithNonExistentFile_ShouldReturnFalse()
        {
            // Arrange
            var nonExistentProvider = new JsonSettingsProvider("definitely_does_not_exist.json");

            // Act
            var exists = nonExistentProvider.SettingsFileExists();

            // Assert
            Assert.IsFalse(exists, "Should detect non-existent settings file");
        }

        [TestMethod]
        public void DeleteSettingsFile_WithExistingFile_ShouldRemoveFile()
        {
            // Arrange
            _provider.SaveSettings(new Dictionary<string, object> { ["test"] = "value" });
            Assert.IsTrue(File.Exists(_testFilePath), "File should exist before deletion");

            // Act
            _provider.DeleteSettingsFile();

            // Assert
            Assert.IsFalse(File.Exists(_testFilePath), "File should be deleted");
        }

        [TestMethod]
        public void DeleteSettingsFile_WithNonExistentFile_ShouldNotThrow()
        {
            // Arrange
            Assert.IsFalse(File.Exists(_testFilePath), "File should not exist");

            // Act & Assert
            try
            {
                _provider.DeleteSettingsFile();
                // Should not throw
            }
            catch (Exception ex)
            {
                Assert.Fail($"DeleteSettingsFile should not throw for non-existent file: {ex.Message}");
            }
        }
    }
}