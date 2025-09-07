using System;
using System.Threading;
using System.Threading.Tasks;
using Kinectv1.Discord;

namespace Kinectv1
{
    /// <summary>
    /// Hosted service wrapper for DiscordNetBotManager to provide centralized lifecycle management.
    /// </summary>
    public class DiscordBotHostedService : IHostedService
    {
        private volatile bool _isStarted = false;
        private volatile bool _isShuttingDown = false; // Flag to ignore late events post-stop
        private readonly object _lock = new object();
        private int _startInProgress = 0;
        private int _stopInProgress = 0;

        public string ServiceName => "DiscordBot";
        
        public bool IsRunning 
        { 
            get 
            { 
                lock (_lock)
                {
                    return _isStarted && !_isShuttingDown && DiscordNetBotManager.IsRunning;
                }
            } 
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // Atomic check to prevent concurrent starts
            if (Interlocked.CompareExchange(ref _startInProgress, 1, 0) != 0)
            {
                return; // Already starting
            }

            try
            {
                lock (_lock)
                {
                    if (_isStarted)
                    {
                        return; // Already started
                    }
                }

                Console.WriteLine("?? Starting Discord Bot service...");
                
                // Check if Discord is enabled in settings
                if (!(Kinectv1.App.SettingsProvider?.Current?.Discord?.Enabled ?? false))
                {
                    Console.WriteLine("?? Discord Bot service disabled in settings, skipping startup");
                    return;
                }

                // Use existing StartAsync method with cancellation token awareness
                using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                combinedCts.CancelAfter(TimeSpan.FromSeconds(30)); // Startup timeout
                
                // Monitor for cancellation during startup
                var startupTask = DiscordNetBotManager.StartAsync();
                var delayTask = Task.Delay(Timeout.Infinite, combinedCts.Token);
                
                var completedTask = await Task.WhenAny(startupTask, delayTask);
                
                if (completedTask == delayTask)
                {
                    // Startup was cancelled or timed out
                    Console.WriteLine("?? Discord Bot startup was cancelled or timed out");
                    return;
                }
                
                var success = await startupTask;
                
                lock (_lock)
                {
                    _isStarted = success;
                }

                if (success)
                {
                    Console.WriteLine("? Discord Bot service started successfully");
                }
                else
                {
                    Console.WriteLine("?? Discord Bot service failed to start (check configuration)");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("?? Discord Bot startup was cancelled");
                lock (_lock)
                {
                    _isStarted = false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Failed to start Discord Bot service: {ex.Message}");
                lock (_lock)
                {
                    _isStarted = false;
                }
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref _startInProgress, 0);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            // Atomic check to prevent concurrent stops
            if (Interlocked.CompareExchange(ref _stopInProgress, 1, 0) != 0)
            {
                return; // Already stopping
            }

            try
            {
                lock (_lock)
                {
                    if (!_isStarted)
                    {
                        return; // Already stopped
                    }
                    _isShuttingDown = true; // Set flag to ignore late events
                }

                Console.WriteLine("?? Stopping Discord Bot service...");
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                
                // Use existing ShutdownAsync method with enhanced timeout handling
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(5)); // 5s timeout for Discord disconnect

                    var shutdownTask = DiscordNetBotManager.ShutdownAsync();
                    
                    // Wait for shutdown with timeout
                    var completedTask = await Task.WhenAny(shutdownTask, Task.Delay(5000, timeoutCts.Token));
                    
                    if (completedTask == shutdownTask)
                    {
                        await shutdownTask; // Get any exceptions
                        Console.WriteLine("? Discord Bot disconnected gracefully");
                    }
                    else
                    {
                        Console.WriteLine("?? Discord Bot shutdown timed out after 5 seconds, forcing disconnect");
                        Telemetry.Counter("app.stop.forced_kill");
                        
                        // Force disconnect - DiscordNetBotManager should handle cleanup
                        try
                        {
                            // Additional force cleanup if needed
                            await shutdownTask; // Still try to get the result for cleanup
                        }
                        catch (OperationCanceledException)
                        {
                            Console.WriteLine("?? Discord Bot force disconnect completed");
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine("?? Discord Bot shutdown was cancelled");
                    Telemetry.Counter("app.stop.forced_kill");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"? Discord Bot shutdown error: {ex.Message}");
                }
                
                lock (_lock)
                {
                    _isStarted = false;
                }
                
                stopwatch.Stop();
                Console.WriteLine($"? Discord Bot service stopped in {stopwatch.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"?? Error stopping Discord Bot service: {ex.Message}");
                // Still mark as stopped even if there was an error
                lock (_lock)
                {
                    _isStarted = false;
                }
                // Don't throw on stop to allow other services to shutdown
            }
            finally
            {
                Interlocked.Exchange(ref _stopInProgress, 0);
            }
        }

        /// <summary>
        /// Check if service is shutting down (used to ignore late events)
        /// </summary>
        public bool IsShuttingDown => _isShuttingDown;
    }
}
