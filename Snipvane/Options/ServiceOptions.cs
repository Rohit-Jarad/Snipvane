namespace Snipvane.Options;

public class StorageOptions
{
    public const string SectionName = "Storage";
    public string RootPath { get; set; } = "Storage";
}

public class FFmpegOptions
{
    public const string SectionName = "FFmpeg";
    public string ExecutablePath { get; set; } = string.Empty;
    public string FfprobePath { get; set; } = string.Empty;
}

public class OpenAIOptions
{
    public const string SectionName = "OpenAI";
    public string ApiKey { get; set; } = string.Empty;
    public string WhisperModel { get; set; } = "whisper-1";
}

public class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>Gemini or Claude.</summary>
    public string Provider { get; set; } = "Gemini";
    public string GeminiApiKey { get; set; } = string.Empty;
    public string GeminiModel { get; set; } = "gemini-3.6-flash";
    public string ClaudeApiKey { get; set; } = string.Empty;
    public string ClaudeModel { get; set; } = "claude-sonnet-4-20250514";

    /// <summary>Gemini (free) or OpenAI (paid Whisper).</summary>
    public string TranscriptionProvider { get; set; } = "Gemini";
}

public class PipelineOptions
{
    public const string SectionName = "Pipeline";
    public int MaxClipsToGenerate { get; set; } = 5;
    public double MinSegmentSeconds { get; set; } = 30;
    public double MaxSegmentSeconds { get; set; } = 60;
    public bool AutoProcessOnUpload { get; set; } = true;
}
