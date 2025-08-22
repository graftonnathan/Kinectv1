using System;
using System.Threading.Tasks;

namespace Kinectv1
{
    /// <summary>
    /// Simple demonstration and testing program for TTS preemption and ASR gating
    /// </summary>
    public static class TtsAsrGatingDemo
    {
        /// <summary>
        /// Main demo method showing the TTS/ASR coordination features
        /// </summary>
        public static async Task RunDemo()
        {
            Console.WriteLine("🚀 TTS PREEMPTION & ASR GATING DEMONSTRATION");
            Console.WriteLine("============================================");
            Console.WriteLine();

            // Show current telemetry state
            ShowTelemetrySnapshot("Initial State");

            // Demo 1: Basic TTS Playback State
            Console.WriteLine("📍 DEMO 1: TTS Playback State Management");
            await DemoPlaybackState();
            Console.WriteLine();

            // Demo 2: ASR Suppression during TTS
            Console.WriteLine("📍 DEMO 2: ASR Suppression During TTS");
            await DemoAsrSuppression();
            Console.WriteLine();

            // Demo 3: TTS Preemption with Timing
            Console.WriteLine("📍 DEMO 3: TTS Preemption with Timing Requirements");
            await TtsAsrGatingTest.TestTtsPreemptionTiming();
            Console.WriteLine();

            // Demo 4: Wake Word Gating
            Console.WriteLine("📍 DEMO 4: Wake Word Gating (Local Scenario)");
            await DemoWakeWordGating();
            Console.WriteLine();

            // Final telemetry
            ShowTelemetrySnapshot("Final State");
            
            Console.WriteLine("✅ TTS PREEMPTION & ASR GATING DEMO COMPLETED");
        }

        private static async Task DemoPlaybackState()
        {
            Console.WriteLine("   Testing IPlaybackState interface...");
            
            var playbackState = TtsPlaybackController.Instance;
            var eventReceived = false;

            // Subscribe to events
            playbackState.OnPlaybackStart += () => {
                Console.WriteLine("   🎵 EVENT: TTS playback started");
                eventReceived = true;
            };

            playbackState.OnPlaybackStop += () => {
                Console.WriteLine("   🔇 EVENT: TTS playback stopped");
            };

            Console.WriteLine($"   Initial IsSpeaking: {playbackState.IsSpeaking}");

            // Simulate TTS operation
            var mockTts = new Func<string, string, System.Threading.CancellationToken, Task<bool>>(
                async (text, speaker, ct) => {
                    Console.WriteLine($"   🎤 Mock TTS: '{text}' ({speaker})");
                    await Task.Delay(500, ct);
                    return true;
                });

            await TtsPlaybackController.StartUtterance("Hello world", "MockSpeaker", mockTts);
            
            Console.WriteLine($"   Final IsSpeaking: {playbackState.IsSpeaking}");
            Console.WriteLine($"   Event received: {eventReceived}");
        }

        private static async Task DemoAsrSuppression()
        {
            Console.WriteLine("   Simulating ASR dispatch attempts during TTS...");
            
            // Start a longer TTS operation
            var longTts = new Func<string, string, System.Threading.CancellationToken, Task<bool>>(
                async (text, speaker, ct) => {
                    Console.WriteLine($"   🎵 Long TTS: '{text}' starting...");
                    await Task.Delay(1000, ct);
                    Console.WriteLine($"   🎵 Long TTS: '{text}' completed");
                    return true;
                });

            // Start TTS in background
            var ttsTask = TtsPlaybackController.StartUtterance("This is a longer utterance to test ASR suppression", "MockSpeaker", longTts);
            
            // Try to dispatch ASR while TTS is running
            await Task.Delay(100); // Let TTS start
            
            Console.WriteLine("   📥 Attempting ASR dispatch while TTS is speaking...");
            var suppressionCount = 0;
            
            for (int i = 0; i < 3; i++)
            {
                if (TtsPlaybackController.Instance.IsSpeaking)
                {
                    Console.WriteLine($"   🚫 ASR dispatch #{i+1} would be SUPPRESSED (TTS speaking)");
                    suppressionCount++;
                }
                else
                {
                    Console.WriteLine($"   ✅ ASR dispatch #{i+1} would be ALLOWED (TTS not speaking)");
                }
                await Task.Delay(200);
            }
            
            await ttsTask;
            Console.WriteLine($"   📊 Suppression effectiveness: {suppressionCount}/3 attempts blocked");
        }

        private static async Task DemoTtsPreemption()
        {
            Console.WriteLine("   Testing rapid TTS calls with preemption...");
            
            var preemptibleTts = new Func<string, string, System.Threading.CancellationToken, Task<bool>>(
                async (text, speaker, ct) => {
                    try
                    {
                        Console.WriteLine($"   🎵 TTS Start: '{text.Substring(0, Math.Min(20, text.Length))}...'");
                        await Task.Delay(1500, ct);
                        Console.WriteLine($"   ✅ TTS Complete: '{text.Substring(0, Math.Min(20, text.Length))}...'");
                        return true;
                    }
                    catch (System.Threading.OperationCanceledException)
                    {
                        Console.WriteLine($"   🛑 TTS Canceled: '{text.Substring(0, Math.Min(20, text.Length))}...'");
                        throw;
                    }
                });

            // Rapid fire utterances
            var utterances = new[]
            {
                "First utterance that should be interrupted",
                "Second utterance that should also be interrupted", 
                "Final utterance that should complete"
            };

            for (int i = 0; i < utterances.Length; i++)
            {
                var task = TtsPlaybackController.StartUtterance(utterances[i], "MockSpeaker", preemptibleTts);
                
                if (i < utterances.Length - 1)
                {
                    await Task.Delay(300); // Brief delay before next
                    // Don't await - let it be preempted
                }
                else
                {
                    await task; // Wait for final utterance
                }
            }
        }

        private static async Task DemoWakeWordGating()
        {
            Console.WriteLine("   Testing wake word requirements for Local scenario...");
            
            // Note: This is a simplified demo since we can't easily test the full VoiceProcessor
            // In a real scenario, the VoiceProcessor would check for wake words
            
            var testTranscriptions = new[]
            {
                "Hello there how are you today",           // No wake word
                "Hey Kinect what time is it",              // Has potential wake word
                "Computer please tell me the weather",     // Has potential wake word
                "I think this is a test"                    // No wake word
            };

            Console.WriteLine("   📝 Testing transcriptions (simulated):");
            foreach (var transcription in testTranscriptions)
            {
                // Simulate wake word detection logic
                var hasWakeWord = transcription.IndexOf("hey", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 transcription.IndexOf("computer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 transcription.IndexOf("kinect", StringComparison.OrdinalIgnoreCase) >= 0;
                
                var status = hasWakeWord ? "✅ WOULD DISPATCH" : "🚫 WOULD BLOCK";
                Console.WriteLine($"   • '{transcription}' → {status}");
            }
            
            await Task.Delay(100); // Brief pause for readability
        }

        private static void ShowTelemetrySnapshot(string label)
        {
            Console.WriteLine($"📊 TELEMETRY SNAPSHOT ({label}):");
            
            var ttsActiveValue = Telemetry.GetCounter("tts.active"); // Note: Gauge values stored in accumulators
            var blockedCount = Telemetry.GetCounter("asr.dispatch_blocked_due_to_tts");
            var noWakeWordCount = Telemetry.GetCounter("asr.dispatch_blocked_no_wake_word");
            
            Console.WriteLine($"   • tts.active: {ttsActiveValue}");
            Console.WriteLine($"   • asr.dispatch_blocked_due_to_tts: {blockedCount}");
            Console.WriteLine($"   • asr.dispatch_blocked_no_wake_word: {noWakeWordCount}");
            Console.WriteLine();
        }
    }
}