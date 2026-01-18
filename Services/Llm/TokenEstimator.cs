using System;

namespace Kinectv1.Llm
{
    /// <summary>
    /// Simple token estimator for approximate token counting.
    /// Uses character-based heuristics (~4 chars per token for English text).
    /// </summary>
    public static class TokenEstimator
    {
        /// <summary>
        /// Average characters per token for English text.
        /// GPT models average ~4 chars/token, but this varies by content.
        /// </summary>
        private const double CharsPerToken = 4.0;

        /// <summary>
        /// Estimate token count for a string.
        /// </summary>
        public static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            // Primary estimate: character count / 4
            int charEstimate = (int)Math.Ceiling(text.Length / CharsPerToken);

            // Secondary estimate: word count * 1.3 (accounts for subword tokenization)
            int wordCount = CountWords(text);
            int wordEstimate = (int)Math.Ceiling(wordCount * 1.3);

            // Use the average of both methods for better accuracy
            return (charEstimate + wordEstimate) / 2;
        }

        /// <summary>
        /// Estimate tokens for multiple strings.
        /// </summary>
        public static int EstimateTokens(params string[] texts)
        {
            int total = 0;
            foreach (var text in texts)
            {
                total += EstimateTokens(text);
            }
            return total;
        }

        /// <summary>
        /// Check if text exceeds a token limit.
        /// </summary>
        public static bool ExceedsLimit(string text, int tokenLimit)
        {
            return EstimateTokens(text) > tokenLimit;
        }

        /// <summary>
        /// Truncate text to fit within a token limit (approximate).
        /// </summary>
        public static string TruncateToTokenLimit(string text, int tokenLimit)
        {
            if (string.IsNullOrEmpty(text) || tokenLimit <= 0)
                return string.Empty;

            int estimatedTokens = EstimateTokens(text);
            if (estimatedTokens <= tokenLimit)
                return text;

            // Calculate approximate character limit
            double ratio = (double)tokenLimit / estimatedTokens;
            int targetChars = (int)(text.Length * ratio * 0.95); // 5% safety margin

            if (targetChars >= text.Length)
                return text;

            // Truncate at word boundary if possible
            int cutoff = Math.Min(targetChars, text.Length);
            int lastSpace = text.LastIndexOf(' ', cutoff);

            if (lastSpace > cutoff * 0.8) // Only use word boundary if within 80% of target
                cutoff = lastSpace;

            return text.Substring(0, cutoff).TrimEnd() + "...";
        }

        /// <summary>
        /// Count words in text (simple whitespace split).
        /// </summary>
        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            int count = 0;
            bool inWord = false;

            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    inWord = false;
                }
                else if (!inWord)
                {
                    inWord = true;
                    count++;
                }
            }

            return count;
        }
    }
}
