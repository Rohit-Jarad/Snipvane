using Microsoft.Extensions.Options;
using Snipvane.Data;
using Snipvane.DTOs;
using Snipvane.Models;
using Snipvane.Options;
using Snipvane.Services.FFmpeg;
using Snipvane.Services.Highlights;
using Snipvane.Services.Storage;
using Snipvane.Services.Subtitles;

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
    private readonly ISubtitleService _subtitles;
    private readonly IMediaStorage _storage;
    private readonly PipelineOptions _pipeline;
    private readonly ILogger<ClipGenerationService> _logger;

    public ClipGenerationService(
        AppDbContext db,
        IFFmpegService ffmpeg,
        ISubtitleService subtitles,
        IMediaStorage storage,
        IOptionsSnapshot<PipelineOptions> pipeline,
        ILogger<ClipGenerationService> logger)
    {
        _db = db;
        _ffmpeg = ffmpeg;
        _subtitles = subtitles;
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
            "Generating {Count} clips for video {VideoId} (from {Total} candidates)",
            selected.Count, video.Id, segments.Count);

        var created = new List<Clip>();
        var sort = 0;

        foreach (var segment in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clipId = Guid.NewGuid();
            var clipPath = _storage.GetClipPath(video.Id, clipId);
            var assPath = _storage.GetClipAssPath(video.Id, clipId);
            var duration = segment.EndTime - segment.StartTime;

            try
            {
                var ass = _subtitles.BuildKaraokeAss(transcript.Words, segment.StartTime, segment.EndTime);
                await File.WriteAllTextAsync(assPath, ass, cancellationToken);

                await _ffmpeg.CutVerticalClipAsync(
                    video.StoredFilePath,
                    clipPath,
                    segment.StartTime,
                    duration,
                    assPath,
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

        await _db.SaveChangesAsync(cancellationToken);
        return created;
    }
}
