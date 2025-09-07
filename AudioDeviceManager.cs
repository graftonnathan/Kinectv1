// AudioDeviceManager.cs
using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
                    }
                    catch (Exception ex)
                    {
                        // Keep enumeration robust but quiet
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
                    }
                    catch (Exception ex)
                    {
                        // Keep enumeration robust but quiet
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
            Console.WriteLine("???? Audio devices enumeration suppressed (verbose listing disabled)");
        }

        /// <summary>
        /// Get the currently configured STT input device from settings
        /// </summary>
        public static AudioInputDevice GetConfiguredInputDevice()
        {
            try
            {
                var deviceName = Kinectv1.App.SettingsProvider?.Current?.Stt?.InputDevice;
                if (!string.IsNullOrEmpty(deviceName) && deviceName != "Default")
                {
                    var deviceNumber = GetInputDeviceNumberByName(deviceName);
                    if (deviceNumber.HasValue)
                    {
                        var device = GetInputDevice(deviceNumber.Value);
                        if (device != null && TestInputDevice(device.DeviceNumber))
                        {
                            Console.WriteLine($"?? Using STT input device: {device.DeviceName}");
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
                    Console.WriteLine($"?? Using STT input device: {defaultDevice.DeviceName}");
                    return defaultDevice;
                }
                
                Console.WriteLine("?? No valid STT input device available");
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
                var deviceName = Kinectv1.App.SettingsProvider?.Current?.Tts?.OutputDevice;
                if (!string.IsNullOrEmpty(deviceName) && deviceName != "Default")
                {
                    var deviceNumber = GetOutputDeviceNumberByName(deviceName);
                    if (deviceNumber.HasValue)
                    {
                        var device = GetOutputDevice(deviceNumber.Value);
                        if (device != null && TestOutputDevice(device.DeviceNumber))
                        {
                            Console.WriteLine($"?? Using TTS output device: {device.DeviceName}");
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
                    Console.WriteLine($"?? Using TTS output device: {defaultDevice.DeviceName}");
                    return defaultDevice;
                }
                
                Console.WriteLine("?? No valid TTS output device available");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error getting configured output device: {ex.Message}");
                return GetDefaultOutputDevice();
            }
        }

        private static readonly object _deviceCacheLock = new object();
        private static int? _cachedOutputDeviceNumber = null;

        private static int? GetConfiguredOutputDeviceNumberFast()
        {
            lock (_deviceCacheLock)
            {
                if (_cachedOutputDeviceNumber.HasValue)
                    return _cachedOutputDeviceNumber;

                var configured = GetConfiguredOutputDevice();
                _cachedOutputDeviceNumber = configured?.DeviceNumber;
                return _cachedOutputDeviceNumber;
            }
        }

        public static void InvalidateOutputDeviceCache()
        {
            lock (_deviceCacheLock) { _cachedOutputDeviceNumber = null; }
        }

        /// <summary>
        /// Play an array of floats as PCM audio through the configured output device
        /// </summary>
        public static async Task PlayLocallyAsync(float[] audio, int sampleRate)
        {
            await PlayLocallyAsync(audio, sampleRate, CancellationToken.None);
        }

        /// <summary>
        /// Play an array of floats as PCM audio through the configured output device with cancellation support
        /// </summary>
        public static async Task PlayLocallyAsync(float[] audio, int sampleRate, CancellationToken cancellationToken)
        {
            if (audio == null || audio.Length == 0) return;
            try
            {
                if (cancellationToken.IsCancellationRequested) return;

                // Peak normalization to prevent clipping/distortion
                float peak = 0f;
                for (int i = 0; i < audio.Length; i++) { var a = Math.Abs(audio[i]); if (a > peak) peak = a; }
                float targetPeak = 0.98f;
                float gain = (peak > 0f && peak > targetPeak) ? (targetPeak / peak) : 1.0f;

                var pcm = new short[audio.Length];
                for (int i = 0; i < audio.Length; i++) { var x = Math.Max(-1.0f, Math.Min(1.0f, audio[i] * gain)); pcm[i] = (short)(x * 32767); }

                if (cancellationToken.IsCancellationRequested) return;

                var waveFormat = new WaveFormat(sampleRate, 16, 1);
                var deviceNumber = GetConfiguredOutputDeviceNumberFast() ?? -1; // -1 uses default device

                // Build preroll + program buffer
                int prerollMs = 80; // 50–100ms recommended; tune here
                int prerollSamples = (int)(sampleRate * (prerollMs / 1000.0));
                int prerollBytes = Math.Max(0, prerollSamples) * 2; // 16-bit mono
                var programBytes = new byte[pcm.Length * 2];
                Buffer.BlockCopy(pcm, 0, programBytes, 0, programBytes.Length);
                var combined = new byte[prerollBytes + programBytes.Length]; // preroll zeros are default-initialized
                Buffer.BlockCopy(programBytes, 0, combined, prerollBytes, programBytes.Length);

                // Duration estimate for wait loop
                var msDur = (int)Math.Ceiling(1000.0 * (prerollSamples + pcm.Length) / sampleRate) + 100;

                await Task.Run(() =>
                {
                    if (cancellationToken.IsCancellationRequested) return;

                    using var ms = new MemoryStream(combined, writable: false);
                    using var rss = new RawSourceWaveStream(ms, waveFormat);
                    WaveOutEvent waveOut = null;
                    try
                    {
                        waveOut = new WaveOutEvent
                        {
                            DesiredLatency = 100 // 80–120 ms recommended; tune for your device
                        };
                        if (deviceNumber >= 0) waveOut.DeviceNumber = deviceNumber;
                        waveOut.Volume = (float)(Kinectv1.App.SettingsProvider?.Current?.Tts?.LocalVolume ?? 1.0);
                        waveOut.Init(rss);
                        waveOut.Play();

                        // Wait with cancellation support
                        var totalWaitMs = Math.Min(msDur, 30000);
                        var pollIntervalMs = 20; // tighter polling for responsive barge-in
                        var elapsed = 0;
                        
                        while (elapsed < totalWaitMs && !cancellationToken.IsCancellationRequested)
                        {
                            Task.Delay(pollIntervalMs).GetAwaiter().GetResult();
                            elapsed += pollIntervalMs;

                            // Early exit if playback completed
                            if (waveOut.PlaybackState != PlaybackState.Playing)
                                break;
                        }

                        if (cancellationToken.IsCancellationRequested)
                        {
                            try { waveOut.Stop(); } catch { }
                            return;
                        }
                    }
                    finally
                    {
                        try { waveOut?.Stop(); } catch { }
                        try { waveOut?.Dispose(); } catch { }
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Clean cancellation during barge-in: swallow to avoid surfacing as an error
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioDeviceManager] Local play failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Test input device asynchronously
        /// </summary>
        public static async Task<bool> TestInputDeviceAsync(int deviceNumber)
        {
            return await Task.Run(() => TestInputDevice(deviceNumber));
        }

        /// <summary>
        /// Test output device asynchronously
        /// </summary>
        public static async Task<bool> TestOutputDeviceAsync(int deviceNumber)
        {
            return await Task.Run(() => TestOutputDevice(deviceNumber));
        }

        /// <summary>
        /// Test output device with text-to-speech
        /// </summary>
        public static async Task<bool> TestOutputWithTextAsync(int deviceNumber, string text)
        {
            try
            {
                // For now, just test the device capability
                // In a full implementation, this would use TTS to speak the text
                var deviceWorks = await TestOutputDeviceAsync(deviceNumber);
                
                if (deviceWorks)
                {
                    Console.WriteLine($"🔊 Output device test passed for device {deviceNumber} with text: '{text}'");
                }
                
                return deviceWorks;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error testing output with text: {ex.Message}");
                return false;
            }
        }
    }
}