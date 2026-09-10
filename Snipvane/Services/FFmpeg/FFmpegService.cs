using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using Snipvane.Options;

namespace Snipvane.Services.FFmpeg;

public interface IFFmpegService
{
    Task ExtractAudioAsync(string videoPath, string audioPath, CancellationToken cancellationToken = default);
    Task ExtractAudioSegmentAsync(string audioPath, string outputPath, double startSeconds, double durationSeconds, CancellationToken cancellationToken = default);
    Task<double> GetDurationSecondsAsync(string mediaPath, CancellationToken cancellationToken = default);
    Task<(int Width, int Height)> GetVideoSizeAsync(string videoPath, CancellationToken cancellationToken = default);
    Task CutVerticalClipAsync(
        string sourceVideoPath,
        string outputPath,
        double startSeconds,
        double durationSeconds,
        string? assSubtitlePath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Thin Process.Start wrapper around ffmpeg/ffprobe. Kept separate from subtitle
/// generation and highlight detection so those can be iterated independently.
/// </summary>
public class FFmpegService : IFFmpegService
{
    private readonly ILogger<FFmpegService> _logger;
    private readonly FFmpegOptions _options;
    private string? _ffmpeg;
    private string? _ffprobe;

    public FFmpegService(IOptions<FFmpegOptions> options, ILogger<FFmpegService> logger)
    {
        _logger = logger;
        _options = options.Value;
    }

    private string FfmpegPath => _ffmpeg ??= ResolveAndLog(_options.ExecutablePath, "ffmpeg");
    private string FfprobePath => _ffprobe ??= ResolveAndLog(_options.FfprobePath, "ffprobe");

    private string ResolveAndLog(string configured, string toolName)
    {
        var resolved = ResolveTool(configured, toolName);
        _logger.LogInformation("{Tool} resolved to {Path}", toolName, resolved);
        return resolved;
    }

    public async Task ExtractAudioAsync(string videoPath, string audioPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);

        // 16 kHz mono 64 kbps MP3 stays well under Whisper's 25 MB upload cap for <20 min videos.
        var args =
            $"-y -i \"{videoPath}\" -vn -ac 1 -ar 16000 -b:a 64k \"{audioPath}\"";

        _logger.LogInformation("Extracting audio from {Video} -> {Audio}", videoPath, audioPath);
        await RunAsync(FfmpegPath, args, cancellationToken);
    }

    public async Task ExtractAudioSegmentAsync(
        string audioPath,
        string outputPath,
        double startSeconds,
        double durationSeconds,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var start = startSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var duration = durationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var args =
            $"-y -ss {start} -i \"{audioPath}\" -t {duration} -ac 1 -ar 16000 -b:a 64k \"{outputPath}\"";
        await RunAsync(FfmpegPath, args, cancellationToken);
    }

    public async Task<double> GetDurationSecondsAsync(string mediaPath, CancellationToken cancellationToken = default)
    {
        var args =
            $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{mediaPath}\"";
        var output = await RunAsync(FfprobePath, args, cancellationToken);
        if (!double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
        {
            throw new InvalidOperationException($"ffprobe did not return a duration for '{mediaPath}'. Output: '{output}'");
        }

        return duration;
    }

    public async Task<(int Width, int Height)> GetVideoSizeAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        var args =
            $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0:s=x \"{videoPath}\"";
        var output = (await RunAsync(FfprobePath, args, cancellationToken)).Trim();
        var parts = output.Split('x', 'X');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var width)
            || !int.TryParse(parts[1], out var height))
        {
            throw new InvalidOperationException($"ffprobe did not return width x height for '{videoPath}'. Output: '{output}'");
        }

        return (width, height);
    }

    public async Task CutVerticalClipAsync(
        string sourceVideoPath,
        string outputPath,
        double startSeconds,
        double durationSeconds,
        string? assSubtitlePath,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var start = startSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var duration = durationSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        // Center-crop to 9:16, then scale to 1080x1920.
        // FUTURE: replace the geometric center crop with face/subject-detection smart crop
        // (e.g. a first-frame face box from a detector, or FFmpeg cropdetect + tracking).
        // scale=...:force_original_aspect_ratio=increase fills the 9:16 frame; crop then trims overflow.
        var videoFilter = "scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920";

        if (!string.IsNullOrWhiteSpace(assSubtitlePath))
        {
            var escapedAss = EscapeSubtitlesFilterPath(assSubtitlePath);
            videoFilter += $",subtitles='{escapedAss}'";
        }

        // Re-encode is required for crop + burned-in subs. -ss after -i is slower but frame-accurate,
        // which keeps karaoke subtitles aligned with the word timestamps.
        var args =
            $"-y -i \"{sourceVideoPath}\" -ss {start} -t {duration} " +
            $"-vf \"{videoFilter}\" " +
            "-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p " +
            "-c:a aac -b:a 128k -ac 2 -movflags +faststart " +
            $"\"{outputPath}\"";

        _logger.LogInformation(
            "Generating vertical clip {Output} start={Start}s duration={Duration}s subtitles={HasSubs}",
            outputPath, startSeconds, durationSeconds, !string.IsNullOrWhiteSpace(assSubtitlePath));

        await RunAsync(FfmpegPath, args, cancellationToken);
    }

    private async Task<string> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger.LogDebug("Running {Tool} {Args}", fileName, arguments);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.WriteLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.WriteLine(e.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{fileName}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var error = stderr.ToString();
        var output = stdout.ToString();

        if (process.ExitCode != 0)
        {
            _logger.LogError(
                "{Tool} failed with exit {Code}. Args: {Args}\nstderr:\n{Stderr}",
                fileName, process.ExitCode, arguments, error);
            throw new InvalidOperationException(
                $"{Path.GetFileName(fileName)} exited with code {process.ExitCode}. {TrimForException(error)}");
        }

        _logger.LogDebug("{Tool} stderr: {Stderr}", fileName, error);
        return string.IsNullOrWhiteSpace(output) ? error : output;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cancellation.
        }
    }

    private static string TrimForException(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 2000 ? trimmed : trimmed[^2000..];
    }

    /// <summary>
    /// FFmpeg's subtitles filter uses a mini-language: Windows drive colons and
    /// backslashes must be escaped, and the path should use forward slashes.
    /// </summary>
    internal static string EscapeSubtitlesFilterPath(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/');
        normalized = normalized.Replace(":", @"\:");
        normalized = normalized.Replace("'", @"\'");
        return normalized;
    }

    private static string ResolveTool(string configuredPath, string toolName)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var exeName = OperatingSystem.IsWindows() && !toolName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? toolName + ".exe"
            : toolName;

        var fromPath = FindOnPath(exeName);
        if (fromPath is not null)
        {
            return fromPath;
        }

        foreach (var candidate in EnumerateCommonInstallPaths(exeName))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Could not find {toolName}. Install FFmpeg and add it to PATH, or set FFmpeg:ExecutablePath / FFmpeg:FfprobePath in appsettings.");
    }

    private static string? FindOnPath(string exeName)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in paths)
        {
            var candidate = Path.Combine(dir.Trim('"'), exeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCommonInstallPaths(string exeName)
    {
        yield return Path.Combine(@"C:\ffmpeg\bin", exeName);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", exeName);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var wingetRoot = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(wingetRoot))
        {
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(wingetRoot, "*FFmpeg*", SearchOption.TopDirectoryOnly))
        {
            foreach (var bin in Directory.EnumerateDirectories(dir, "bin", SearchOption.AllDirectories))
            {
                yield return Path.Combine(bin, exeName);
            }
        }
    }
}
