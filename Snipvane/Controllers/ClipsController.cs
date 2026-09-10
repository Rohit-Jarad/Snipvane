using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Snipvane.Data;
using Snipvane.DTOs;

namespace Snipvane.Controllers;

[ApiController]
[Route("api/clips")]
public class ClipsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<ClipsController> _logger;

    public ClipsController(AppDbContext db, ILogger<ClipsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<ClipDto>> Update(Guid id, [FromBody] UpdateClipRequest request, CancellationToken cancellationToken)
    {
        var clip = await _db.Clips.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (clip is null)
        {
            return NotFound();
        }

        if (request.IsKept is not null)
        {
            clip.IsKept = request.IsKept.Value;
        }

        if (request.SortOrder is not null)
        {
            clip.SortOrder = request.SortOrder.Value;
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Updated clip {ClipId}: kept={Kept} order={Order}", clip.Id, clip.IsKept, clip.SortOrder);

        return new ClipDto
        {
            Id = clip.Id,
            Title = clip.Title,
            StartTime = clip.StartTime,
            EndTime = clip.EndTime,
            ViralityScore = clip.ViralityScore,
            Reason = clip.Reason,
            SortOrder = clip.SortOrder,
            IsKept = clip.IsKept,
            HasFile = !string.IsNullOrWhiteSpace(clip.FilePath) && System.IO.File.Exists(clip.FilePath)
        };
    }

    [HttpPost("reorder")]
    public async Task<IActionResult> Reorder([FromBody] ReorderClipsRequest request, CancellationToken cancellationToken)
    {
        if (request.ClipIds.Count == 0)
        {
            return BadRequest("ClipIds is required.");
        }

        var clips = await _db.Clips.Where(c => request.ClipIds.Contains(c.Id)).ToListAsync(cancellationToken);
        if (clips.Count != request.ClipIds.Count)
        {
            return BadRequest("One or more clip ids were not found.");
        }

        for (var i = 0; i < request.ClipIds.Count; i++)
        {
            var clip = clips.First(c => c.Id == request.ClipIds[i]);
            clip.SortOrder = i;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("{id:guid}/file")]
    [HttpHead("{id:guid}/file")]
    public async Task<IActionResult> Preview(Guid id, CancellationToken cancellationToken)
    {
        var clip = await _db.Clips.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (clip is null || string.IsNullOrWhiteSpace(clip.FilePath) || !System.IO.File.Exists(clip.FilePath))
        {
            return NotFound();
        }

        return PhysicalFile(clip.FilePath, "video/mp4", enableRangeProcessing: true);
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var clip = await _db.Clips.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (clip is null || string.IsNullOrWhiteSpace(clip.FilePath) || !System.IO.File.Exists(clip.FilePath))
        {
            return NotFound();
        }

        var downloadName = SanitizeFileName(clip.Title) + ".mp4";
        return PhysicalFile(clip.FilePath, "video/mp4", downloadName);
    }

    private static string SanitizeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "clip" : cleaned;
    }
}
