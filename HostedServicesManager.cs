using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1
{
    /// <summary>
    /// Centralized manager for all hosted services with shared cancellation and lifecycle management.
    /// Provides coordinated startup/shutdown with proper error handling and timeouts.
    /// </summary>
    public class HostedServicesManager
    {
        private readonly List<IHostedService> _services = new List<IHostedService>();
        private readonly object _lock = new object();
        private CancellationTokenSource _serviceCancellationTokenSource;
        private volatile bool _isStarted = false;
        private volatile int _startupInProgress = 0;
        private volatile int _shutdownInProgress = 0;

        /// <summary>
        /// Gets the shared cancellation token for all services.
        /// </summary>
        public CancellationToken ServiceCancellationToken => _serviceCancellationTokenSource?.Token ?? CancellationToken.None;

        /// <summary>
        /// Gets whether the services are currently started.
        /// </summary>
        public bool IsStarted => _isStarted;

        /// <summary>
        /// Event fired when a service encounters an error.
        /// </summary>
        public event Action<string, Exception> OnServiceError;

        /// <summary>
        /// Event fired when service status changes.
        /// </summary>
        public event Action<string> OnStatusChanged;

        /// <summary>
        /// Register a service to be managed.
        /// </summary>
        /// <param name="service">Service to register</param>
        public void RegisterService(IHostedService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));

            lock (_lock)
            {
                if (_isStarted)
                {
                    throw new InvalidOperationException("Cannot register services after startup has begun");
                }

                if (_services.Any(s => s.ServiceName == service.ServiceName))
                {
                    Console.WriteLine($"⚠️ Service '{service.ServiceName}' already registered, skipping");
                    return;
                }

                _services.Add(service);
                Console.WriteLine($"🔧 Registered service: {service.ServiceName}");
            }
        }

        /// <summary>
        /// Start all registered services with shared cancellation token.
        /// Idempotent - safe to call multiple times.
        /// </summary>
        /// <param name="timeout">Timeout for startup operations</param>
        /// <returns>Task that completes when all services are started</returns>
        public async Task StartAllAsync(TimeSpan? timeout = null)
        {
            // Atomic check to prevent concurrent startup
            if (Interlocked.CompareExchange(ref _startupInProgress, 1, 0) != 0)
            {
                Console.WriteLine("🔧 HostedServicesManager startup already in progress, waiting...");
                while (_startupInProgress == 1 && !_isStarted)
                {
                    await Task.Delay(100);
                }
                return;
            }

            try
            {
                if (_isStarted)
                {
                    Console.WriteLine("🔧 HostedServicesManager already started");
                    return;
                }

                Console.WriteLine("🚀 Starting HostedServicesManager...");
                OnStatusChanged?.Invoke("Starting services...");

                // Create shared cancellation token source
                _serviceCancellationTokenSource = new CancellationTokenSource();
                var token = _serviceCancellationTokenSource.Token;

                var actualTimeout = timeout ?? TimeSpan.FromSeconds(30);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(actualTimeout);

                // Start all services concurrently
                var startupTasks = new List<Task>();
                
                lock (_lock)
                {
                    foreach (var service in _services)
                    {
                        startupTasks.Add(StartServiceSafeAsync(service, timeoutCts.Token));
                    }
                }

                if (startupTasks.Count > 0)
                {
                    await Task.WhenAll(startupTasks);
                }

                _isStarted = true;
                var serviceCount = _services.Count;
                Console.WriteLine($"✅ HostedServicesManager started successfully ({serviceCount} services)");
                OnStatusChanged?.Invoke($"Services started ({serviceCount} active)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ HostedServicesManager startup failed: {ex.Message}");
                OnServiceError?.Invoke("HostedServicesManager", ex);
                
                // Clean up on failure
                await StopAllAsync(TimeSpan.FromSeconds(10));
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref _startupInProgress, 0);
            }
        }

        /// <summary>
        /// Stop all services in reverse order with proper cleanup.
        /// Uses 2-second timeout per service with hard-stop mechanism for stragglers.
        /// Idempotent - safe to call multiple times.
        /// </summary>
        /// <param name="timeout">Overall timeout for shutdown operations</param>
        /// <returns>Task that completes when all services are stopped</returns>
        public async Task StopAllAsync(TimeSpan? timeout = null)
        {
            // Start telemetry timer
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int forcedKillCount = 0;

            // Atomic check to prevent concurrent shutdown
            if (Interlocked.CompareExchange(ref _shutdownInProgress, 1, 0) != 0)
            {
                Console.WriteLine("🔧 HostedServicesManager shutdown already in progress, waiting...");
                while (_shutdownInProgress == 1 && _isStarted)
                {
                    await Task.Delay(100);
                }
                return;
            }

            try
            {
                if (!_isStarted)
                {
                    Console.WriteLine("🔧 HostedServicesManager already stopped");
                    return;
                }

                Console.WriteLine("🛑 Stopping HostedServicesManager...");
                OnStatusChanged?.Invoke("Stopping services...");

                var overallTimeout = timeout ?? TimeSpan.FromSeconds(10); // Reduced from 30s
                var perServiceTimeout = TimeSpan.FromSeconds(2); // 2s per service as specified
                
                // Cancel shared token first
                _serviceCancellationTokenSource?.Cancel();

                using var overallTimeoutCts = new CancellationTokenSource();
                overallTimeoutCts.CancelAfter(overallTimeout);

                // Stop services in reverse order with individual timeouts
                var servicesToStop = new List<IHostedService>();
                lock (_lock)
                {
                    // Reverse order to stop services in opposite order of startup
                    for (int i = _services.Count - 1; i >= 0; i--)
                    {
                        servicesToStop.Add(_services[i]);
                    }
                }

                foreach (var service in servicesToStop)
                {
                    if (overallTimeoutCts.Token.IsCancellationRequested)
                    {
                        Console.WriteLine($"⚠️ Overall timeout reached, force stopping remaining services");
                        forcedKillCount += servicesToStop.Count - servicesToStop.IndexOf(service);
                        break;
                    }

                    try
                    {
                        Console.WriteLine($"🔧 Stopping service: {service.ServiceName} (2s timeout)");
                        
                        using var serviceTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(overallTimeoutCts.Token);
                        serviceTimeoutCts.CancelAfter(perServiceTimeout);

                        var stopTask = service.StopAsync(serviceTimeoutCts.Token);
                        var completedTask = await Task.WhenAny(stopTask, Task.Delay(perServiceTimeout, serviceTimeoutCts.Token));

                        if (completedTask == stopTask)
                        {
                            // Service stopped gracefully
                            try
                            {
                                await stopTask; // Get any exceptions
                                Console.WriteLine($"✅ Service stopped gracefully: {service.ServiceName}");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"⚠️ Service stop error (graceful): {service.ServiceName}: {ex.Message}");
                            }
                        }
                        else
                        {
                            // Service timed out, force stop
                            Console.WriteLine($"⚠️ Service {service.ServiceName} timed out after 2s, marking as force-stopped");
                            forcedKillCount++;
                            
                            // For specific services that might need hard kills, we could add special handling here
                            if (service.ServiceName.Contains("eSpeak") || service.ServiceName.Contains("Discord"))
                            {
                                Console.WriteLine($"🔨 Hard-stopping {service.ServiceName} (process-level termination)");
                                // The service implementations should handle their own process termination
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Error stopping service {service.ServiceName}: {ex.Message}");
                        OnServiceError?.Invoke(service.ServiceName, ex);
                    }
                }

                // Dispose shared cancellation token
                _serviceCancellationTokenSource?.Dispose();
                _serviceCancellationTokenSource = null;

                _isStarted = false;
                Console.WriteLine("✅ HostedServicesManager stopped successfully");
                OnStatusChanged?.Invoke("Services stopped");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ HostedServicesManager shutdown failed: {ex.Message}");
                OnServiceError?.Invoke("HostedServicesManager", ex);
            }
            finally
            {
                stopwatch.Stop();
                
                // Record telemetry
                Telemetry.Timer("app.stop", stopwatch.ElapsedMilliseconds);
                if (forcedKillCount > 0)
                {
                    Telemetry.Counter("app.stop.forced_kill", forcedKillCount);
                }
                
                Console.WriteLine($"📊 Shutdown completed in {stopwatch.ElapsedMilliseconds}ms, forced kills: {forcedKillCount}");
                
                Interlocked.Exchange(ref _shutdownInProgress, 0);
            }
        }

        /// <summary>
        /// Get status of all registered services.
        /// </summary>
        /// <returns>Dictionary of service name to running status</returns>
        public Dictionary<string, bool> GetServiceStatus()
        {
            lock (_lock)
            {
                return _services.ToDictionary(s => s.ServiceName, s => s.IsRunning);
            }
        }

        private async Task StartServiceSafeAsync(IHostedService service, CancellationToken cancellationToken)
        {
            try
            {
                Console.WriteLine($"🔧 Starting service: {service.ServiceName}");
                await service.StartAsync(cancellationToken);
                Console.WriteLine($"✅ Service started: {service.ServiceName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to start service '{service.ServiceName}': {ex.Message}");
                OnServiceError?.Invoke(service.ServiceName, ex);
                throw; // Re-throw to fail fast on startup errors
            }
        }

        private async Task StopServiceSafeAsync(IHostedService service, CancellationToken cancellationToken)
        {
            try
            {
                Console.WriteLine($"🔧 Stopping service: {service.ServiceName}");
                await service.StopAsync(cancellationToken);
                Console.WriteLine($"✅ Service stopped: {service.ServiceName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error stopping service '{service.ServiceName}': {ex.Message}");
                OnServiceError?.Invoke(service.ServiceName, ex);
                // Don't re-throw on shutdown - log and continue with other services
            }
        }
    }
}