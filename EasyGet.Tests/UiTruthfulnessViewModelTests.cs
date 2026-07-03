using EasyGet.Models;
using EasyGet.Services;
using EasyGet.ViewModels;
using Xunit;

namespace EasyGet.Tests;

public class UiTruthfulnessViewModelTests
{
    [Fact]
    public void MainViewModelUsesChineseBatchPageTitleAndAssemblyVersion()
    {
        using var context = CreateViewModelContext();

        context.Main.SelectedNavIndex = 1;

        Assert.Equal("批量下载", context.Main.CurrentPageTitle);
        Assert.Matches(@"^v\d+\.\d+\.\d+", context.Main.AppVersion);
    }

    [Fact]
    public void MainViewModelNavigatesToDouyinWorkspace()
    {
        using var context = CreateViewModelContext();

        context.Main.NavigateCommand.Execute("douyin");

        Assert.Equal(2, context.Main.SelectedNavIndex);
        Assert.Equal("抖音工作台", context.Main.CurrentPageTitle);
        Assert.Same(context.Douyin, context.Main.CurrentPage);
    }

    [Fact]
    public void DownloadViewModelSummariesComeFromRuntimeConfig()
    {
        var configService = new ConfigService();
        configService.Config.UseProxy = false;
        configService.Config.ProxyAddress = "socks5://127.0.0.1:7890";
        configService.Config.ConcurrentFragments = 8;

        Assert.Equal("未启用", DownloadViewModel.DescribeProxyStatus(configService.Config));
        Assert.Equal("8 分片", DownloadViewModel.DescribeConcurrentFragments(configService.Config));

        configService.Config.UseProxy = true;

        Assert.Equal("socks5://127.0.0.1:7890", DownloadViewModel.DescribeProxyStatus(configService.Config));
    }

    [Fact]
    public void BatchDownloadViewModelCountsOnlyActiveDownloads()
    {
        using var context = CreateBatchContext();
        var waiting = new DownloadTask { Status = DownloadStatus.Waiting };
        var downloading = new DownloadTask { Status = DownloadStatus.Downloading };
        var merging = new DownloadTask { Status = DownloadStatus.Merging };
        var completed = new DownloadTask { Status = DownloadStatus.Completed };

        context.Manager.Tasks.Add(waiting);
        context.Manager.Tasks.Add(downloading);
        context.Manager.Tasks.Add(merging);
        context.Manager.Tasks.Add(completed);

        Assert.Equal(1, context.Batch.ActiveDownloadCount);

        waiting.Status = DownloadStatus.Downloading;

        Assert.Equal(2, context.Batch.ActiveDownloadCount);
    }

    [Fact]
    public void DouyinViewModelCountsOnlyDouyinTasks()
    {
        using var context = CreateViewModelContext();

        context.BatchContext.Manager.Tasks.Add(new DownloadTask
        {
            Url = "https://www.douyin.com/video/123",
            Platform = "",
            Status = DownloadStatus.Downloading
        });
        context.BatchContext.Manager.Tasks.Add(new DownloadTask
        {
            Url = "https://example.com/video",
            Platform = "Douyin",
            Status = DownloadStatus.Completed
        });
        context.BatchContext.Manager.Tasks.Add(new DownloadTask
        {
            Url = "https://example.com/video",
            Platform = "YouTube",
            Status = DownloadStatus.Failed
        });

        Assert.Equal(2, context.Douyin.DouyinTaskCount);
        Assert.Equal(1, context.Douyin.ActiveDouyinTaskCount);
        Assert.Equal(1, context.Douyin.CompletedDouyinTaskCount);
        Assert.Equal(0, context.Douyin.FailedDouyinTaskCount);
    }

    [Fact]
    public void DouyinViewModelMaintainsFilteredTaskCenterItems()
    {
        using var context = CreateViewModelContext();
        var douyinTask = new DownloadTask
        {
            Url = "https://www.douyin.com/video/123",
            Platform = "",
            Status = DownloadStatus.Waiting,
            Title = "douyin task"
        };
        var nonDouyinTask = new DownloadTask
        {
            Url = "https://example.com/video",
            Platform = "YouTube",
            Status = DownloadStatus.Waiting,
            Title = "other task"
        };

        context.BatchContext.Manager.Tasks.Add(douyinTask);
        context.BatchContext.Manager.Tasks.Add(nonDouyinTask);

        Assert.Collection(
            context.Douyin.DouyinTaskItems,
            item => Assert.Equal("douyin task", item.Title));

        nonDouyinTask.Platform = "Douyin";

        Assert.Collection(
            context.Douyin.DouyinTaskItems,
            item => Assert.Equal("douyin task", item.Title),
            item => Assert.Equal("other task", item.Title));

        context.BatchContext.Manager.Tasks.Remove(douyinTask);

        Assert.Collection(
            context.Douyin.DouyinTaskItems,
            item => Assert.Equal("other task", item.Title));
    }

    [Fact]
    public void DouyinViewModelIgnoresClearedTaskStateChanges()
    {
        using var context = CreateViewModelContext();
        var clearedTask = new DownloadTask
        {
            Url = "https://www.douyin.com/video/123",
            Status = DownloadStatus.Downloading
        };
        context.BatchContext.Manager.Tasks.Add(clearedTask);
        context.BatchContext.Manager.Tasks.Clear();

        var notificationCount = 0;
        context.Douyin.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DouyinViewModel.DouyinTaskCount))
                notificationCount++;
        };

        clearedTask.Status = DownloadStatus.Completed;

        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public void DouyinViewModelFiltersArchiveHistoryToDouyinItems()
    {
        using var context = CreateViewModelContext();

        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "douyin by platform",
            Platform = "Douyin",
            Url = "https://example.com/video"
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "douyin by url",
            Platform = "",
            Url = "https://v.douyin.com/abc123"
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "not douyin",
            Platform = "YouTube",
            Url = "https://youtube.com/watch?v=abc"
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "lookalike domain",
            Platform = "",
            Url = "https://notdouyin.com/video/123"
        });

        Assert.Collection(
            context.Douyin.DouyinHistoryItems,
            item => Assert.Equal("douyin by platform", item.Title),
            item => Assert.Equal("douyin by url", item.Title));
    }

    [Fact]
    public void DouyinViewModelExposesOnlyDouyinManifestSummaryItems()
    {
        using var context = CreateViewModelContext();

        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "douyin manifest",
            Platform = "Douyin",
            Url = "https://www.douyin.com/video/123",
            DouyinManifestSummaryText = "作品 3 / 视频 1 / 图文 1 / 音乐 1 / 附属 2",
            DouyinManifestSummary = new DouyinManifestSummary(
                3,
                2,
                1,
                1,
                1,
                0,
                4,
                false,
                [
                    new DouyinManifestItem(
                        "v1",
                        "video",
                        "视频",
                        "视频标题",
                        "作者 A",
                        "2026-07-01",
                        "2026-07-03T10:00:00",
                        ["旅行", "美食"],
                        ["video.mp4"])
                ])
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "douyin without manifest",
            Platform = "Douyin",
            Url = "https://www.douyin.com/video/456",
            DouyinManifestSummaryText = ""
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "other manifest",
            Platform = "YouTube",
            Url = "https://youtube.com/watch?v=abc",
            DouyinManifestSummaryText = "作品 1 / 视频 1 / 附属 0"
        });

        Assert.Collection(
            context.Douyin.DouyinManifestSummaryItems,
            item =>
            {
                Assert.Equal("douyin manifest", item.Title);
                Assert.Equal("作品 3 / 视频 1 / 图文 1 / 音乐 1 / 附属 2", item.DouyinManifestSummaryText);
                Assert.True(item.HasDouyinManifestDetails);
                var detail = Assert.Single(item.DouyinManifestItems);
                Assert.Equal("视频", detail.MediaTypeText);
                Assert.Equal("视频标题", detail.Description);
                Assert.Equal("作者 A", detail.AuthorName);
                Assert.Equal("video.mp4", detail.FileNamesText);
            });
    }

    [Fact]
    public void DownloadHistoryNotifiesWhenStructuredDouyinManifestSummaryChanges()
    {
        var item = new DownloadHistory();
        var propertyNames = new List<string>();
        item.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName ?? "");

        item.DouyinManifestSummary = new DouyinManifestSummary(
            1,
            1,
            1,
            0,
            0,
            0,
            1,
            false,
            [
                new DouyinManifestItem(
                    "v1",
                    "video",
                    "视频",
                    "视频标题",
                    "作者 A",
                    "2026-07-01",
                    "",
                    [],
                    ["video.mp4"])
            ]);

        Assert.Contains(nameof(DownloadHistory.DouyinManifestSummary), propertyNames);
        Assert.Contains(nameof(DownloadHistory.DouyinManifestItems), propertyNames);
        Assert.Contains(nameof(DownloadHistory.HasDouyinManifestDetails), propertyNames);
        Assert.True(item.HasDouyinManifestDetails);
    }

    [Fact]
    public void DouyinViewModelNotifiesManifestSummaryCountChanges()
    {
        using var context = CreateViewModelContext();
        var notificationCount = 0;
        context.Douyin.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DouyinViewModel.DouyinManifestSummaryCount))
                notificationCount++;
        };

        var item = new DownloadHistory
        {
            Title = "douyin manifest",
            Platform = "Douyin",
            Url = "https://www.douyin.com/video/123",
            DouyinManifestSummaryText = "作品 1 / 视频 1 / 附属 0"
        };

        context.History.HistoryItems.Add(item);

        Assert.Equal(1, context.Douyin.DouyinManifestSummaryCount);
        Assert.Equal(1, notificationCount);

        context.History.HistoryItems.Clear();

        Assert.Equal(0, context.Douyin.DouyinManifestSummaryCount);
        Assert.Equal(2, notificationCount);
    }

    [Theory]
    [InlineData(1024L, "1 KB 可用")]
    [InlineData(1024L * 1024L * 1536L, "1.5 GB 可用")]
    public void HistoryViewModelFormatsDownloadDriveFreeSpace(long bytes, string expected)
    {
        Assert.Equal(expected, HistoryViewModel.FormatAvailableSpace(bytes));
    }

    private static ViewModelContext CreateViewModelContext()
    {
        var batchContext = CreateBatchContext();
        var settings = new SettingsViewModel(
            batchContext.Config,
            batchContext.Environment,
            batchContext.Manager,
            new TelegramDownloadService(batchContext.Config));
        var download = new DownloadViewModel(
            batchContext.Manager,
            batchContext.Config,
            new YtDlpVideoInfoProvider(batchContext.YtDlp));
        var history = new HistoryViewModel(batchContext.History, batchContext.Config);
        var douyin = new DouyinViewModel(
            batchContext.Config,
            batchContext.Manager,
            download,
            batchContext.Batch,
            history,
            settings);
        var main = new MainViewModel(
            batchContext.Config,
            batchContext.Environment,
            batchContext.Manager,
            download,
            batchContext.Batch,
            history,
            douyin,
            settings);

        return new ViewModelContext(batchContext, download, history, douyin, settings, main);
    }

    private static BatchContext CreateBatchContext()
    {
        var config = new ConfigService();
        var environment = new EnvironmentService();
        var historyPath = Path.Combine(
            Path.GetTempPath(),
            "EasyGetTests",
            Guid.NewGuid().ToString("N"),
            "history.db");
        var history = new HistoryService(historyPath);
        var ytDlp = new YtDlpService(config, environment);
        var manager = new DownloadManager(ytDlp, history, config);
        var batch = new BatchDownloadViewModel(manager, config, ytDlp);

        return new BatchContext(config, environment, history, ytDlp, manager, batch);
    }

    private sealed record BatchContext(
        ConfigService Config,
        EnvironmentService Environment,
        HistoryService History,
        YtDlpService YtDlp,
        DownloadManager Manager,
        BatchDownloadViewModel Batch) : IDisposable
    {
        public void Dispose() => History.Dispose();
    }

    private sealed record ViewModelContext(
        BatchContext BatchContext,
        DownloadViewModel Download,
        HistoryViewModel History,
        DouyinViewModel Douyin,
        SettingsViewModel Settings,
        MainViewModel Main) : IDisposable
    {
        public void Dispose() => BatchContext.Dispose();
    }
}
