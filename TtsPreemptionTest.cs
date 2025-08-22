using System;
using System.Threading.Tasks;

namespace Kinectv1
{
    /// <summary>
    /// Simple test class to verify TTS preemption functionality
    /// </summary>
    public static class TtsPreemptionTest
    {
        /// <summary>
        /// Test method to verify TTS preemption behavior
        /// </summary>
        public static async Task TestPreemptionBehavior()
        {
            Console.WriteLine("🎤 TESTING: TTS Preemption Behavior");
            Console.WriteLine("   Simulating rapid utterance changes to verify preemption...");

            // Initialize TTS if needed
            if (!CoquiTtsService.IsEnabled())
            {
                Console.WriteLine("   TTS is not enabled or initialized, skipping test");
                return;
            }

            try
            {
                var testPhrases = new[]
                {
                    "This is the first long utterance that should be interrupted",
                    "This is the second utterance that will replace the first",
                    "Final utterance that should complete successfully"
                };

                Console.WriteLine($"   Starting {testPhrases.Length} consecutive utterances with preemption...");

                for (int i = 0; i < testPhrases.Length; i++)
                {
                    var phrase = testPhrases[i];
                    Console.WriteLine($"   Utterance {i + 1}: \"{phrase.Substring(0, Math.Min(30, phrase.Length))}...\"");
                    
                    // Start utterance with preemption (don't await - simulate rapid calls)
                    var task = CoquiTtsService.SpeakStreamingWithPreemptionAsync(phrase);
                    
                    // Simulate rapid successive calls by waiting only briefly
                    if (i < testPhrases.Length - 1)
                    {
                        await Task.Delay(500); // Brief delay to allow some audio generation before next utterance
                    }
                    else
                    {
                        // Wait for the final utterance to complete
                        var result = await task;
                        Console.WriteLine($"   Final utterance completed: {result}");
                    }
                }

                Console.WriteLine("✅ TTS preemption test completed");
                Console.WriteLine($"   Active utterance: {TtsPlaybackController.HasActiveUtterance()}");
                Console.WriteLine($"   Current utterance ID: {TtsPlaybackController.GetCurrentUtteranceId() ?? "none"}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ TTS preemption test failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Test manual cancellation behavior
        /// </summary>
        public static async Task TestManualCancellation()
        {
            Console.WriteLine("🎤 TESTING: Manual TTS Cancellation");
            
            if (!CoquiTtsService.IsEnabled())
            {
                Console.WriteLine("   TTS is not enabled or initialized, skipping test");
                return;
            }

            try
            {
                var longText = "This is a very long utterance that should be manually canceled before it completes. " +
                              "It contains multiple sentences to ensure there is enough time to test cancellation. " +
                              "If you can hear this entire message, then cancellation did not work properly.";

                Console.WriteLine("   Starting long utterance...");
                var task = CoquiTtsService.SpeakStreamingWithPreemptionAsync(longText);

                // Wait a bit, then cancel
                await Task.Delay(1000);
                Console.WriteLine("   Canceling utterance manually...");
                CoquiTtsService.StopCurrentPlayback();

                // Wait for the task to complete (should be canceled)
                var result = await task;
                Console.WriteLine($"   Utterance result after cancellation: {result}");
                Console.WriteLine($"   Active utterance after cancellation: {TtsPlaybackController.HasActiveUtterance()}");

                Console.WriteLine("✅ Manual cancellation test completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Manual cancellation test failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Test rapid successive outputs to verify immediate stop-and-restart behavior
        /// Simulates the scenario where new LLM output events trigger immediate TTS interruption
        /// </summary>
        public static async Task TestRapidSuccessiveOutputs()
        {
            Console.WriteLine("🎤 TESTING: Rapid Successive TTS Outputs (LLM Output Simulation)");
            Console.WriteLine("   Testing immediate stop-and-restart behavior with rapid output events...");
            
            if (!CoquiTtsService.IsEnabled())
            {
                Console.WriteLine("   TTS is not enabled or initialized, skipping test");
                return;
            }

            try
            {
                var rapidOutputs = new[]
                {
                    "First output that should be immediately interrupted",
                    "Second output interrupting the first one",
                    "Third output cutting off the second",
                    "Fourth rapid output replacing the third",
                    "Final output that should complete successfully"
                };

                Console.WriteLine($"   Simulating {rapidOutputs.Length} rapid successive LLM outputs...");
                
                var startTime = DateTime.UtcNow;
                Task<bool> lastTask = null;

                for (int i = 0; i < rapidOutputs.Length; i++)
                {
                    var output = rapidOutputs[i];
                    var outputTime = DateTime.UtcNow;
                    
                    Console.WriteLine($"   Output {i + 1} at +{(outputTime - startTime).TotalMilliseconds:F0}ms: \"{output.Substring(0, Math.Min(25, output.Length))}...\"");
                    
                    // Immediately start new TTS (should stop any current playback)
                    lastTask = CoquiTtsService.SpeakStreamingWithPreemptionAsync(output);
                    
                    // Very short delay to simulate rapid LLM output events
                    if (i < rapidOutputs.Length - 1)
                    {
                        await Task.Delay(200); // 200ms between outputs - rapid succession
                    }
                }

                // Wait for the final output to complete
                if (lastTask != null)
                {
                    Console.WriteLine("   Waiting for final output to complete...");
                    var result = await lastTask;
                    Console.WriteLine($"   Final output completed: {result}");
                }

                var totalTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                Console.WriteLine($"✅ Rapid successive outputs test completed in {totalTime:F0}ms");
                Console.WriteLine($"   Active utterance: {TtsPlaybackController.HasActiveUtterance()}");
                Console.WriteLine($"   Current utterance ID: {TtsPlaybackController.GetCurrentUtteranceId() ?? "none"}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Rapid successive outputs test failed: {ex.Message}");
            }
        }
    }
}