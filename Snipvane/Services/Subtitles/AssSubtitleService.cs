using System.Globalization;
using System.Text;
using Snipvane.DTOs;

namespace Snipvane.Services.Subtitles;

public interface ISubtitleService
{
    /// <summary>
    /// Builds a karaoke-style ASS file for a clip using word-level timestamps.
    /// Times in <paramref name="words"/> are absolute (video timeline); they are
    /// converted to clip-local times using <paramref name="clipStartSeconds"/>.
    /// </summary>
    string BuildKaraokeAss(
        IReadOnlyList<TranscriptWord> words,
        double clipStartSeconds,
        double clipEndSeconds,
        int playResX = 1080,
        int playResY = 1920);
}

/// <summary>
/// Isolated subtitle-burning helper. Iterate on grouping, colors, and karaoke
/// timing here without touching FFmpeg invocation or highlight detection.
///
/// Approach: group words into short caption lines. For each word in a line,
/// emit a Dialogue event covering that word's duration, with the active word
/// highlighted (yellow + bold) and the rest of the line in white. FFmpeg then
/// burns the ASS via `-vf subtitles=`.
/// </summary>
public class AssSubtitleService : ISubtitleService
{
    private const int MaxWordsPerLine = 6;
    private const double MaxLineDurationSeconds = 2.8;

    public string BuildKaraokeAss(
        IReadOnlyList<TranscriptWord> words,
        double clipStartSeconds,
        double clipEndSeconds,
        int playResX = 1080,
        int playResY = 1920)
    {
        var inRange = words
            .Where(w => w.End > clipStartSeconds && w.Start < clipEndSeconds && !string.IsNullOrWhiteSpace(w.Word))
            .Select(w => new TranscriptWord
            {
                Word = w.Word.Trim(),
                Start = Math.Max(w.Start, clipStartSeconds),
                End = Math.Min(w.End, clipEndSeconds)
            })
            .Where(w => w.End > w.Start)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("Title: Snipvane karaoke captions");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine("WrapStyle: 0");
        sb.AppendLine("ScaledBorderAndShadow: yes");
        sb.AppendLine(CultureInfo.InvariantCulture, $"PlayResX: {playResX}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"PlayResY: {playResY}");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine(
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, " +
            "Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, " +
            "Alignment, MarginL, MarginR, MarginV, Encoding");
        // BorderStyle 3 = opaque box behind the line. Alignment 2 = bottom-center.
        // Primary white, outline/box dark. Font size tuned for 1080x1920.
        sb.AppendLine(
            "Style: Default,Arial,68,&H00FFFFFF,&H0000FFFF,&H00000000,&H96000000," +
            "0,0,0,0,100,100,0,0,3,4,0,2,60,60,220,1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        foreach (var line in GroupIntoLines(inRange))
        {
            foreach (var word in line)
            {
                var localStart = Math.Max(0, word.Start - clipStartSeconds);
                var localEnd = Math.Max(localStart + 0.05, word.End - clipStartSeconds);
                var text = BuildHighlightedLine(line, word);
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"Dialogue: 0,{FormatAssTime(localStart)},{FormatAssTime(localEnd)},Default,,0,0,0,,{text}");
            }
        }

        return sb.ToString();
    }

    private static IEnumerable<List<TranscriptWord>> GroupIntoLines(IReadOnlyList<TranscriptWord> words)
    {
        var current = new List<TranscriptWord>();
        foreach (var word in words)
        {
            var wouldOverflowCount = current.Count >= MaxWordsPerLine;
            var wouldOverflowTime = current.Count > 0
                && word.End - current[0].Start > MaxLineDurationSeconds;
            var naturalBreak = current.Count > 0 && EndsSentence(current[^1].Word);

            if (current.Count > 0 && (wouldOverflowCount || wouldOverflowTime || naturalBreak))
            {
                yield return current;
                current = [];
            }

            current.Add(word);
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }

    private static bool EndsSentence(string word) =>
        word.EndsWith('.') || word.EndsWith('!') || word.EndsWith('?');

    private static string BuildHighlightedLine(IReadOnlyList<TranscriptWord> line, TranscriptWord active)
    {
        var parts = new List<string>(line.Count);
        foreach (var word in line)
        {
            var escaped = EscapeAss(word.Word);
            if (ReferenceEquals(word, active))
            {
                // Yellow + bold current word (ASS colour is &HAABBGGRR).
                parts.Add(@"{\b1\c&H0000FFFF&}" + escaped + @"{\c&H00FFFFFF&\b0}");
            }
            else
            {
                parts.Add(escaped);
            }
        }

        return string.Join(' ', parts);
    }

    internal static string EscapeAss(string text)
    {
        return text
            .Replace("\\", @"\\")
            .Replace("{", @"\{")
            .Replace("}", @"\}")
            .Replace("\r", string.Empty)
            .Replace("\n", @"\N");
    }

    internal static string FormatAssTime(double seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        var time = TimeSpan.FromSeconds(seconds);
        // ASS uses H:MM:SS.cs (centiseconds).
        return string.Create(CultureInfo.InvariantCulture, $"{time.Hours}:{time.Minutes:D2}:{time.Seconds:D2}.{time.Milliseconds / 10:D2}");
    }
}
