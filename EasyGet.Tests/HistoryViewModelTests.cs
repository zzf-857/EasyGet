using System.Diagnostics;
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

    [Fact]
    public void HistoryViewModelExposesHistoryCardQuickActionCommands()
    {
        var commandProperties = typeof(HistoryViewModel)
            .GetProperties()
            .Select(property => property.Name)
            .ToList();

        Assert.Contains("OpenFolderCommand", commandProperties);
        Assert.Contains("PreviewFileCommand", commandProperties);
        Assert.Contains("OpenSourceUrlCommand", commandProperties);
        Assert.Contains("DeleteItemCommand", commandProperties);
    }

    [Fact]
    public async Task PreviewFileCommand_OpensExistingFileWithDefaultShellHandler()
    {
        var dbPath = CreateTempDatabasePath();
        var mediaPath = Path.Combine(Path.GetTempPath(), $"easyget-preview-{Guid.NewGuid():N}.mp4");
        var startedProcesses = new List<ProcessStartInfo>();

        try
        {
            await File.WriteAllTextAsync(mediaPath, "preview target");
            using var service = new HistoryService(dbPath);
            var viewModel = new HistoryViewModel(service, startedProcesses.Add);

            await viewModel.PreviewFileCommand.ExecuteAsync(mediaPath);

            var processStartInfo = Assert.Single(startedProcesses);
            Assert.Equal(mediaPath, processStartInfo.FileName);
            Assert.True(processStartInfo.UseShellExecute);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteFile(mediaPath);
        }
    }

    [Fact]
    public async Task PreviewFileCommand_OpensResolvedMediaFileWhenPathIsDirectory()
    {
        var dbPath = CreateTempDatabasePath();
        var directory = Path.Combine(Path.GetTempPath(), $"easyget-preview-dir-{Guid.NewGuid():N}");
        var startedProcesses = new List<ProcessStartInfo>();

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "metadata.txt"), "metadata");
            var mediaPath = Path.Combine(directory, "video.mp4");
            File.WriteAllText(mediaPath, "preview target");

            using var service = new HistoryService(dbPath);
            var viewModel = new HistoryViewModel(service, startedProcesses.Add);

            await viewModel.PreviewFileCommand.ExecuteAsync(directory);

            var processStartInfo = Assert.Single(startedProcesses);
            Assert.Equal(mediaPath, processStartInfo.FileName);
            Assert.True(processStartInfo.UseShellExecute);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task OpenSourceUrlCommand_OpensHttpUrlWithDefaultBrowser()
    {
        var dbPath = CreateTempDatabasePath();
        var startedProcesses = new List<ProcessStartInfo>();
        const string sourceUrl = "https://www.douyin.com/video/7646616475909852431";

        try
        {
            using var service = new HistoryService(dbPath);
            var viewModel = new HistoryViewModel(service, startedProcesses.Add);

            await viewModel.OpenSourceUrlCommand.ExecuteAsync(sourceUrl);

            var processStartInfo = Assert.Single(startedProcesses);
            Assert.Equal(sourceUrl, processStartInfo.FileName);
            Assert.True(processStartInfo.UseShellExecute);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public void CreateOpenFolderStartInfo_SelectsTargetFileInExplorer()
    {
        const string filePath = @"C:\Downloads\EasyGet\video.mp4";

        var startInfo = HistoryViewModel.CreateOpenFolderStartInfo(filePath);

        Assert.Equal("explorer.exe", startInfo.FileName);
        Assert.Equal(@"/select,""C:\Downloads\EasyGet\video.mp4""", startInfo.Arguments);
        Assert.True(startInfo.UseShellExecute);
    }

    [Fact]
    public void OpenFolderCommand_UsesInjectedProcessLauncher()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("ViewModels", "HistoryViewModel.cs")));
        var openFolderStart = source.IndexOf("private async Task OpenFolder", StringComparison.Ordinal);
        var previewFileStart = source.IndexOf("private async Task PreviewFile", StringComparison.Ordinal);

        Assert.True(openFolderStart >= 0, "Expected HistoryViewModel.OpenFolder method to exist.");
        Assert.True(previewFileStart > openFolderStart, "Expected PreviewFile to follow OpenFolder in HistoryViewModel.");

        var openFolderSource = source[openFolderStart..previewFileStart];

        Assert.Contains("_startProcess(CreateOpenFolderStartInfo(filePath));", openFolderSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", openFolderSource, StringComparison.Ordinal);
    }

    private static string CreateTempDatabasePath()
        => TestTempPaths.CreateSqliteDatabasePath("easyget-history-vm");

    private static void TryDeleteDatabase(string dbPath)
        => TestTempPaths.TryDeleteSqliteDatabase(dbPath);

    private static void TryDeleteFile(string path)
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
                return true;

            await Task.Delay(25);
        }

        return condition();
    }

    [Fact]
    public async Task SearchKeywordChange_TriggersDebouncedSearch()
    {
        var dbPath = CreateTempDatabasePath();
        try
        {
            using var service = new HistoryService(dbPath);
            await service.AddAsync(new DownloadHistory
            {
                Url = "https://example.com/test",
                Title = "special query match",
                Format = "mp4",
                DownloadTime = DateTime.Now
            });

            var viewModel = new HistoryViewModel(service);
            await viewModel.LoadHistory();
            Assert.Single(viewModel.HistoryItems);

            // 改变 SearchKeyword 触发防抖
            viewModel.SearchKeyword = "no match expected";
            
            // 此时由于有 300ms 延时，列表应该还没有刷新
            Assert.Single(viewModel.HistoryItems);

            // 等待防抖任务完成；CI runner 上固定 350ms 容易和调度抢跑。
            var refreshed = await WaitUntilAsync(
                () => viewModel.HistoryItems.Count == 0,
                TimeSpan.FromSeconds(2));

            // 此时防抖自动查询应该已执行完毕，列表应当变空
            Assert.True(refreshed);
            Assert.Empty(viewModel.HistoryItems);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task ClearAll_WhenConfirmed_ClearsHistory()
    {
        var dbPath = CreateTempDatabasePath();
        try
        {
            using var service = new HistoryService(dbPath);
            await service.AddAsync(new DownloadHistory
            {
                Url = "https://example.com/test",
                Title = "item to clear",
                Format = "mp4",
                DownloadTime = DateTime.Now
            });

            var viewModel = new HistoryViewModel(service)
            {
                ConfirmFunc = (msg, title) => true // 确认清空
            };
            await viewModel.LoadHistory();
            Assert.Single(viewModel.HistoryItems);

            await viewModel.ClearAllCommand.ExecuteAsync(null);

            Assert.Empty(viewModel.HistoryItems);
            Assert.Equal(0, viewModel.TotalHistoryCount);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task ClearAll_WhenCancelled_KeepsHistory()
    {
        var dbPath = CreateTempDatabasePath();
        try
        {
            using var service = new HistoryService(dbPath);
            await service.AddAsync(new DownloadHistory
            {
                Url = "https://example.com/test",
                Title = "item to clear",
                Format = "mp4",
                DownloadTime = DateTime.Now
            });

            var viewModel = new HistoryViewModel(service)
            {
                ConfirmFunc = (msg, title) => false // 取消清空
            };
            await viewModel.LoadHistory();
            Assert.Single(viewModel.HistoryItems);

            await viewModel.ClearAllCommand.ExecuteAsync(null);

            Assert.Single(viewModel.HistoryItems);
            Assert.Equal(1, viewModel.TotalHistoryCount);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public void IsSearchOrFilterActive_ReturnsCorrectStatus()
    {
        var dbPath = CreateTempDatabasePath();
        try
        {
            using var service = new HistoryService(dbPath);
            var viewModel = new HistoryViewModel(service);

            // 默认状态
            Assert.False(viewModel.IsSearchOrFilterActive);

            // 仅关键字被修改
            viewModel.SearchKeyword = "test";
            Assert.True(viewModel.IsSearchOrFilterActive);

            // 仅筛选被修改
            viewModel.SearchKeyword = "";
            viewModel.SelectedMediaFilter = "视频";
            Assert.True(viewModel.IsSearchOrFilterActive);

            // 恢复默认
            viewModel.SelectedMediaFilter = "全部";
            Assert.False(viewModel.IsSearchOrFilterActive);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task ClearFilterAndSearchCommand_ResetsFiltersAndKeyword()
    {
        var dbPath = CreateTempDatabasePath();
        try
        {
            using var service = new HistoryService(dbPath);
            var viewModel = new HistoryViewModel(service)
            {
                SearchKeyword = "some query",
                SelectedMediaFilter = "音频"
            };

            Assert.True(viewModel.IsSearchOrFilterActive);

            await viewModel.ClearFilterAndSearchCommand.ExecuteAsync(null);

            Assert.Equal("", viewModel.SearchKeyword);
            Assert.Equal("全部", viewModel.SelectedMediaFilter);
            Assert.False(viewModel.IsSearchOrFilterActive);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }
}
