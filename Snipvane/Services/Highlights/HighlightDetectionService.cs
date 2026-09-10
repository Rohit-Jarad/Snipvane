using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Snipvane.DTOs;
using Snipvane.Options;
using Snipvane.Services.Ai;

namespace Snipvane.Services.Highlights;

public interface IHighlightAnalyzer
{
    Task<IReadOnlyList<HighlightSegment>> DetectHighlightsAsync(
        TranscriptDocument transcript,
        double videoDurationSeconds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Calls Gemini (default) or Claude, forces JSON-only output, then validates
/// and normalizes segments (duration clamp, ranking, bounds).
/// </summary>
public class HighlightDetectionService : IHighlightAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsSnapshot<AiOptions> _ai;
    private readonly IOptionsSnapshot<PipelineOptions> _pipeline;
    private readonly ILogger<HighlightDetectionService> _logger;

    public HighlightDetectionService(
        IHttpClientFactory httpClientFactory,
        IOptionsSnapshot<AiOptions> aiOptions,
        IOptionsSnapshot<PipelineOptions> pipelineOptions,
        ILogger<HighlightDetectionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _ai = aiOptions;
        _pipeline = pipelineOptions;
        _logger = logger;
    }

    private AiOptions Ai => _ai.Value;
    private PipelineOptions Pipeline => _pipeline.Value;

    public async Task<IReadOnlyList<HighlightSegment>> DetectHighlightsAsync(
        TranscriptDocument transcript,
        double videoDurationSeconds,
        CancellationToken cancellationToken = default)
    {
        var prompt = HighlightPrompt.BuildUserPrompt(transcript, videoDurationSeconds, Pipeline);
        var provider = (Ai.Provider ?? "Gemini").Trim();

        _logger.LogInformation(
            "Highlight detection via {Provider} for transcript with {WordCount} words, duration {Duration}s",
            provider, transcript.Words.Count, videoDurationSeconds);

        string raw;
        try
        {
            raw = provider.Equals("Claude", StringComparison.OrdinalIgnoreCase)
                ? await CallClaudeAsync(prompt, cancellationToken)
                : await CallGeminiAsync(prompt, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Highlight API call failed ({Provider})", provider);
            throw;
        }

        var parsed = ParseSegments(raw);
        var normalized = Normalize(parsed, videoDurationSeconds);

        if (normalized.Count == 0)
        {
            _logger.LogWarning(
                "AI returned no usable highlights; falling back to transcript windows. Raw: {Raw}",
                Trim(raw));
            normalized = FallbackSegments(transcript, videoDurationSeconds);
        }

        _logger.LogInformation(
            "Highlight detection produced {Count} valid segments (raw parsed: {RawCount})",
            normalized.Count, parsed.Count);

        if (normalized.Count == 0)
        {
            throw new InvalidOperationException(
                "AI highlight detection returned no valid segments. Raw output: " + Trim(raw));
        }

        return normalized;
    }

    private async Task<string> CallGeminiAsync(string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Ai.GeminiApiKey))
        {
            throw new InvalidOperationException(
                "Gemini API key is missing. Add Ai:GeminiApiKey to Snipvane/appsettings.Local.json (gitignored), or set GEMINI_API_KEY.");
        }

        var model = string.IsNullOrWhiteSpace(Ai.GeminiModel) ? "gemini-3.5-flash-lite" : Ai.GeminiModel;
        var payload = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = HighlightPrompt.SystemInstruction } }
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = prompt } }
                }
            },
            generationConfig = new
            {
                temperature = 0.3,
                responseMimeType = "application/json"
            }
        };

        var client = _httpClientFactory.CreateClient("gemini");
        var (body, usedModel) = await GeminiGenerate.PostJsonAsync(
            client,
            Ai.GeminiApiKey,
            model,
            payload,
            _logger,
            cancellationToken);
        _logger.LogInformation("Gemini highlights used model {Model}", usedModel);

        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Gemini returned an empty text part.");
        }

        return text;
    }

    private async Task<string> CallClaudeAsync(string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Ai.ClaudeApiKey))
        {
            throw new InvalidOperationException(
                "Claude API key is missing. Add Ai:ClaudeApiKey to Snipvane/appsettings.Local.json, or set ANTHROPIC_API_KEY.");
        }

        var payload = new
        {
            model = Ai.ClaudeModel,
            max_tokens = 8192,
            temperature = 0.3,
            system = HighlightPrompt.SystemInstruction,
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        var client = _httpClientFactory.CreateClient("claude");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", Ai.ClaudeApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Claude API failed ({Status}): {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Claude API failed ({(int)response.StatusCode}): {Trim(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var texts = new StringBuilder();
        foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) && type.GetString() == "text")
            {
                texts.Append(block.GetProperty("text").GetString());
            }
        }

        var text = texts.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Claude returned no text blocks.");
        }

        return text;
    }

    internal List<HighlightSegment> ParseSegments(string raw)
    {
        var candidate = ExtractJsonPayload(raw);

        try
        {
            var wrapped = JsonSerializer.Deserialize<HighlightAnalysisResult>(candidate, JsonOptions);
            if (wrapped?.Segments is { Count: > 0 })
            {
                return wrapped.Segments;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse highlight JSON as object wrapper. Trying array.");
        }

        try
        {
            var array = JsonSerializer.Deserialize<List<HighlightSegment>>(candidate, JsonOptions);
            if (array is { Count: > 0 })
            {
                return array;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse highlight JSON as array.");
        }

        // Last resort: walk a JsonNode for a "segments" array anywhere in the payload.
        try
        {
            var node = JsonNode.Parse(candidate);
            var segmentsNode = node?["segments"] ?? (node as JsonArray);
            if (segmentsNode is JsonArray arr)
            {
                var list = arr.Deserialize<List<HighlightSegment>>(JsonOptions);
                if (list is { Count: > 0 })
                {
                    return list;
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Could not parse AI highlight output. Raw: {Raw}", Trim(raw));
        }

        return [];
    }

    internal static string ExtractJsonPayload(string raw)
    {
        var trimmed = raw.Trim();

        // Models sometimes ignore "JSON only" and wrap in ```json fences.
        trimmed = Regex.Replace(trimmed, @"^```(?:json)?\s*", string.Empty, RegexOptions.IgnoreCase);
        trimmed = Regex.Replace(trimmed, @"\s*```$", string.Empty);

        var objectStart = trimmed.IndexOf('{');
        var arrayStart = trimmed.IndexOf('[');

        if (objectStart >= 0 && (arrayStart < 0 || objectStart < arrayStart))
        {
            var objectEnd = trimmed.LastIndexOf('}');
            if (objectEnd > objectStart)
            {
                return trimmed[objectStart..(objectEnd + 1)];
            }
        }

        if (arrayStart >= 0)
        {
            var arrayEnd = trimmed.LastIndexOf(']');
            if (arrayEnd > arrayStart)
            {
                return trimmed[arrayStart..(arrayEnd + 1)];
            }
        }

        return trimmed;
    }

    private List<HighlightSegment> Normalize(IEnumerable<HighlightSegment> segments, double videoDurationSeconds)
    {
        var min = Pipeline.MinSegmentSeconds;
        var max = Pipeline.MaxSegmentSeconds;
        var duration = Math.Max(videoDurationSeconds, 0);
        var result = new List<HighlightSegment>();

        foreach (var segment in segments)
        {
            var start = Math.Max(0, segment.StartTime);
            var end = segment.EndTime > start ? segment.EndTime : start;
            if (duration > 0)
            {
                start = Math.Min(start, Math.Max(0, duration - min));
                end = Math.Min(end, duration);
            }

            var length = end - start;
            if (length < min)
            {
                end = start + min;
                if (duration > 0 && end > duration)
                {
                    end = duration;
                    start = Math.Max(0, end - min);
                }
            }
            else if (length > max)
            {
                end = start + max;
            }

            length = end - start;
            var minAcceptable = duration > 0 && duration < min ? Math.Max(8, duration * 0.5) : Math.Min(15, min);
            if (length < minAcceptable)
            {
                _logger.LogWarning(
                    "Dropping highlight segment '{Title}' ({Start}-{End}) after normalization",
                    segment.Title, segment.StartTime, segment.EndTime);
                continue;
            }

            var score = Math.Clamp(segment.ViralityScore <= 0 ? 5 : segment.ViralityScore, 1, 10);
            var title = string.IsNullOrWhiteSpace(segment.Title) ? "Highlight" : segment.Title.Trim();
            result.Add(new HighlightSegment
            {
                StartTime = Math.Round(start, 3),
                EndTime = Math.Round(end, 3),
                Title = title,
                ViralityScore = score,
                Reason = string.IsNullOrWhiteSpace(segment.Reason) ? "AI-selected highlight" : segment.Reason.Trim()
            });
        }

        return result
            .OrderByDescending(s => s.ViralityScore)
            .ThenBy(s => s.StartTime)
            .ToList();
    }

    private List<HighlightSegment> FallbackSegments(TranscriptDocument transcript, double videoDurationSeconds)
    {
        var duration = videoDurationSeconds;
        if (duration <= 0)
        {
            duration = transcript.Words.Count > 0 ? transcript.Words.Max(w => w.End) : 0;
        }

        if (duration < 8)
        {
            return [];
        }

        var maxClips = Math.Max(1, Pipeline.MaxClipsToGenerate);
        var min = Pipeline.MinSegmentSeconds;
        var max = Pipeline.MaxSegmentSeconds;
        double window;
        if (duration <= max)
        {
            window = duration;
            maxClips = duration < min + 8 ? 1 : Math.Min(maxClips, (int)Math.Floor(duration / Math.Max(12, min * 0.6)));
            maxClips = Math.Max(1, maxClips);
        }
        else
        {
            window = Math.Clamp(duration / maxClips, min, max);
        }

        var words = transcript.Words.OrderBy(w => w.Start).ToList();
        var segments = new List<HighlightSegment>();
        for (var i = 0; i < maxClips; i++)
        {
            var start = i * window;
            if (start >= duration - 4)
            {
                break;
            }

            var end = Math.Min(duration, start + window);
            if (end - start < 8)
            {
                break;
            }

            segments.Add(new HighlightSegment
            {
                StartTime = Math.Round(start, 3),
                EndTime = Math.Round(end, 3),
                Title = TitleFromWords(words, start, end, i + 1),
                ViralityScore = Math.Max(1, 8 - i),
                Reason = "Auto-selected window because the model returned no highlight segments."
            });
        }

        return segments;
    }

    private static string TitleFromWords(IReadOnlyList<TranscriptWord> words, double start, double end, int index)
    {
        var snippet = string.Join(' ', words
            .Where(w => w.End > start && w.Start < end && !string.IsNullOrWhiteSpace(w.Word))
            .Take(8)
            .Select(w => w.Word.Trim()));
        if (string.IsNullOrWhiteSpace(snippet))
        {
            return "Clip " + index;
        }

        return snippet.Length <= 48 ? snippet : snippet[..45].Trim() + "…";
    }

    private static string Trim(string text) =>
        text.Length <= 2000 ? text : text[..2000];
}
