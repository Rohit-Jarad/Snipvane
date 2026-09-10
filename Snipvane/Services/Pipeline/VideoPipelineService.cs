using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Snipvane.Data;
using Snipvane.DTOs;
using Snipvane.Models;
using Snipvane.Services.Clips;
using Snipvane.Services.FFmpeg;
using Snipvane.Services.Highlights;
using Snipvane.Services.Storage;
using Snipvane.Services.Transcription;

namespace Snipvane.Services.Pipeline;

public interface IVideoPipeline
{
    Task ProcessAsync(Guid videoId, CancellationToken cancellationToken = default);
}

public class VideoPipelineService : IVideoPipeline
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly AppDbContext _db;
    private readonly IFFmpegService _ffmpeg;
    private readonly ITranscriptionService _transcription;
    private readonly IHighlightAnalyzer _highlights;
    private readonly IClipGenerationService _clips;
    private readonly IMediaStorage _storage;
    private readonly ILogger<VideoPipelineService> _logger;

    public VideoPipelineService(
        AppDbContext db,
        IFFmpegService ffmpeg,
        ITranscriptionService transcription,
        IHighlightAnalyzer highlights,
        IClipGenerationService clips,
        IMediaStorage storage,
        ILogger<VideoPipelineService> logger)
    {
        _db = db;
        _ffmpeg = ffmpeg;
        _transcription = transcription;
        _highlights = highlights;
        _clips = clips;
        _storage = storage;
        _logger = logger;
    }

    public async Task ProcessAsync(Guid videoId, CancellationToken cancellationToken = default)
    {
        var video = await _db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken)
            ?? throw new InvalidOperationException($"Video {videoId} was not found.");

        _logger.LogInformation("Pipeline started for video {VideoId} ({File})", video.Id, video.OriginalFileName);

        try
        {
            await ExtractAudioAsync(video, cancellationToken);
            var transcript = await TranscribeAsync(video, cancellationToken);
            var segments = await AnalyzeAsync(video, transcript, cancellationToken);
            await GenerateClipsAsync(video, transcript, segments, cancellationToken);

            video.Status = VideoStatus.Completed;
            video.ErrorMessage = null;
            video.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Pipeline completed for video {VideoId}", video.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline failed for video {VideoId} at status {Status}", video.Id, video.Status);
            video.Status = VideoStatus.Failed;
            video.ErrorMessage = ex.Message;
            video.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private async Task ExtractAudioAsync(Video video, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Pipeline stage {Stage} started for video {VideoId}", "ExtractingAudio", video.Id);
        video.Status = VideoStatus.ExtractingAudio;
        video.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        if (video.DurationSeconds is null or <= 0)
        {
            video.DurationSeconds = await _ffmpeg.GetDurationSecondsAsync(video.StoredFilePath, cancellationToken);
        }

        var audioPath = _storage.GetAudioPath(video.Id);
        await _ffmpeg.ExtractAudioAsync(video.StoredFilePath, audioPath, cancellationToken);
        video.AudioFilePath = audioPath;
        video.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Pipeline stage {Stage} finished for video {VideoId}. Duration={Duration}s Audio={Audio}",
            "ExtractingAudio", video.Id, video.DurationSeconds, audioPath);
    }

    private async Task<TranscriptDocument> TranscribeAsync(Video video, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Pipeline stage {Stage} started for video {VideoId}", "Transcribing", video.Id);
        video.Status = VideoStatus.Transcribing;
        video.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(video.AudioFilePath) || !File.Exists(video.AudioFilePath))
        {
            throw new InvalidOperationException("Audio file is missing; cannot transcribe.");
        }

        var transcript = await _transcription.TranscribeAsync(video.AudioFilePath, cancellationToken);
        var json = JsonSerializer.Serialize(transcript, JsonOptions);
        video.TranscriptJson = json;
        await File.WriteAllTextAsync(_storage.GetTranscriptPath(video.Id), json, cancellationToken);
        video.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Pipeline stage {Stage} finished for video {VideoId}. Words={WordCount}",
            "Transcribing", video.Id, transcript.Words.Count);

        return transcript;
    }

    private async Task<IReadOnlyList<HighlightSegment>> AnalyzeAsync(
        Video video,
        TranscriptDocument transcript,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Pipeline stage {Stage} started for video {VideoId}", "Analyzing", video.Id);
        video.Status = VideoStatus.Analyzing;
        video.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var duration = video.DurationSeconds ?? transcript.Duration ?? 0;
        var segments = await _highlights.DetectHighlightsAsync(transcript, duration, cancellationToken);

        _logger.LogInformation(
            "Pipeline stage {Stage} finished for video {VideoId}. Segments={Count}",
            "Analyzing", video.Id, segments.Count);

        return segments;
    }

    private async Task GenerateClipsAsync(
        Video video,
        TranscriptDocument transcript,
        IReadOnlyList<HighlightSegment> segments,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Pipeline stage {Stage} started for video {VideoId}", "GeneratingClips", video.Id);
        video.Status = VideoStatus.GeneratingClips;
        video.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var existing = await _db.Clips.Where(c => c.VideoId == video.Id).ToListAsync(cancellationToken);
        foreach (var clip in existing)
        {
            TryDelete(clip.FilePath);
        }
        _db.Clips.RemoveRange(existing);
        await _db.SaveChangesAsync(cancellationToken);

        await _clips.GenerateAsync(video, transcript, segments, cancellationToken);

        _logger.LogInformation("Pipeline stage {Stage} finished for video {VideoId}", "GeneratingClips", video.Id);
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Leave orphaned media; the next generate uses a new clip id.
        }
    }
}
