using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Snipvane.DTOs;
using Snipvane.Options;
using Snipvane.Services.Ai;
using Snipvane.Services.FFmpeg;
using Snipvane.Services.Highlights;

namespace Snipvane.Services.Transcription;

/// <summary>
/// Free-tier transcription via Gemini. Long files are split into ~8 minute
/// chunks so podcasts and lectures are not blocked by inline-upload size.
/// Word timestamps are model-estimated (less precise than Whisper).
/// </summary>
public class GeminiTranscriptionService
{
    private const double ChunkSeconds = 8 * 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsSnapshot<AiOptions> _ai;
    private readonly IFFmpegService _ffmpeg;
    private readonly ILogger<GeminiTranscriptionService> _logger;

    public GeminiTranscriptionService(
        IHttpClientFactory httpClientFactory,
        IOptionsSnapshot<AiOptions> ai,
        IFFmpegService ffmpeg,
        ILogger<GeminiTranscriptionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _ai = ai;
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    public async Task<TranscriptDocument> TranscribeAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_ai.Value.GeminiApiKey))
        {
            throw new InvalidOperationException(
                "Gemini API key is missing. Add Ai:GeminiApiKey to Snipvane/appsettings.Local.json.");
        }

        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException("Audio file missing for transcription.", audioPath);
        }

        var duration = await _ffmpeg.GetDurationSecondsAsync(audioPath, cancellationToken);
        if (duration <= ChunkSeconds + 15)
        {
            return await TranscribeFileAsync(audioPath, duration, offsetSeconds: 0, cancellationToken);
        }

        var chunkDir = Path.Combine(Path.GetDirectoryName(audioPath)!, "audio-chunks");
        Directory.CreateDirectory(chunkDir);

        var merged = new TranscriptDocument { Duration = duration };
        var languages = new List<string>();
        var chunkIndex = 0;

        for (double start = 0; start < duration; start += ChunkSeconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(ChunkSeconds, duration - start);
            var chunkPath = Path.Combine(chunkDir, $"chunk-{chunkIndex:D3}.mp3");
            _logger.LogInformation(
                "Transcribing audio chunk {Index} ({Start:0.#}s-{End:0.#}s) of {Duration:0.#}s",
                chunkIndex, start, start + length, duration);

            await _ffmpeg.ExtractAudioSegmentAsync(audioPath, chunkPath, start, length, cancellationToken);
            var piece = await TranscribeFileAsync(chunkPath, length, start, cancellationToken);

            if (!string.IsNullOrWhiteSpace(piece.Text))
            {
                merged.Text = string.IsNullOrWhiteSpace(merged.Text)
                    ? piece.Text
                    : merged.Text + " " + piece.Text;
            }

            if (!string.IsNullOrWhiteSpace(piece.Language))
            {
                languages.Add(piece.Language);
            }

            merged.Words.AddRange(piece.Words);
            chunkIndex++;
        }

        merged.Language = languages.GroupBy(l => l).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        _logger.LogInformation(
            "Gemini chunked transcript ready: {Chunks} chunks, {WordCount} words, {Duration:0.#}s",
            chunkIndex, merged.Words.Count, duration);
        return merged;
    }

    private async Task<TranscriptDocument> TranscribeFileAsync(
        string audioPath,
        double duration,
        double offsetSeconds,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(audioPath, cancellationToken);
        if (bytes.Length > 18 * 1024 * 1024)
        {
            throw new InvalidOperationException(
                "An audio chunk is too large for Gemini inline upload. Re-extract at a lower bitrate.");
        }

        var model = string.IsNullOrWhiteSpace(_ai.Value.GeminiModel) ? "gemini-3.5-flash-lite" : _ai.Value.GeminiModel;
        var prompt =
            $$"""
            Transcribe this audio with word-level timestamps.
            Audio duration is {{duration.ToString("0.###", CultureInfo.InvariantCulture)}} seconds.

            Return STRICT JSON only. No markdown, no code fences, no preamble.
            {
              "text": "full transcript",
              "language": "en",
              "words": [
                { "word": "Hello", "start": 0.0, "end": 0.35 }
              ]
            }

            Rules:
            - Cover the entire audio from 0 to the duration.
            - start and end are seconds relative to THIS clip (starting at 0), monotonic, and never exceed the duration.
            - Split into individual words, not sentences.
            - Keep original language; set language to an ISO-ish label (en, hi, mr, ...).
            """;

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new
                        {
                            inlineData = new
                            {
                                mimeType = "audio/mpeg",
                                data = Convert.ToBase64String(bytes)
                            }
                        },
                        new { text = prompt }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.1,
                responseMimeType = "application/json"
            }
        };

        var client = _httpClientFactory.CreateClient("gemini");
        var (body, usedModel) = await GeminiGenerate.PostJsonAsync(
            client,
            _ai.Value.GeminiApiKey,
            model,
            payload,
            _logger,
            cancellationToken);
        _logger.LogInformation("Gemini transcription used model {Model}", usedModel);

        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Gemini returned an empty transcript.");
        }

        var parsed = JsonSerializer.Deserialize<TranscriptDocument>(
            HighlightDetectionService.ExtractJsonPayload(text), JsonOptions)
            ?? new TranscriptDocument();

        var words = (parsed.Words ?? [])
            .Where(w => !string.IsNullOrWhiteSpace(w.Word))
            .Select(w => new TranscriptWord
            {
                Word = w.Word.Trim(),
                Start = Math.Round(Math.Max(0, w.Start) + offsetSeconds, 3),
                End = Math.Round(Math.Max(w.Start, w.End) + offsetSeconds, 3)
            })
            .ToList();

        if (words.Count == 0 && !string.IsNullOrWhiteSpace(parsed.Text))
        {
            _logger.LogWarning("Gemini returned no word timings; spreading words evenly across {Duration}s", duration);
            words = SpreadEvenly(parsed.Text, duration)
                .Select(w => new TranscriptWord
                {
                    Word = w.Word,
                    Start = Math.Round(w.Start + offsetSeconds, 3),
                    End = Math.Round(w.End + offsetSeconds, 3)
                })
                .ToList();
        }

        if (words.Count == 0)
        {
            throw new InvalidOperationException("Gemini returned no usable transcript words.");
        }

        var fullText = string.IsNullOrWhiteSpace(parsed.Text)
            ? string.Join(' ', words.Select(w => w.Word))
            : parsed.Text.Trim();

        return new TranscriptDocument
        {
            Text = fullText,
            Language = parsed.Language,
            Duration = duration,
            Words = words
        };
    }

    private static List<TranscriptWord> SpreadEvenly(string text, double duration)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || duration <= 0)
        {
            return [];
        }

        var slot = duration / tokens.Length;
        var words = new List<TranscriptWord>(tokens.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            words.Add(new TranscriptWord
            {
                Word = tokens[i],
                Start = Math.Round(i * slot, 3),
                End = Math.Round(Math.Min(duration, (i + 1) * slot), 3)
            });
        }

        return words;
    }
}
