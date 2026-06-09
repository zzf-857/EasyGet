using EasyGet.Models;
using EasyGet.Services;
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
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            $"easyget-history-{Guid.NewGuid():N}.db");
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
