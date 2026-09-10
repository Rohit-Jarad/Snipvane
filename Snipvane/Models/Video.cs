namespace Snipvane.Models;

public class Video
{
    public Guid Id { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    public string StoredFilePath { get; set; } = string.Empty;

    public string? AudioFilePath { get; set; }

    /// <summary>
    /// Full Whisper transcript JSON, including word-level timestamps.
    /// Stored as text so it stays portable across SQL Server / Postgres / Azure SQL.
    /// </summary>
    public string? TranscriptJson { get; set; }

    public VideoStatus Status { get; set; } = VideoStatus.Uploaded;

    public string? ErrorMessage { get; set; }

    public double? DurationSeconds { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<Clip> Clips { get; set; } = new List<Clip>();
}
