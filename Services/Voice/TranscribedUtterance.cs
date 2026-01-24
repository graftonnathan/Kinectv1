using System;
using System.Collections.Generic;
using Kinectv1.Services.Speaker;

namespace Kinectv1.Services.Voice
{
    public sealed class TranscribedUtterance
    {
        public string Text { get; init; } = "";
        public double StartSec { get; init; }
        public double EndSec { get; init; }
        public long StartSample { get; init; }
        public long EndSample { get; init; }
        public string RawJson { get; init; } = "";
        public bool IsFinal { get; init; } = true;

        /// <summary>
        /// Word-level speaker assignments (only populated when word timings are available).
        /// Each word has its own speaker label based on the audio segment it occupies.
        /// </summary>
        public List<WordSpeakerAssignment> WordSpeakers { get; init; }

        /// <summary>
        /// Dominant speaker for this utterance (computed from word-level assignments).
        /// </summary>
        public string DominantSpeaker { get; init; } = "Unknown";

        /// <summary>
        /// Confidence score for the dominant speaker assignment.
        /// </summary>
        public float DominantSpeakerConfidence { get; init; }
    }
}
