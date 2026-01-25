using System;

namespace Kinectv1.Services.Transcription
{
    /// <summary>
    /// Cheap, stable token estimator. Good enough for deterministic packing.
    /// Typical English: ~4 chars/token. Add small overhead for newlines/timestamps.
    /// </summary>
    public static class TokenBudget
    {
        public static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return (int)Math.Ceiling(text.Length / 4.0) + 8;
        }
    }
}
