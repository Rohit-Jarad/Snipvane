using Microsoft.Extensions.Options;
using Snipvane.Data;
using Snipvane.DTOs;
using Snipvane.Models;
using Snipvane.Options;
using Snipvane.Services.FFmpeg;
using Snipvane.Services.Highlights;
using Snipvane.Services.Storage;

namespace Snipvane.Services.Clips;

public interface IClipGenerationService
{
    Task<IReadOnlyList<Clip>> GenerateAsync(
        Video video,
        TranscriptDocument transcript,
        IReadOnlyList<HighlightSegment> segments,
        CancellationToken cancellationToken = default);
}

public class ClipGenerationService : IClipGenerationService
{
    private readonly AppDbContext _db;
    private readonly IFFmpegService _ffmpeg;
    private readonly IMediaStorage _storage;
    private readonly PipelineOptions _pipeline;
    private readonly ILogger<ClipGenerationService> _logger;

    public ClipGenerationService(
        AppDbContext db,
        IFFmpegService ffmpeg,
        IMediaStorage storage,
        IOptionsSnapshot<PipelineOptions> pipeline,
        ILogger<ClipGenerationService> logger)
    {
        _db = db;
        _ffmpeg = ffmpeg;
        _storage = storage;
        _pipeline = pipeline.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Clip>> GenerateAsync(
        Video video,
        TranscriptDocument transcript,
        IReadOnlyList<HighlightSegment> segments,
        CancellationToken cancellationToken = default)
    {
        var selected = segments
            .OrderByDescending(s => s.ViralityScore)
            .Take(_pipeline.MaxClipsToGenerate)
            .ToList();

        _logger.LogInformation(
            "Generating {Count} clips for video {VideoId} (from {Total} candidates, {WordCount} transcript words)",
            selected.Count, video.Id, segments.Count, transcript.Words.Count);

        var created = new List<Clip>();
        var sort = 0;

        foreach (var segment in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clipId = Guid.NewGuid();
            var clipPath = _storage.GetClipPath(video.Id, clipId);
            var duration = segment.EndTime - segment.StartTime;

            try
            {
                await _ffmpeg.CutVerticalClipAsync(
                    video.StoredFilePath,
                    clipPath,
                    segment.StartTime,
                    duration,
                    assSubtitlePath: null,
                    cancellationToken);

                var clip = new Clip
                {
                    Id = clipId,
                    VideoId = video.Id,
                    Title = segment.Title,
                    StartTime = segment.StartTime,
                    EndTime = segment.EndTime,
                    ViralityScore = segment.ViralityScore,
                    Reason = segment.Reason,
                    FilePath = clipPath,
                    SortOrder = sort++,
                    IsKept = true,
                    CreatedAt = DateTime.UtcNow
                };

                _db.Clips.Add(clip);
                await _db.SaveChangesAsync(cancellationToken);
                created.Add(clip);
                _logger.LogInformation(
                    "Clip {ClipId} generated for video {VideoId}: '{Title}' score={Score} {Start}-{End}",
                    clipId, video.Id, clip.Title, clip.ViralityScore, clip.StartTime, clip.EndTime);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed generating clip '{Title}' ({Start}-{End}) for video {VideoId}",
                    segment.Title, segment.StartTime, segment.EndTime, video.Id);
                throw;
            }
        }

        return created;
    }
}
