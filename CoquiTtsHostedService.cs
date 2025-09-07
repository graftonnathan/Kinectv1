using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1
{
    /// <summary>
    /// Hosted service wrapper for CoquiTtsService to provide centralized lifecycle management.
    /// </summary>
    public class CoquiTtsHostedService : IHostedService
    {
        private volatile bool _isStarted = false;
        private readonly object _lock = new object();
        private int _startInProgress = 0;
        private int _stopInProgress = 0;

        public string ServiceName => "CoquiTTS";
        
        public bool IsRunning 
        { 
            get 
            { 
                lock (_lock)
                {
                    return _isStarted && CoquiTtsService.IsEnabled();
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

                    Console.WriteLine("🔊 Starting CoquiTTS service...");
                    
                    // Initialize the TTS service
                    var initSuccess = CoquiTtsService.Initialize();
                    
                    if (initSuccess)
                    {
                        // Enable TTS based on saved settings
                        var ttsEnabled = Kinectv1.App.SettingsProvider?.Current?.Tts?.Enabled ?? false;
                        CoquiTtsService.SetEnabled(ttsEnabled);
                        
                        _isStarted = true;
                        Console.WriteLine($"✅ CoquiTTS service started successfully (enabled: {ttsEnabled})");
                    }
                    else
                    {
                        Console.WriteLine("⚠️ CoquiTTS service failed to initialize");
                        _isStarted = false;
                    }
                }

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to start CoquiTTS service: {ex.Message}");
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

                    Console.WriteLine("🔊 Stopping CoquiTTS service...");
                    
                    // Disable TTS service
                    CoquiTtsService.SetEnabled(false);
                    
                    // Call shutdown to clean up resources
                    CoquiTtsService.Shutdown();
                    
                    _isStarted = false;
                    Console.WriteLine("✅ CoquiTTS service stopped successfully");
                }

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error stopping CoquiTTS service: {ex.Message}");
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