using System.IO;

namespace EasyGet.Services;

public enum DownloadPreflightSeverity
{
    Information,
    Warning,
    Blocking
}

public sealed record DownloadPreflightIssue(
    string Code,
    DownloadPreflightSeverity Severity,
    string Message);

public sealed record DownloadPreflightResult(
    string OutputDirectory,
    long? AvailableBytes,
    long ExpectedBytes,
    IReadOnlyList<DownloadPreflightIssue> Issues)
{
    public bool CanProceed => Issues.All(issue => issue.Severity != DownloadPreflightSeverity.Blocking);

    public string BlockingMessage => string.Join(
        Environment.NewLine,
        Issues.Where(issue => issue.Severity == DownloadPreflightSeverity.Blocking)
            .Select(issue => issue.Message));
}

public sealed class DownloadPreflightService
{
    internal const long LowSpaceWarningBytes = 1024L * 1024 * 1024;
    internal const long MinimumReserveBytes = 512L * 1024 * 1024;

    private readonly Func<string, bool> _directoryExists;
    private readonly Action<string> _createDirectory;
    private readonly Func<string, bool> _canWriteDirectory;
    private readonly Func<string, long?> _getAvailableBytes;

    public DownloadPreflightService()
        : this(
            Directory.Exists,
            path => Directory.CreateDirectory(path),
            CanWriteDirectory,
            GetAvailableBytes)
    {
    }

    internal DownloadPreflightService(
        Func<string, bool> directoryExists,
        Action<string> createDirectory,
        Func<string, bool> canWriteDirectory,
        Func<string, long?> getAvailableBytes)
    {
        _directoryExists = directoryExists;
        _createDirectory = createDirectory;
        _canWriteDirectory = canWriteDirectory;
        _getAvailableBytes = getAvailableBytes;
    }

    public DownloadPreflightResult Check(string? outputDirectory, long expectedBytes = 0)
    {
        var issues = new List<DownloadPreflightIssue>();
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            issues.Add(new(
                "output-directory-missing",
                DownloadPreflightSeverity.Blocking,
                "请选择下载目录。"));
            return new DownloadPreflightResult("", null, Math.Max(0, expectedBytes), issues);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(outputDirectory.Trim());
            if (!_directoryExists(fullPath))
                _createDirectory(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            issues.Add(new(
                "output-directory-invalid",
                DownloadPreflightSeverity.Blocking,
                $"下载目录不可用：{ex.Message}"));
            return new DownloadPreflightResult(outputDirectory.Trim(), null, Math.Max(0, expectedBytes), issues);
        }

        if (!_canWriteDirectory(fullPath))
        {
            issues.Add(new(
                "output-directory-readonly",
                DownloadPreflightSeverity.Blocking,
                "下载目录不可写，请更换目录或检查权限。"));
        }

        long? availableBytes = null;
        try
        {
            availableBytes = _getAvailableBytes(fullPath);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            issues.Add(new(
                "free-space-unknown",
                DownloadPreflightSeverity.Warning,
                $"无法读取目标磁盘剩余空间：{ex.Message}"));
        }

        var normalizedExpectedBytes = Math.Max(0, expectedBytes);
        if (availableBytes is >= 0)
        {
            var reserve = normalizedExpectedBytes > 0
                ? Math.Max(MinimumReserveBytes, normalizedExpectedBytes / 10)
                : MinimumReserveBytes;
            var required = SaturatingAdd(normalizedExpectedBytes, reserve);

            if (normalizedExpectedBytes > 0 && availableBytes < required)
            {
                issues.Add(new(
                    "insufficient-space",
                    DownloadPreflightSeverity.Blocking,
                    $"目标磁盘空间不足，预计至少需要 {ByteSizeFormatter.FormatClampZero(required)}，当前可用 {ByteSizeFormatter.FormatClampZero(availableBytes.Value)}。"));
            }
            else if (availableBytes < LowSpaceWarningBytes)
            {
                issues.Add(new(
                    "low-space",
                    DownloadPreflightSeverity.Warning,
                    $"目标磁盘剩余空间较少：{ByteSizeFormatter.FormatClampZero(availableBytes.Value)}。"));
            }
        }

        return new DownloadPreflightResult(
            fullPath,
            availableBytes,
            normalizedExpectedBytes,
            issues);
    }

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static bool CanWriteDirectory(string directory)
    {
        var probePath = Path.Combine(directory, $".easyget-write-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
            stream.WriteByte(0);
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch
            {
            }
        }
    }

    private static long? GetAvailableBytes(string directory)
    {
        var root = Path.GetPathRoot(directory);
        if (string.IsNullOrWhiteSpace(root))
            return null;

        return new DriveInfo(root).AvailableFreeSpace;
    }
}
