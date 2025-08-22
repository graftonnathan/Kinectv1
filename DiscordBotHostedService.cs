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
                    return _isStarted && DiscordNetBotManager.IsRunning;
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

                Console.WriteLine("🤖 Starting Discord Bot service...");
                
                // Check if Discord is enabled in settings
                if (!AppSettings.LoadDiscordBotEnabled())
                {
                    Console.WriteLine("🤖 Discord Bot service disabled in settings, skipping startup");
                    return;
                }

                // Use existing StartAsync method
                var success = await DiscordNetBotManager.StartAsync();
                
                lock (_lock)
                {
                    _isStarted = success;
                }

                if (success)
                {
                    Console.WriteLine("✅ Discord Bot service started successfully");
                }
                else
                {
                    Console.WriteLine("⚠️ Discord Bot service failed to start (check configuration)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to start Discord Bot service: {ex.Message}");
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
                }

                Console.WriteLine("🤖 Stopping Discord Bot service...");
                
                // Use existing ShutdownAsync method with timeout handling
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

                    var shutdownTask = DiscordNetBotManager.ShutdownAsync();
                    
                    // Wait for shutdown with timeout
                    await Task.WhenAny(shutdownTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));
                    
                    if (timeoutCts.Token.IsCancellationRequested)
                    {
                        Console.WriteLine("⚠️ Discord Bot shutdown timed out after 10 seconds");
                    }
                    else
                    {
                        await shutdownTask; // Get any exceptions
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine("⚠️ Discord Bot shutdown was cancelled");
                }
                
                lock (_lock)
                {
                    _isStarted = false;
                }
                
                Console.WriteLine("✅ Discord Bot service stopped successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error stopping Discord Bot service: {ex.Message}");
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
    }
}