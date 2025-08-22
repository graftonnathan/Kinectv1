using System;
using System.Diagnostics;

namespace Kinectv1
{
    /// <summary>
    /// Test class to validate the new audio resampler functionality
    /// </summary>
    public static class AudioResamplerTest
    {
        /// <summary>
        /// Test the resampler with synthetic audio data
        /// </summary>
        public static void TestResampler()
        {
            Console.WriteLine("=== Audio Resampler Test ===");
            
            // Create synthetic 48kHz stereo test data (20ms chunk = 1920 samples)
            const int inputSampleRate = 48000;
            const int chunkMs = 20;
            const int stereoSamples = inputSampleRate * chunkMs / 1000 * 2; // 1920 samples
            
            var testInput = new float[stereoSamples];
            
            // Generate test sine wave at 1kHz (L channel) and 2kHz (R channel)
            for (int i = 0; i < stereoSamples / 2; i++)
            {
                double time = (double)i / inputSampleRate;
                testInput[i * 2] = (float)(0.5 * Math.Sin(2.0 * Math.PI * 1000.0 * time));     // Left: 1kHz
                testInput[i * 2 + 1] = (float)(0.3 * Math.Sin(2.0 * Math.PI * 2000.0 * time)); // Right: 2kHz
            }
            
            Console.WriteLine($"Input: {stereoSamples} samples (48kHz stereo, {chunkMs}ms)");
            
            // Test the resampler
            var sw = Stopwatch.StartNew();
            var resampledOutput = AudioUtils.Resample48kTo16kMono(testInput, stereoSamples);
            sw.Stop();
            
            Console.WriteLine($"Output: {resampledOutput.Length} samples (16kHz mono)");
            Console.WriteLine($"Processing time: {sw.ElapsedMilliseconds}ms");
            Console.WriteLine($"Expected output length: ~{stereoSamples / 6} samples");
            
            // Test frame building
            Console.WriteLine("\n=== Frame Builder Test ===");
            AudioUtils.FrameBuilder.Reset();
            
            // Feed the resampled data to frame builder
            var frames = AudioUtils.FrameBuilder.AccumulateFrames(resampledOutput);
            Console.WriteLine($"Generated {frames.Length} complete frames");
            
            if (frames.Length > 0)
            {
                Console.WriteLine($"First frame length: {frames[0].Length} samples");
                
                // Check if frame is exactly 320 samples
                if (frames[0].Length == 320)
                {
                    Console.WriteLine("✅ Frame size is correct (320 samples = 20ms at 16kHz)");
                }
                else
                {
                    Console.WriteLine($"❌ Frame size incorrect: expected 320, got {frames[0].Length}");
                }
            }
            
            // Check buffer occupancy
            int occupancy = AudioUtils.FrameBuilder.GetBufferOccupancy();
            Console.WriteLine($"Frame buffer occupancy: {occupancy}/320 samples");
            
            // Test multiple chunks
            Console.WriteLine("\n=== Multiple Chunk Test ===");
            AudioUtils.FrameBuilder.Reset();
            
            int totalFrames = 0;
            for (int chunk = 0; chunk < 5; chunk++)
            {
                // Process the same test data multiple times
                var chunkOutput = AudioUtils.Resample48kTo16kMono(testInput, stereoSamples);
                var chunkFrames = AudioUtils.FrameBuilder.AccumulateFrames(chunkOutput);
                totalFrames += chunkFrames.Length;
                
                Console.WriteLine($"Chunk {chunk + 1}: {chunkOutput.Length} samples -> {chunkFrames.Length} frames");
            }
            
            Console.WriteLine($"Total frames generated from 5 chunks: {totalFrames}");
            Console.WriteLine($"Final buffer occupancy: {AudioUtils.FrameBuilder.GetBufferOccupancy()}/320 samples");
            
            Console.WriteLine("\n✅ Audio resampler test completed");
        }
        
        /// <summary>
        /// Performance comparison between old and new approaches
        /// </summary>
        public static void PerformanceTest()
        {
            Console.WriteLine("=== Performance Comparison Test ===");
            
            const int iterations = 1000;
            const int stereoSamples = 1920; // 20ms at 48kHz stereo
            
            var testInput = new float[stereoSamples];
            for (int i = 0; i < stereoSamples; i++)
            {
                testInput[i] = (float)(0.5 * Math.Sin(2.0 * Math.PI * 1000.0 * i / 48000.0));
            }
            
            // Test new resampler
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var output = AudioUtils.Resample48kTo16kMono(testInput, stereoSamples);
            }
            sw.Stop();
            
            double avgTimeMs = (double)sw.ElapsedMilliseconds / iterations;
            Console.WriteLine($"New resampler: {avgTimeMs:F3}ms average per chunk ({iterations} iterations)");
            Console.WriteLine($"Estimated real-time performance: {(20.0 / avgTimeMs):F1}x faster than real-time");
            
            Console.WriteLine("✅ Performance test completed");
        }
    }
}