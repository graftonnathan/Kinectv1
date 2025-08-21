using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1
{
    /// <summary>
    /// Controls TTS playback with robust interrupt/flush semantics.
    /// Owns a CancellationTokenSource and utteranceId for each TTS operation.
    /// </summary>
    public class TtsPlaybackController
    {
        private static readonly object _lock = new object();
        private static CancellationTokenSource _currentCts;
        private static string _currentUtteranceId;
        private static int _utteranceCounter = 0;

        /// <summary>
        /// Start a new utterance, canceling any current utterance immediately.
        /// </summary>
        /// <param name="text">Text to speak</param>
        /// <param name="speaker">Speaker voice to use</param>
        /// <param name="speechFunc">Function to perform the actual speech synthesis and playback</param>
        /// <returns>Task that completes when utterance finishes or is canceled</returns>
        public static async Task<bool> StartUtterance(string text, string speaker, Func<string, string, CancellationToken, Task<bool>> speechFunc)
        {
            if (string.IsNullOrWhiteSpace(text) || speechFunc == null)
                return false;

            string utteranceId;
            CancellationToken token;

            lock (_lock)
            {
                // Cancel any existing utterance
                _currentCts?.Cancel();
                _currentCts?.Dispose();

                // Create new utterance with unique ID
                _utteranceCounter++;
                utteranceId = $"utterance_{_utteranceCounter}";
                _currentUtteranceId = utteranceId;
                _currentCts = new CancellationTokenSource();
                token = _currentCts.Token;
            }

            try
            {
                Console.WriteLine($"[TtsPlaybackController] Starting utterance: {utteranceId}");
                var result = await speechFunc(text, speaker, token);
                
                lock (_lock)
                {
                    // Only log completion if this utterance wasn't superseded
                    if (_currentUtteranceId == utteranceId)
                    {
                        Console.WriteLine($"[TtsPlaybackController] Completed utterance: {utteranceId}");
                    }
                    else
                    {
                        Console.WriteLine($"[TtsPlaybackController] Utterance {utteranceId} was superseded");
                    }
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
                lock (_lock)
                {
                    // Clean up if this was the current utterance
                    if (_currentUtteranceId == utteranceId)
                    {
                        _currentCts?.Dispose();
                        _currentCts = null;
                        _currentUtteranceId = null;
                    }
                }
            }
        }

        /// <summary>
        /// Cancel the current utterance if any.
        /// </summary>
        public static void CancelCurrent()
        {
            lock (_lock)
            {
                if (_currentCts != null && !_currentCts.IsCancellationRequested)
                {
                    Console.WriteLine($"[TtsPlaybackController] Canceling current utterance: {_currentUtteranceId}");
                    _currentCts.Cancel();
                }
            }
        }

        /// <summary>
        /// Check if there's currently an active utterance.
        /// </summary>
        public static bool HasActiveUtterance()
        {
            lock (_lock)
            {
                return _currentCts != null && !_currentCts.IsCancellationRequested;
            }
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