using EasyGet.Models;
using EasyGet.Services;
using EasyGet.ViewModels;
using Xunit;

namespace EasyGet.Tests;

public class HistoryViewModelTests
{
    [Fact]
    public async Task LoadHistory_FiltersAudioItemsWhenAudioFilterIsSelected()
    {
        var dbPath = CreateTempDatabasePath();

        try
        {
            using (var service = new HistoryService(dbPath))
            {
                await service.AddAsync(new DownloadHistory
                {
                    Url = "https://example.com/video",
                    Title = "video item",
                    Format = "mp4",
                    DownloadTime = new DateTime(2026, 6, 9, 10, 0, 0)
                });
                await service.AddAsync(new DownloadHistory
                {
                    Url = "https://example.com/audio",
                    Title = "audio item",
                    Format = "mp3",
                    DownloadTime = new DateTime(2026, 6, 9, 11, 0, 0)
                });

                var viewModel = new HistoryViewModel(service)
                {
                    SelectedMediaFilter = "音频"
                };

                await viewModel.LoadHistory();

                var item = Assert.Single(viewModel.HistoryItems);
                Assert.Equal("audio item", item.Title);
                Assert.Equal(2, viewModel.TotalHistoryCount);
            }
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task LoadHistory_FiltersVideoItemsWhenVideoFilterIsSelected()
    {
        var dbPath = CreateTempDatabasePath();

        try
        {
            using (var service = new HistoryService(dbPath))
            {
                await service.AddAsync(new DownloadHistory
                {
                    Url = "https://example.com/video",
                    Title = "video item",
                    Format = "mkv",
                    DownloadTime = new DateTime(2026, 6, 9, 10, 0, 0)
                });
                await service.AddAsync(new DownloadHistory
                {
                    Url = "https://example.com/audio",
                    Title = "audio item",
                    Format = "m4a",
                    DownloadTime = new DateTime(2026, 6, 9, 11, 0, 0)
                });

                var viewModel = new HistoryViewModel(service)
                {
                    SelectedMediaFilter = "视频"
                };

                await viewModel.LoadHistory();

                var item = Assert.Single(viewModel.HistoryItems);
                Assert.Equal("video item", item.Title);
                Assert.Equal(2, viewModel.TotalHistoryCount);
            }
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    private static string CreateTempDatabasePath()
        => Path.Combine(
            Path.GetTempPath(),
            $"easyget-history-vm-{Guid.NewGuid():N}.db");

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
            }
        }
    }
}
