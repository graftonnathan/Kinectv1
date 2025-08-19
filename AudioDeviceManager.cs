// AudioDeviceManager.cs
using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;

namespace Kinectv1
{
    /// <summary>
    /// Manages audio device enumeration and selection for STT (microphone input) and TTS (audio output)
    /// </summary>
    public static class AudioDeviceManager
    {
        /// <summary>
        /// Audio input device information
        /// </summary>
        public class AudioInputDevice
        {
            public int DeviceNumber { get; set; }
            public string DeviceName { get; set; }
            public string ProductName { get; set; }
            public int Channels { get; set; }
            public WaveInCapabilities Capabilities { get; set; }
            public bool IsDefault { get; set; }
            
            public override string ToString()
            {
                var defaultIndicator = IsDefault ? " (Default)" : "";
                return $"{DeviceName}{defaultIndicator}";
            }
        }

        /// <summary>
        /// Audio output device information
        /// </summary>
        public class AudioOutputDevice
        {
            public int DeviceNumber { get; set; }
            public string DeviceName { get; set; }
            public string ProductName { get; set; }
            public int Channels { get; set; }
            public WaveOutCapabilities Capabilities { get; set; }
            public bool IsDefault { get; set; }
            
            public override string ToString()
            {
                var defaultIndicator = IsDefault ? " (Default)" : "";
                return $"{DeviceName}{defaultIndicator}";
            }
        }

        /// <summary>
        /// Get all available microphone input devices for STT
        /// </summary>
        public static List<AudioInputDevice> GetInputDevices()
        {
            var devices = new List<AudioInputDevice>();
            
            try
            {
                int deviceCount = WaveIn.DeviceCount;
                Console.WriteLine($"?? Found {deviceCount} input devices");
                
                for (int i = 0; i < deviceCount; i++)
                {
                    try
                    {
                        var capabilities = WaveIn.GetCapabilities(i);
                        var device = new AudioInputDevice
                        {
                            DeviceNumber = i,
                            DeviceName = capabilities.ProductName ?? $"Input Device {i}",
                            ProductName = capabilities.ProductName ?? "Unknown",
                            Channels = capabilities.Channels,
                            Capabilities = capabilities,
                            IsDefault = i == 0 // First device is typically default
                        };
                        
                        devices.Add(device);
                        Console.WriteLine($"   Input {i}: {device.DeviceName} ({device.Channels} channels)");
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
        /// Get all available audio output devices for TTS
        /// </summary>
        public static List<AudioOutputDevice> GetOutputDevices()
        {
            var devices = new List<AudioOutputDevice>();
            
            try
            {
                int deviceCount = WaveOut.DeviceCount;
                Console.WriteLine($"?? Found {deviceCount} output devices");
                
                for (int i = 0; i < deviceCount; i++)
                {
                    try
                    {
                        var capabilities = WaveOut.GetCapabilities(i);
                        var device = new AudioOutputDevice
                        {
                            DeviceNumber = i,
                            DeviceName = capabilities.ProductName ?? $"Output Device {i}",
                            ProductName = capabilities.ProductName ?? "Unknown",
                            Channels = capabilities.Channels,
                            Capabilities = capabilities,
                            IsDefault = i == 0 // First device is typically default
                        };
                        
                        devices.Add(device);
                        Console.WriteLine($"   Output {i}: {device.DeviceName} ({device.Channels} channels)");
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
        /// Get input device by device number
        /// </summary>
        public static AudioInputDevice GetInputDevice(int deviceNumber)
        {
            var devices = GetInputDevices();
            return devices.FirstOrDefault(d => d.DeviceNumber == deviceNumber);
        }

        /// <summary>
        /// Get output device by device number
        /// </summary>
        public static AudioOutputDevice GetOutputDevice(int deviceNumber)
        {
            var devices = GetOutputDevices();
            return devices.FirstOrDefault(d => d.DeviceNumber == deviceNumber);
        }

        /// <summary>
        /// Get default input device
        /// </summary>
        public static AudioInputDevice GetDefaultInputDevice()
        {
            var devices = GetInputDevices();
            return devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();
        }

        /// <summary>
        /// Get default output device
        /// </summary>
        public static AudioOutputDevice GetDefaultOutputDevice()
        {
            var devices = GetOutputDevices();
            return devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();
        }

        /// <summary>
        /// Test if an input device is available and working
        /// </summary>
        public static bool TestInputDevice(int deviceNumber)
        {
            try
            {
                using (var waveIn = new WaveInEvent { DeviceNumber = deviceNumber })
                {
                    waveIn.WaveFormat = new WaveFormat(16000, 16, 1);
                    // Just checking if we can create it without exceptions
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Input device {deviceNumber} test failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Test if an output device is available and working
        /// </summary>
        public static bool TestOutputDevice(int deviceNumber)
        {
            try
            {
                using (var waveOut = new WaveOutEvent { DeviceNumber = deviceNumber })
                {
                    // Just checking if we can create it without exceptions
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Output device {deviceNumber} test failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get a formatted list of input devices for UI display
        /// </summary>
        public static List<string> GetInputDeviceNames()
        {
            var devices = GetInputDevices();
            return devices.Select(d => d.ToString()).ToList();
        }

        /// <summary>
        /// Get a formatted list of output devices for UI display
        /// </summary>
        public static List<string> GetOutputDeviceNames()
        {
            var devices = GetOutputDevices();
            return devices.Select(d => d.ToString()).ToList();
        }

        /// <summary>
        /// Get input device number by name
        /// </summary>
        public static int? GetInputDeviceNumberByName(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName)) return null;
            
            var devices = GetInputDevices();
            var device = devices.FirstOrDefault(d => d.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase) ||
                                                   d.ToString().Equals(deviceName, StringComparison.OrdinalIgnoreCase));
            return device?.DeviceNumber;
        }

        /// <summary>
        /// Get output device number by name
        /// </summary>
        public static int? GetOutputDeviceNumberByName(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName)) return null;
            
            var devices = GetOutputDevices();
            var device = devices.FirstOrDefault(d => d.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase) ||
                                                   d.ToString().Equals(deviceName, StringComparison.OrdinalIgnoreCase));
            return device?.DeviceNumber;
        }

        /// <summary>
        /// Log all available devices for debugging
        /// </summary>
        public static void LogAllDevices()
        {
            Console.WriteLine("???? === AUDIO DEVICE ENUMERATION ===");
            
            Console.WriteLine("?? Input Devices (Microphones):");
            var inputDevices = GetInputDevices();
            if (inputDevices.Any())
            {
                foreach (var device in inputDevices)
                {
                    Console.WriteLine($"   [{device.DeviceNumber}] {device.DeviceName}");
                    Console.WriteLine($"       Product: {device.ProductName}");
                    Console.WriteLine($"       Channels: {device.Channels}");
                    Console.WriteLine($"       Default: {device.IsDefault}");
                    Console.WriteLine($"       Test Result: {(TestInputDevice(device.DeviceNumber) ? "? OK" : "? Failed")}");
                }
            }
            else
            {
                Console.WriteLine("   ? No input devices found");
            }
            
            Console.WriteLine("?? Output Devices (Speakers/Headphones):");
            var outputDevices = GetOutputDevices();
            if (outputDevices.Any())
            {
                foreach (var device in outputDevices)
                {
                    Console.WriteLine($"   [{device.DeviceNumber}] {device.DeviceName}");
                    Console.WriteLine($"       Product: {device.ProductName}");
                    Console.WriteLine($"       Channels: {device.Channels}");
                    Console.WriteLine($"       Default: {device.IsDefault}");
                    Console.WriteLine($"       Test Result: {(TestOutputDevice(device.DeviceNumber) ? "? OK" : "? Failed")}");
                }
            }
            else
            {
                Console.WriteLine("   ? No output devices found");
            }
            
            Console.WriteLine("???? === END AUDIO DEVICE ENUMERATION ===");
        }

        /// <summary>
        /// Get the currently configured STT input device from settings
        /// </summary>
        public static AudioInputDevice GetConfiguredInputDevice()
        {
            try
            {
                var deviceName = AppSettings.LoadSttInputDevice();
                if (!string.IsNullOrEmpty(deviceName) && deviceName != "Default")
                {
                    var deviceNumber = GetInputDeviceNumberByName(deviceName);
                    if (deviceNumber.HasValue)
                    {
                        var device = GetInputDevice(deviceNumber.Value);
                        if (device != null && TestInputDevice(device.DeviceNumber))
                        {
                            Console.WriteLine($"?? Using configured STT input device: {device.DeviceName}");
                            return device;
                        }
                        else
                        {
                            Console.WriteLine($"?? Configured STT input device '{deviceName}' not available, falling back to default");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"?? Configured STT input device '{deviceName}' not found, falling back to default");
                    }
                }
                
                // Fall back to default device
                var defaultDevice = GetDefaultInputDevice();
                if (defaultDevice != null)
                {
                    Console.WriteLine($"?? Using default STT input device: {defaultDevice.DeviceName}");
                    return defaultDevice;
                }
                
                Console.WriteLine("? No valid STT input device available");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error getting configured input device: {ex.Message}");
                return GetDefaultInputDevice();
            }
        }

        /// <summary>
        /// Get the currently configured TTS output device from settings
        /// </summary>
        public static AudioOutputDevice GetConfiguredOutputDevice()
        {
            try
            {
                var deviceName = AppSettings.LoadTtsOutputDevice();
                if (!string.IsNullOrEmpty(deviceName) && deviceName != "Default")
                {
                    var deviceNumber = GetOutputDeviceNumberByName(deviceName);
                    if (deviceNumber.HasValue)
                    {
                        var device = GetOutputDevice(deviceNumber.Value);
                        if (device != null && TestOutputDevice(device.DeviceNumber))
                        {
                            Console.WriteLine($"?? Using configured TTS output device: {device.DeviceName}");
                            return device;
                        }
                        else
                        {
                            Console.WriteLine($"?? Configured TTS output device '{deviceName}' not available, falling back to default");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"?? Configured TTS output device '{deviceName}' not found, falling back to default");
                    }
                }
                
                // Fall back to default device
                var defaultDevice = GetDefaultOutputDevice();
                if (defaultDevice != null)
                {
                    Console.WriteLine($"?? Using default TTS output device: {defaultDevice.DeviceName}");
                    return defaultDevice;
                }
                
                Console.WriteLine("? No valid TTS output device available");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error getting configured output device: {ex.Message}");
                return GetDefaultOutputDevice();
            }
        }
    }
}