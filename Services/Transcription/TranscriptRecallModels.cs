using System;
using System.Collections.Generic;
using System.Linq;

namespace Kinectv1.Services.Transcription
{
    public sealed class TranscriptContextPack
    {
        public string Header { get; init; } = string.Empty;
        public IReadOnlyList<TranscriptSnippet> Snippets { get; init; } = Array.Empty<TranscriptSnippet>();
        public bool IsExpandedContext { get; init; } = false;

        public string ToPromptBlock(int maxSnippets = 10)
        {
            if (Snippets == null || Snippets.Count == 0)
                return Header ?? string.Empty;

            var lines = new List<string> { Header ?? string.Empty };

            foreach (var s in Snippets.Take(Math.Max(1, maxSnippets)))
            {
                var time = (!string.IsNullOrWhiteSpace(s.TimeStart) || !string.IsNullOrWhiteSpace(s.TimeEnd))
                    ? $"[{s.TimeStart}-{s.TimeEnd}]"
                    : string.Empty;

                lines.Add($"- {s.Title} {time} {s.Speaker}: {s.Text}".Trim());
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    public sealed class TranscriptSnippet
    {
        public string SessionId { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Speaker { get; init; } = string.Empty;
        public string TimeStart { get; init; } = string.Empty;
        public string TimeEnd { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public bool IsContextWindow { get; init; } = false;
    }
}
