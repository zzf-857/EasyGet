using System.IO;
using System.IO.Compression;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public sealed class SupportBundleServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"EasyGet-support-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateAsync_WritesVersionsAndRedactsSensitiveLogContent()
    {
        var configDirectory = CreateDirectory("config");
        var logsDirectory = Directory.CreateDirectory(Path.Combine(configDirectory, "logs")).FullName;
        var outputDirectory = CreateDirectory("output");
        var profile = Path.Combine("C:\\Users", "SupportUser");
        File.WriteAllText(
            Path.Combine(logsDirectory, "download.log"),
            string.Join(Environment.NewLine,
                "GET https://alice:password@example.com/video?id=42&token=query-secret",
                "Cookie: SESSDATA=cookie-secret",
                "Authorization: Bearer auth-secret",
                "token=token-secret",
                "password: password-secret",
                "--cookies cookies.txt",
                $"Output: {profile}\\Downloads\\video.mp4",
                "normal log line"));

        var service = new SupportBundleService(
            configDirectory,
            outputDirectory: outputDirectory,
            utcNow: () => new DateTimeOffset(2026, 7, 29, 10, 11, 12, TimeSpan.Zero),
            userProfileDirectory: profile);

        var bundlePath = await service.CreateAsync(
            "1.2.3",
            new Dictionary<string, string> { ["yt-dlp"] = "2026.07.29" });

        using var archive = ZipFile.OpenRead(bundlePath);
        var summary = ReadEntry(archive, "summary.txt");
        var log = ReadOnlyLogEntry(archive);

        Assert.Contains("ApplicationVersion: 1.2.3", summary, StringComparison.Ordinal);
        Assert.Contains("yt-dlp: 2026.07.29", summary, StringComparison.Ordinal);
        Assert.Contains("OS:", summary, StringComparison.Ordinal);
        Assert.Contains("Framework:", summary, StringComparison.Ordinal);
        Assert.Contains("normal log line", log, StringComparison.Ordinal);
        Assert.Contains("<redacted>", log, StringComparison.Ordinal);
        Assert.Contains("%USERPROFILE%", log, StringComparison.Ordinal);
        Assert.DoesNotContain("password@example.com", log, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("cookie-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("auth-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("token-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("password-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("cookies.txt", log, StringComparison.Ordinal);
        Assert.DoesNotContain("SupportUser", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_NeverCollectsConfigurationOrDatabaseFiles()
    {
        var configDirectory = CreateDirectory("config");
        var logsDirectory = Directory.CreateDirectory(Path.Combine(configDirectory, "logs")).FullName;
        File.WriteAllText(Path.Combine(configDirectory, "config.json"), "{\"token\":\"config-secret\"}");
        File.WriteAllText(Path.Combine(configDirectory, "downloads.db"), "database-secret");
        File.WriteAllText(Path.Combine(logsDirectory, "valid.log"), "valid-log-content");
        File.WriteAllText(Path.Combine(logsDirectory, "misleading.json"), "json-secret");

        var service = new SupportBundleService(
            configDirectory,
            outputDirectory: CreateDirectory("output"));

        var bundlePath = await service.CreateAsync("1.0");

        using var archive = ZipFile.OpenRead(bundlePath);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        var allText = string.Join(Environment.NewLine, archive.Entries.Select(ReadEntry));
        Assert.DoesNotContain(names, name => name.Contains("config.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("downloads.db", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("misleading.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("config-secret", allText, StringComparison.Ordinal);
        Assert.DoesNotContain("database-secret", allText, StringComparison.Ordinal);
        Assert.DoesNotContain("json-secret", allText, StringComparison.Ordinal);
        Assert.Contains("valid-log-content", allText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_GeneratesRedactedCrashSummary()
    {
        var configDirectory = CreateDirectory("config");
        var logsDirectory = Directory.CreateDirectory(Path.Combine(configDirectory, "logs")).FullName;
        File.WriteAllText(
            Path.Combine(logsDirectory, "app-crash.log"),
            "fatal error" + Environment.NewLine + "Authorization: Bearer crash-secret");

        var service = new SupportBundleService(
            configDirectory,
            outputDirectory: CreateDirectory("output"));

        var bundlePath = await service.CreateAsync("1.0");

        using var archive = ZipFile.OpenRead(bundlePath);
        var crashSummary = ReadEntry(archive, "crash-summary.txt");
        Assert.Contains("app-crash.log", crashSummary, StringComparison.Ordinal);
        Assert.Contains("fatal error", crashSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("crash-secret", crashSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_SkipsUnreadableLogAndKeepsReadableLogs()
    {
        var configDirectory = CreateDirectory("config");
        var logsDirectory = Directory.CreateDirectory(Path.Combine(configDirectory, "logs")).FullName;
        var lockedPath = Path.Combine(logsDirectory, "locked.log");
        File.WriteAllText(lockedPath, "locked-secret");
        File.WriteAllText(Path.Combine(logsDirectory, "valid.log"), "valid-log-content");

        using var lockStream = new FileStream(
            lockedPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var service = new SupportBundleService(
            configDirectory,
            outputDirectory: CreateDirectory("output"));

        var bundlePath = await service.CreateAsync("1.0");

        using var archive = ZipFile.OpenRead(bundlePath);
        var allText = string.Join(Environment.NewLine, archive.Entries.Select(ReadEntry));
        Assert.Contains("valid-log-content", allText, StringComparison.Ordinal);
        Assert.DoesNotContain("locked-secret", allText, StringComparison.Ordinal);
        Assert.Contains("locked.log: skipped", ReadEntry(archive, "logs/index.txt"), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string CreateDirectory(string name)
        => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static string ReadOnlyLogEntry(ZipArchive archive)
    {
        var entry = Assert.Single(archive.Entries,
            candidate => candidate.FullName.StartsWith("logs/", StringComparison.Ordinal)
                         && !candidate.FullName.EndsWith("index.txt", StringComparison.Ordinal));
        return ReadEntry(entry);
    }

    private static string ReadEntry(ZipArchive archive, string name)
        => ReadEntry(Assert.Single(archive.Entries, entry => entry.FullName == name));

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
