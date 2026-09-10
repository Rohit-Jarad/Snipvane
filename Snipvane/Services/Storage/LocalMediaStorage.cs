using Microsoft.Extensions.Options;
using Snipvane.Options;

namespace Snipvane.Services.Storage;

public interface IMediaStorage
{
    string RootPath { get; }
    string GetVideoDirectory(Guid videoId);
    string GetOriginalVideoPath(Guid videoId, string originalFileName);
    string GetAudioPath(Guid videoId);
    string GetTranscriptPath(Guid videoId);
    string GetClipPath(Guid videoId, Guid clipId);
    string GetClipAssPath(Guid videoId, Guid clipId);
}

public class LocalMediaStorage : IMediaStorage
{
    public LocalMediaStorage(IOptions<StorageOptions> options, IWebHostEnvironment env)
    {
        var configured = options.Value.RootPath;
        RootPath = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(env.ContentRootPath, configured);

        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public string GetVideoDirectory(Guid videoId)
    {
        var dir = Path.Combine(RootPath, videoId.ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "clips"));
        return dir;
    }

    public string GetOriginalVideoPath(Guid videoId, string originalFileName)
    {
        var ext = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(ext) || !ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            ext = ".mp4";
        }

        return Path.Combine(GetVideoDirectory(videoId), "original" + ext.ToLowerInvariant());
    }

    public string GetAudioPath(Guid videoId) =>
        Path.Combine(GetVideoDirectory(videoId), "audio.mp3");

    public string GetTranscriptPath(Guid videoId) =>
        Path.Combine(GetVideoDirectory(videoId), "transcript.json");

    public string GetClipPath(Guid videoId, Guid clipId) =>
        Path.Combine(GetVideoDirectory(videoId), "clips", $"{clipId:N}.mp4");

    public string GetClipAssPath(Guid videoId, Guid clipId) =>
        Path.Combine(GetVideoDirectory(videoId), "clips", $"{clipId:N}.ass");
}
