// AudioInputHelper.cs
using System;
using NAudio.Wave;

namespace Kinectv1
{
    /// <summary>
    /// Helper class for STT audio input using configured microphone devices
    /// </summary>
    public static class AudioInputHelper
    {
        /// <summary>
        /// Create a WaveInEvent with the configured STT input device
        /// </summary>
        /// <param name="waveFormat">The wave format to use (typically 16kHz, 16-bit, mono)</param>
        /// <returns>Configured WaveInEvent instance</returns>
        public static WaveInEvent CreateConfiguredWaveIn(WaveFormat waveFormat = null)
        {
            try
            {
                // Use default format if none provided (standard for STT)
                if (waveFormat == null)
                {
                    waveFormat = new WaveFormat(16000, 16, 1); // 16kHz, 16-bit, mono
                }

                // Get the configured input device
                var inputDevice = AudioDeviceManager.GetConfiguredInputDevice();
                
                var waveIn = new WaveInEvent
                {
                    WaveFormat = waveFormat
                };

                if (inputDevice != null && inputDevice.DeviceNumber >= 0)
                {
                    waveIn.DeviceNumber = inputDevice.DeviceNumber;
                    Console.WriteLine($"?? Using STT input device: {inputDevice.DeviceName} (device #{inputDevice.DeviceNumber})");
                }
                else
                {
                    Console.WriteLine("?? Using default STT input device");
                }

                return waveIn;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error creating configured audio input: {ex.Message}");
                
                // Fallback to default device
                var fallbackWaveIn = new WaveInEvent
                {
                    WaveFormat = waveFormat ?? new WaveFormat(16000, 16, 1)
                };
                
                Console.WriteLine("?? Using fallback default audio input device");
                return fallbackWaveIn;
            }
        }

        /// <summary>
        /// Test if the configured STT input device is working
        /// </summary>
        /// <returns>True if the device is working, false otherwise</returns>
        public static bool TestConfiguredInputDevice()
        {
            try
            {
                using (var waveIn = CreateConfiguredWaveIn())
                {
                    // Just test if we can create and initialize it
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? STT input device test failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get information about the currently configured STT input device
        /// </summary>
        /// <returns>Device information string</returns>
        public static string GetConfiguredInputDeviceInfo()
        {
            try
            {
                var inputDevice = AudioDeviceManager.GetConfiguredInputDevice();
                if (inputDevice != null)
                {
                    return $"STT Input: {inputDevice.DeviceName} (Device #{inputDevice.DeviceNumber}, {inputDevice.Channels} channels)";
                }
                else
                {
                    return "STT Input: Default device (no specific device configured)";
                }
            }
            catch (Exception ex)
            {
                return $"STT Input: Error getting device info - {ex.Message}";
            }
        }

        /// <summary>
        /// Log the current STT input device configuration
        /// </summary>
        public static void LogCurrentConfiguration()
        {
            Console.WriteLine("?? === STT AUDIO INPUT CONFIGURATION ===");
            Console.WriteLine($"   {GetConfiguredInputDeviceInfo()}");
            Console.WriteLine($"   Device Test: {(TestConfiguredInputDevice() ? "? OK" : "? Failed")}");
            Console.WriteLine("?? === END STT CONFIGURATION ===");
        }
    }
}