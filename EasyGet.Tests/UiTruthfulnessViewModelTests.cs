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
    public void MainViewModelIgnoresRemovedDouyinWorkspaceNavigation()
    {
        using var context = CreateViewModelContext();

        context.Main.NavigateCommand.Execute("douyin");

        Assert.Equal(0, context.Main.SelectedNavIndex);
        Assert.Equal("单个视频下载", context.Main.CurrentPageTitle);
        Assert.Same(context.Download, context.Main.CurrentPage);
    }

    [Fact]
    public void DownloadViewModelSummariesComeFromRuntimeConfig()
    {
        var configService = new TestConfigService();
        configService.Config.UseProxy = false;
        configService.Config.ProxyAddress = "socks5://127.0.0.1:7890";
        configService.Config.ConcurrentFragments = 8;
        configService.Config.MaxConcurrentDownloads = 3;

        Assert.Equal("未启用", DownloadViewModel.DescribeProxyStatus(configService.Config));
        Assert.Equal("8 分片", DownloadViewModel.DescribeConcurrentFragments(configService.Config));

        configService.Config.MaxConcurrentDownloads = 10;
        Assert.Equal("4 分片（智能限流）", DownloadViewModel.DescribeConcurrentFragments(configService.Config));

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
        var telegram = new TelegramDownloadService(batchContext.Config);
        var settings = new SettingsViewModel(
            batchContext.Config,
            batchContext.Environment,
            batchContext.Manager,
            telegram);
        var download = new DownloadViewModel(
            batchContext.Manager,
            batchContext.Config,
            new YtDlpVideoInfoProvider(batchContext.YtDlp));
        var history = new HistoryViewModel(batchContext.History, batchContext.Config);
        var main = new MainViewModel(
            batchContext.Config,
            batchContext.Environment,
            batchContext.Manager,
            download,
            batchContext.Batch,
            history,
            settings);

        return new ViewModelContext(batchContext, telegram, download, history, settings, main);
    }

    private static BatchContext CreateBatchContext()
    {
        var root = new TestDirectory();
        var config = new ConfigService(root.Path("config"));
        var environment = new EnvironmentService();
        environment.Status.YtDlpPath = root.Path("missing-test-yt-dlp.exe");
        var history = new HistoryService(root.Path("history.db"));
        var ytDlp = new YtDlpService(config, environment);
        var manager = new DownloadManager(ytDlp, history, config);
        var batch = new BatchDownloadViewModel(manager, config, ytDlp);

        return new BatchContext(root, config, environment, history, ytDlp, manager, batch);
    }

    private sealed record BatchContext(
        TestDirectory Root,
        ConfigService Config,
        EnvironmentService Environment,
        HistoryService History,
        YtDlpService YtDlp,
        DownloadManager Manager,
        BatchDownloadViewModel Batch) : IDisposable
    {
        public void Dispose()
        {
            Manager.Dispose();
            History.Dispose();
            Root.Dispose();
        }
    }

    private sealed record ViewModelContext(
        BatchContext BatchContext,
        TelegramDownloadService Telegram,
        DownloadViewModel Download,
        HistoryViewModel History,
        SettingsViewModel Settings,
        MainViewModel Main) : IDisposable
    {
        public void Dispose()
        {
            Settings.FlushPendingSaveAsync().GetAwaiter().GetResult();
            Telegram.Dispose();
            BatchContext.Dispose();
        }
    }
}
