using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace Kinectv1
{
    /// <summary>
    /// Simple test to validate shutdown lifecycle behavior
    /// </summary>
    public static class ShutdownLifecycleTest
    {
        /// <summary>
        /// Test the HostedServicesManager shutdown logic
        /// </summary>
        public static async Task TestHostedServicesManagerShutdown()
        {
            Console.WriteLine("=== Testing HostedServicesManager Shutdown ===");
            
            var manager = new HostedServicesManager();
            
            // Register a mock service
            var mockService = new MockHostedService("TestService", TimeSpan.FromMilliseconds(1500)); // 1.5s shutdown
            manager.RegisterService(mockService);
            
            // Start services
            await manager.StartAllAsync();
            Console.WriteLine($"Service started: {mockService.IsRunning}");
            
            // Test shutdown with timing
            var stopwatch = Stopwatch.StartNew();
            await manager.StopAllAsync(TimeSpan.FromSeconds(5));
            stopwatch.Stop();
            
            Console.WriteLine($"Shutdown completed in {stopwatch.ElapsedMilliseconds}ms");
            Console.WriteLine($"Service stopped: {!mockService.IsRunning}");
            
            // Verify telemetry
            var stopTime = Telemetry.GetAccumulator("app.stop.total_ms");
            var forcedKills = Telemetry.GetCounter("app.stop.forced_kill");
            
            Console.WriteLine($"Telemetry - Stop time: {stopTime}ms, Forced kills: {forcedKills}");
            Console.WriteLine("✅ HostedServicesManager test completed");
        }
        
        /// <summary>
        /// Test EspeakIpaService process cleanup
        /// </summary>
        public static void TestEspeakProcessCleanup()
        {
            Console.WriteLine("=== Testing EspealIpaService Process Cleanup ===");
            
            try
            {
                // Create and dispose service to test cleanup
                using (var service = new EspeakIpaService())
                {
                    // Service should start espeak-ng process
                    Console.WriteLine("EspeakIpaService created");
                }
                
                // Service disposal should clean up process
                Console.WriteLine("✅ EspeakIpaService cleanup test completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ EspeakIpaService test failed: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Mock hosted service for testing
        /// </summary>
        private class MockHostedService : IHostedService
        {
            private readonly TimeSpan _shutdownDelay;
            private volatile bool _isRunning = false;
            
            public MockHostedService(string serviceName, TimeSpan shutdownDelay)
            {
                ServiceName = serviceName;
                _shutdownDelay = shutdownDelay;
            }
            
            public string ServiceName { get; }
            public bool IsRunning => _isRunning;
            
            public Task StartAsync(CancellationToken cancellationToken)
            {
                _isRunning = true;
                Console.WriteLine($"Mock service {ServiceName} started");
                return Task.CompletedTask;
            }
            
            public async Task StopAsync(CancellationToken cancellationToken)
            {
                Console.WriteLine($"Mock service {ServiceName} stopping (will take {_shutdownDelay.TotalMilliseconds}ms)");
                
                try
                {
                    await Task.Delay(_shutdownDelay, cancellationToken);
                    _isRunning = false;
                    Console.WriteLine($"Mock service {ServiceName} stopped gracefully");
                }
                catch (OperationCanceledException)
                {
                    _isRunning = false;
                    Console.WriteLine($"Mock service {ServiceName} stopped via cancellation");
                    throw;
                }
            }
        }
        
        /// <summary>
        /// Run all shutdown tests
        /// </summary>
        public static async Task RunAllTests()
        {
            try
            {
                Console.WriteLine("🧪 === SHUTDOWN LIFECYCLE TESTS ===");
                
                // Reset telemetry for clean test
                Telemetry.Reset();
                
                await TestHostedServicesManagerShutdown();
                Console.WriteLine();
                
                TestEspeakProcessCleanup();
                Console.WriteLine();
                
                Console.WriteLine("✅ All shutdown lifecycle tests completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Test suite failed: {ex.Message}");
            }
        }
    }
}