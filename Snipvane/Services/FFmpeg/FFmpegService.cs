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

        var (srcWidth, srcHeight) = await GetVideoSizeAsync(sourceVideoPath, cancellationToken);
        var portrait = srcHeight >= srcWidth;
        var burnSubs = !string.IsNullOrWhiteSpace(assSubtitlePath);
        var width = _options.OutputWidth > 0 ? _options.OutputWidth : 720;
        var height = _options.OutputHeight > 0 ? _options.OutputHeight : 1280;
        var preset = string.IsNullOrWhiteSpace(_options.VideoPreset) ? "ultrafast" : _options.VideoPreset;
        var threads = _options.Threads > 0 ? _options.Threads : 1;

        // Karaoke ASS burn is too slow for Render free. Portrait WhatsApp clips
        // can stream-copy in seconds; captions overlay in the review UI instead.
        if (!burnSubs && portrait)
        {
            var copyArgs =
                $"-y -ss {start} -i \"{sourceVideoPath}\" -t {duration} " +
                "-c copy -avoid_negative_ts make_zero -movflags +faststart " +
                $"\"{outputPath}\"";
            _logger.LogInformation(
                "Fast-cutting portrait clip {Output} start={Start}s duration={Duration}s (stream copy)",
                outputPath, startSeconds, durationSeconds);
            try
            {
                await RunAsync(FfmpegPath, copyArgs, cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stream copy failed for {Output}; falling back to ultrafast re-encode", outputPath);
            }
        }

        string? videoFilter = null;
        if (!portrait)
        {
            videoFilter =
                $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height}";
        }

        if (burnSubs)
        {
            var escapedAss = EscapeSubtitlesFilterPath(assSubtitlePath!);
            videoFilter = string.IsNullOrWhiteSpace(videoFilter)
                ? $"subtitles='{escapedAss}'"
                : videoFilter + $",subtitles='{escapedAss}'";
        }

        var filterArg = string.IsNullOrWhiteSpace(videoFilter) ? "" : $"-vf \"{videoFilter}\" ";
        var args =
            $"-y -ss {start} -i \"{sourceVideoPath}\" -t {duration} " +
            $"-threads {threads} -filter_threads {threads} " +
            filterArg +
            $"-c:v libx264 -preset {preset} -crf 28 -pix_fmt yuv420p " +
            "-c:a aac -b:a 96k -ac 1 -movflags +faststart " +
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
