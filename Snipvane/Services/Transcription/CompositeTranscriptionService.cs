using Microsoft.Extensions.Options;
using Snipvane.DTOs;
using Snipvane.Options;

namespace Snipvane.Services.Transcription;

/// <summary>
/// Default path is Gemini (free tier). Whisper remains available if
/// Transcription:Provider is OpenAI and the paid key has credits.
/// </summary>
public class CompositeTranscriptionService : ITranscriptionService
{
    private readonly GeminiTranscriptionService _gemini;
    private readonly WhisperTranscriptionService _whisper;
    private readonly IOptionsSnapshot<AiOptions> _ai;
    private readonly ILogger<CompositeTranscriptionService> _logger;

    public CompositeTranscriptionService(
        GeminiTranscriptionService gemini,
        WhisperTranscriptionService whisper,
        IOptionsSnapshot<AiOptions> ai,
        ILogger<CompositeTranscriptionService> logger)
    {
        _gemini = gemini;
        _whisper = whisper;
        _ai = ai;
        _logger = logger;
    }

    public async Task<TranscriptDocument> TranscribeAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        var provider = (_ai.Value.TranscriptionProvider ?? "Gemini").Trim();
        if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return await _whisper.TranscribeAsync(audioPath, cancellationToken);
            }
            catch (Exception ex) when (LooksLikeQuota(ex))
            {
                _logger.LogWarning(ex, "OpenAI Whisper has no credits; falling back to free Gemini transcription");
                return await _gemini.TranscribeAsync(audioPath, cancellationToken);
            }
        }

        _logger.LogInformation("Using free Gemini transcription");
        return await _gemini.TranscribeAsync(audioPath, cancellationToken);
    }

    private static bool LooksLikeQuota(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no credits", StringComparison.OrdinalIgnoreCase)
            || message.Contains("429");
    }
}
