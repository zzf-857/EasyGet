using EasyGet.Models;
using EasyGet.Services;
using Microsoft.Data.Sqlite;
using System.Globalization;
using Xunit;

namespace EasyGet.Tests;

public class HistoryServiceTests
{
    [Fact]
    public async Task GetAllAsync_RoundTripsDownloadTimeAcrossCultureChanges()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var dbPath = CreateTempDatabasePath();
        var expected = new DateTime(2026, 6, 9, 12, 34, 56);

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            CultureInfo.CurrentUICulture = new CultureInfo("th-TH");

            using (var historyService = new HistoryService(dbPath))
            {
                await historyService.AddAsync(new DownloadHistory
                {
                    Url = "https://example.com/video",
                    Title = "culture-sensitive history",
                    Platform = "Example",
                    Format = "mp4",
                    Quality = "best",
                    FileSize = 1024,
                    FilePath = @"D:\Videos\example.mp4",
                    DownloadTime = expected
                });
            }

            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");

            using var readService = new HistoryService(dbPath);
            var history = Assert.Single(await readService.GetAllAsync());

            Assert.Equal(expected, history.DownloadTime);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task GetAllAsync_UsesUnknownTimeWhenStoredTimeIsMalformed()
    {
        var dbPath = CreateTempDatabasePath();

        try
        {
            using (new HistoryService(dbPath))
            {
            }

            await InsertHistoryRowAsync(dbPath, "not-a-date");

            using var readService = new HistoryService(dbPath);
            var history = Assert.Single(await readService.GetAllAsync());

            Assert.Equal(DateTime.MinValue, history.DownloadTime);
            Assert.Equal("--", history.DownloadTimeText);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public void DownloadTimeText_ShowsPlaceholderForUnknownTime()
    {
        var history = new DownloadHistory { DownloadTime = DateTime.MinValue };

        Assert.Equal("--", history.DownloadTimeText);
    }

    private static string CreateTempDatabasePath()
        => Path.Combine(
            Path.GetTempPath(),
            $"easyget-history-{Guid.NewGuid():N}.db");

    private static async Task InsertHistoryRowAsync(string dbPath, string downloadTime)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO download_history (url, title, platform, format, quality, file_size, file_path, download_time, thumbnail_url)
            VALUES ($url, $title, $platform, $format, $quality, $fileSize, $filePath, $downloadTime, $thumbnailUrl)
            """;
        cmd.Parameters.AddWithValue("$url", "https://example.com/video");
        cmd.Parameters.AddWithValue("$title", "malformed history");
        cmd.Parameters.AddWithValue("$platform", "Example");
        cmd.Parameters.AddWithValue("$format", "mp4");
        cmd.Parameters.AddWithValue("$quality", "best");
        cmd.Parameters.AddWithValue("$fileSize", 1024);
        cmd.Parameters.AddWithValue("$filePath", @"D:\Videos\example.mp4");
        cmd.Parameters.AddWithValue("$downloadTime", downloadTime);
        cmd.Parameters.AddWithValue("$thumbnailUrl", "");
        await cmd.ExecuteNonQueryAsync();
    }

    private static void TryDeleteDatabase(string dbPath)
    {
        foreach (var path in new[] { dbPath, $"{dbPath}-wal", $"{dbPath}-shm" })
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // 测试临时数据库清理失败不影响断言结果。
            }
        }
    }
}
