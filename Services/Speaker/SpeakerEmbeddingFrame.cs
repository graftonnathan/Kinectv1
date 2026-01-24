using System;

namespace Kinectv1.Services.Speaker
{
    public sealed class SpeakerEmbeddingFrame
    {
        public long StartSample { get; init; }
        public long EndSample { get; init; }
        public float[] Embedding { get; init; } = Array.Empty<float>();
        public float RmsDb { get; init; }
        public bool IsSpeech { get; init; }
    }
}
