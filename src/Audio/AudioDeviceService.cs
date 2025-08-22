// AudioDeviceService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;

namespace Kinectv1.Audio
{
    /// <summary>
    /// Audio device information with stable identifier
    /// </summary>
    public class AudioDeviceInfo
    {
        public string StableId { get; set; }
        public string FriendlyName { get; set; }
        public int DeviceNumber { get; set; }
        public bool IsInputDevice { get; set; }
        public bool IsDefault { get; set; }
        public string ProductName { get; set; }
        public int Channels { get; set; }
        
        public override string ToString()
        {
            var defaultIndicator = IsDefault ? " (Default)" : "";
            var typeIndicator = IsInputDevice ? "Input" : "Output";
            return $"{FriendlyName}{defaultIndicator} [{typeIndicator}]";
        }
    }

    /// <summary>
    /// Service for enumerating audio devices with stable identifiers
    /// Provides stable device IDs that persist across device reconnections
    /// </summary>
    public static class AudioDeviceService
    {
        /// <summary>
        /// Generate a stable identifier for an audio device
        /// Uses device name and type to create a consistent ID
        /// </summary>
        private static string GenerateStableId(string deviceName, bool isInput, int deviceNumber)
        {
            var typePrefix = isInput ? "input" : "output";
            var cleanName = deviceName?.Replace(" ", "_").Replace("(", "").Replace(")", "") ?? "unknown";
            return $"{typePrefix}_{cleanName}_{deviceNumber}";
        }

        /// <summary>
        /// Get all available audio input devices with stable identifiers
        /// </summary>
        public static List<AudioDeviceInfo> GetInputDevices()
        {
            var devices = new List<AudioDeviceInfo>();
            
            try
            {
                int deviceCount = WaveIn.DeviceCount;
                
                for (int i = 0; i < deviceCount; i++)
                {
                    try
                    {
                        var capabilities = WaveIn.GetCapabilities(i);
                        var deviceName = capabilities.ProductName ?? $"Input Device {i}";
                        
                        var device = new AudioDeviceInfo
                        {
                            StableId = GenerateStableId(deviceName, true, i),
                            FriendlyName = deviceName,
                            DeviceNumber = i,
                            IsInputDevice = true,
                            IsDefault = i == 0, // First device is typically default
                            ProductName = capabilities.ProductName ?? "Unknown",
                            Channels = capabilities.Channels
                        };
                        
                        devices.Add(device);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   Error reading input device {i}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error enumerating input devices: {ex.Message}");
            }
            
            return devices;
        }

        /// <summary>
        /// Get all available audio output devices with stable identifiers
        /// </summary>
        public static List<AudioDeviceInfo> GetOutputDevices()
        {
            var devices = new List<AudioDeviceInfo>();
            
            try
            {
                int deviceCount = WaveOut.DeviceCount;
                
                for (int i = 0; i < deviceCount; i++)
                {
                    try
                    {
                        var capabilities = WaveOut.GetCapabilities(i);
                        var deviceName = capabilities.ProductName ?? $"Output Device {i}";
                        
                        var device = new AudioDeviceInfo
                        {
                            StableId = GenerateStableId(deviceName, false, i),
                            FriendlyName = deviceName,
                            DeviceNumber = i,
                            IsInputDevice = false,
                            IsDefault = i == 0, // First device is typically default
                            ProductName = capabilities.ProductName ?? "Unknown",
                            Channels = capabilities.Channels
                        };
                        
                        devices.Add(device);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   Error reading output device {i}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error enumerating output devices: {ex.Message}");
            }
            
            return devices;
        }

        /// <summary>
        /// Try to resolve a device by its stable ID with graceful fallback
        /// </summary>
        /// <param name="stableId">The stable device identifier</param>
        /// <returns>Device info if found, null if not available</returns>
        public static AudioDeviceInfo TryResolveDevice(string stableId)
        {
            if (string.IsNullOrEmpty(stableId))
                return null;

            try
            {
                // First try to find exact match by stable ID
                var allDevices = GetInputDevices().Concat(GetOutputDevices()).ToList();
                var exactMatch = allDevices.FirstOrDefault(d => d.StableId.Equals(stableId, StringComparison.OrdinalIgnoreCase));
                
                if (exactMatch != null)
                {
                    // Verify device is still functional
                    if (TestDevice(exactMatch))
                    {
                        return exactMatch;
                    }
                    else
                    {
                        Console.WriteLine($"? Device '{exactMatch.FriendlyName}' found but not functional");
                    }
                }

                // Fallback: try to match by device name (in case device number changed)
                var deviceNameFromId = ExtractDeviceNameFromStableId(stableId);
                if (!string.IsNullOrEmpty(deviceNameFromId))
                {
                    var nameMatch = allDevices.FirstOrDefault(d => 
                        d.FriendlyName.Replace(" ", "_").Replace("(", "").Replace(")", "")
                        .Equals(deviceNameFromId, StringComparison.OrdinalIgnoreCase));
                    
                    if (nameMatch != null && TestDevice(nameMatch))
                    {
                        Console.WriteLine($"? Device resolved by name fallback: {nameMatch.FriendlyName}");
                        return nameMatch;
                    }
                }

                // Final fallback: return default device of the appropriate type
                if (stableId.StartsWith("input_", StringComparison.OrdinalIgnoreCase))
                {
                    var defaultInput = GetInputDevices().FirstOrDefault(d => d.IsDefault);
                    if (defaultInput != null && TestDevice(defaultInput))
                    {
                        Console.WriteLine($"? Falling back to default input device: {defaultInput.FriendlyName}");
                        return defaultInput;
                    }
                }
                else if (stableId.StartsWith("output_", StringComparison.OrdinalIgnoreCase))
                {
                    var defaultOutput = GetOutputDevices().FirstOrDefault(d => d.IsDefault);
                    if (defaultOutput != null && TestDevice(defaultOutput))
                    {
                        Console.WriteLine($"? Falling back to default output device: {defaultOutput.FriendlyName}");
                        return defaultOutput;
                    }
                }

                Console.WriteLine($"? Unable to resolve device with stable ID: {stableId}");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error resolving device '{stableId}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Extract device name from stable ID for fallback matching
        /// </summary>
        private static string ExtractDeviceNameFromStableId(string stableId)
        {
            if (string.IsNullOrEmpty(stableId))
                return null;

            try
            {
                var parts = stableId.Split('_');
                if (parts.Length >= 3)
                {
                    // Skip type prefix and device number suffix, rejoin middle parts
                    return string.Join("_", parts.Skip(1).Take(parts.Length - 2));
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Test if a device is available and functional
        /// </summary>
        private static bool TestDevice(AudioDeviceInfo device)
        {
            if (device == null)
                return false;

            try
            {
                if (device.IsInputDevice)
                {
                    using (var waveIn = new WaveInEvent { DeviceNumber = device.DeviceNumber })
                    {
                        waveIn.WaveFormat = new WaveFormat(16000, 16, 1);
                        return true;
                    }
                }
                else
                {
                    using (var waveOut = new WaveOutEvent { DeviceNumber = device.DeviceNumber })
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Device test failed for {device.FriendlyName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get device by stable ID with settings integration
        /// Stores the stable ID in settings and can resolve it later
        /// </summary>
        public static AudioDeviceInfo GetConfiguredInputDevice()
        {
            try
            {
                var stableId = AppSettings.LoadSttInputDevice();
                if (string.IsNullOrEmpty(stableId) || stableId == "Default")
                {
                    // Return first available input device as default
                    var defaultDevice = GetInputDevices().FirstOrDefault();
                    return defaultDevice;
                }

                // Try to resolve by stable ID
                var device = TryResolveDevice(stableId);
                if (device != null)
                {
                    return device;
                }

                // Fallback to default if configured device not found
                Console.WriteLine($"? Configured input device '{stableId}' not found, using default");
                return GetInputDevices().FirstOrDefault();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error getting configured input device: {ex.Message}");
                return GetInputDevices().FirstOrDefault();
            }
        }

        /// <summary>
        /// Get configured output device with settings integration
        /// </summary>
        public static AudioDeviceInfo GetConfiguredOutputDevice()
        {
            try
            {
                var stableId = AppSettings.LoadTtsOutputDevice();
                if (string.IsNullOrEmpty(stableId) || stableId == "Default")
                {
                    // Return first available output device as default
                    var defaultDevice = GetOutputDevices().FirstOrDefault();
                    return defaultDevice;
                }

                // Try to resolve by stable ID
                var device = TryResolveDevice(stableId);
                if (device != null)
                {
                    return device;
                }

                // Fallback to default if configured device not found
                Console.WriteLine($"? Configured output device '{stableId}' not found, using default");
                return GetOutputDevices().FirstOrDefault();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error getting configured output device: {ex.Message}");
                return GetOutputDevices().FirstOrDefault();
            }
        }

        /// <summary>
        /// Save device selection using stable ID
        /// </summary>
        public static void SaveInputDeviceSelection(AudioDeviceInfo device)
        {
            if (device?.IsInputDevice == true)
            {
                AppSettings.SaveSttInputDevice(device.StableId);
                Console.WriteLine($"STT: Saved input device stable ID: {device.StableId} ({device.FriendlyName})");
            }
        }

        /// <summary>
        /// Save output device selection using stable ID
        /// </summary>
        public static void SaveOutputDeviceSelection(AudioDeviceInfo device)
        {
            if (device?.IsInputDevice == false)
            {
                AppSettings.SaveTtsOutputDevice(device.StableId);
                Console.WriteLine($"TTS: Saved output device stable ID: {device.StableId} ({device.FriendlyName})");
            }
        }

        /// <summary>
        /// Demonstrate device enumeration and stable ID functionality
        /// </summary>
        public static void DemonstrateDeviceEnumeration()
        {
            try
            {
                Console.WriteLine("🎧 === AUDIO DEVICE SERVICE DEMONSTRATION ===");

                // Show input devices
                Console.WriteLine("\n🎤 Input Devices:");
                var inputDevices = GetInputDevices();
                foreach (var device in inputDevices)
                {
                    var testResult = TestDevice(device) ? "✅" : "❌";
                    Console.WriteLine($"   {device.StableId}: {device.FriendlyName} {testResult}");
                }

                // Show output devices
                Console.WriteLine("\n🔊 Output Devices:");
                var outputDevices = GetOutputDevices();
                foreach (var device in outputDevices)
                {
                    var testResult = TestDevice(device) ? "✅" : "❌";
                    Console.WriteLine($"   {device.StableId}: {device.FriendlyName} {testResult}");
                }

                // Test device resolution
                Console.WriteLine("\n🔍 Device Resolution Test:");
                if (inputDevices.Any())
                {
                    var firstInput = inputDevices.First();
                    var resolved = TryResolveDevice(firstInput.StableId);
                    Console.WriteLine($"   Test resolve '{firstInput.StableId}': {(resolved != null ? "✅ Success" : "❌ Failed")}");
                }

                // Test graceful fallback
                Console.WriteLine("\n🛡️ Graceful Fallback Test:");
                var fakerId = "input_nonexistent_device_99";
                var fallback = TryResolveDevice(fakerId);
                Console.WriteLine($"   Test fallback for '{fakerId}': {(fallback != null ? $"✅ Fallback to {fallback.FriendlyName}" : "❌ No fallback")}");

                Console.WriteLine("\n🎧 === END DEMONSTRATION ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error in demonstration: {ex.Message}");
            }
        }
    }
}