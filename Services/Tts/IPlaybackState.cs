using System;
using System.Threading;

namespace Kinectv1
{
    /// <summary>
    /// Playback state abstraction for coordination with ASR / barge-in.
    /// </summary>
    public interface IPlaybackState
    {
        bool IsSpeaking { get; }
        CancellationToken PlaybackCancellationToken { get; }
        event Action OnPlaybackStart;
        event Action OnPlaybackStop;
    }
}
