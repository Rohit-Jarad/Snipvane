using System.Text.Json.Serialization;

namespace Snipvane.DTOs;

public class VideoListItemDto
{
    public Guid Id { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public double? DurationSeconds { get; set; }
    public int ClipCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

public class VideoDetailDto
{
    public Guid Id { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public double? DurationSeconds { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public TranscriptDto? Transcript { get; set; }
    public List<ClipDto> Clips { get; set; } = [];
}

public class TranscriptDto
{
    public string Text { get; set; } = string.Empty;
    public string? Language { get; set; }
    public List<TranscriptWordDto> Words { get; set; } = [];
}

public class TranscriptWordDto
{
    public string Word { get; set; } = string.Empty;
    public double Start { get; set; }
    public double End { get; set; }
}

public class ClipDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public double ViralityScore { get; set; }
    public string Reason { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsKept { get; set; }
    public bool HasFile { get; set; }
}

public class UpdateClipRequest
{
    public bool? IsKept { get; set; }
    public int? SortOrder { get; set; }
}

public class ReorderClipsRequest
{
    public List<Guid> ClipIds { get; set; } = [];
}

public class UploadResponseDto
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
}

public class TranscriptDocument
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    [JsonPropertyName("words")]
    public List<TranscriptWord> Words { get; set; } = [];
}

public class TranscriptWord
{
    [JsonPropertyName("word")]
    public string Word { get; set; } = string.Empty;

    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }
}
