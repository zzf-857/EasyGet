using System.IO;

namespace EasyGet.Services;

/// <summary>Validates sidecar outputs and preserves their first-seen order with linear path deduplication.</summary>
internal sealed class DouyinOutputFileResolver
{
    private const string DouyinManifestFileName = "download_manifest.jsonl";
    private const string DouyinManifestSnapshotPrefix = "download_manifest.easyget-";
    private const string DouyinManifestExtension = ".jsonl";
    private const int DouyinManifestSnapshotTimestampLength = 16;
    private const int DouyinManifestSnapshotUuidLength = 8;

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly string? _directoryPrefix;

    internal DouyinOutputFileResolver(string? outputDirectory)
    {
        if (TryNormalizePath(outputDirectory, out var fullDirectory))
            _directoryPrefix = Path.EndsInDirectorySeparator(fullDirectory)
                ? fullDirectory
                : fullDirectory + Path.DirectorySeparatorChar;
    }

    internal bool IsSafeFilePath(string? path)
        => TryGetSafePath(path, out _);

    internal List<string> Collect(string? primaryPath, IEnumerable<string> outputPaths, string? manifestPath)
    {
        var results = new List<string>();
        var knownPaths = new HashSet<string>(PathComparer);
        foreach (var rawPath in outputPaths)
        {
            if (TryGetSafePath(rawPath, out var fullPath) && knownPaths.Add(fullPath))
                results.Add(rawPath.Trim());
        }

        if (TryGetSafePath(primaryPath, out var fullPrimaryPath) && knownPaths.Add(fullPrimaryPath))
            results.Insert(0, primaryPath!.Trim());

        if (TryGetSafePath(manifestPath, out var fullManifestPath)
            && IsDouyinManifestPath(fullManifestPath)
            && File.Exists(fullManifestPath)
            && knownPaths.Add(fullManifestPath))
        {
            results.Add(fullManifestPath);
        }

        return results;
    }

    private bool TryGetSafePath(string? path, out string fullPath)
    {
        fullPath = "";
        return _directoryPrefix is not null
            && TryNormalizePath(path, out fullPath)
            && fullPath.StartsWith(_directoryPrefix, PathComparison);
    }

    private static bool TryNormalizePath(string? path, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            fullPath = Path.GetFullPath(path.Trim());
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool IsDouyinManifestPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            return IsDouyinManifestFileName(Path.GetFileName(path.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsDouyinManifestFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(fileName, DouyinManifestFileName, comparison))
            return true;

        if (!fileName.StartsWith(DouyinManifestSnapshotPrefix, comparison)
            || !fileName.EndsWith(DouyinManifestExtension, comparison))
        {
            return false;
        }

        var tokenLength = fileName.Length
            - DouyinManifestSnapshotPrefix.Length
            - DouyinManifestExtension.Length;
        var expectedTokenLength = DouyinManifestSnapshotTimestampLength
            + 1
            + DouyinManifestSnapshotUuidLength;
        if (tokenLength != expectedTokenLength)
            return false;

        var tokenStart = DouyinManifestSnapshotPrefix.Length;
        var timestamp = fileName.Substring(tokenStart, DouyinManifestSnapshotTimestampLength);
        var separatorIndex = tokenStart + DouyinManifestSnapshotTimestampLength;
        var uuidStart = separatorIndex + 1;
        var uuid = fileName.Substring(uuidStart, DouyinManifestSnapshotUuidLength);

        return fileName[separatorIndex] == '-'
               && IsDouyinManifestSnapshotTimestamp(timestamp)
               && uuid.All(IsHexDigit);
    }

    private static bool IsDouyinManifestSnapshotTimestamp(string value)
    {
        if (value.Length != DouyinManifestSnapshotTimestampLength
            || value[8] != 'T'
            || value[15] != 'Z')
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (index is 8 or 15)
                continue;

            if (!char.IsDigit(value[index]))
                return false;
        }

        return true;
    }

    private static bool IsHexDigit(char value)
        => value is >= '0' and <= '9'
           or >= 'a' and <= 'f'
           or >= 'A' and <= 'F';

}
