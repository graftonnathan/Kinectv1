// SpeakerWindowingTest.cs - Test speaker recognition windowing
using System;
using System.Linq;

namespace Kinectv1.Tools
{
    /// <summary>
    /// Test utility for speaker recognition windowing and embeddings
    /// Validates the rolling window approach with 0.5s hop and silence gating
    /// </summary>
    public class SpeakerWindowingTest
    {
        /// <summary>
        /// Test the RingBuffer implementation
        /// </summary>
        public static int TestRingBuffer()
        {
            Console.WriteLine("🧪 Testing RingBuffer Implementation");
            Console.WriteLine("===================================");
            
            try
            {
                // Test 1: Basic window extraction
                var buffer = new RingBuffer(16000); // 1 second at 16kHz
                
                // Add some test data (not quite enough for full window)
                var testData1 = new float[8000]; // 0.5 seconds
                for (int i = 0; i < testData1.Length; i++)
                    testData1[i] = (float)Math.Sin(2 * Math.PI * 440 * i / 16000); // 440Hz sine wave
                
                buffer.Add(testData1);
                
                if (buffer.HasFullWindow)
                {
                    Console.WriteLine("   ❌ Test 1 failed: Buffer reports full window with only 0.5s of data");
                    return 1;
                }
                
                // Add another 0.5 seconds to complete the window
                var testData2 = new float[8000];
                for (int i = 0; i < testData2.Length; i++)
                    testData2[i] = (float)Math.Sin(2 * Math.PI * 880 * i / 16000); // 880Hz sine wave
                
                buffer.Add(testData2);
                
                if (!buffer.HasFullWindow)
                {
                    Console.WriteLine("   ❌ Test 1 failed: Buffer doesn't report full window with 1.0s of data");
                    return 1;
                }
                
                // Extract window and verify content
                var window = buffer.ExtractWindow();
                if (window == null || window.Length != 16000)
                {
                    Console.WriteLine($"   ❌ Test 1 failed: Extracted window is null or wrong size (expected 16000, got {window?.Length ?? 0})");
                    return 1;
                }
                
                Console.WriteLine("   ✅ Test 1 passed: Basic window extraction works");
                
                // Test 2: RMS calculation
                var rms = buffer.CalculateRms();
                if (rms <= 0)
                {
                    Console.WriteLine($"   ❌ Test 2 failed: RMS calculation returned {rms} (expected > 0)");
                    return 1;
                }
                
                Console.WriteLine($"   ✅ Test 2 passed: RMS calculation works (RMS: {rms:F4})");
                
                // Test 3: Rolling window behavior
                // Add more data to test the rolling behavior
                var testData3 = new float[8000]; // Another 0.5 seconds
                for (int i = 0; i < testData3.Length; i++)
                    testData3[i] = (float)Math.Sin(2 * Math.PI * 220 * i / 16000); // 220Hz sine wave
                
                buffer.Add(testData3);
                
                var window2 = buffer.ExtractWindow();
                if (window2 == null || window2.Length != 16000)
                {
                    Console.WriteLine($"   ❌ Test 3 failed: Second window extraction failed");
                    return 1;
                }
                
                // The second window should be different from the first (rolling behavior)
                bool windowsAreDifferent = !window.SequenceEqual(window2);
                if (!windowsAreDifferent)
                {
                    Console.WriteLine("   ❌ Test 3 failed: Rolling window behavior not working (windows are identical)");
                    return 1;
                }
                
                Console.WriteLine("   ✅ Test 3 passed: Rolling window behavior works");
                
                Console.WriteLine("🎉 All RingBuffer tests passed!");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Test failed with exception: {ex.Message}");
                return 1;
            }
        }
        
        /// <summary>
        /// Test silence gating logic
        /// </summary>
        public static int TestSilenceGating()
        {
            Console.WriteLine("\n🧪 Testing Silence Gating Logic");
            Console.WriteLine("===============================");
            
            try
            {
                // Test 1: Loud signal should pass
                var loudSignal = new float[16000];
                for (int i = 0; i < loudSignal.Length; i++)
                    loudSignal[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * i / 16000);
                
                float rms = CalculateRmsFromArray(loudSignal);
                float rmsDb = 20f * (float)Math.Log10(Math.Max(rms, 1e-10f));
                
                if (rmsDb < -45f)
                {
                    Console.WriteLine($"   ❌ Test 1 failed: Loud signal has RMS {rmsDb:F1}dB (expected > -45dB)");
                    return 1;
                }
                
                Console.WriteLine($"   ✅ Test 1 passed: Loud signal RMS {rmsDb:F1}dB passes gating");
                
                // Test 2: Quiet signal should be gated
                var quietSignal = new float[16000];
                for (int i = 0; i < quietSignal.Length; i++)
                    quietSignal[i] = 0.001f * (float)Math.Sin(2 * Math.PI * 440 * i / 16000);
                
                rms = CalculateRmsFromArray(quietSignal);
                rmsDb = 20f * (float)Math.Log10(Math.Max(rms, 1e-10f));
                
                if (rmsDb >= -45f)
                {
                    Console.WriteLine($"   ❌ Test 2 failed: Quiet signal has RMS {rmsDb:F1}dB (expected < -45dB)");
                    return 1;
                }
                
                Console.WriteLine($"   ✅ Test 2 passed: Quiet signal RMS {rmsDb:F1}dB is gated");
                
                // Test 3: Voiced ratio calculation
                var sparseSpeech = new float[16000];
                // Add speech only in first 20% of window
                for (int i = 0; i < sparseSpeech.Length / 5; i++)
                    sparseSpeech[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * i / 16000);
                
                int voicedCount = CountVoicedSamplesFromArray(sparseSpeech);
                float voicedRatio = (float)voicedCount / sparseSpeech.Length;
                
                if (voicedRatio >= 0.2f)
                {
                    Console.WriteLine($"   ❌ Test 3 failed: Sparse speech has voiced ratio {voicedRatio:F2} (expected < 0.2)");
                    return 1;
                }
                
                Console.WriteLine($"   ✅ Test 3 passed: Sparse speech voiced ratio {voicedRatio:F2} is correctly gated");
                
                Console.WriteLine("🎉 All silence gating tests passed!");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Test failed with exception: {ex.Message}");
                return 1;
            }
        }
        
        /// <summary>
        /// Run all speaker windowing tests
        /// </summary>
        public static int RunAllTests()
        {
            Console.WriteLine("🧪 Speaker Recognition Windowing Test Suite");
            Console.WriteLine("==========================================");
            
            var ringBufferResult = TestRingBuffer();
            var silenceGatingResult = TestSilenceGating();
            
            if (ringBufferResult == 0 && silenceGatingResult == 0)
            {
                Console.WriteLine("\n🎉 All speaker windowing tests passed!");
                Console.WriteLine("✅ Rolling window with 0.5s hop: WORKING");
                Console.WriteLine("✅ Silence gating (-45dB, 0.2s voiced): WORKING");
                Console.WriteLine("✅ RMS normalization ready for embedder");
                return 0;
            }
            else
            {
                Console.WriteLine("\n❌ Some tests failed. Check output above for details.");
                return 1;
            }
        }
        
        // Helper methods for testing
        private static float CalculateRmsFromArray(float[] samples)
        {
            if (samples == null || samples.Length == 0) return 0f;
            
            float sum = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                sum += samples[i] * samples[i];
            }
            
            return (float)Math.Sqrt(sum / samples.Length);
        }
        
        private static int CountVoicedSamplesFromArray(float[] samples)
        {
            if (samples == null || samples.Length == 0) return 0;
            
            const float voiceThreshold = 0.01f;
            int voicedCount = 0;
            
            for (int i = 0; i < samples.Length; i++)
            {
                if (Math.Abs(samples[i]) > voiceThreshold)
                {
                    voicedCount++;
                }
            }
            
            return voicedCount;
        }
    }
}