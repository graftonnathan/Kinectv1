using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1
{
    /// <summary>
    /// Simple test to validate telemetry functionality over a 1-minute run
    /// Tests that metrics log file is present and counters update during execution
    /// </summary>
    public static class TelemetryTest
    {
        /// <summary>
        /// Run a 1-minute telemetry test to validate metrics collection
        /// </summary>
        /// <returns>0 for success, 1 for failure</returns>
        public static int RunOneMinuteTest()
        {
            Console.WriteLine("🧪 Starting 1-minute telemetry test...");
            
            try
            {
                // Configure telemetry for testing
                AppSettings.ConfigureTelemetrySettings(true, "logs/telemetry.ndjson", 100);
                Telemetry.RefreshSettings();
                
                // Clear existing metrics
                Telemetry.Reset();
                
                // Record start event
                Telemetry.Info("test.start", new { duration_seconds = 60 });
                
                var startTime = DateTime.UtcNow;
                var testDuration = TimeSpan.FromMinutes(1);
                var interval = TimeSpan.FromSeconds(5);
                int iteration = 0;
                
                Console.WriteLine($"   📊 Test duration: {testDuration.TotalSeconds} seconds");
                Console.WriteLine($"   📊 Metric interval: {interval.TotalSeconds} seconds");
                
                // Run test loop for 1 minute
                while (DateTime.UtcNow - startTime < testDuration)
                {
                    iteration++;
                    
                    // Simulate key metrics that should be tracked
                    Telemetry.Counter("test.iterations");
                    Telemetry.Timer("test.discord.join.ms", 150 + (iteration % 50)); // Simulate discord join timing
                    Telemetry.Timer("test.audio.resample.ms", 12 + (iteration % 8)); // Simulate audio resampling  
                    Telemetry.Timer("test.asr.latency.ms", 45 + (iteration % 20)); // Simulate ASR latency
                    Telemetry.Timer("test.tts.synth.ms", 200 + (iteration % 100)); // Simulate TTS synthesis
                    Telemetry.Counter("test.frames.processed", 10);
                    Telemetry.Counter("test.frames.dropped", iteration % 10 == 0 ? 1 : 0); // Occasional drops
                    
                    // Record periodic health snapshot
                    if (iteration % 6 == 0) // Every ~30 seconds (6 * 5 seconds)
                    {
                        var healthData = new
                        {
                            iteration = iteration,
                            uptime_seconds = (DateTime.UtcNow - startTime).TotalSeconds,
                            discord_join_avg = Telemetry.GetCounter("test.discord.join.ms.count") > 0 
                                ? Telemetry.GetAccumulator("test.discord.join.ms.total_ms") / Telemetry.GetCounter("test.discord.join.ms.count")
                                : 0,
                            frames_processed = Telemetry.GetCounter("test.frames.processed"),
                            frames_dropped = Telemetry.GetCounter("test.frames.dropped"),
                            test_active = true
                        };
                        
                        Telemetry.Metric("test.health.snapshot", healthData);
                        Console.WriteLine($"   📈 Iteration {iteration}: Processed {healthData.frames_processed}, Dropped {healthData.frames_dropped}");
                    }
                    
                    Thread.Sleep(interval);
                }
                
                // Record completion event
                var finalMetrics = new
                {
                    duration_seconds = (DateTime.UtcNow - startTime).TotalSeconds,
                    total_iterations = iteration,
                    total_frames_processed = Telemetry.GetCounter("test.frames.processed"),
                    total_frames_dropped = Telemetry.GetCounter("test.frames.dropped"),
                    avg_discord_join_ms = Telemetry.GetCounter("test.discord.join.ms.count") > 0 
                        ? Telemetry.GetAccumulator("test.discord.join.ms.total_ms") / Telemetry.GetCounter("test.discord.join.ms.count")
                        : 0
                };
                
                Telemetry.Info("test.completed", finalMetrics);
                
                // Verify log file exists and has content
                var logPath = AppSettings.LoadTelemetryFile();
                if (!File.Exists(logPath))
                {
                    Console.WriteLine("   ❌ Telemetry log file not found");
                    return 1;
                }
                
                var logSize = new FileInfo(logPath).Length;
                if (logSize == 0)
                {
                    Console.WriteLine("   ❌ Telemetry log file is empty");
                    return 1;
                }
                
                Console.WriteLine($"   ✅ Test completed successfully");
                Console.WriteLine($"   📊 Final metrics:");
                Console.WriteLine($"      Duration: {finalMetrics.duration_seconds:F1}s");
                Console.WriteLine($"      Iterations: {finalMetrics.total_iterations}");
                Console.WriteLine($"      Frames processed: {finalMetrics.total_frames_processed}");
                Console.WriteLine($"      Frames dropped: {finalMetrics.total_frames_dropped}");
                Console.WriteLine($"      Avg Discord join: {finalMetrics.avg_discord_join_ms:F1}ms");
                Console.WriteLine($"   📁 Log file: {logPath} ({logSize} bytes)");
                
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Test failed: {ex.Message}");
                return 1;
            }
        }
        
        /// <summary>
        /// Quick validation that telemetry basics work
        /// </summary>
        /// <returns>0 for success, 1 for failure</returns>
        public static int RunQuickTest()
        {
            Console.WriteLine("🧪 Running quick telemetry validation...");
            
            try
            {
                // Configure telemetry
                AppSettings.ConfigureTelemetrySettings(true, "logs/telemetry.ndjson", 100);
                Telemetry.RefreshSettings();
                
                // Test basic functionality
                Telemetry.Info("test.quick.start");
                Telemetry.Counter("test.counter", 5);
                Telemetry.Timer("test.timer", 42);
                Telemetry.Metric("test.metric", new { value = 123, status = "ok" });
                Telemetry.Warn("test.warning", new { message = "test warning" });
                
                // Verify counters work
                if (Telemetry.GetCounter("test.counter") != 5)
                {
                    Console.WriteLine("   ❌ Counter test failed");
                    return 1;
                }
                
                if (Telemetry.GetCounter("test.timer.count") != 1)
                {
                    Console.WriteLine("   ❌ Timer test failed");
                    return 1;
                }
                
                Console.WriteLine("   ✅ Quick test passed");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Quick test failed: {ex.Message}");
                return 1;
            }
        }
    }
}