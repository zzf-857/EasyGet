using System.IO;
using System.Text;

namespace EasyGet.Services.Cookies;

public sealed class CookieFileLease : IAsyncDisposable
{
    private int _disposed;

    private CookieFileLease(string filePath)
    {
        FilePath = filePath;
        Arguments = ["--cookies", filePath];
    }

    public static string DefaultTemporaryDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyGet",
        "temp",
        "cookies");

    public string FilePath { get; }
    public IReadOnlyList<string> Arguments { get; }

    public static Task<CookieFileLease> CreateLegacyAsync(
        string content,
        MediaPlatformDefinition platform,
        string targetHost,
        string rootDirectory,
        CancellationToken cancellationToken)
        => CreateLinesAsync(
            CookieFileSerializer.BuildScopedLines(content, platform, targetHost),
            platform.Id,
            rootDirectory,
            cancellationToken);

    public static Task<CookieFileLease> CreateCookiesAsync(
        IReadOnlyList<BrowserCookie> cookies,
        MediaPlatformDefinition platform,
        string rootDirectory,
        CancellationToken cancellationToken)
        => CreateLinesAsync(
            CookieFileSerializer.BuildScopedLines(cookies, platform),
            platform.Id,
            rootDirectory,
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        TryDelete(FilePath);
        await ValueTask.CompletedTask;
    }

    public static int CleanupStaleFiles(
        string rootDirectory,
        DateTime utcNow,
        TimeSpan maximumAge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (maximumAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        if (!Directory.Exists(rootDirectory))
            return 0;

        string[] paths;
        try
        {
            paths = Directory.GetFiles(rootDirectory, "*.txt", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            return 0;
        }

        var deleted = 0;
        foreach (var path in paths)
        {
            try
            {
                if (utcNow - File.GetLastWriteTimeUtc(path) <= maximumAge)
                    continue;
                File.Delete(path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or System.Security.SecurityException)
            {
            }
        }

        return deleted;
    }

    private static async Task<CookieFileLease> CreateLinesAsync(
        IReadOnlyList<string> lines,
        string platformId,
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        CookieStorageKey.ValidatePlatformId(platformId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(rootDirectory);
        var filePath = Path.Combine(rootDirectory, $"{platformId}-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllLinesAsync(
                filePath,
                lines,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            CookieFilePermissions.RestrictToCurrentUser(filePath);
            return new CookieFileLease(filePath);
        }
        catch
        {
            TryDelete(filePath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
        }
    }
}
