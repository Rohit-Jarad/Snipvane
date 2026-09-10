using Snipvane.DTOs;
using Snipvane.Options;

namespace Snipvane.Services.Highlights;

/// <summary>
/// Isolated highlight-detection prompt. Edit this file when iterating on
/// virality heuristics — keep it out of HTTP / FFmpeg code.
/// </summary>
public static class HighlightPrompt
{
    public const string SystemInstruction =
        "You are a short-form video editor who cuts YouTube Shorts, Instagram Reels, and TikToks. " +
        "You output STRICT JSON only. No markdown, no code fences, no preamble, no commentary.";

    public static string BuildUserPrompt(
        TranscriptDocument transcript,
        double videoDurationSeconds,
        PipelineOptions pipeline)
    {
        var min = pipeline.MinSegmentSeconds;
        var max = pipeline.MaxSegmentSeconds;
        var transcriptText = transcript.Text ?? string.Empty;
        if (transcriptText.Length > 120_000)
        {
            transcriptText = transcriptText[..120_000] + "\n[transcript truncated for model context]";
        }

        var compactWords = CompactWordTimings(transcript.Words, 80_000);

        return
            $$"""
            Given a timestamped transcript of a long-form video, select 5-10 highlight segments that would perform well as standalone vertical shorts.

            Constraints:
            - Each segment MUST be between {{min:0}} and {{max:0}} seconds long (end_time - start_time).
            - start_time and end_time are in seconds on the source timeline and MUST fall within 0 and {{videoDurationSeconds:0.##}}.
            - Do not cut mid-sentence. Use the word timings to start/end on natural phrase boundaries.
            - Prefer segments with a strong hook in the first 3 seconds, a complete thought or punchline, and a satisfying ending.
            - Avoid intros, outros, ads, and filler.
            - Spread picks across the timeline (not only the first 10 minutes) when the video is long.
            - Rank by virality_score descending (1-10).
            - Return STRICT JSON only. No markdown. No code fences. No extra keys.

            JSON schema:
            {
              "segments": [
                {
                  "start_time": 12.4,
                  "end_time": 48.9,
                  "title": "short punchy title",
                  "virality_score": 8,
                  "reason": "why this works as a short (hook / emotional peak / punchline / controversy / insight)"
                }
              ]
            }

            Video duration seconds: {{videoDurationSeconds:0.##}}

            Full transcript:
            {{transcriptText}}

            Word-level timestamps (start-end word):
            {{compactWords}}
            """;
    }

    private static string CompactWordTimings(IReadOnlyList<TranscriptWord> words, int maxChars)
    {
        if (words.Count == 0)
        {
            return "(no word timings)";
        }

        var lines = words.Select(w => $"{w.Start:0.##}-{w.End:0.##} {w.Word}").ToList();
        var joined = string.Join('\n', lines);
        if (joined.Length <= maxChars)
        {
            return joined;
        }

        var step = Math.Max(2, (int)Math.Ceiling(joined.Length / (double)maxChars));
        return string.Join('\n', words.Where((_, i) => i % step == 0)
            .Select(w => $"{w.Start:0.##}-{w.End:0.##} {w.Word}"))
            + "\n[word timings downsampled for model context]";
    }
}
