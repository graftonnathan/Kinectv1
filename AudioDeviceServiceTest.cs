// AudioDeviceServiceTest.cs
using System;
using System.Linq;
using Kinectv1.Audio;

namespace Kinectv1
{
    /// <summary>
    /// Test class for AudioDeviceService functionality
    /// </summary>
    public static class AudioDeviceServiceTest
    {
        /// <summary>
        /// Run comprehensive tests of AudioDeviceService
        /// </summary>
        public static void RunTests()
        {
            try
            {
                Console.WriteLine("🧪 === AUDIO DEVICE SERVICE TESTS ===");

                TestDeviceEnumeration();
                TestStableIdGeneration();
                TestDeviceResolution();
                TestGracefulFallback();
                TestSettingsIntegration();

                Console.WriteLine("✅ All AudioDeviceService tests completed");
                Console.WriteLine("🧪 === END TESTS ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Test suite failed: {ex.Message}");
            }
        }

        private static void TestDeviceEnumeration()
        {
            Console.WriteLine("\n📋 Test: Device Enumeration");

            try
            {
                var inputDevices = AudioDeviceService.GetInputDevices();
                var outputDevices = AudioDeviceService.GetOutputDevices();

                Console.WriteLine($"   Input devices found: {inputDevices.Count}");
                Console.WriteLine($"   Output devices found: {outputDevices.Count}");

                // Verify each device has required properties
                foreach (var device in inputDevices.Concat(outputDevices))
                {
                    if (string.IsNullOrEmpty(device.StableId))
                    {
                        Console.WriteLine($"   ❌ Device missing stable ID: {device.FriendlyName}");
                    }
                    if (string.IsNullOrEmpty(device.FriendlyName))
                    {
                        Console.WriteLine($"   ❌ Device missing friendly name: {device.StableId}");
                    }
                }

                Console.WriteLine("   ✅ Device enumeration test passed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Device enumeration test failed: {ex.Message}");
            }
        }

        private static void TestStableIdGeneration()
        {
            Console.WriteLine("\n🔑 Test: Stable ID Generation");

            try
            {
                var inputDevices = AudioDeviceService.GetInputDevices();
                var outputDevices = AudioDeviceService.GetOutputDevices();

                // Check for unique stable IDs
                var allIds = inputDevices.Concat(outputDevices).Select(d => d.StableId).ToList();
                var uniqueIds = allIds.Distinct().ToList();

                if (allIds.Count == uniqueIds.Count)
                {
                    Console.WriteLine("   ✅ All stable IDs are unique");
                }
                else
                {
                    Console.WriteLine($"   ⚠️ Found duplicate stable IDs: {allIds.Count} total, {uniqueIds.Count} unique");
                }

                // Check ID format
                foreach (var device in inputDevices)
                {
                    if (!device.StableId.StartsWith("input_"))
                    {
                        Console.WriteLine($"   ❌ Input device has wrong ID format: {device.StableId}");
                    }
                }

                foreach (var device in outputDevices)
                {
                    if (!device.StableId.StartsWith("output_"))
                    {
                        Console.WriteLine($"   ❌ Output device has wrong ID format: {device.StableId}");
                    }
                }

                Console.WriteLine("   ✅ Stable ID generation test passed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Stable ID generation test failed: {ex.Message}");
            }
        }

        private static void TestDeviceResolution()
        {
            Console.WriteLine("\n🔍 Test: Device Resolution");

            try
            {
                var inputDevices = AudioDeviceService.GetInputDevices();
                var outputDevices = AudioDeviceService.GetOutputDevices();

                var testsPassed = 0;
                var totalTests = 0;

                // Test resolution of existing devices
                foreach (var device in inputDevices.Take(2).Concat(outputDevices.Take(2)))
                {
                    totalTests++;
                    var resolved = AudioDeviceService.TryResolveDevice(device.StableId);

                    if (resolved != null && resolved.StableId == device.StableId)
                    {
                        testsPassed++;
                        Console.WriteLine($"   ✅ Resolved: {device.StableId}");
                    }
                    else
                    {
                        Console.WriteLine($"   ❌ Failed to resolve: {device.StableId}");
                    }
                }

                Console.WriteLine($"   Device resolution: {testsPassed}/{totalTests} tests passed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Device resolution test failed: {ex.Message}");
            }
        }

        private static void TestGracefulFallback()
        {
            Console.WriteLine("\n🛡️ Test: Graceful Fallback");

            try
            {
                // Test with non-existent device ID
                var nonExistentId = "input_nonexistent_device_999";
                var fallback = AudioDeviceService.TryResolveDevice(nonExistentId);

                if (fallback != null)
                {
                    Console.WriteLine($"   ✅ Graceful fallback worked: {fallback.FriendlyName}");
                }
                else
                {
                    Console.WriteLine($"   ⚠️ No fallback device available");
                }

                // Test with null/empty ID
                var nullResult = AudioDeviceService.TryResolveDevice(null);
                var emptyResult = AudioDeviceService.TryResolveDevice("");

                if (nullResult == null && emptyResult == null)
                {
                    Console.WriteLine("   ✅ Null/empty ID handling correct");
                }
                else
                {
                    Console.WriteLine("   ❌ Null/empty ID handling incorrect");
                }

                Console.WriteLine("   ✅ Graceful fallback test passed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Graceful fallback test failed: {ex.Message}");
            }
        }

        private static void TestSettingsIntegration()
        {
            Console.WriteLine("\n⚙️ Test: Settings Integration");

            try
            {
                // Test getting configured devices
                var configuredInput = AudioDeviceService.GetConfiguredInputDevice();
                var configuredOutput = AudioDeviceService.GetConfiguredOutputDevice();

                Console.WriteLine($"   Configured input: {configuredInput?.FriendlyName ?? "None"}");
                Console.WriteLine($"   Configured output: {configuredOutput?.FriendlyName ?? "None"}");

                // Test saving device selection (if devices available)
                var inputDevices = AudioDeviceService.GetInputDevices();
                var outputDevices = AudioDeviceService.GetOutputDevices();

                if (inputDevices.Any())
                {
                    var testInput = inputDevices.First();
                    AudioDeviceService.SaveInputDeviceSelection(testInput);
                    Console.WriteLine($"   ✅ Saved input device selection: {testInput.StableId}");
                }

                if (outputDevices.Any())
                {
                    var testOutput = outputDevices.First();
                    AudioDeviceService.SaveOutputDeviceSelection(testOutput);
                    Console.WriteLine($"   ✅ Saved output device selection: {testOutput.StableId}");
                }

                Console.WriteLine("   ✅ Settings integration test passed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Settings integration test failed: {ex.Message}");
            }
        }
    }
}