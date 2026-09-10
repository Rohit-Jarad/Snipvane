using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Snipvane.DTOs;
using Snipvane.Options;

namespace Snipvane.Services.Transcription;

public interface ITranscriptionService
{
    Task<TranscriptDocument> TranscribeAsync(string audioPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// OpenAI Whisper transcription with word-level timestamps.
/// Word timings are required later to burn karaoke subtitles in sync.
/// </summary>
public class WhisperTranscriptionService : ITranscriptionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsSnapshot<OpenAIOptions> _options;
    private readonly ILogger<WhisperTranscriptionService> _logger;

    public WhisperTranscriptionService(
        IHttpClientFactory httpClientFactory,
        IOptionsSnapshot<OpenAIOptions> options,
        ILogger<WhisperTranscriptionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<TranscriptDocument> TranscribeAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        var apiKey = _options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "OpenAI API key is missing. Add OpenAI:ApiKey to Snipvane/appsettings.Local.json (gitignored), set OPENAI_API_KEY, or use dotnet user-secrets, then click Run again.");
        }

        var fileInfo = new FileInfo(audioPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Audio file missing for transcription.", audioPath);
        }

        if (fileInfo.Length > 24 * 1024 * 1024)
        {
            throw new InvalidOperationException(
                $"Audio file is {fileInfo.Length / (1024 * 1024)} MB; Whisper's multipart limit is 25 MB. Re-extract at a lower bitrate.");
        }

        _logger.LogInformation(
            "Sending {Audio} ({SizeMb:F1} MB) to Whisper model {Model} with word-level timestamps",
            audioPath, fileInfo.Length / (1024.0 * 1024.0), _options.Value.WhisperModel);

        using var content = new MultipartFormDataContent();
        await using var stream = File.OpenRead(audioPath);
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        content.Add(fileContent, "file", Path.GetFileName(audioPath));
        content.Add(new StringContent(_options.Value.WhisperModel), "model");
        content.Add(new StringContent("verbose_json"), "response_format");
        content.Add(new StringContent("word"), "timestamp_granularities[]");

        var client = _httpClientFactory.CreateClient("openai");
        using var request = new HttpRequestMessage(HttpMethod.Post, "audio/transcriptions")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Whisper API failed ({Status}): {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Whisper API failed ({(int)response.StatusCode}): {Trim(body)}");
        }

        var parsed = JsonSerializer.Deserialize<WhisperVerboseResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("Whisper returned an empty payload.");

        var words = (parsed.Words ?? [])
            .Select(w => new TranscriptWord
            {
                Word = w.Word ?? string.Empty,
                Start = w.Start,
                End = w.End
            })
            .Where(w => !string.IsNullOrWhiteSpace(w.Word))
            .ToList();

        // Some Whisper responses omit `words` if the granularity was ignored; fall back to segment tokens.
        if (words.Count == 0 && parsed.Segments is { Count: > 0 })
        {
            _logger.LogWarning("Whisper response had no word timestamps; falling back to segment-level timings.");
            words = parsed.Segments
                .Select(s => new TranscriptWord
                {
                    Word = (s.Text ?? string.Empty).Trim(),
                    Start = s.Start,
                    End = s.End
                })
                .Where(w => !string.IsNullOrWhiteSpace(w.Word))
                .ToList();
        }

        _logger.LogInformation(
            "Whisper transcript ready: {WordCount} words, language={Language}, duration={Duration}",
            words.Count, parsed.Language, parsed.Duration);

        return new TranscriptDocument
        {
            Text = parsed.Text ?? string.Empty,
            Language = parsed.Language,
            Duration = parsed.Duration,
            Words = words
        };
    }

    private static string Trim(string body) =>
        body.Length <= 1500 ? body : body[..1500];

    private sealed class WhisperVerboseResponse
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("duration")]
        public double? Duration { get; set; }

        [JsonPropertyName("words")]
        public List<WhisperWord>? Words { get; set; }

        [JsonPropertyName("segments")]
        public List<WhisperSegment>? Segments { get; set; }
    }

    private sealed class WhisperWord
    {
        [JsonPropertyName("word")]
        public string? Word { get; set; }

        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }
    }

    private sealed class WhisperSegment
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }
    }
}
