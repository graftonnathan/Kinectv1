using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1
{
    /// <summary>
    /// Hosted service wrapper for VoiceRecognizer to provide centralized lifecycle management.
    /// </summary>
    public class VoiceRecognizerHostedService : IHostedService
    {
        private readonly string _modelPath;
        private readonly string _triggerName;
        private volatile bool _isStarted = false;
        private readonly object _lock = new object();
        private int _startInProgress = 0;
        private int _stopInProgress = 0;

        public string ServiceName => "VoiceRecognizer";
        
        public bool IsRunning 
        { 
            get 
            { 
                lock (_lock)
                {
                    return _isStarted && VoiceRecognizer.IsReady();
                }
            } 
        }

        public VoiceRecognizerHostedService(string modelPath, string triggerName = "john")
        {
            _modelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
            _triggerName = triggerName ?? "john";
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

                    // Check for cancellation before starting
                    cancellationToken.ThrowIfCancellationRequested();

                    Console.WriteLine($"🎤 Starting VoiceRecognizer service with model: {_modelPath}");
                    
                    // Use existing static Start method
                    VoiceRecognizer.Start(_modelPath, _triggerName);
                    
                    // Check for cancellation after starting
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    _isStarted = true;
                    Console.WriteLine("✅ VoiceRecognizer service started successfully");
                }

                return Task.CompletedTask;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("🎤 VoiceRecognizer startup was cancelled");
                _isStarted = false;
                return Task.CompletedTask; // Don't throw on cancellation during startup
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to start VoiceRecognizer service: {ex.Message}");
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

                    Console.WriteLine("🎤 Stopping VoiceRecognizer service...");
                    
                    // Use existing static Stop method
                    VoiceRecognizer.Stop();
                    
                    _isStarted = false;
                    Console.WriteLine("✅ VoiceRecognizer service stopped successfully");
                }

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error stopping VoiceRecognizer service: {ex.Message}");
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