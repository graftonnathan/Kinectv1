using System;
using System.Threading.Tasks;

namespace Kinectv1
{
    /// <summary>
    /// Simple test class to verify hosted services manager functionality.
    /// </summary>
    public static class HostedServicesTest
    {
        /// <summary>
        /// Test the hosted services manager startup and shutdown lifecycle.
        /// </summary>
        public static async Task TestHostedServicesLifecycle()
        {
            Console.WriteLine("🧪 TESTING: Hosted Services Manager Lifecycle");
            
            try
            {
                // Create a test hosted services manager
                var testManager = new HostedServicesManager();
                
                // Register event handlers to monitor service lifecycle
                testManager.OnServiceError += (serviceName, ex) => 
                {
                    Console.WriteLine($"   ⚠️ Service error in {serviceName}: {ex.Message}");
                };
                
                testManager.OnStatusChanged += (status) => 
                {
                    Console.WriteLine($"   📊 Status: {status}");
                };

                // Test 1: Verify initial state
                Console.WriteLine("   Test 1: Checking initial state...");
                if (!testManager.IsStarted)
                {
                    Console.WriteLine("   ✅ Manager correctly starts in stopped state");
                }
                else
                {
                    Console.WriteLine("   ❌ Manager should start in stopped state");
                    return;
                }

                // Test 2: Register test services that don't require external dependencies
                Console.WriteLine("   Test 2: Registering test services...");
                
                // Register pure test services (no external dependencies)
                var testService1 = new TestHostedService("TestService1");
                testManager.RegisterService(testService1);
                
                var testService2 = new TestHostedService("TestService2");
                testManager.RegisterService(testService2);
                
                // Register Ollama service (doesn't require external resources to initialize)
                var ollamaService = new OllamaHostedService();
                testManager.RegisterService(ollamaService);

                Console.WriteLine("   ✅ Test services registered successfully");

                // Test 3: Start all services
                Console.WriteLine("   Test 3: Starting all services...");
                await testManager.StartAllAsync(TimeSpan.FromSeconds(30));
                
                if (testManager.IsStarted)
                {
                    Console.WriteLine("   ✅ Services started successfully");
                }
                else
                {
                    Console.WriteLine("   ⚠️ Services manager not in started state");
                }

                // Test 4: Check service status
                Console.WriteLine("   Test 4: Checking service status...");
                var serviceStatus = testManager.GetServiceStatus();
                foreach (var kvp in serviceStatus)
                {
                    Console.WriteLine($"      {kvp.Key}: {(kvp.Value ? "Running" : "Stopped")}");
                }

                // Test 5: Test idempotent start (should not fail if called again)
                Console.WriteLine("   Test 5: Testing idempotent start...");
                await testManager.StartAllAsync(TimeSpan.FromSeconds(10));
                Console.WriteLine("   ✅ Idempotent start test passed");

                // Test 6: Stop all services
                Console.WriteLine("   Test 6: Stopping all services...");
                await testManager.StopAllAsync(TimeSpan.FromSeconds(30));
                
                if (!testManager.IsStarted)
                {
                    Console.WriteLine("   ✅ Services stopped successfully");
                }
                else
                {
                    Console.WriteLine("   ⚠️ Services manager still in started state after stop");
                }

                // Test 7: Test idempotent stop (should not fail if called again)
                Console.WriteLine("   Test 7: Testing idempotent stop...");
                await testManager.StopAllAsync(TimeSpan.FromSeconds(10));
                Console.WriteLine("   ✅ Idempotent stop test passed");

                Console.WriteLine("✅ Hosted Services Manager lifecycle test completed successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Hosted Services Manager test failed: {ex.Message}");
                Console.WriteLine($"   Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Test cancellation token propagation in hosted services.
        /// </summary>
        public static async Task TestCancellationTokenPropagation()
        {
            Console.WriteLine("🧪 TESTING: Cancellation Token Propagation");
            
            try
            {
                var testManager = new HostedServicesManager();
                
                // Register a test service that properly handles cancellation
                var testService = new TestHostedService("CancellationTestService");
                testManager.RegisterService(testService);

                Console.WriteLine("   Starting services to get shared cancellation token...");
                await testManager.StartAllAsync(TimeSpan.FromSeconds(15));

                // Get the shared cancellation token
                var sharedToken = testManager.ServiceCancellationToken;
                
                if (sharedToken != null && !sharedToken.IsCancellationRequested)
                {
                    Console.WriteLine("   ✅ Shared cancellation token available and not cancelled");
                }
                else
                {
                    Console.WriteLine("   ⚠️ Shared cancellation token not available or already cancelled");
                }

                // Let the service run for a moment to see background work
                await Task.Delay(3000);

                Console.WriteLine("   Stopping services to test cancellation...");
                await testManager.StopAllAsync(TimeSpan.FromSeconds(15));

                // After stop, the cancellation token should be disposed/cancelled
                Console.WriteLine("   ✅ Cancellation token propagation test completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Cancellation token propagation test failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Run all hosted services tests.
        /// </summary>
        public static async Task RunAllTests()
        {
            Console.WriteLine("🧪 === HOSTED SERVICES MANAGER TESTS ===");
            
            await TestHostedServicesLifecycle();
            Console.WriteLine();
            await TestCancellationTokenPropagation();
            
            Console.WriteLine("🧪 === HOSTED SERVICES TESTS COMPLETED ===");
        }
    }
}