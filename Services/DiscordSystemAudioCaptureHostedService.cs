using System;
using System.Threading;
using System.Threading.Tasks;
using Kinectv1.Discord;

namespace Kinectv1
{
    /// <summary>
    /// Hosted service wrapper for DiscordSystemAudioCapture to provide centralized lifecycle management.
    /// </summary>
    public class DiscordSystemAudioCaptureHostedService : IHostedService
    {
        private volatile bool _isStarted = false;
        private readonly object _lock = new object();
        private int _startInProgress = 0;
        private int _stopInProgress = 0;

        public string ServiceName => "DiscordSystemAudioCapture";
        
        public bool IsRunning 
        { 
            get 
            { 
                lock (_lock)
                {
                    return _isStarted && DiscordSystemAudioCapture.IsRunning;
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

                    Console.WriteLine("?? Starting Discord System Audio Capture service...");
                    
                    // Use existing static Start method
                    DiscordSystemAudioCapture.Start();
                    
                    _isStarted = DiscordSystemAudioCapture.IsRunning;
                    
                    if (_isStarted)
                    {
                        Console.WriteLine("? Discord System Audio Capture service started successfully");
                    }
                    else
                    {
                        Console.WriteLine("?? Discord System Audio Capture service failed to start");
                    }
                }

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Failed to start Discord System Audio Capture service: {ex.Message}");
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

                    Console.WriteLine("?? Stopping Discord System Audio Capture service...");
                    
                    // Use existing static Stop method
                    DiscordSystemAudioCapture.Stop();
                    
                    _isStarted = false;
                    Console.WriteLine("? Discord System Audio Capture service stopped successfully");
                }

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"?? Error stopping Discord System Audio Capture service: {ex.Message}");
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
