using System.IO;

namespace EasyGet.Services;

/// <summary>
/// Discovers immediate download-root folders plus directories referenced by history.
/// </summary>
public sealed class HistoryDirectoryDiscoveryService
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public Task<IReadOnlyList<string>> DiscoverAsync(
        string? rootDirectory,
        IReadOnlyCollection<string> knownDirectories,
        CancellationToken cancellationToken = default)
    {
        var root = rootDirectory ?? "";
        var known = knownDirectories.ToArray();
        return Task.Run(
            () => Discover(root, known, cancellationToken),
            cancellationToken);
    }

    internal static IReadOnlyList<string> Discover(
        string rootDirectory,
        IEnumerable<string> knownDirectories,
        CancellationToken cancellationToken = default)
    {
        var results = new HashSet<string>(PathComparer);
        var normalizedRoot = NormalizeExistingDirectory(rootDirectory);
        if (!string.IsNullOrWhiteSpace(normalizedRoot))
        {
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (var directory in Directory.EnumerateDirectories(
                             normalizedRoot,
                             "*",
                             options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsLikelyDownloadTarget(directory))
                        AddDirectory(results, directory, normalizedRoot);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or DirectoryNotFoundException
                                       or IOException
                                       or NotSupportedException
                                       or PathTooLongException
                                       or UnauthorizedAccessException)
            {
                // Known history directories below still remain available as a fallback.
            }
        }

        foreach (var directory in knownDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddDirectory(results, directory, normalizedRoot);
        }

        return results
            .OrderBy(path => DescribeDirectory(path, normalizedRoot), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(path => path, PathComparer)
            .ToList();
    }

    private static bool IsLikelyDownloadTarget(string directory)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            if (!Directory.EnumerateDirectories(directory, "*", options).Any())
                return true;

            return Directory.EnumerateFiles(directory, "*", options)
                .Any(filePath =>
                {
                    var extension = Path.GetExtension(filePath);
                    return MediaFileClassifier.IsPreviewExtension(extension)
                           || MediaFileClassifier.IsFfmpegThumbnailExtension(extension);
                });
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or DirectoryNotFoundException
                                   or IOException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string DescribeDirectory(string directory, string? rootDirectory)
    {
        var normalizedDirectory = NormalizeExistingDirectory(directory);
        var normalizedRoot = NormalizeExistingDirectory(rootDirectory);
        if (string.IsNullOrWhiteSpace(normalizedDirectory))
            return "本地文件夹";
        if (string.IsNullOrWhiteSpace(normalizedRoot)
            || !IsWithinRoot(normalizedDirectory, normalizedRoot))
        {
            return normalizedDirectory;
        }

        var relative = Path.GetRelativePath(normalizedRoot, normalizedDirectory);
        return string.IsNullOrWhiteSpace(relative) || relative == "."
            ? Path.GetFileName(normalizedDirectory) is { Length: > 0 } rootName
                ? rootName
                : normalizedDirectory
            : relative;
    }

    private static void AddDirectory(
        ISet<string> results,
        string? directory,
        string normalizedRoot)
    {
        var normalized = NormalizeExistingDirectory(directory);
        normalized = ResolveExistingCanonicalPlatformDirectory(normalized);
        if (string.IsNullOrWhiteSpace(normalized)
            || (!string.IsNullOrWhiteSpace(normalizedRoot)
                && PathComparer.Equals(normalized, normalizedRoot)))
        {
            return;
        }

        results.Add(normalized);
    }

    private static string ResolveExistingCanonicalPlatformDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)
            || !PlatformDirectoryPolicy.TryCanonicalizeDirectoryName(
                Path.GetFileName(directory),
                out var canonicalFolder)
            || string.Equals(
                Path.GetFileName(directory),
                canonicalFolder,
                StringComparison.OrdinalIgnoreCase))
        {
            return directory;
        }

        var parent = Path.GetDirectoryName(directory);
        if (string.IsNullOrWhiteSpace(parent))
            return directory;

        var canonical = NormalizeExistingDirectory(Path.Combine(parent, canonicalFolder));
        return canonical.Length > 0 ? canonical : directory;
    }

    private static string NormalizeExistingDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return "";

        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(directory.Trim()));
            return Directory.Exists(fullPath) ? fullPath : "";
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".."
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }
}
