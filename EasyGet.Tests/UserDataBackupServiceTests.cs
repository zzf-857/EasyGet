using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public sealed class UserDataBackupServiceTests
{
    [Fact]
    public async Task CreateBackupAsync_ContainsOnlyValidatedHistoryAndSafeSettings()
    {
        using var root = new TestDirectory();
        var paths = CreatePaths(root, "source");
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFilePath)!);
        await File.WriteAllTextAsync(paths.SettingsFilePath, """
            {
              "configVersion": 3,
              "defaultFormat": "mkv",
              "collectionDirectories": ["D:\\Collections\\RAG"],
              "selectedCollectionDirectory": "D:\\Collections\\RAG",
              "globalDownloadRateLimitKilobytesPerSecond": 2048,
              "themeColor": "Rose",
              "cookieContent": "COOKIE-SECRET-123",
              "legacyCookiePlatform": "bilibili",
              "tgApiId": "123456",
              "tgApiHash": "TG-HASH-SECRET",
              "tgPhoneNumber": "+8613800000000",
              "proxyAddress": "http://user:password@example.test:8080"
            }
            """);
        Directory.CreateDirectory(root.Path("source", "sessions"));
        await File.WriteAllTextAsync(root.Path("source", "sessions", "telegram.session"), "SESSION-SECRET");

        using (var history = new HistoryService(paths.HistoryDatabasePath))
        {
            await history.AddAsync(new DownloadHistory
            {
                Url = "https://example.test/watch?v=content-1",
                Title = "First item",
                FilePath = root.Path("downloads", "first.mp4")
            });
        }

        var service = new UserDataBackupService(
            paths,
            () => new DateTimeOffset(2026, 7, 29, 1, 2, 3, TimeSpan.Zero));
        var archivePath = root.Path("exports", "easyget.zip");
        var preview = await service.CreateBackupAsync(archivePath);

        Assert.Equal(1, preview.HistoryRecordCount);
        Assert.Contains("defaultFormat", preview.IncludedSettingNames);
        Assert.Contains("collectionDirectories", preview.IncludedSettingNames);
        Assert.Contains("selectedCollectionDirectory", preview.IncludedSettingNames);
        Assert.Contains("globalDownloadRateLimitKilobytesPerSecond", preview.IncludedSettingNames);
        Assert.Contains("themeColor", preview.IncludedSettingNames);
        Assert.DoesNotContain("cookieContent", preview.IncludedSettingNames);
        Assert.DoesNotContain("tgApiHash", preview.IncludedSettingNames);
        Assert.NotEmpty(preview.ExplicitlyExcludedData);

        using var archive = ZipFile.OpenRead(archivePath);
        Assert.Equal(
            new[]
            {
                UserDataBackupService.HistoryEntryName,
                UserDataBackupService.ManifestEntryName,
                UserDataBackupService.SettingsEntryName
            },
            archive.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToArray());
        var settingsText = await ReadEntryTextAsync(
            archive.GetEntry(UserDataBackupService.SettingsEntryName)!);
        Assert.Contains("\"defaultFormat\": \"mkv\"", settingsText, StringComparison.Ordinal);
        Assert.Contains("\"collectionDirectories\"", settingsText, StringComparison.Ordinal);
        Assert.Contains("\"selectedCollectionDirectory\"", settingsText, StringComparison.Ordinal);
        Assert.Contains("\"globalDownloadRateLimitKilobytesPerSecond\": 2048", settingsText, StringComparison.Ordinal);
        Assert.DoesNotContain("COOKIE-SECRET-123", settingsText, StringComparison.Ordinal);
        Assert.DoesNotContain("cookieContent", settingsText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TG-HASH-SECRET", settingsText, StringComparison.Ordinal);
        Assert.DoesNotContain("tgApi", settingsText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("proxyAddress", settingsText, StringComparison.OrdinalIgnoreCase);

        var validation = await service.ValidateBackupAsync(archivePath);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
    }

    [Fact]
    public async Task ValidateBackupAsync_RejectsUnexpectedEntries()
    {
        using var root = new TestDirectory();
        var paths = CreatePaths(root, "source");
        await SeedHistoryAsync(paths.HistoryDatabasePath, "https://example.test/item/1", "One");
        await WriteSettingsAsync(paths.SettingsFilePath, "Blue", "source-cookie", "source-hash");
        var service = new UserDataBackupService(paths);
        var archivePath = root.Path("backup.zip");
        await service.CreateBackupAsync(archivePath);

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
        {
            var unexpected = archive.CreateEntry("sessions/login.dat");
            await using var unexpectedStream = unexpected.Open();
            await unexpectedStream.WriteAsync(Encoding.UTF8.GetBytes("must-not-restore"));
        }

        var validation = await service.ValidateBackupAsync(archivePath);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error =>
            error.Contains("unexpected entry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateBackupAsync_RejectsChecksumChanges()
    {
        using var root = new TestDirectory();
        var paths = CreatePaths(root, "source");
        await SeedHistoryAsync(paths.HistoryDatabasePath, "https://example.test/item/1", "One");
        await WriteSettingsAsync(paths.SettingsFilePath, "Blue", "source-cookie", "source-hash");
        var service = new UserDataBackupService(paths);
        var archivePath = root.Path("backup.zip");
        await service.CreateBackupAsync(archivePath);

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
        {
            archive.GetEntry(UserDataBackupService.SettingsEntryName)!.Delete();
            var replacement = archive.CreateEntry(UserDataBackupService.SettingsEntryName);
            await using var stream = replacement.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"themeColor\":\"Amber\"}"));
        }

        var validation = await service.ValidateBackupAsync(archivePath);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error =>
            error.Contains("checksum", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RestoreBackupAsync_RestoresHistoryAndSafeSettingsWhilePreservingLocalSecrets()
    {
        using var root = new TestDirectory();
        var sourcePaths = CreatePaths(root, "source");
        await SeedHistoryAsync(
            sourcePaths.HistoryDatabasePath,
            "https://example.test/source",
            "Source history");
        await WriteSettingsAsync(sourcePaths.SettingsFilePath, "Rose", "source-cookie", "source-hash");
        var sourceService = new UserDataBackupService(sourcePaths);
        var archivePath = root.Path("transfer", "backup.zip");
        await sourceService.CreateBackupAsync(archivePath);

        var targetPaths = CreatePaths(root, "target");
        await SeedHistoryAsync(
            targetPaths.HistoryDatabasePath,
            "https://example.test/target",
            "Target history");
        await WriteSettingsAsync(targetPaths.SettingsFilePath, "Blue", "target-cookie", "target-hash");
        var targetService = new UserDataBackupService(
            targetPaths,
            () => new DateTimeOffset(2026, 7, 29, 2, 3, 4, TimeSpan.Zero));

        var restored = await targetService.RestoreBackupAsync(archivePath);

        Assert.NotNull(restored.SafetyBackupPath);
        Assert.True(File.Exists(restored.SafetyBackupPath));
        var safetyPreview = await targetService.PreviewBackupAsync(restored.SafetyBackupPath!);
        Assert.Equal(1, safetyPreview.HistoryRecordCount);

        using (var restoredHistory = new HistoryService(targetPaths.HistoryDatabasePath))
        {
            var item = Assert.Single(await restoredHistory.GetAllAsync());
            Assert.Equal("Source history", item.Title);
        }

        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(targetPaths.SettingsFilePath));
        Assert.Equal("Rose", settings.RootElement.GetProperty("themeColor").GetString());
        Assert.Equal("target-cookie", settings.RootElement.GetProperty("cookieContent").GetString());
        Assert.Equal("target-hash", settings.RootElement.GetProperty("tgApiHash").GetString());
        Assert.DoesNotContain(
            Directory.EnumerateFiles(root.Path("target"), "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path).Contains(".restore-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoreBackupAsync_DoesNotOverwriteWhenArchiveValidationFails()
    {
        using var root = new TestDirectory();
        var sourcePaths = CreatePaths(root, "source");
        await SeedHistoryAsync(sourcePaths.HistoryDatabasePath, "https://example.test/source", "Source");
        await WriteSettingsAsync(sourcePaths.SettingsFilePath, "Rose", "source-cookie", "source-hash");
        var archivePath = root.Path("invalid.zip");
        await new UserDataBackupService(sourcePaths).CreateBackupAsync(archivePath);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
        {
            var entry = archive.CreateEntry("cookies.txt");
            await using var stream = entry.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("secret"));
        }

        var targetPaths = CreatePaths(root, "target");
        await SeedHistoryAsync(targetPaths.HistoryDatabasePath, "https://example.test/target", "Keep me");
        await WriteSettingsAsync(targetPaths.SettingsFilePath, "Blue", "target-cookie", "target-hash");
        var targetService = new UserDataBackupService(targetPaths);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            targetService.RestoreBackupAsync(archivePath));

        using var history = new HistoryService(targetPaths.HistoryDatabasePath);
        Assert.Equal("Keep me", Assert.Single(await history.GetAllAsync()).Title);
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(targetPaths.SettingsFilePath));
        Assert.Equal("Blue", settings.RootElement.GetProperty("themeColor").GetString());
        Assert.Equal("target-cookie", settings.RootElement.GetProperty("cookieContent").GetString());
        var safetyBackups = Directory.Exists(targetPaths.SafetyBackupDirectory)
            ? Directory.EnumerateFiles(targetPaths.SafetyBackupDirectory)
            : Enumerable.Empty<string>();
        Assert.Empty(safetyBackups);
    }

    private static UserDataBackupPaths CreatePaths(TestDirectory root, string name)
    {
        var configRoot = root.Path(name);
        return new UserDataBackupPaths(
            Path.Combine(configRoot, "history.db"),
            Path.Combine(configRoot, "config.json"),
            Path.Combine(configRoot, "backups"),
            root.Path("work", name));
    }

    private static async Task SeedHistoryAsync(string path, string url, string title)
    {
        using var history = new HistoryService(path);
        await history.AddAsync(new DownloadHistory
        {
            Url = url,
            Title = title,
            FilePath = Path.ChangeExtension(path, ".mp4")
        });
    }

    private static async Task WriteSettingsAsync(
        string path,
        string theme,
        string cookie,
        string telegramHash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, $$"""
            {
              "configVersion": 3,
              "defaultFormat": "mp4",
              "themeColor": "{{theme}}",
              "cookieContent": "{{cookie}}",
              "tgApiId": "42",
              "tgApiHash": "{{telegramHash}}",
              "tgPhoneNumber": "+8613800000000"
            }
            """);
    }

    private static async Task<string> ReadEntryTextAsync(ZipArchiveEntry entry)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
