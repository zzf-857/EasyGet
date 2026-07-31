using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace EasyGet.Services;

/// <summary>
/// Produces durable local thumbnails for downloaded media so history cards do not
/// depend on remote poster URLs that may expire or reject hotlinking.
/// </summary>
public sealed class HistoryThumbnailService
{
    private const int ThumbnailWidth = 640;
    private const int ThumbnailHeight = 360;
    private static readonly string CacheVersion = $"v2-{ThumbnailWidth}x{ThumbnailHeight}";
    private static readonly string ScaleAndCropFilter =
        $"scale={ThumbnailWidth}:{ThumbnailHeight}:force_original_aspect_ratio=increase," +
        $"crop={ThumbnailWidth}:{ThumbnailHeight}";
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ProcessStopTimeout = TimeSpan.FromSeconds(2);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly EnumerationOptions MediaEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };
    private static readonly FrameExtractionStrategy[] ExtractionStrategies =
    [
        new(Seek: null, UseRepresentativeFrameFilter: true),
        new(Seek: TimeSpan.FromSeconds(1), UseRepresentativeFrameFilter: false),
        new(Seek: null, UseRepresentativeFrameFilter: false)
    ];
    private readonly ConfigService _configService;
    private readonly EnvironmentService _environmentService;
    private readonly SemaphoreSlim _generationGate = new(2, 2);
    // Removing an idle keyed gate can race a waiter; retain the small per-session set instead.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _cacheGates = new(PathComparer);

    public HistoryThumbnailService(
        ConfigService configService,
        EnvironmentService environmentService)
    {
        _configService = configService;
        _environmentService = environmentService;
    }

    public async Task<string> ResolveLocalThumbnailAsync(
        IEnumerable<string> availablePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(availablePaths);
        var seenPaths = new HashSet<string>(PathComparer);

        foreach (var availablePath in availablePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mediaCandidates = ResolveMediaCandidates(availablePath, seenPaths, cancellationToken);
            foreach (var mediaPath in mediaCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var thumbnailPath = await ResolveMediaThumbnailAsync(mediaPath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(thumbnailPath))
                    return thumbnailPath;
            }
        }

        return "";
    }

    private async Task<string> ResolveMediaThumbnailAsync(
        string mediaPath,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(mediaPath);
        if (MediaFileClassifier.IsDirectImageExtension(extension))
            return IsUsableImage(mediaPath) ? mediaPath : "";

        string cachePath;
        try
        {
            cachePath = BuildCachePath(mediaPath);
            if (IsUsableImage(cachePath))
                return cachePath;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            return "";
        }

        var ffmpegPath = ResolveFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
            return "";

        var cacheGate = _cacheGates.GetOrAdd(cachePath, _ => new SemaphoreSlim(1, 1));
        await cacheGate.WaitAsync(cancellationToken);
        try
        {
            if (IsUsableImage(cachePath))
                return cachePath;

            await _generationGate.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                return await GenerateThumbnailAsync(
                        ffmpegPath,
                        mediaPath,
                        cachePath,
                        cancellationToken)
                    ? cachePath
                    : "";
            }
            finally
            {
                _generationGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine($"[HistoryThumbnail] Extraction failed: {ex.Message}");
            return "";
        }
        finally
        {
            cacheGate.Release();
        }
    }

    private static IReadOnlyList<string> ResolveMediaCandidates(
        string availablePath,
        ISet<string> seenPaths,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(availablePath))
            return [];

        var directoryImages = new List<string>();
        var directoryFrameSources = new List<string>();
        try
        {
            var path = availablePath.Trim();
            if (File.Exists(path))
            {
                return TryResolveMediaCandidate(path, seenPaths, out var candidate)
                    ? [candidate]
                    : [];
            }

            if (!Directory.Exists(path))
                return [];

            var directorySeenPaths = new HashSet<string>(seenPaths, PathComparer);
            foreach (var filePath in Directory.EnumerateFiles(path, "*", MediaEnumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryResolveMediaCandidate(filePath, directorySeenPaths, out var candidate))
                    continue;

                if (MediaFileClassifier.IsDirectImageExtension(Path.GetExtension(candidate)))
                    directoryImages.Add(candidate);
                else
                    directoryFrameSources.Add(candidate);
            }
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            // Keep candidates found before a directory entry disappeared or became inaccessible.
        }

        directoryFrameSources.Sort(PathComparer);
        directoryImages.Sort(PathComparer);
        IReadOnlyList<string> candidates = [.. directoryFrameSources, .. directoryImages];
        foreach (var candidate in candidates)
            seenPaths.Add(candidate);
        return candidates;
    }

    private static bool TryResolveMediaCandidate(
        string path,
        ISet<string> seenPaths,
        out string fullPath)
    {
        var extension = Path.GetExtension(path);
        if (!MediaFileClassifier.IsDirectImageExtension(extension)
            && !MediaFileClassifier.IsFfmpegThumbnailExtension(extension))
        {
            fullPath = "";
            return false;
        }

        fullPath = Path.GetFullPath(path);
        if (!seenPaths.Add(fullPath))
        {
            fullPath = "";
            return false;
        }

        return true;
    }

    private string BuildCachePath(string mediaPath)
    {
        var file = new FileInfo(mediaPath);
        var identity = $"{CacheVersion}|{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var fileName = $"{Convert.ToHexString(digest).ToLowerInvariant()}.jpg";
        return Path.Combine(_configService.ConfigDirectory, "cache", "history-thumbnails", fileName);
    }

    private string ResolveFfmpegPath()
    {
        var candidates = new[]
        {
            _environmentService.Status.FfmpegPath,
            Path.Combine(_configService.ConfigDirectory, "tools", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"),
            EnvironmentService.FindExecutableOnPath("ffmpeg") ?? ""
        };

        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (File.Exists(candidate))
                return candidate;

            if (!candidate.Contains(Path.DirectorySeparatorChar)
                && !candidate.Contains(Path.AltDirectorySeparatorChar))
            {
                return candidate;
            }
        }

        return "";
    }

    private static async Task<bool> GenerateThumbnailAsync(
        string ffmpegPath,
        string mediaPath,
        string cachePath,
        CancellationToken cancellationToken)
    {
        foreach (var strategy in ExtractionStrategies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp.jpg";
            try
            {
                var startInfo = CreateFfmpegStartInfo(
                    ffmpegPath,
                    mediaPath,
                    temporaryPath,
                    strategy);
                if (!await RunExtractionAsync(startInfo, cancellationToken)
                    || !IsUsableImage(temporaryPath))
                {
                    continue;
                }

                File.Move(temporaryPath, cachePath, overwrite: true);
                return true;
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        return false;
    }

    private static ProcessStartInfo CreateFfmpegStartInfo(
        string ffmpegPath,
        string mediaPath,
        string outputPath,
        FrameExtractionStrategy strategy)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var arguments = startInfo.ArgumentList;
        arguments.Add("-hide_banner");
        arguments.Add("-loglevel");
        arguments.Add("error");
        arguments.Add("-nostdin");
        arguments.Add("-y");
        if (strategy.Seek is { } seek)
        {
            arguments.Add("-ss");
            arguments.Add(seek.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }
        arguments.Add("-i");
        arguments.Add(mediaPath);
        arguments.Add("-map");
        arguments.Add("0:v:0");
        arguments.Add("-an");
        arguments.Add("-sn");
        arguments.Add("-dn");
        arguments.Add("-vf");
        arguments.Add(
            strategy.UseRepresentativeFrameFilter
                ? $"thumbnail=48,{ScaleAndCropFilter}"
                : ScaleAndCropFilter);
        arguments.Add("-frames:v");
        arguments.Add("1");
        arguments.Add("-q:v");
        arguments.Add("3");
        arguments.Add(outputPath);
        return startInfo;
    }

    private static async Task<bool> RunExtractionAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo);
        if (process is null)
            return false;

        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ExtractionTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopProcessAsync(process, stderrTask);
            return false;
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process, stderrTask);
            throw;
        }

        var error = await stderrTask;
        if (process.ExitCode == 0)
            return true;

        if (!string.IsNullOrWhiteSpace(error))
            Debug.WriteLine($"[HistoryThumbnail] ffmpeg: {error.Trim()}");
        return false;
    }

    private static bool IsUsableImage(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0)
                return false;

            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            return decoder.Frames.Count > 0
                   && decoder.Frames[0].PixelWidth > 0
                   && decoder.Frames[0].PixelHeight > 0;
        }
        catch
        {
            // Invalid or partially written images should fall through to another candidate.
            return false;
        }
    }

    private static async Task DrainErrorAsync(Task<string> stderrTask)
    {
        try
        {
            if (ReferenceEquals(await Task.WhenAny(stderrTask, Task.Delay(1000)), stderrTask))
                _ = await stderrTask;
        }
        catch
        {
            // Process cleanup is best effort after cancellation or timeout.
        }
    }

    private static async Task StopProcessAsync(
        Process process,
        Task<string> stderrTask)
    {
        TryKill(process);

        using var stopSource = new CancellationTokenSource(ProcessStopTimeout);
        try
        {
            await process.WaitForExitAsync(stopSource.Token);
        }
        catch (OperationCanceledException)
        {
            // The process did not exit within the bounded cleanup window.
        }
        catch (InvalidOperationException)
        {
            // The process handle may already be unavailable during shutdown.
        }

        await DrainErrorAsync(stderrTask);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process cleanup is best effort.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A temporary file may still be locked while ffmpeg is terminating.
        }
    }

    private sealed record FrameExtractionStrategy(
        TimeSpan? Seek,
        bool UseRepresentativeFrameFilter);
}
