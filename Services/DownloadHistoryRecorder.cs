using System.IO;
using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>Persists completed downloads and filters their attachment paths before recording history.</summary>
internal sealed class DownloadHistoryRecorder(HistoryService historyService)
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private readonly HistoryService _historyService = historyService;
    private readonly SemaphoreSlim _historyWriteSemaphore = new(1, 1);

    internal async Task SaveIfCompletedAsync(DownloadTask task)
    {
        if (task.Status != DownloadStatus.Completed)
            return;

        await _historyWriteSemaphore.WaitAsync();
        try
        {
            await _historyService.AddAsync(new DownloadHistory
            {
                Url = task.Url,
                Title = task.Title,
                Platform = task.Platform,
                Format = task.Format,
                Quality = task.Quality,
                FileSize = task.FileSize,
                FilePath = task.OutputFilePath,
                BatchId = task.BatchId,
                BatchName = task.BatchName,
                BatchDirectory = task.BatchDirectory,
                AttachmentFilePaths = GetAttachmentFilePathsForHistory(task),
                ThumbnailUrl = task.ThumbnailUrl,
                DownloadTime = DateTime.Now
            });
        }
        finally
        {
            _historyWriteSemaphore.Release();
        }
    }

    private static List<string> GetAttachmentFilePathsForHistory(DownloadTask task)
    {
        var attachments = new List<string>();
        if (!TryNormalizePath(task.OutputDirectory, out var outputDirectory))
            return attachments;

        var directoryPrefix = Path.EndsInDirectorySeparator(outputDirectory)
            ? outputDirectory
            : outputDirectory + Path.DirectorySeparatorChar;
        var knownPaths = new HashSet<string>(PathComparer);
        if (TryNormalizePath(task.OutputFilePath, out var primaryPath))
            knownPaths.Add(primaryPath);

        foreach (var rawPath in task.OutputFilePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            var path = rawPath.Trim();
            if (TryNormalizePath(path, out var fullPath)
                && fullPath.StartsWith(directoryPrefix, PathComparison)
                && knownPaths.Add(fullPath))
            {
                attachments.Add(path);
            }
        }

        return attachments;
    }

    private static bool TryNormalizePath(string? path, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
