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
            Console.WriteLine($"Expected output length: ~{stereoSamples / 6} samples (actual: {stereoSamples / 6})");
            
            // Validate output length is reasonable
            int expectedLength = stereoSamples / 6; // 48kHz stereo -> 16kHz mono = 6:1 ratio
            if (Math.Abs(resampledOutput.Length - expectedLength) <= 2) // Allow small rounding difference
            {
                Console.WriteLine("✅ Output length is correct");
            }
            else
            {
                Console.WriteLine($"❌ Output length incorrect: expected ~{expectedLength}, got {resampledOutput.Length}");
            }
            
            // Test frame building
            Console.WriteLine("\n=== Frame Builder Test ===");
            AudioUtils.FrameBuilder.Reset();
            
            // Feed the resampled data to frame builder
            var frames = AudioUtils.FrameBuilder.AccumulateFrames(resampledOutput);
            Console.WriteLine($"Generated {frames.Length} complete frames from {resampledOutput.Length} samples");
            
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
                
                // Check all frames have correct size
                bool allFramesCorrect = true;
                for (int i = 0; i < frames.Length; i++)
                {
                    if (frames[i].Length != 320)
                    {
                        Console.WriteLine($"❌ Frame {i} has incorrect size: {frames[i].Length}");
                        allFramesCorrect = false;
                    }
                }
                
                if (allFramesCorrect)
                {
                    Console.WriteLine($"✅ All {frames.Length} frames have correct size");
                }
            }
            
            // Check buffer occupancy
            int occupancy = AudioUtils.FrameBuilder.GetBufferOccupancy();
            Console.WriteLine($"Frame buffer occupancy: {occupancy}/320 samples");
            Console.WriteLine($"Frame builder status: {AudioUtils.FrameBuilder.GetStatus()}");
            
            // Test edge cases
            Console.WriteLine("\n=== Edge Case Tests ===");
            TestEdgeCases();
            
            Console.WriteLine("\n✅ Audio resampler test completed");
        }
        
        /// <summary>
        /// Test edge cases and error conditions
        /// </summary>
        private static void TestEdgeCases()
        {
            // Test null input
            var result1 = AudioUtils.Resample48kTo16kMono((float[])null, 0);
            Console.WriteLine($"Null input test: {(result1.Length == 0 ? "✅ PASS" : "❌ FAIL")}");
            
            // Test empty input
            var result2 = AudioUtils.Resample48kTo16kMono(new float[0], 0);
            Console.WriteLine($"Empty input test: {(result2.Length == 0 ? "✅ PASS" : "❌ FAIL")}");
            
            // Test very small input
            var smallInput = new float[] { 0.1f, -0.1f, 0.2f, -0.2f }; // 2 stereo samples
            var result3 = AudioUtils.Resample48kTo16kMono(smallInput, 4);
            Console.WriteLine($"Small input test: got {result3.Length} samples from 4 input samples");
            
            // Test frame builder with partial frames
            AudioUtils.FrameBuilder.Reset();
            var partialSamples = new float[100]; // Less than 320
            for (int i = 0; i < partialSamples.Length; i++)
            {
                partialSamples[i] = (float)Math.Sin(i * 0.1);
            }
            
            var partialFrames = AudioUtils.FrameBuilder.AccumulateFrames(partialSamples);
            Console.WriteLine($"Partial frame test: {partialFrames.Length} frames from {partialSamples.Length} samples");
            Console.WriteLine($"Buffer after partial: {AudioUtils.FrameBuilder.GetBufferOccupancy()}/320 samples");
            
            // Add more samples to complete a frame
            var morePartialSamples = new float[250]; // This should complete one frame
            var moreFrames = AudioUtils.FrameBuilder.AccumulateFrames(morePartialSamples);
            Console.WriteLine($"Completion test: {moreFrames.Length} frames after adding {morePartialSamples.Length} more samples");
            
            // Test byte array resampling
            var byteInput = new byte[1920 * 2]; // 20ms stereo at 48kHz, s16 = 2 bytes per sample
            for (int i = 0; i < byteInput.Length; i += 4) // 4 bytes = 1 stereo sample
            {
                short leftSample = (short)(1000 * Math.Sin(i * 0.01));
                short rightSample = (short)(500 * Math.Sin(i * 0.02));
                
                // Little-endian encoding
                byteInput[i] = (byte)(leftSample & 0xFF);
                byteInput[i + 1] = (byte)((leftSample >> 8) & 0xFF);
                byteInput[i + 2] = (byte)(rightSample & 0xFF);
                byteInput[i + 3] = (byte)((rightSample >> 8) & 0xFF);
            }
            
            var byteResult = AudioUtils.Resample48kTo16kMono(byteInput, byteInput.Length);
            Console.WriteLine($"Byte array test: {byteResult.Length} samples from {byteInput.Length} bytes");
            
            // Test odd byte length (should be handled gracefully)
            var oddByteResult = AudioUtils.Resample48kTo16kMono(byteInput, byteInput.Length - 1);
            Console.WriteLine($"Odd byte length test: {oddByteResult.Length} samples from {byteInput.Length - 1} bytes");
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
        
        /// <summary>
        /// Validate the entire audio processing pipeline
        /// </summary>
        public static void ValidatePipeline()
        {
            Console.WriteLine("=== Pipeline Validation Test ===");
            
            // Test the complete Discord audio processing pipeline
            try
            {
                // Simulate Discord audio: 48kHz stereo, 16-bit, 20ms chunks
                const int sampleRate = 48000;
                const int channels = 2;
                const int chunkMs = 20;
                const int samples = sampleRate * chunkMs / 1000; // 960 samples per channel
                const int totalSamples = samples * channels; // 1920 total samples
                const int bytesPerSample = 2; // 16-bit
                const int totalBytes = totalSamples * bytesPerSample; // 3840 bytes
                
                Console.WriteLine($"Simulating Discord input: {totalBytes} bytes ({totalSamples} samples, {chunkMs}ms)");
                
                // Create test audio data (sine wave)
                var audioBytes = new byte[totalBytes];
                for (int i = 0; i < samples; i++)
                {
                    double time = (double)i / sampleRate;
                    short leftSample = (short)(16383 * Math.Sin(2.0 * Math.PI * 440.0 * time));  // 440Hz tone
                    short rightSample = (short)(8191 * Math.Sin(2.0 * Math.PI * 880.0 * time)); // 880Hz tone
                    
                    int byteIndex = i * 4; // 4 bytes per stereo sample
                    // Little-endian encoding
                    audioBytes[byteIndex] = (byte)(leftSample & 0xFF);
                    audioBytes[byteIndex + 1] = (byte)((leftSample >> 8) & 0xFF);
                    audioBytes[byteIndex + 2] = (byte)(rightSample & 0xFF);
                    audioBytes[byteIndex + 3] = (byte)((rightSample >> 8) & 0xFF);
                }
                
                // Process through Discord audio processor
                var sw = Stopwatch.StartNew();
                var (processedAudio, processedLength, rmsLevel) = DiscordAudioProcessor.ProcessDiscordAudio(audioBytes, totalBytes, "TestUser");
                sw.Stop();
                
                Console.WriteLine($"Processing time: {sw.ElapsedMilliseconds}ms");
                Console.WriteLine($"Processed output: {processedLength} bytes");
                Console.WriteLine($"RMS level: {rmsLevel:F2}");
                
                if (processedAudio != null && processedLength > 0)
                {
                    int outputSamples = processedLength / 2; // 16-bit output
                    Console.WriteLine($"Output samples: {outputSamples}");
                    
                    // Validate output format (should be exactly 320 samples for 20ms at 16kHz)
                    if (outputSamples == 320)
                    {
                        Console.WriteLine("✅ Output frame size is correct (320 samples = 20ms at 16kHz)");
                    }
                    else
                    {
                        Console.WriteLine($"❌ Output frame size incorrect: expected 320, got {outputSamples}");
                    }
                    
                    // Test multiple chunks to ensure consistent output
                    Console.WriteLine("\n--- Testing multiple chunks ---");
                    AudioUtils.FrameBuilder.Reset();
                    DiscordAudioProcessor.ResetStatistics();
                    
                    int successfulChunks = 0;
                    int totalOutputSamples = 0;
                    
                    for (int chunk = 0; chunk < 10; chunk++)
                    {
                        var (chunkAudio, chunkLength, chunkRms) = DiscordAudioProcessor.ProcessDiscordAudio(audioBytes, totalBytes, "TestUser");
                        if (chunkAudio != null && chunkLength > 0)
                        {
                            successfulChunks++;
                            totalOutputSamples += chunkLength / 2;
                        }
                    }
                    
                    Console.WriteLine($"Successfully processed {successfulChunks}/10 chunks");
                    Console.WriteLine($"Average output samples per chunk: {totalOutputSamples / Math.Max(1, successfulChunks)}");
                    
                    // Get processor status
                    Console.WriteLine("\n--- Processor Status ---");
                    Console.WriteLine(DiscordAudioProcessor.GetProcessorStatus());
                    
                    Console.WriteLine("✅ Pipeline validation completed successfully");
                }
                else
                {
                    Console.WriteLine("❌ Pipeline validation failed - no output generated");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Pipeline validation failed with exception: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }
    }
}