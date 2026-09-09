using System.IO;
using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>
/// Resolves usable local history files and reads only manifests anchored to those files.
/// </summary>
internal static class HistoryItemEnrichmentService
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    internal static HistoryItemEnrichment Enrich(DownloadHistory item)
        => new(
            item,
            ResolveExistingHistoryPath(item),
            BuildDouyinManifestSummary(item));

    private static string ResolveExistingHistoryPath(DownloadHistory item)
    {
        if (!IsDouyinManifestPath(item.FilePath) && PathExists(item.FilePath))
            return item.FilePath;

        return item.AttachmentFilePaths
            .FirstOrDefault(path => !IsDouyinManifestPath(path) && PathExists(path))
            ?? "";
    }

    private static DouyinManifestSummaryResult BuildDouyinManifestSummary(DownloadHistory item)
    {
        var manifestPath = ResolveSafeDouyinManifestPath(item);
        if (string.IsNullOrWhiteSpace(manifestPath))
            return DouyinManifestSummaryResult.Empty;

        var summary = DouyinManifestReader.ReadSummary(manifestPath);
        if (summary is null)
            return DouyinManifestSummaryResult.Empty;

        var attachmentCount = item.AttachmentFilePaths
            .Count(path => !IsDouyinManifestPath(path));
        return new DouyinManifestSummaryResult(
            FormatDouyinManifestSummary(summary, attachmentCount),
            summary);
    }

    private static string ResolveSafeDouyinManifestPath(DownloadHistory item)
    {
        // Most history entries have no manifest. Avoid probing every attachment on disk
        // when no candidate could possibly supply a manifest summary.
        var candidatePaths = EnumerateDouyinManifestCandidatePaths(item)
            .Where(IsDouyinManifestPath)
            .ToList();
        if (candidatePaths.Count == 0)
            return "";

        var anchorPaths = ResolveExistingNonManifestAnchorPaths(item);
        if (anchorPaths.Count == 0)
            return "";

        foreach (var rawPath in candidatePaths)
        {
            try
            {
                var fullPath = Path.GetFullPath(rawPath.Trim());
                var manifestDirectory = Path.GetDirectoryName(fullPath);
                if (File.Exists(fullPath)
                    && !string.IsNullOrWhiteSpace(manifestDirectory)
                    && IsSafeDouyinManifestParentDirectory(manifestDirectory)
                    && anchorPaths.All(anchorPath => IsDirectoryAncestorOfPathOrSelf(manifestDirectory, anchorPath)))
                {
                    return fullPath;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return "";
    }

    private static IEnumerable<string> EnumerateDouyinManifestCandidatePaths(DownloadHistory item)
    {
        if (!string.IsNullOrWhiteSpace(item.FilePath))
            yield return item.FilePath;

        foreach (var path in item.AttachmentFilePaths)
            yield return path;
    }

    private static HashSet<string> ResolveExistingNonManifestAnchorPaths(DownloadHistory item)
    {
        var anchorPaths = new HashSet<string>(PathComparer);
        AddExistingNonManifestAnchorPath(anchorPaths, item.FilePath);

        foreach (var path in item.AttachmentFilePaths)
            AddExistingNonManifestAnchorPath(anchorPaths, path);

        return anchorPaths;
    }

    private static void AddExistingNonManifestAnchorPath(ISet<string> anchorPaths, string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || IsDouyinManifestPath(rawPath))
            return;

        try
        {
            var fullPath = Path.GetFullPath(rawPath.Trim());
            if (!anchorPaths.Contains(fullPath) && File.Exists(fullPath))
                anchorPaths.Add(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
    }

    internal static bool IsSafeDouyinManifestParentDirectory(string manifestDirectory)
    {
        if (string.IsNullOrWhiteSpace(manifestDirectory))
            return false;

        try
        {
            var fullDirectory = Path.GetFullPath(manifestDirectory.Trim());
            var root = Path.GetPathRoot(fullDirectory);
            if (string.IsNullOrWhiteSpace(root))
                return false;

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return !string.Equals(
                TrimTrailingDirectorySeparators(fullDirectory),
                TrimTrailingDirectorySeparators(root),
                comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsDirectoryAncestorOfPathOrSelf(string ancestorDirectory, string path)
    {
        try
        {
            var fullAncestor = Path.GetFullPath(ancestorDirectory);
            var fullPath = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (string.Equals(fullAncestor, fullPath, comparison))
                return true;

            var ancestorWithSeparator = fullAncestor.EndsWith(Path.DirectorySeparatorChar)
                || fullAncestor.EndsWith(Path.AltDirectorySeparatorChar)
                    ? fullAncestor
                    : fullAncestor + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(ancestorWithSeparator, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string TrimTrailingDirectorySeparators(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string FormatDouyinManifestSummary(DouyinManifestSummary summary, int attachmentCount)
    {
        var itemCountText = summary.IsTruncated
            ? $"{summary.ItemCount}+"
            : summary.ItemCount.ToString();
        var parts = new List<string> { $"作品 {itemCountText}" };
        if (summary.VideoCount > 0)
            parts.Add($"视频 {summary.VideoCount}");
        if (summary.GalleryCount > 0)
            parts.Add($"图文 {summary.GalleryCount}");
        if (summary.MusicCount > 0)
            parts.Add($"音乐 {summary.MusicCount}");
        parts.Add($"附属 {Math.Max(0, attachmentCount)}");
        return string.Join(" / ", parts);
    }

    private static bool IsDouyinManifestPath(string path)
        => DouyinSpecialDownloadService.IsDouyinManifestPath(path);

    private static bool PathExists(string path)
        => !string.IsNullOrWhiteSpace(path)
           && (System.IO.File.Exists(path) || System.IO.Directory.Exists(path));

    internal sealed record HistoryItemEnrichment(
        DownloadHistory Item,
        string AvailableFilePath,
        DouyinManifestSummaryResult DouyinManifestSummary);

    internal sealed record DouyinManifestSummaryResult(
        string SummaryText,
        DouyinManifestSummary? Summary)
    {
        public static DouyinManifestSummaryResult Empty { get; } = new("", null);
    }
}
