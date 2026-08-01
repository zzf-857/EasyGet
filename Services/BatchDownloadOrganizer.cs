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
/// 解析合集身份并确保显式选择的合集目录保持为最终目录。
/// </summary>
internal static partial class BatchDownloadOrganizer
{
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

    [GeneratedRegex(@"/(BV[0-9A-Za-z]+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BilibiliVideoIdRegex();
}
