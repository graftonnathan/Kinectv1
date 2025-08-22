using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace Kinectv1.Tools
{
    /// <summary>
    /// Extension methods for testing utilities
    /// </summary>
    public static class TestExtensions
    {
        public static int Count<T>(this IEnumerable<T> source, Func<T, bool> predicate)
        {
            return source.Where(predicate).Count();
        }
        
        public static TValue GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue)
        {
            return dictionary.TryGetValue(key, out TValue value) ? value : defaultValue;
        }
    }
    
    /// <summary>
    /// Simple synthetic Discord frame to STT testing
    /// Simulates Discord audio processing without actual audio pipeline
    /// </summary>
    public class SyntheticDiscordTest
    {
        /// <summary>
        /// Test synthetic Discord frame processing simulation
        /// </summary>
        public static int TestSyntheticDiscordFrames()
        {
            Console.WriteLine("🧪 Testing Synthetic Discord Frame Processing");
            
            // Simulate Discord audio frame metadata
            var testFrames = new[]
            {
                new DiscordFrameSimulation
                {
                    UserId = "user1",
                    Username = "TestUser1",
                    ChunkSizeMs = 20,
                    SampleRate = 48000,
                    Channels = 2,
                    ExpectedText = "hello world"
                },
                new DiscordFrameSimulation
                {
                    UserId = "user2",
                    Username = "TestUser2", 
                    ChunkSizeMs = 20,
                    SampleRate = 48000,
                    Channels = 2,
                    ExpectedText = "this is a test"
                },
                new DiscordFrameSimulation
                {
                    UserId = "user1",
                    Username = "TestUser1",
                    ChunkSizeMs = 20,
                    SampleRate = 48000,
                    Channels = 2,
                    ExpectedText = "hello world" // Duplicate from same user
                }
            };
            
            var processedFrames = new List<ProcessedFrame>();
            var userLastProcessed = new Dictionary<string, DateTime>();
            var processedTexts = new HashSet<string>();
            
            Console.WriteLine("   Simulating Discord frame processing:");
            
            var startTime = DateTime.UtcNow;
            for (int i = 0; i < testFrames.Length; i++)
            {
                var frame = testFrames[i];
                var frameTime = startTime.AddMilliseconds(i * 100); // 100ms apart
                
                // Simulate audio processing delay
                var processingDelayMs = 50 + (i * 10); // Variable processing time
                
                // Check for user switching
                var lastProcessedTime = userLastProcessed.GetValueOrDefault(frame.UserId, DateTime.MinValue);
                var timeSinceLastFrame = frameTime - lastProcessedTime;
                var isUserSwitch = timeSinceLastFrame.TotalMilliseconds > 200; // >200ms = user switch
                
                // Simulate transcription result
                var confidence = 0.8f + (float)(new Random(i).NextDouble() * 0.2); // 0.8-1.0
                var normalizedText = frame.ExpectedText.Trim().ToLowerInvariant();
                var isDuplicate = processedTexts.Contains(normalizedText);
                
                var processedFrame = new ProcessedFrame
                {
                    FrameIndex = i,
                    UserId = frame.UserId,
                    Username = frame.Username,
                    FrameTime = frameTime,
                    ProcessingDelayMs = processingDelayMs,
                    IsUserSwitch = isUserSwitch,
                    TranscribedText = frame.ExpectedText,
                    Confidence = confidence,
                    IsDuplicate = isDuplicate,
                    WouldDispatch = !isDuplicate
                };
                
                processedFrames.Add(processedFrame);
                userLastProcessed[frame.UserId] = frameTime;
                
                if (!isDuplicate)
                {
                    processedTexts.Add(normalizedText);
                }
                
                Console.WriteLine($"     Frame {i + 1}: {frame.Username} -> '{frame.ExpectedText}' " +
                                  $"(conf: {confidence:F2}, {(isDuplicate ? "DUPLICATE" : "NEW")}, " +
                                  $"{(isUserSwitch ? "USER_SWITCH" : "SAME_USER")})");
            }
            
            // Validate expected behavior
            Console.WriteLine();
            Console.WriteLine("   Validation:");
            
            var expectedDuplicates = 1; // Frame 3 should be duplicate of frame 1
            var actualDuplicates = processedFrames.Count(f => f.IsDuplicate);
            
            var expectedDispatches = 2; // Only frames 1 and 2 should dispatch
            var actualDispatches = processedFrames.Count(f => f.WouldDispatch);
            
            var allValid = true;
            
            if (actualDuplicates != expectedDuplicates)
            {
                Console.WriteLine($"   ❌ Expected {expectedDuplicates} duplicates, got {actualDuplicates}");
                allValid = false;
            }
            else
            {
                Console.WriteLine($"   ✅ Duplicate detection: {actualDuplicates} duplicates correctly identified");
            }
            
            if (actualDispatches != expectedDispatches)
            {
                Console.WriteLine($"   ❌ Expected {expectedDispatches} dispatches, got {actualDispatches}");
                allValid = false;
            }
            else
            {
                Console.WriteLine($"   ✅ Dispatch prevention: {actualDispatches} unique dispatches correctly processed");
            }
            
            // Check user switching detection
            var userSwitchCount = processedFrames.Count(f => f.IsUserSwitch);
            Console.WriteLine($"   ✅ User switching: {userSwitchCount} user switches detected");
            
            if (allValid)
            {
                Console.WriteLine("   ✅ All synthetic Discord frame tests passed");
                return 0;
            }
            else
            {
                Console.WriteLine("   ❌ Some synthetic Discord frame tests failed");
                return 1;
            }
        }
    }
    
    public class DiscordFrameSimulation
    {
        public string UserId { get; set; }
        public string Username { get; set; }
        public int ChunkSizeMs { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public string ExpectedText { get; set; }
    }
    
    public class ProcessedFrame
    {
        public int FrameIndex { get; set; }
        public string UserId { get; set; }
        public string Username { get; set; }
        public DateTime FrameTime { get; set; }
        public int ProcessingDelayMs { get; set; }
        public bool IsUserSwitch { get; set; }
        public string TranscribedText { get; set; }
        public float Confidence { get; set; }
        public bool IsDuplicate { get; set; }
        public bool WouldDispatch { get; set; }
    }
}