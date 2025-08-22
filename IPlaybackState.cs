using System;
using System.Threading;

namespace Kinectv1
{
    /// <summary>
    /// Interface for shared TTS playback state to coordinate ASR gating.
    /// Used to prevent ASR dispatch while TTS is actively speaking.
    /// </summary>
    public interface IPlaybackState
    {
        /// <summary>
        /// True if TTS is currently speaking/playing audio
        /// </summary>
        bool IsSpeaking { get; }

        /// <summary>
        /// Cancellation token for the current TTS operation
        /// </summary>
        CancellationToken PlaybackCancellationToken { get; }

        /// <summary>
        /// Event raised when TTS playback starts
        /// </summary>
        event Action OnPlaybackStart;

        /// <summary>
        /// Event raised when TTS playback stops (completed or canceled)
        /// </summary>
        event Action OnPlaybackStop;
    }
}