using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyGet.Services;

/// <summary>
/// Creates a bounded, redacted support archive without reading application
/// configuration, browser sessions, cookies, or credential stores.
/// </summary>
public sealed class SupportBundleService
{
    private const int MaxLogFiles = 12;
    private const int MaxLogBytesPerFile = 256 * 1024;
    private const int MaxCrashSummaryCharacters = 1200;

    private static readonly Regex UrlUserInfoRegex = new(
        @"(?i)(https?://)[^/\s:@]+:[^/\s@]+@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UrlQueryRegex = new(
        @"(?i)(?<base>\bhttps?://[^\s\""'<>?#]+)\?[^\s\""'<>#]*(?:#[^\s\""'<>]*)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveLineRegex = new(
        @"(?im)(?<prefix>\b(?:cookie(?:content)?|set-cookie|authorization|proxy-authorization|auth(?:entication)?|password|passwd|secret|token|access[_-]?token|refresh[_-]?token|auth[_-]?token|api[_-]?(?:key|hash)|sessdata|bili_jct)\b\s*[:=]\s*).*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveArgumentRegex = new(
        @"(?i)(?<prefix>--cookies(?:-from-browser)?(?:=|\s+))(?:(?:\""[^\""\r\n]*\"")|(?:'[^'\r\n]*')|\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WindowsUserPathRegex = new(
        @"(?i)\b[A-Z]:[\\/]+Users[\\/]+[^\\/\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnixUserPathRegex = new(
        @"(?i)(?:/home|/Users)/[^/\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _configDirectory;
    private readonly IReadOnlyList<string> _logSources;
    private readonly string _outputDirectory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly string _userProfileDirectory;

    public SupportBundleService(
        string configDirectory,
        IEnumerable<string>? logSources = null,
        string? outputDirectory = null,
        Func<DateTimeOffset>? utcNow = null,
        string? userProfileDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
            throw new ArgumentException("A configuration directory is required.", nameof(configDirectory));

        _configDirectory = Path.GetFullPath(configDirectory);
        _logSources = (logSources ?? [])
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(Path.GetFullPath)
            .Distinct(GetPathComparer())
            .ToArray();
        _outputDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(_configDirectory, "support-bundles")
                : outputDirectory);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _userProfileDirectory = userProfileDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public async Task<string> CreateAsync(
        string? applicationVersion,
        IReadOnlyDictionary<string, string>? environmentVersions = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_outputDirectory);

        var warnings = new List<string>();
        var candidates = DiscoverLogCandidates(warnings)
            .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
            .ThenBy(candidate => candidate.FileName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxLogFiles)
            .ToList();
        var collectedLogs = new List<CollectedLog>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var readResult = await ReadLogTailAsync(candidate.Path, cancellationToken);
                var redacted = RedactSensitiveText(readResult.Content, _userProfileDirectory);
                collectedLogs.Add(new CollectedLog(
                    candidate.FileName,
                    candidate.LastWriteTimeUtc,
                    candidate.Length,
                    redacted,
                    readResult.WasTruncated,
                    candidate.IsCrashLog));
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or System.Security.SecurityException)
            {
                warnings.Add($"{candidate.FileName}: skipped ({ex.GetType().Name})");
            }
        }

        var createdAt = _utcNow();
        var finalPath = GetAvailableBundlePath(createdAt);
        var temporaryPath = finalPath + $".tmp-{Guid.NewGuid():N}";

        try
        {
            await using (var file = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             81920,
                             useAsync: true))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteTextEntry(
                    archive,
                    "summary.txt",
                    BuildSummary(
                        applicationVersion,
                        environmentVersions,
                        createdAt,
                        collectedLogs.Count,
                        warnings.Count));
                WriteTextEntry(archive, "logs/index.txt", BuildLogIndex(collectedLogs, warnings));

                for (var index = 0; index < collectedLogs.Count; index++)
                {
                    var log = collectedLogs[index];
                    var entryName = $"logs/{index + 1:D2}-{SanitizeEntryFileName(log.FileName)}";
                    WriteTextEntry(archive, entryName, log.Content);
                }

                WriteTextEntry(archive, "crash-summary.txt", BuildCrashSummary(collectedLogs));
            }

            File.Move(temporaryPath, finalPath);
            return finalPath;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public static string RedactSensitiveText(string? text, string? userProfileDirectory = null)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var redacted = RemoveUnsafeControlCharacters(text);
        redacted = UrlUserInfoRegex.Replace(redacted, "$1<redacted>@");
        redacted = UrlQueryRegex.Replace(redacted, "${base}?<redacted>");
        redacted = SensitiveArgumentRegex.Replace(redacted, "${prefix}<redacted>");
        redacted = SensitiveLineRegex.Replace(redacted, "${prefix}<redacted>");

        if (!string.IsNullOrWhiteSpace(userProfileDirectory))
        {
            var normalizedProfile = userProfileDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (normalizedProfile.Length > 0)
            {
                redacted = redacted.Replace(
                    normalizedProfile,
                    "%USERPROFILE%",
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        redacted = WindowsUserPathRegex.Replace(redacted, "%USERPROFILE%");
        redacted = UnixUserPathRegex.Replace(redacted, "~");
        return redacted;
    }

    private IReadOnlyList<LogCandidate> DiscoverLogCandidates(ICollection<string> warnings)
    {
        var paths = new HashSet<string>(GetPathComparer());
        var sources = new List<string> { Path.Combine(_configDirectory, "logs") };
        sources.AddRange(_logSources);

        foreach (var source in sources.Distinct(GetPathComparer()))
        {
            try
            {
                if (File.Exists(source))
                {
                    if (IsAllowedLogFile(source))
                        paths.Add(Path.GetFullPath(source));
                    continue;
                }

                if (!Directory.Exists(source))
                    continue;

                foreach (var file in Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly))
                {
                    if (IsAllowedLogFile(file))
                        paths.Add(Path.GetFullPath(file));
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or System.Security.SecurityException)
            {
                warnings.Add($"{GetSafeSourceName(source)}: unavailable ({ex.GetType().Name})");
            }
        }

        var candidates = new List<LogCandidate>();
        foreach (var path in paths)
        {
            try
            {
                var file = new FileInfo(path);
                candidates.Add(new LogCandidate(
                    path,
                    file.Name,
                    file.LastWriteTimeUtc,
                    Math.Max(0, file.Length),
                    file.Name.Contains("crash", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or System.Security.SecurityException)
            {
                warnings.Add($"{Path.GetFileName(path)}: unavailable ({ex.GetType().Name})");
            }
        }

        return candidates;
    }

    private string BuildSummary(
        string? applicationVersion,
        IReadOnlyDictionary<string, string>? environmentVersions,
        DateTimeOffset createdAt,
        int collectedLogCount,
        int warningCount)
    {
        var builder = new StringBuilder()
            .AppendLine("EasyGet support bundle")
            .Append("CreatedUtc: ").AppendLine(createdAt.ToUniversalTime().ToString("O"))
            .Append("ApplicationVersion: ").AppendLine(RedactSensitiveText(
                string.IsNullOrWhiteSpace(applicationVersion) ? "unknown" : applicationVersion,
                _userProfileDirectory))
            .Append("OS: ").AppendLine(RedactSensitiveText(
                RuntimeInformation.OSDescription,
                _userProfileDirectory))
            .Append("Framework: ").AppendLine(RedactSensitiveText(
                RuntimeInformation.FrameworkDescription,
                _userProfileDirectory))
            .Append("OSArchitecture: ").AppendLine(RuntimeInformation.OSArchitecture.ToString())
            .Append("ProcessArchitecture: ").AppendLine(RuntimeInformation.ProcessArchitecture.ToString())
            .Append("ConfigDirectory: ").AppendLine(RedactSensitiveText(
                _configDirectory,
                _userProfileDirectory))
            .Append("CollectedLogs: ").AppendLine(collectedLogCount.ToString())
            .Append("CollectionWarnings: ").AppendLine(warningCount.ToString())
            .AppendLine()
            .AppendLine("Privacy: configuration files, cookies, browser sessions, credentials, and databases are not collected.")
            .AppendLine("Log content is bounded and redacted before it is written to this archive.");

        if (environmentVersions is { Count: > 0 })
        {
            builder.AppendLine().AppendLine("Components:");
            foreach (var component in environmentVersions.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("- ")
                    .Append(RedactSensitiveText(component.Key, _userProfileDirectory))
                    .Append(": ")
                    .AppendLine(RedactSensitiveText(component.Value, _userProfileDirectory));
            }
        }

        return builder.ToString();
    }

    private static string BuildLogIndex(
        IReadOnlyList<CollectedLog> logs,
        IReadOnlyList<string> warnings)
    {
        var builder = new StringBuilder();
        foreach (var log in logs)
        {
            builder.Append(log.FileName)
                .Append(" | modifiedUtc=")
                .Append(log.LastWriteTimeUtc.ToUniversalTime().ToString("O"))
                .Append(" | bytes=")
                .Append(log.OriginalLength)
                .Append(" | truncated=")
                .AppendLine(log.WasTruncated.ToString());
        }

        foreach (var warning in warnings)
            builder.Append("warning | ").AppendLine(warning);

        if (builder.Length == 0)
            builder.AppendLine("No readable log files were found.");
        return builder.ToString();
    }

    private static string BuildCrashSummary(IReadOnlyList<CollectedLog> logs)
    {
        var crashes = logs.Where(log => log.IsCrashLog).ToList();
        if (crashes.Count == 0)
            return "No recent crash logs were found." + Environment.NewLine;

        var builder = new StringBuilder();
        foreach (var crash in crashes)
        {
            builder.Append("## ").AppendLine(crash.FileName);
            var excerpt = string.Join(
                Environment.NewLine,
                crash.Content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .Take(8));
            if (excerpt.Length > MaxCrashSummaryCharacters)
                excerpt = excerpt[..MaxCrashSummaryCharacters] + "...";
            builder.AppendLine(excerpt).AppendLine();
        }

        return builder.ToString();
    }

    private static async Task<LogReadResult> ReadLogTailAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            useAsync: true);
        var wasTruncated = stream.Length > MaxLogBytesPerFile;
        if (wasTruncated)
            stream.Seek(-MaxLogBytesPerFile, SeekOrigin.End);

        var length = (int)Math.Min(stream.Length, MaxLogBytesPerFile);
        var buffer = new byte[length];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
                break;
            offset += read;
        }

        var content = Encoding.UTF8.GetString(buffer, 0, offset);
        if (wasTruncated)
            content = $"[truncated to the most recent {MaxLogBytesPerFile} bytes]{Environment.NewLine}{content}";
        return new LogReadResult(content, wasTruncated);
    }

    private static void WriteTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private string GetAvailableBundlePath(DateTimeOffset createdAt)
    {
        var baseName = $"EasyGet-support-{createdAt:yyyyMMdd-HHmmss}";
        for (var suffix = 0; suffix < 1000; suffix++)
        {
            var fileName = suffix == 0 ? $"{baseName}.zip" : $"{baseName}-{suffix}.zip";
            var candidate = Path.Combine(_outputDirectory, fileName);
            if (!File.Exists(candidate) && !File.Exists(candidate + ".tmp"))
                return candidate;
        }

        throw new IOException("Unable to allocate a unique support bundle file name.");
    }

    private static bool IsAllowedLogFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeEntryFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(fileName
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "log.txt" : sanitized;
    }

    private static string GetSafeSourceName(string source)
    {
        var trimmed = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : "log source";
    }

    private static string RemoveUnsafeControlCharacters(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character is '\r' or '\n' or '\t' || !char.IsControl(character)
                ? character
                : '\uFFFD');
        }
        return builder.ToString();
    }

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed record LogCandidate(
        string Path,
        string FileName,
        DateTime LastWriteTimeUtc,
        long Length,
        bool IsCrashLog);

    private sealed record CollectedLog(
        string FileName,
        DateTime LastWriteTimeUtc,
        long OriginalLength,
        string Content,
        bool WasTruncated,
        bool IsCrashLog);

    private sealed record LogReadResult(string Content, bool WasTruncated);
}
