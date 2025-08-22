using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1
{
    /// <summary>
    /// Hosted service wrapper for OllamaService to provide centralized lifecycle management.
    /// </summary>
    public class OllamaHostedService : IHostedService
    {
        private volatile bool _isStarted = false;
        private readonly object _lock = new object();
        private int _startInProgress = 0;
        private int _stopInProgress = 0;

        public string ServiceName => "Ollama";
        
        public bool IsRunning 
        { 
            get 
            { 
                lock (_lock)
                {
                    return _isStarted && OllamaService.IsEnabled();
                }
            } 
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Atomic check to prevent concurrent starts
            if (Interlocked.CompareExchange(ref _startInProgress, 1, 0) != 0)
            {
                return Task.CompletedTask; // Already starting
            }

            try
            {
                lock (_lock)
                {
                    if (_isStarted)
                    {
                        return Task.CompletedTask; // Already started
                    }

                    Console.WriteLine("🤖 Starting Ollama service...");
                    
                    // Load saved enabled state and apply it
                    var ollamaEnabled = AppSettings.LoadOllamaEnabled();
                    OllamaService.SetEnabled(ollamaEnabled);
                    
                    _isStarted = true;
                    Console.WriteLine($"✅ Ollama service started successfully (enabled: {ollamaEnabled})");
                }

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to start Ollama service: {ex.Message}");
                _isStarted = false;
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref _startInProgress, 0);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // Atomic check to prevent concurrent stops
            if (Interlocked.CompareExchange(ref _stopInProgress, 1, 0) != 0)
            {
                return Task.CompletedTask; // Already stopping
            }

            try
            {
                lock (_lock)
                {
                    if (!_isStarted)
                    {
                        return Task.CompletedTask; // Already stopped
                    }

                    Console.WriteLine("🤖 Stopping Ollama service...");
                    
                    // Disable Ollama service
                    OllamaService.SetEnabled(false);
                    
                    _isStarted = false;
                    Console.WriteLine("✅ Ollama service stopped successfully");
                }

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error stopping Ollama service: {ex.Message}");
                // Still mark as stopped even if there was an error
                _isStarted = false;
                return Task.CompletedTask; // Don't throw on stop
            }
            finally
            {
                Interlocked.Exchange(ref _stopInProgress, 0);
            }
        }
    }
}