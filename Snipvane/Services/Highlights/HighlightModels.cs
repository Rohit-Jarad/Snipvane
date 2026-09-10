using System.Text.Json.Serialization;

namespace Snipvane.Services.Highlights;

public class HighlightAnalysisResult
{
    [JsonPropertyName("segments")]
    public List<HighlightSegment> Segments { get; set; } = [];
}

public class HighlightSegment
{
    [JsonPropertyName("start_time")]
    public double StartTime { get; set; }

    [JsonPropertyName("end_time")]
    public double EndTime { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("virality_score")]
    public double ViralityScore { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}
