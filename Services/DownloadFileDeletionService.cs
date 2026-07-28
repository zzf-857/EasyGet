using System.IO;
using EasyGet.Models;

namespace EasyGet.Services;

public sealed record DownloadFileDeletionResult(
    int DeletedFileCount,
    int MissingFileCount,
    int SkippedUnsafePathCount,
    IReadOnlyList<string> Errors);

public sealed class DownloadFileDeletionService
{
    private readonly Func<string, bool> _fileExists;
    private readonly Action<string> _deleteFile;

    public DownloadFileDeletionService()
        : this(File.Exists, File.Delete)
    {
    }

    internal DownloadFileDeletionService(
        Func<string, bool> fileExists,
        Action<string> deleteFile)
    {
        _fileExists = fileExists;
        _deleteFile = deleteFile;
    }

    public DownloadFileDeletionResult DeleteFiles(
        IEnumerable<DownloadHistory> items,
        IEnumerable<string> allowedRootDirectories)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(allowedRootDirectories);

        var roots = allowedRootDirectories
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(TryNormalizePath)
            .Where(root => root is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var paths = items.SelectMany(EnumeratePaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var deleted = 0;
        var missing = 0;
        var skipped = 0;
        var errors = new List<string>();
        foreach (var path in paths)
        {
            var fullPath = TryNormalizePath(path);
            if (fullPath is null || !roots.Any(root => IsWithinRoot(fullPath, root)))
            {
                skipped++;
                continue;
            }

            if (!_fileExists(fullPath))
            {
                missing++;
                continue;
            }

            try
            {
                _deleteFile(fullPath);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or System.Security.SecurityException)
            {
                errors.Add($"{Path.GetFileName(fullPath)}: {ex.Message}");
            }
        }

        return new DownloadFileDeletionResult(deleted, missing, skipped, errors);
    }

    internal static bool IsWithinRoot(string fullPath, string rootDirectory)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var normalizedPath = Path.GetFullPath(fullPath);
        var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        return !Path.IsPathRooted(relative)
               && relative != ".."
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string? TryNormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumeratePaths(DownloadHistory item)
    {
        if (!string.IsNullOrWhiteSpace(item.FilePath))
            yield return item.FilePath;
        foreach (var path in item.AttachmentFilePaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
                yield return path;
        }
    }
}
