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

    [Theory]
    [InlineData("not-a-size")]
    [InlineData(-2048L)]
    public async Task GetAllAsync_UsesZeroWhenStoredFileSizeIsInvalid(object fileSize)
    {
        var dbPath = CreateTempDatabasePath();

        try
        {
            using (new HistoryService(dbPath))
            {
            }

            await InsertHistoryRowAsync(dbPath, "2026-06-09 12:34:56", fileSize);

            using var readService = new HistoryService(dbPath);
            var history = Assert.Single(await readService.GetAllAsync());

            Assert.Equal(0, history.FileSize);
            Assert.Equal("0 B", history.FileSizeText);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task GetAllAsync_UsesEmptyStringsWhenStoredTextFieldsAreNull()
    {
        var dbPath = CreateTempDatabasePath();

        try
        {
            await CreateLegacyNullableHistoryTableAsync(dbPath);
            await InsertHistoryRowAsync(
                dbPath,
                "2026-06-09 12:34:56",
                url: DBNull.Value,
                title: DBNull.Value,
                platform: DBNull.Value,
                format: DBNull.Value,
                quality: DBNull.Value,
                filePath: DBNull.Value,
                thumbnailUrl: DBNull.Value);

            using var readService = new HistoryService(dbPath);
            var history = Assert.Single(await readService.GetAllAsync());

            Assert.Equal("", history.Url);
            Assert.Equal("", history.Title);
            Assert.Equal("", history.Platform);
            Assert.Equal("", history.Format);
            Assert.Equal("", history.Quality);
            Assert.Equal("", history.FilePath);
            Assert.Equal("", history.ThumbnailUrl);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task AddAsync_NormalizesNullTextFieldsBeforeSaving()
    {
        var dbPath = CreateTempDatabasePath();

        try
        {
            using (var historyService = new HistoryService(dbPath))
            {
                await historyService.AddAsync(new DownloadHistory
                {
                    Url = null!,
                    Title = null!,
                    Platform = null!,
                    Format = null!,
                    Quality = null!,
                    FilePath = null!,
                    ThumbnailUrl = null!,
                    DownloadTime = new DateTime(2026, 6, 9, 12, 34, 56)
                });
            }

            using var readService = new HistoryService(dbPath);
            var history = Assert.Single(await readService.GetAllAsync());

            Assert.Equal("", history.Url);
            Assert.Equal("", history.Title);
            Assert.Equal("", history.Platform);
            Assert.Equal("", history.Format);
            Assert.Equal("", history.Quality);
            Assert.Equal("", history.FilePath);
            Assert.Equal("", history.ThumbnailUrl);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public void FileSizeText_ClampsNegativeBytesToZero()
    {
        var history = new DownloadHistory { FileSize = -2048 };

        Assert.Equal("0 B", history.FileSizeText);
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

    private static async Task InsertHistoryRowAsync(
        string dbPath,
        string downloadTime,
        object? fileSize = null,
        object? url = null,
        object? title = null,
        object? platform = null,
        object? format = null,
        object? quality = null,
        object? filePath = null,
        object? thumbnailUrl = null)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO download_history (url, title, platform, format, quality, file_size, file_path, download_time, thumbnail_url)
            VALUES ($url, $title, $platform, $format, $quality, $fileSize, $filePath, $downloadTime, $thumbnailUrl)
            """;
        cmd.Parameters.AddWithValue("$url", ToDbValue(url, "https://example.com/video"));
        cmd.Parameters.AddWithValue("$title", ToDbValue(title, "malformed history"));
        cmd.Parameters.AddWithValue("$platform", ToDbValue(platform, "Example"));
        cmd.Parameters.AddWithValue("$format", ToDbValue(format, "mp4"));
        cmd.Parameters.AddWithValue("$quality", ToDbValue(quality, "best"));
        cmd.Parameters.AddWithValue("$fileSize", fileSize ?? 1024);
        cmd.Parameters.AddWithValue("$filePath", ToDbValue(filePath, @"D:\Videos\example.mp4"));
        cmd.Parameters.AddWithValue("$downloadTime", downloadTime);
        cmd.Parameters.AddWithValue("$thumbnailUrl", ToDbValue(thumbnailUrl, ""));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateLegacyNullableHistoryTableAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE download_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                url TEXT,
                title TEXT,
                platform TEXT,
                format TEXT,
                quality TEXT,
                file_size INTEGER,
                file_path TEXT,
                download_time TEXT,
                thumbnail_url TEXT
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static object ToDbValue(object? value, object fallback)
        => value ?? fallback;

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
