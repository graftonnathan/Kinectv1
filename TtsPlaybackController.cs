using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1
{
    /// <summary>
    /// Controls TTS playback with robust interrupt/flush semantics.
    /// Owns a CancellationTokenSource and utteranceId for each TTS operation.
    /// Implements IPlaybackState for ASR gating coordination.
    /// </summary>
    public class TtsPlaybackController : IPlaybackState
    {
        private static readonly object _lock = new object();
        private static CancellationTokenSource _currentCts;
        private static Task _currentTask = Task.CompletedTask;
        private static string _currentUtteranceId;
        private static int _utteranceCounter = 0;

        // IPlaybackState implementation
        private static readonly TtsPlaybackController _instance = new TtsPlaybackController();
        public static IPlaybackState Instance => _instance;

        /// <summary>
        /// True if TTS is currently speaking/playing audio
        /// </summary>
        public bool IsSpeaking 
        { 
            get 
            { 
                lock (_lock)
                {
                    return _currentCts != null && !_currentCts.IsCancellationRequested && _currentTask != null && !_currentTask.IsCompleted;
                }
            } 
        }

        /// <summary>
        /// Cancellation token for the current TTS operation
        /// </summary>
        public CancellationToken PlaybackCancellationToken 
        { 
            get 
            { 
                lock (_lock)
                {
                    return _currentCts?.Token ?? CancellationToken.None;
                }
            } 
        }

        /// <summary>
        /// Event raised when TTS playback starts
        /// </summary>
        public event Action OnPlaybackStart;

        /// <summary>
        /// Event raised when TTS playback stops (completed or canceled)
        /// </summary>
        public event Action OnPlaybackStop;

        /// <summary>
        /// Start a new utterance, canceling any current utterance immediately. Waits briefly for
        /// the previous playback to stop (device release) before starting the next.
        /// </summary>
        public static async Task<bool> StartUtterance(string text, string speaker, Func<string, string, CancellationToken, Task<bool>> speechFunc)
        {
            if (string.IsNullOrWhiteSpace(text) || speechFunc == null)
                return false;

            // Prepare a new CTS and swap it in atomically
            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _currentCts, newCts);

            // Snapshot and cancel previous
            Task prevTask;
            lock (_lock)
            {
                prevTask = _currentTask;
            }
            try { oldCts?.Cancel(); } catch { }

            // Await previous playback to stop and release the device (short cap)
            if (prevTask != null && !prevTask.IsCompleted)
            {
                try { await Task.WhenAny(prevTask, Task.Delay(500)).ConfigureAwait(false); } catch { }
            }

            // Dispose old CTS after cancellation
            try { oldCts?.Dispose(); } catch { }

            // Create new utterance id
            string utteranceId;
            lock (_lock)
            {
                _utteranceCounter++;
                utteranceId = $"utterance_{_utteranceCounter}";
                _currentUtteranceId = utteranceId;
            }

            // Raise playback start
            try
            {
                _instance.OnPlaybackStart?.Invoke();
                Telemetry.Gauge("tts.active", 1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TtsPlaybackController] Error raising OnPlaybackStart: {ex.Message}");
            }

            // Start new playback and track task
            Task<bool> playTask;
            lock (_lock)
            {
                playTask = speechFunc(text, speaker, newCts.Token);
                _currentTask = playTask;
            }

            try
            {
                Console.WriteLine($"[TtsPlaybackController] Starting utterance: {utteranceId}");
                var result = await playTask.ConfigureAwait(false);

                lock (_lock)
                {
                    if (_currentUtteranceId == utteranceId)
                        Console.WriteLine($"[TtsPlaybackController] Completed utterance: {utteranceId}");
                    else
                        Console.WriteLine($"[TtsPlaybackController] Utterance {utteranceId} was superseded");
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[TtsPlaybackController] Utterance {utteranceId} was canceled");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TtsPlaybackController] Error in utterance {utteranceId}: {ex.Message}");
                return false;
            }
            finally
            {
                // Clear current if still ours; dispose CTS and raise stop
                bool raiseStop = false;
                lock (_lock)
                {
                    if (_currentUtteranceId == utteranceId)
                    {
                        try { _currentCts?.Dispose(); } catch { }
                        _currentCts = null;
                        _currentUtteranceId = null;
                        raiseStop = true;
                    }
                }
                if (raiseStop)
                {
                    try
                    {
                        _instance.OnPlaybackStop?.Invoke();
                        Telemetry.Gauge("tts.active", 0);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TtsPlaybackController] Error raising OnPlaybackStop: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Cancel the current utterance if any.
        /// </summary>
        public static void CancelCurrent()
        {
            var cts = _currentCts;
            try { cts?.Cancel(); } catch { }
            // Stop event will be raised by StartUtterance finally when it unwinds
        }

        /// <summary>
        /// Check if there's currently an active utterance.
        /// </summary>
        public static bool HasActiveUtterance()
        {
            Task t;
            lock (_lock)
            {
                t = _currentTask;
            }
            return t != null && !t.IsCompleted;
        }

        /// <summary>
        /// Get the current utterance ID if any.
        /// </summary>
        public static string GetCurrentUtteranceId()
        {
            lock (_lock)
            {
                return _currentUtteranceId;
            }
        }
    }
}