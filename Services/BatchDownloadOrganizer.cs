using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyGet.Services;

internal sealed record DownloadBatchContext(
    string Id,
    string Name,
    string Directory,
    string CollectionTitle);

/// <summary>
/// 为批量和合集下载创建稳定、可读且不会覆盖已有内容的根目录。
/// </summary>
internal static partial class BatchDownloadOrganizer
{
    private const int MaxFolderSegmentLength = 120;

    internal static DownloadBatchContext? Create(
        string baseOutputDirectory,
        IReadOnlyCollection<string> urls,
        string? collectionSourceUrl = null,
        DateTime? now = null,
        string? collectionTitle = null)
    {
        ArgumentNullException.ThrowIfNull(urls);
        if (urls.Count == 0
            || (urls.Count == 1
                && string.IsNullOrWhiteSpace(collectionSourceUrl)
                && string.IsNullOrWhiteSpace(collectionTitle)))
        {
            return null;
        }

        var timestamp = now ?? DateTime.Now;
        var actualCollectionTitle = (collectionTitle ?? "").Trim();
        var descriptor = Describe(urls, collectionSourceUrl);
        var folderStem = SanitizeFolderSegment(
            actualCollectionTitle.Length > 0
                ? actualCollectionTitle
                : $"{descriptor.FolderPrefix}_{timestamp:yyyyMMdd_HHmmss}");
        var baseDirectory = Path.GetFullPath(baseOutputDirectory);
        Directory.CreateDirectory(baseDirectory);

        var batchDirectory = actualCollectionTitle.Length > 0
            ? Path.Combine(baseDirectory, folderStem)
            : GetUniqueDirectory(baseDirectory, folderStem);
        Directory.CreateDirectory(batchDirectory);

        return new DownloadBatchContext(
            CreateBatchId(collectionSourceUrl, actualCollectionTitle),
            actualCollectionTitle.Length > 0
                ? actualCollectionTitle
                : $"{descriptor.DisplayName} · {timestamp:yyyy-MM-dd HH:mm}",
            batchDirectory,
            actualCollectionTitle);
    }

    internal static DownloadBatchContext ReuseExisting(
        string directory,
        string batchId,
        string batchName,
        string collectionTitle)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Existing collection directory is required.", nameof(directory));
        if (string.IsNullOrWhiteSpace(batchId))
            throw new ArgumentException("Existing collection batch ID is required.", nameof(batchId));

        var fullDirectory = Path.GetFullPath(directory.Trim());
        if (!Directory.Exists(fullDirectory))
            throw new DirectoryNotFoundException($"Existing collection directory was not found: {fullDirectory}");

        var resolvedName = string.IsNullOrWhiteSpace(batchName)
            ? Path.GetFileName(fullDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar))
            : batchName.Trim();
        if (string.IsNullOrWhiteSpace(resolvedName))
            resolvedName = "已有合集";

        return new DownloadBatchContext(
            batchId.Trim(),
            resolvedName,
            fullDirectory,
            string.IsNullOrWhiteSpace(collectionTitle)
                ? resolvedName
                : collectionTitle.Trim());
    }

    internal static string CreateBatchId(string? collectionSourceUrl, string collectionTitle)
    {
        string identity;
        if (!string.IsNullOrWhiteSpace(collectionSourceUrl)
            && TryDescribeCollectionUrl(collectionSourceUrl, out var collectionKey, out _))
        {
            identity = collectionKey;
        }
        else if (!string.IsNullOrWhiteSpace(collectionSourceUrl))
        {
            identity = collectionSourceUrl.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(collectionTitle))
        {
            identity = collectionTitle.Trim();
        }
        else
        {
            return Guid.NewGuid().ToString("N");
        }

        return CreateStableId("collection", identity);
    }

    internal static string CreateDirectoryGroupId(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Directory is required.", nameof(directory));

        var identity = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(directory.Trim()));
        if (OperatingSystem.IsWindows())
            identity = identity.ToUpperInvariant();
        return CreateStableId("folder", identity);
    }

    internal static string ResolveCommonOutputDirectory(IEnumerable<string?> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var directories = filePaths
            .Select(ResolveOutputDirectory)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToList();
        return directories.Count == 1 ? directories[0] : "";
    }

    internal static string ResolveOutputDirectory(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "";

        try
        {
            return Path.GetDirectoryName(filePath) ?? "";
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return "";
        }
    }

    private static string CreateStableId(string prefix, string identity)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"{prefix}-{Convert.ToHexString(digest)[..20].ToLowerInvariant()}";
    }

    private static BatchDescriptor Describe(
        IReadOnlyCollection<string> urls,
        string? collectionSourceUrl)
    {
        var candidates = urls
            .Append(collectionSourceUrl ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        var bilibiliIds = candidates
            .Select(TryGetBilibiliVideoId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (bilibiliIds.Count == 1)
        {
            var id = bilibiliIds[0]!;
            return new BatchDescriptor(
                $"Bilibili合集_{id}",
                $"Bilibili 合集 · {id}");
        }

        if (!string.IsNullOrWhiteSpace(collectionSourceUrl))
        {
            if (TryGetYoutubePlaylistId(collectionSourceUrl, out var playlistId))
            {
                var safeId = SanitizeFolderSegment(playlistId);
                return new BatchDescriptor(
                    $"YouTube播放列表_{safeId}",
                    $"YouTube 播放列表 · {safeId}");
            }

            var platform = GetPlatformName(collectionSourceUrl);
            return new BatchDescriptor($"{platform}合集", $"{platform} 合集");
        }

        return new BatchDescriptor("批量下载", "批量下载");
    }

    internal static bool TryDescribeCollectionUrl(
        string value,
        out string collectionKey,
        out string displayName)
    {
        collectionKey = "";
        displayName = "";

        var bilibiliId = TryGetBilibiliVideoId(value);
        if (!string.IsNullOrWhiteSpace(bilibiliId))
        {
            collectionKey = $"bilibili:{bilibiliId}";
            displayName = $"Bilibili 合集 · {bilibiliId}";
            return true;
        }

        if (TryGetYoutubePlaylistId(value, out var playlistId))
        {
            collectionKey = $"youtube:{playlistId}";
            displayName = $"YouTube 播放列表 · {playlistId}";
            return true;
        }

        return false;
    }

    private static string? TryGetBilibiliVideoId(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Host.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = BilibiliVideoIdRegex().Match(uri.AbsolutePath);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool TryGetYoutubePlaylistId(string value, out string playlistId)
    {
        playlistId = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (!uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
                && !uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2
                && string.Equals(Uri.UnescapeDataString(parts[0]), "list", StringComparison.OrdinalIgnoreCase))
            {
                playlistId = Uri.UnescapeDataString(parts[1]).Trim();
                return playlistId.Length > 0;
            }
        }

        return false;
    }

    private static string GetPlatformName(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return "视频";

        var host = uri.Host.ToLowerInvariant();
        if (host.Contains("bilibili")) return "Bilibili";
        if (host.Contains("youtube") || host.Contains("youtu.be")) return "YouTube";
        if (host.Contains("douyin")) return "抖音";
        if (host.Contains("kuaishou")) return "快手";
        if (host.Contains("ixigua")) return "西瓜视频";
        return "视频";
    }

    private static string GetUniqueDirectory(string baseDirectory, string folderStem)
    {
        var candidate = Path.Combine(baseDirectory, folderStem);
        for (var suffix = 2; Directory.Exists(candidate); suffix++)
            candidate = Path.Combine(baseDirectory, $"{folderStem}_{suffix}");
        return candidate;
    }

    internal static string SanitizeFolderSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            var text = rune.ToString();
            builder.Append(text.Any(invalidChars.Contains) || Rune.IsControl(rune) ? '_' : text);
        }

        var sanitized = builder.ToString().Trim().TrimEnd('.', ' ');
        if (sanitized.Length > MaxFolderSegmentLength)
            sanitized = sanitized[..MaxFolderSegmentLength].TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "批量下载" : sanitized;
    }

    [GeneratedRegex(@"/(BV[0-9A-Za-z]+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BilibiliVideoIdRegex();

    private sealed record BatchDescriptor(string FolderPrefix, string DisplayName);
}
