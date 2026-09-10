using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Snipvane.Data;
using Snipvane.DTOs;
using Snipvane.Models;
using Snipvane.Options;
using Snipvane.Services.Pipeline;
using Snipvane.Services.Storage;

namespace Snipvane.Controllers;

[ApiController]
[Route("api/videos")]
public class VideosController : ControllerBase
{
    private const long MaxUploadBytes = 2L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AppDbContext _db;
    private readonly IMediaStorage _storage;
    private readonly IPipelineQueue _queue;
    private readonly PipelineOptions _pipeline;
    private readonly ILogger<VideosController> _logger;

    public VideosController(
        AppDbContext db,
        IMediaStorage storage,
        IPipelineQueue queue,
        IOptionsSnapshot<PipelineOptions> pipeline,
        ILogger<VideosController> logger)
    {
        _db = db;
        _storage = storage;
        _queue = queue;
        _pipeline = pipeline.Value;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<List<VideoListItemDto>>> List(CancellationToken cancellationToken)
    {
        var items = await _db.Videos
            .AsNoTracking()
            .OrderByDescending(v => v.CreatedAt)
            .Select(v => new VideoListItemDto
            {
                Id = v.Id,
                OriginalFileName = v.OriginalFileName,
                Status = v.Status.ToString(),
                DurationSeconds = v.DurationSeconds,
                ClipCount = v.Clips.Count,
                CreatedAt = v.CreatedAt,
                ErrorMessage = v.ErrorMessage
            })
            .ToListAsync(cancellationToken);

        return items;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<VideoDetailDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var video = await _db.Videos
            .AsNoTracking()
            .Include(v => v.Clips)
            .FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

        if (video is null)
        {
            return NotFound();
        }

        return MapDetail(video);
    }

    [HttpPost("upload")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    public async Task<ActionResult<UploadResponseDto>> Upload(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest("A video file is required.");
        }

        if (file.Length > MaxUploadBytes)
        {
            return BadRequest("File exceeds the 2 GB upload limit.");
        }

        var ext = Path.GetExtension(file.FileName);
        if (!ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Only .mp4 uploads are supported right now.");
        }

        var videoId = Guid.NewGuid();
        var storedPath = _storage.GetOriginalVideoPath(videoId, file.FileName);

        _logger.LogInformation(
            "Pipeline stage {Stage} started. Saving {FileName} ({SizeMb:F1} MB) as video {VideoId}",
            "Upload", file.FileName, file.Length / (1024.0 * 1024.0), videoId);

        await using (var stream = System.IO.File.Create(storedPath))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        var video = new Video
        {
            Id = videoId,
            OriginalFileName = Path.GetFileName(file.FileName),
            StoredFilePath = storedPath,
            Status = VideoStatus.Uploaded,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Videos.Add(video);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Pipeline stage {Stage} finished for video {VideoId}. Path={Path}",
            "Upload", videoId, storedPath);

        if (_pipeline.AutoProcessOnUpload)
        {
            await _queue.EnqueueAsync(videoId, cancellationToken);
            _logger.LogInformation("Queued video {VideoId} for automatic processing", videoId);
        }

        return CreatedAtAction(nameof(Get), new { id = videoId }, new UploadResponseDto
        {
            Id = video.Id,
            Status = video.Status.ToString(),
            OriginalFileName = video.OriginalFileName
        });
    }

    [HttpPost("{id:guid}/process")]
    public async Task<IActionResult> Process(Guid id, CancellationToken cancellationToken)
    {
        var video = await _db.Videos.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);
        if (video is null)
        {
            return NotFound();
        }

        if (video.Status is VideoStatus.ExtractingAudio or VideoStatus.Transcribing
            or VideoStatus.Analyzing or VideoStatus.GeneratingClips)
        {
            return Conflict(new { message = "This video is already being processed." });
        }

        video.Status = VideoStatus.Uploaded;
        video.ErrorMessage = null;
        video.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        await _queue.EnqueueAsync(id, cancellationToken);
        _logger.LogInformation("Manually queued video {VideoId} for processing", id);
        return Accepted(new { id, status = video.Status.ToString() });
    }

    [HttpGet("{id:guid}/file")]
    [HttpHead("{id:guid}/file")]
    public async Task<IActionResult> FileStream(Guid id, CancellationToken cancellationToken)
    {
        var video = await _db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, cancellationToken);
        if (video is null || !System.IO.File.Exists(video.StoredFilePath))
        {
            return NotFound();
        }

        return PhysicalFile(video.StoredFilePath, "video/mp4", enableRangeProcessing: true);
    }

    private static VideoDetailDto MapDetail(Video video)
    {
        TranscriptDto? transcript = null;
        if (!string.IsNullOrWhiteSpace(video.TranscriptJson))
        {
            var doc = JsonSerializer.Deserialize<TranscriptDocument>(video.TranscriptJson, JsonOptions);
            if (doc is not null)
            {
                transcript = new TranscriptDto
                {
                    Text = doc.Text,
                    Language = doc.Language,
                    Words = doc.Words.Select(w => new TranscriptWordDto
                    {
                        Word = w.Word,
                        Start = w.Start,
                        End = w.End
                    }).ToList()
                };
            }
        }

        return new VideoDetailDto
        {
            Id = video.Id,
            OriginalFileName = video.OriginalFileName,
            Status = video.Status.ToString(),
            DurationSeconds = video.DurationSeconds,
            CreatedAt = video.CreatedAt,
            UpdatedAt = video.UpdatedAt,
            ErrorMessage = video.ErrorMessage,
            Transcript = transcript,
            Clips = video.Clips
                .OrderBy(c => c.SortOrder)
                .ThenByDescending(c => c.ViralityScore)
                .Select(c => new ClipDto
                {
                    Id = c.Id,
                    Title = c.Title,
                    StartTime = c.StartTime,
                    EndTime = c.EndTime,
                    ViralityScore = c.ViralityScore,
                    Reason = c.Reason,
                    SortOrder = c.SortOrder,
                    IsKept = c.IsKept,
                    HasFile = !string.IsNullOrWhiteSpace(c.FilePath) && System.IO.File.Exists(c.FilePath)
                })
                .ToList()
        };
    }
}
