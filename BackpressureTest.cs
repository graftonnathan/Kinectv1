using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1
{
    /// <summary>
    /// Test utility to demonstrate backpressure functionality in audio and TTS pipelines
    /// </summary>
    public static class BackpressureTest
    {
        /// <summary>
        /// Test VoiceRecognizer audio queue backpressure by flooding it with audio data
        /// </summary>
        public static void TestAudioBackpressure()
        {
            Console.WriteLine("🧪 Testing VoiceRecognizer audio queue backpressure...");
            
            var initialMetrics = VoiceRecognizer.GetBackpressureMetrics();
            Console.WriteLine($"   Initial state: External={initialMetrics.externalQueueCount}, Discord Users={initialMetrics.totalDiscordQueues}, Drops={initialMetrics.totalAudioDrops + initialMetrics.totalDiscordDrops}");
            
            // Simulate burst of audio chunks
            var audioData = new byte[320]; // Typical 16kHz mono chunk
            for (int i = 0; i < 100; i++)
            {
                VoiceRecognizer.ProcessExternalAudio(audioData, audioData.Length, $"TestSource{i % 5}");
                if (i % 20 == 0)
                {
                    Thread.Sleep(1); // Brief pause to see intermediate state
                    var metrics = VoiceRecognizer.GetBackpressureMetrics();
                    Console.WriteLine($"   After {i+1} chunks: External={metrics.externalQueueCount}, Discord Items={metrics.totalDiscordItems}, Drops={metrics.totalAudioDrops + metrics.totalDiscordDrops}");
                }
            }
            
            var finalMetrics = VoiceRecognizer.GetBackpressureMetrics();
            Console.WriteLine($"   Final state: External={finalMetrics.externalQueueCount}, Discord Users={finalMetrics.totalDiscordQueues}, Total Drops={finalMetrics.totalAudioDrops + finalMetrics.totalDiscordDrops}");
            
            if (finalMetrics.totalAudioDrops + finalMetrics.totalDiscordDrops > 0)
            {
                Console.WriteLine("✅ Audio backpressure is working - drops detected under burst load");
            }
            else
            {
                Console.WriteLine("ℹ️ No drops detected - queue capacity sufficient for test load");
            }
        }
        
        /// <summary>
        /// Test Discord TTS queue backpressure by flooding it with TTS requests
        /// </summary>
        public static async Task TestTtsBackpressureAsync()
        {
            Console.WriteLine("🧪 Testing Discord TTS queue backpressure...");
            
            if (!Discord.DiscordNetBotManager.IsRunning)
            {
                Console.WriteLine("❌ Discord bot not running - skipping TTS backpressure test");
                return;
            }
            
            var initialMetrics = Discord.DiscordNetBotManager.GetTtsBackpressureMetrics();
            Console.WriteLine($"   Initial state: TTS Queue={initialMetrics.ttsQueueCount}, Drops={initialMetrics.totalTtsDrops}");
            
            // Simulate burst of TTS requests
            var tasks = new Task[25]; // More than queue capacity (10)
            for (int i = 0; i < tasks.Length; i++)
            {
                int requestId = i;
                tasks[i] = Task.Run(async () =>
                {
                    try
                    {
                        await Discord.DiscordNetBotManager.SendTtsToDiscordAsync($"Test TTS message {requestId}");
                        Console.WriteLine($"   TTS {requestId} completed");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   TTS {requestId} failed: {ex.Message}");
                    }
                });
                
                if (i % 5 == 0)
                {
                    var metrics = Discord.DiscordNetBotManager.GetTtsBackpressureMetrics();
                    Console.WriteLine($"   After {i+1} requests: TTS Queue={metrics.ttsQueueCount}, Drops={metrics.totalTtsDrops}");
                }
            }
            
            // Wait for some requests to complete or timeout
            await Task.Delay(2000);
            
            var finalMetrics = Discord.DiscordNetBotManager.GetTtsBackpressureMetrics();
            Console.WriteLine($"   Final state: TTS Queue={finalMetrics.ttsQueueCount}, Total Drops={finalMetrics.totalTtsDrops}");
            
            if (finalMetrics.totalTtsDrops > 0)
            {
                Console.WriteLine("✅ TTS backpressure is working - drops detected under burst load");
            }
            else
            {
                Console.WriteLine("ℹ️ No TTS drops detected - queue capacity sufficient for test load");
            }
        }
        
        /// <summary>
        /// Run comprehensive backpressure tests
        /// </summary>
        public static async Task RunBackpressureTestsAsync()
        {
            Console.WriteLine("🧪 Running comprehensive backpressure tests...");
            Console.WriteLine("");
            
            TestAudioBackpressure();
            Console.WriteLine("");
            
            await TestTtsBackpressureAsync();
            Console.WriteLine("");
            
            Console.WriteLine("✅ Backpressure tests completed");
        }
    }
}