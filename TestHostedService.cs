using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1
{
    /// <summary>
    /// Simple test hosted service for verifying the hosted services manager functionality.
    /// This service doesn't require external dependencies and can be used for testing.
    /// </summary>
    public class TestHostedService : IHostedService
    {
        private readonly string _serviceName;
        private volatile bool _isRunning = false;
        private readonly object _lock = new object();
        private int _startInProgress = 0;
        private int _stopInProgress = 0;
        private CancellationTokenSource _internalCts;
        private Task _backgroundTask;

        public string ServiceName => _serviceName;
        
        public bool IsRunning 
        { 
            get 
            { 
                lock (_lock)
                {
                    return _isRunning;
                }
            } 
        }

        public TestHostedService(string serviceName = "TestService")
        {
            _serviceName = serviceName;
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
                    if (_isRunning)
                    {
                        return; // Already started
                    }

                    // Check for cancellation before starting
                    cancellationToken.ThrowIfCancellationRequested();

                    Console.WriteLine($"🧪 Starting {ServiceName}...");
                    
                    // Create internal cancellation token linked to the provided one
                    _internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    
                    // Start a background task to simulate ongoing work
                    _backgroundTask = Task.Run(async () =>
                    {
                        try
                        {
                            while (!_internalCts.Token.IsCancellationRequested)
                            {
                                await Task.Delay(1000, _internalCts.Token);
                                Console.WriteLine($"🧪 {ServiceName} background work tick");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            Console.WriteLine($"🧪 {ServiceName} background work cancelled");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"🧪 {ServiceName} background work error: {ex.Message}");
                        }
                    }, _internalCts.Token);
                    
                    _isRunning = true;
                    Console.WriteLine($"✅ {ServiceName} started successfully");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"🧪 {ServiceName} startup was cancelled");
                _isRunning = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to start {ServiceName}: {ex.Message}");
                _isRunning = false;
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
                    if (!_isRunning)
                    {
                        return; // Already stopped
                    }

                    Console.WriteLine($"🧪 Stopping {ServiceName}...");
                    
                    // Cancel internal operations
                    _internalCts?.Cancel();
                    
                    _isRunning = false;
                }

                // Wait for background task to complete
                if (_backgroundTask != null)
                {
                    try
                    {
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                        
                        await Task.WhenAny(_backgroundTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));
                        
                        if (!_backgroundTask.IsCompleted)
                        {
                            Console.WriteLine($"⚠️ {ServiceName} background task did not complete within timeout");
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        Console.WriteLine($"🧪 {ServiceName} stop was cancelled");
                    }
                }

                // Dispose resources
                _internalCts?.Dispose();
                _internalCts = null;
                _backgroundTask = null;
                
                Console.WriteLine($"✅ {ServiceName} stopped successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error stopping {ServiceName}: {ex.Message}");
                // Still mark as stopped even if there was an error
                _isRunning = false;
            }
            finally
            {
                Interlocked.Exchange(ref _stopInProgress, 0);
            }
        }
    }
}