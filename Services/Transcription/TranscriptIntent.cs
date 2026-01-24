using System;
using System.Text.RegularExpressions;

namespace Kinectv1.Services.Transcription
{
    public static class TranscriptIntent
    {
        // Simple heuristic; expand as needed.
        private static readonly Regex Rx = new Regex(
            @"\b(transcript|transcription|meeting notes|what did we say|what was said|who said|recap the meeting|summarize the (last )?meeting|from the call|from the teams call|in the meeting|during the meeting|pull up the transcript|pull up.*transcript)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsTranscriptAsk(string userQuery)
        {
            if (string.IsNullOrWhiteSpace(userQuery)) return false;
            return Rx.IsMatch(userQuery);
        }

        public static TranscriptTimeHint? TryParseTimeHint(string userQuery)
        {
            userQuery = userQuery?.ToLowerInvariant() ?? string.Empty;

            if (userQuery.Contains("last meeting")) return TranscriptTimeHint.LastMeeting;
            if (userQuery.Contains("yesterday")) return TranscriptTimeHint.Yesterday;
            if (userQuery.Contains("today")) return TranscriptTimeHint.Today;

            return null;
        }
    }

    public enum TranscriptTimeHint
    {
        Today,
        Yesterday,
        LastMeeting
    }
}
