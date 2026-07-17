using EasyGet.Models;
using EasyGet.Services;
using Microsoft.Data.Sqlite;
using System.Globalization;
using Xunit;

namespace EasyGet.Tests;

public class HistoryServiceTests
{
    [Fact]
    public async Task BatchFields_RoundTripSearchAndDeleteAsOneGroup()
    {
        var dbPath = CreateTempDatabasePath();

        try
        {
            using var service = new HistoryService(dbPath);
            for (var index = 1; index <= 2; index++)
            {
                await service.AddAsync(new DownloadHistory
                {
                    Url = $"https://example.com/video/{index}",
                    Title = $"part {index}",
                    Platform = "Example",
                    Format = "mp4",
                    Quality = "best",
                    FilePath = $@"D:\Videos\batch\{index}.mp4",
                    BatchId = "batch-001",
                    BatchName = "测试合集 · 2026-07-17 12:00",
                    BatchDirectory = @"D:\Videos\batch",
                    DownloadTime = new DateTime(2026, 7, 17, 12, 0, index)
                });
            }

            var saved = await service.GetAllAsync("测试合集");
            Assert.Equal(2, saved.Count);
            Assert.All(saved, item =>
            {
                Assert.Equal("batch-001", item.BatchId);
                Assert.Equal("测试合集 · 2026-07-17 12:00", item.BatchName);
                Assert.Equal(@"D:\Videos\batch", item.BatchDirectory);
            });

            await service.DeleteBatchAsync("batch-001");
            Assert.Empty(await service.GetAllAsync());
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

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
            Assert.Equal("", history.BatchId);
            Assert.Equal("", history.BatchName);
            Assert.Equal("", history.BatchDirectory);
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
    public async Task GetAllAsync_ReadsHistoryByColumnNameWhenTableColumnOrderDiffers()
    {
        var dbPath = CreateTempDatabasePath();

        try
        {
            await CreateShuffledHistoryTableAsync(dbPath);
            await InsertHistoryRowAsync(
                dbPath,
                "2026-06-09 12:34:56",
                url: "https://example.com/shuffled",
                title: "shuffled title",
                platform: "Example",
                format: "mkv",
                quality: "1080",
                fileSize: 4096L,
                filePath: @"D:\Videos\shuffled.mkv",
                thumbnailUrl: "https://example.com/thumb.jpg");

            using var readService = new HistoryService(dbPath);
            var history = Assert.Single(await readService.GetAllAsync());

            Assert.Equal("https://example.com/shuffled", history.Url);
            Assert.Equal("shuffled title", history.Title);
            Assert.Equal("Example", history.Platform);
            Assert.Equal("mkv", history.Format);
            Assert.Equal("1080", history.Quality);
            Assert.Equal(4096, history.FileSize);
            Assert.Equal(@"D:\Videos\shuffled.mkv", history.FilePath);
            Assert.Equal(new DateTime(2026, 6, 9, 12, 34, 56), history.DownloadTime);
            Assert.Equal("https://example.com/thumb.jpg", history.ThumbnailUrl);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task ConstructorCreatesIndexForDescendingHistoryReads()
    {
        var dbPath = CreateTempDatabasePath();

        try
        {
            using (new HistoryService(dbPath))
            {
            }

            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT sql
                FROM sqlite_master
                WHERE type = 'index'
                  AND name = 'idx_download_history_download_time_desc'
                """;

            var indexSql = Assert.IsType<string>(await cmd.ExecuteScalarAsync());
            Assert.Contains("download_time DESC", indexSql, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task AddAsync_RoundTripsAttachmentFilePaths()
    {
        var dbPath = CreateTempDatabasePath();
        var attachments = new[]
        {
            @"D:\Videos\comments.json",
            @"D:\Videos\metadata.json"
        };

        try
        {
            using (var historyService = new HistoryService(dbPath))
            {
                var history = new DownloadHistory
                {
                    Url = "https://example.com/video",
                    Title = "with attachments",
                    Platform = "Douyin",
                    Format = "mp4",
                    Quality = "best",
                    FileSize = 2048,
                    FilePath = @"D:\Videos\video.mp4",
                    DownloadTime = new DateTime(2026, 6, 9, 12, 34, 56)
                };
                SetStringListProperty(history, "AttachmentFilePaths", attachments);

                await historyService.AddAsync(history);
            }

            using var readService = new HistoryService(dbPath);
            var saved = Assert.Single(await readService.GetAllAsync());

            Assert.Equal(attachments, GetStringListProperty(saved, "AttachmentFilePaths"));
            Assert.True(GetBoolProperty(saved, "HasAttachmentFiles"));
            Assert.Equal("附属 2", GetStringProperty(saved, "AttachmentCountText"));
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task GetAllAsync_LegacyHistoryWithoutAttachmentColumnReadsEmptyAttachmentFilePaths()
    {
        var dbPath = CreateTempDatabasePath();

        try
        {
            await CreateLegacyNullableHistoryTableAsync(dbPath);
            await InsertHistoryRowAsync(dbPath, "2026-06-09 12:34:56");

            using var readService = new HistoryService(dbPath);
            var history = Assert.Single(await readService.GetAllAsync());

            Assert.Empty(GetStringListProperty(history, "AttachmentFilePaths"));
            Assert.False(GetBoolProperty(history, "HasAttachmentFiles"));
            Assert.Equal("", GetStringProperty(history, "AttachmentCountText"));
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task GetAllAsync_MalformedAttachmentJsonReadsEmptyAttachmentFilePaths()
    {
        var dbPath = CreateTempDatabasePath();

        try
        {
            await CreateHistoryTableWithAttachmentColumnAsync(dbPath);
            await InsertMalformedAttachmentRowAsync(dbPath);

            using var readService = new HistoryService(dbPath);
            var history = Assert.Single(await readService.GetAllAsync());

            Assert.Empty(GetStringListProperty(history, "AttachmentFilePaths"));
            Assert.False(GetBoolProperty(history, "HasAttachmentFiles"));
            Assert.Equal("", GetStringProperty(history, "AttachmentCountText"));
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
        => TestTempPaths.CreateSqliteDatabasePath("easyget-history");

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

    private static async Task InsertMalformedAttachmentRowAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO download_history (url, title, platform, format, quality, file_size, file_path, download_time, thumbnail_url, attachment_file_paths)
            VALUES ('https://example.com/video', 'malformed attachments', 'Douyin', 'mp4', 'best', 1024, 'D:\Videos\example.mp4', '2026-06-09 12:34:56', '', 'not-json')
            """;
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

    private static async Task CreateHistoryTableWithAttachmentColumnAsync(string dbPath)
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
                thumbnail_url TEXT,
                attachment_file_paths TEXT
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateShuffledHistoryTableAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE download_history (
                title TEXT,
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                thumbnail_url TEXT,
                download_time TEXT,
                file_path TEXT,
                file_size INTEGER,
                quality TEXT,
                format TEXT,
                platform TEXT,
                url TEXT
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static object ToDbValue(object? value, object fallback)
        => value ?? fallback;

    private static IReadOnlyList<string> GetStringListProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        var value = property!.GetValue(instance);
        var strings = Assert.IsAssignableFrom<IEnumerable<string>>(value);
        return strings.ToList();
    }

    private static void SetStringListProperty(object instance, string propertyName, IEnumerable<string> values)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);

        if (property!.CanWrite)
        {
            property.SetValue(instance, values.ToList());
            return;
        }

        var currentValue = property.GetValue(instance);
        var collection = Assert.IsAssignableFrom<ICollection<string>>(currentValue);
        collection.Clear();
        foreach (var value in values)
            collection.Add(value);
    }

    private static string GetStringProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<string>(property!.GetValue(instance));
    }

    private static bool GetBoolProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<bool>(property!.GetValue(instance));
    }

    private static void TryDeleteDatabase(string dbPath)
        => TestTempPaths.TryDeleteSqliteDatabase(dbPath);
}
