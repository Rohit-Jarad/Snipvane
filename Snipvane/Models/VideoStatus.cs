namespace Snipvane.Models;

public enum VideoStatus
{
    Uploaded = 0,
    ExtractingAudio = 1,
    Transcribing = 2,
    Analyzing = 3,
    GeneratingClips = 4,
    Completed = 5,
    Failed = 6
}
