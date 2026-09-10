namespace Snipvane.Models;

public class Clip
{
    public Guid Id { get; set; }

    public Guid VideoId { get; set; }

    public Video Video { get; set; } = null!;

    public string Title { get; set; } = string.Empty;

    public double StartTime { get; set; }

    public double EndTime { get; set; }

    public double ViralityScore { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string? FilePath { get; set; }

    /// <summary>Lower numbers appear first in the review UI.</summary>
    public int SortOrder { get; set; }

    /// <summary>Manual-review keep flag. Nothing is auto-published.</summary>
    public bool IsKept { get; set; } = true;

    public DateTime CreatedAt { get; set; }
}
