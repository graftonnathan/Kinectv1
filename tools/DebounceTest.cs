using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace Kinectv1.Tools
{
    /// <summary>
    /// Simple test utility for debounce and pruning logic validation
    /// Tests VAD debouncing and duplicate dispatch prevention without full ASR pipeline
    /// </summary>
    public class DebounceTest
    {
        /// <summary>
        /// Test VAD debouncing logic with simulated timestamps
        /// </summary>
        public static int TestVadDebouncing()
        {
            Console.WriteLine("🧪 Testing VAD Debounce Logic");
            
            var debounceTimeoutMs = 200; // 200ms debounce window
            var lastFinalResultTime = DateTime.MinValue;
            var testResults = new List<DebounceTestResult>();
            
            // Simulate rapid FinalResult calls
            var testCalls = new[]
            {
                0,    // First call - should pass
                50,   // 50ms later - should be blocked  
                100,  // 100ms later - should be blocked
                150,  // 150ms later - should be blocked
                250   // 250ms later - should pass
            };
            
            Console.WriteLine($"   Debounce timeout: {debounceTimeoutMs}ms");
            Console.WriteLine("   Simulating FinalResult calls:");
            
            var startTime = DateTime.UtcNow;
            foreach (var offsetMs in testCalls)
            {
                var currentTime = startTime.AddMilliseconds(offsetMs);
                var timeSinceLastResult = currentTime - lastFinalResultTime;
                var wouldBeBlocked = timeSinceLastResult.TotalMilliseconds < debounceTimeoutMs && lastFinalResultTime != DateTime.MinValue;
                
                var result = new DebounceTestResult
                {
                    OffsetMs = offsetMs,
                    TimeSinceLastMs = lastFinalResultTime == DateTime.MinValue ? -1 : timeSinceLastResult.TotalMilliseconds,
                    WasBlocked = wouldBeBlocked,
                    Timestamp = currentTime
                };
                
                testResults.Add(result);
                
                Console.WriteLine($"     Call at +{offsetMs}ms: {(wouldBeBlocked ? "BLOCKED" : "ALLOWED")} " +
                                  $"(gap: {result.TimeSinceLastMs:F0}ms)");
                
                if (!wouldBeBlocked)
                {
                    lastFinalResultTime = currentTime;
                }
            }
            
            // Validate results
            var expectedBlocked = new[] { false, true, true, true, false };
            var allCorrect = true;
            
            for (int i = 0; i < testResults.Count; i++)
            {
                if (testResults[i].WasBlocked != expectedBlocked[i])
                {
                    Console.WriteLine($"   ❌ Test {i + 1} failed: expected {(expectedBlocked[i] ? "blocked" : "allowed")}, got {(testResults[i].WasBlocked ? "blocked" : "allowed")}");
                    allCorrect = false;
                }
            }
            
            if (allCorrect)
            {
                Console.WriteLine("   ✅ All debounce tests passed");
                return 0;
            }
            else
            {
                Console.WriteLine("   ❌ Some debounce tests failed");
                return 1;
            }
        }
        
        /// <summary>
        /// Test duplicate dispatch prevention logic
        /// </summary>
        public static int TestDuplicateDispatchPrevention()
        {
            Console.WriteLine("🧪 Testing Duplicate Dispatch Prevention");
            
            var processedTranscriptions = new HashSet<string>();
            var testPhrases = new[]
            {
                "hello world",
                "this is a test",
                "hello world",      // Duplicate
                "another phrase",
                "this is a test",   // Duplicate
                "final phrase"
            };
            
            var expectedResults = new[] { false, false, true, false, true, false }; // true = blocked
            var actualResults = new List<bool>();
            
            Console.WriteLine("   Simulating transcription dispatches:");
            
            for (int i = 0; i < testPhrases.Length; i++)
            {
                var phrase = testPhrases[i].Trim().ToLowerInvariant();
                var isDuplicate = processedTranscriptions.Contains(phrase);
                actualResults.Add(isDuplicate);
                
                Console.WriteLine($"     '{testPhrases[i]}': {(isDuplicate ? "BLOCKED (duplicate)" : "DISPATCHED")}");
                
                if (!isDuplicate)
                {
                    processedTranscriptions.Add(phrase);
                }
            }
            
            // Validate results
            var allCorrect = true;
            for (int i = 0; i < actualResults.Count; i++)
            {
                if (actualResults[i] != expectedResults[i])
                {
                    Console.WriteLine($"   ❌ Test {i + 1} failed: expected {(expectedResults[i] ? "blocked" : "dispatched")}, got {(actualResults[i] ? "blocked" : "dispatched")}");
                    allCorrect = false;
                }
            }
            
            if (allCorrect)
            {
                Console.WriteLine("   ✅ All duplicate prevention tests passed");
                Console.WriteLine($"   📊 Processed {processedTranscriptions.Count} unique transcriptions from {testPhrases.Length} total calls");
                return 0;
            }
            else
            {
                Console.WriteLine("   ❌ Some duplicate prevention tests failed");
                return 1;
            }
        }
        
        /// <summary>
        /// Run all debounce and pruning tests
        /// </summary>
        public static int RunAllTests()
        {
            Console.WriteLine("🧪 Debounce & Pruning Logic Test Suite");
            Console.WriteLine("=====================================");
            
            var vadResult = TestVadDebouncing();
            Console.WriteLine();
            
            var dispatchResult = TestDuplicateDispatchPrevention();
            Console.WriteLine();
            
            if (vadResult == 0 && dispatchResult == 0)
            {
                Console.WriteLine("🎉 All debounce and pruning tests passed!");
                return 0;
            }
            else
            {
                Console.WriteLine("❌ Some tests failed. Check output above for details.");
                return 1;
            }
        }
    }
    
    public class DebounceTestResult
    {
        public int OffsetMs { get; set; }
        public double TimeSinceLastMs { get; set; }
        public bool WasBlocked { get; set; }
        public DateTime Timestamp { get; set; }
    }
}