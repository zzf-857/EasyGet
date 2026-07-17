using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyGet.Models;
using EasyGet.Services;
using EasyGet.ViewModels;
using Xunit;

namespace EasyGet.Tests;

public class BatchDownloadViewModelTests
{
    [Fact]
    public void QueueSummary_FiltersFinishedTasksAndTracksAggregateProgress()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var manager = new DownloadManager(ytDlp, history, config);
        var viewModel = new BatchDownloadViewModel(manager, config, ytDlp);
        manager.Tasks.Add(new DownloadTask
        {
            Url = "https://example.com/done",
            Status = DownloadStatus.Completed,
            Progress = 100,
            Speed = 4096
        });
        manager.Tasks.Add(new DownloadTask { Url = "https://example.com/running", Status = DownloadStatus.Downloading, Progress = 40, Speed = 1024 });
        manager.Tasks.Add(new DownloadTask { Url = "https://example.com/failed", Status = DownloadStatus.Failed, Progress = 20 });

        Assert.Equal(3, viewModel.TotalTaskCount);
        Assert.Equal(1, viewModel.CompletedTaskCount);
        Assert.Equal(1, viewModel.FailedTaskCount);
        Assert.Equal(2, viewModel.FinishedTaskCount);
        Assert.Equal(1, viewModel.RemainingTaskCount);
        Assert.Single(viewModel.VisibleQueueTasks);
        Assert.Equal("https://example.com/running", viewModel.VisibleQueueTasks[0].Url);
        Assert.Equal(160d / 3d, viewModel.OverallProgress, precision: 6);
        Assert.Equal(1024, viewModel.AggregateSpeed);
        Assert.Contains("已完成 1/3", viewModel.QueueSummaryText, StringComparison.Ordinal);

        viewModel.SetQueueFilterCommand.Execute("已结束");
        Assert.Equal(2, viewModel.VisibleQueueTasks.Count);

        viewModel.ClearFinishedCommand.Execute(null);
        Assert.Single(manager.Tasks);
        Assert.Equal(0, viewModel.FinishedTaskCount);
    }

    private static string CreateTempDatabasePath()
        => TestTempPaths.CreateSqliteDatabasePath("easyget-batch-vm");

    private static void TryDeleteDatabase(string dbPath)
        => TestTempPaths.TryDeleteSqliteDatabase(dbPath);

    private static string CreateTempOutputDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easyget-batch-open-folder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
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

    [Fact]
    public async Task StartBatchDownload_AddsAllBilibiliPartsBeforeMetadataResolutionCompletes()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        config.Config.DefaultDownloadPath = root.Path("downloads");
        var blocker = new BlockingYtDlpDownloadService();
        var manager = new DownloadManager(blocker, history, config);
        var concreteYtDlp = new YtDlpService(config, new EnvironmentService());
        var viewModel = new BatchDownloadViewModel(manager, config, concreteYtDlp)
        {
            UrlsText = string.Join(
                '\n',
                Enumerable.Range(1, 85).Select(i =>
                    $"https://www.bilibili.com/video/BV1ddN76xEQY/?p={i}"))
        };

        var command = viewModel.StartBatchDownloadCommand.ExecuteAsync(null);
        await blocker.FirstMetadataRequest.WaitAsync(TimeSpan.FromSeconds(2));
        var admittedTaskCount = manager.Tasks.Count;
        blocker.Release();
        await command.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(85, admittedTaskCount);
        Assert.Equal(85, manager.Tasks.Select(task => task.Url).Distinct().Count());
        Assert.InRange(blocker.MaxConcurrentMetadataRequests, 1, 4);
        var batchId = Assert.Single(manager.Tasks.Select(task => task.BatchId).Distinct());
        var batchDirectory = Assert.Single(manager.Tasks.Select(task => task.BatchDirectory).Distinct());
        Assert.False(string.IsNullOrWhiteSpace(batchId));
        Assert.Contains("Bilibili合集_BV1ddN76xEQY", batchDirectory, StringComparison.Ordinal);
        Assert.True(Directory.Exists(batchDirectory));
        Assert.All(manager.Tasks, task => Assert.StartsWith(
            batchDirectory,
            task.OutputDirectory,
            StringComparison.OrdinalIgnoreCase));

        var savedHistory = await history.GetAllAsync();
        Assert.Equal(85, savedHistory.Count);
        Assert.All(savedHistory, item =>
        {
            Assert.Equal(batchId, item.BatchId);
            Assert.Equal(batchDirectory, item.BatchDirectory);
        });
    }

    [Fact]
    public async Task StartBatchDownload_DeduplicatesInputAndExistingQueueUrls()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        config.Config.DefaultDownloadPath = root.Path("downloads");
        var service = new BlockingYtDlpDownloadService();
        service.Release();
        var manager = new DownloadManager(service, history, config);
        manager.Tasks.Add(new DownloadTask { Url = "https://example.com/already" });
        var concreteYtDlp = new YtDlpService(config, new EnvironmentService());
        var viewModel = new BatchDownloadViewModel(manager, config, concreteYtDlp)
        {
            UrlsText = string.Join('\n',
            [
                "https://example.com/already",
                "https://example.com/new",
                "https://example.com/new",
                "https://example.com/second"
            ])
        };

        Assert.Equal(3, viewModel.LinkCount);

        await viewModel.StartBatchDownloadCommand.ExecuteAsync(null);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(3, manager.Tasks.Count);
        Assert.Single(manager.Tasks, task => string.Equals(
            task.Url,
            "https://example.com/new",
            StringComparison.OrdinalIgnoreCase));
        var newTasks = manager.Tasks.Where(task => task.Url != "https://example.com/already").ToList();
        Assert.Equal(2, newTasks.Count);
        Assert.Single(newTasks.Select(task => task.BatchId).Distinct());
        Assert.Single(newTasks.Select(task => task.BatchDirectory).Distinct());
    }

    [Fact]
    public async Task ImportedPlaylist_UsesActualTitleForFolderAndCollectionTasks()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        config.Config.DefaultDownloadPath = root.Path("downloads");
        const string collectionTitle = "【大模型RAG】2026年系统教程！全程干货！";
        var service = new BlockingYtDlpDownloadService();
        service.MetadataPlatform = "Bilibili";
        service.MetadataTitleFactory = url => url.Contains("p=1", StringComparison.Ordinal)
            ? $"{collectionTitle} p01 00.【指南】完整路径"
            : $"{collectionTitle} p02 01.环境安装";
        service.Release();
        var manager = new DownloadManager(service, history, config);
        var concreteYtDlp = new YtDlpService(config, new EnvironmentService());
        var viewModel = new BatchDownloadViewModel(manager, config, concreteYtDlp);
        var playlist = new PlaylistInfo
        {
            Title = collectionTitle,
            SourceUrl = "https://www.bilibili.com/video/BV1ddN76xEQY/",
            Urls =
            [
                "https://www.bilibili.com/video/BV1ddN76xEQY/?p=1",
                "https://www.bilibili.com/video/BV1ddN76xEQY/?p=2"
            ]
        };

        Assert.True(viewModel.ApplyPlaylistImport(playlist));
        await viewModel.StartBatchDownloadCommand.ExecuteAsync(null);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, manager.Tasks.Count);
        Assert.All(manager.Tasks, task =>
        {
            Assert.Equal(collectionTitle, task.CollectionTitle);
            Assert.Equal(collectionTitle, task.BatchName);
            Assert.Equal(collectionTitle, Path.GetFileName(task.BatchDirectory));
            Assert.Equal(task.BatchDirectory, task.OutputDirectory);
            Assert.Equal(2, task.CollectionItemCount);
        });
        Assert.Equal([1, 2], manager.Tasks.Select(task => task.CollectionItemIndex).ToArray());
        Assert.Equal(
            ["00.【指南】完整路径", "01.环境安装"],
            manager.Tasks.Select(task => task.Title).ToArray());
        Assert.Equal("", viewModel.UrlsText);
    }

    [Fact]
    public async Task ImportedPlaylist_WhenOneEntryAlreadyExists_KeepsOriginalEpisodeIndexes()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        config.Config.DefaultDownloadPath = root.Path("downloads");
        var service = new BlockingYtDlpDownloadService
        {
            MetadataPlatform = "Bilibili"
        };
        service.Release();
        var manager = new DownloadManager(service, history, config);
        var concreteYtDlp = new YtDlpService(config, new EnvironmentService());
        var viewModel = new BatchDownloadViewModel(manager, config, concreteYtDlp);
        const string firstUrl = "https://www.bilibili.com/video/BV1ddN76xEQY/?p=1";
        manager.Tasks.Add(new DownloadTask { Url = firstUrl, Status = DownloadStatus.Completed });
        var playlist = new PlaylistInfo
        {
            Title = "RAG 系列课程",
            SourceUrl = "https://www.bilibili.com/video/BV1ddN76xEQY/",
            Urls =
            [
                firstUrl,
                "https://www.bilibili.com/video/BV1ddN76xEQY/?p=2",
                "https://www.bilibili.com/video/BV1ddN76xEQY/?p=3"
            ]
        };

        Assert.True(viewModel.ApplyPlaylistImport(playlist));
        await viewModel.StartBatchDownloadCommand.ExecuteAsync(null);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var newTasks = manager.Tasks.Where(task => task.Url != firstUrl).ToList();
        Assert.Equal(2, newTasks.Count);
        Assert.Equal([2, 3], newTasks.Select(task => task.CollectionItemIndex).ToArray());
        Assert.All(newTasks, task =>
        {
            Assert.Equal(3, task.CollectionItemCount);
            Assert.Equal("RAG 系列课程", task.CollectionTitle);
            Assert.Equal("RAG 系列课程", task.BatchName);
        });
    }

    [Fact]
    public void CancelTask_DuringMerge_CancelsInsteadOfRemovingQueueEntry()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var manager = new DownloadManager(ytDlp, history, config);
        var viewModel = new BatchDownloadViewModel(manager, config, ytDlp);
        using var cts = new CancellationTokenSource();
        var task = new DownloadTask
        {
            Url = "https://example.com/merging",
            Status = DownloadStatus.Merging,
            Cts = cts
        };
        manager.Tasks.Add(task);

        viewModel.CancelTaskCommand.Execute(task.Id);

        Assert.True(cts.IsCancellationRequested);
        Assert.Contains(task, manager.Tasks);
    }

    [Fact]
    public void TerminalTaskStatus_ClearsStaleSpeedAndEta()
    {
        var task = new DownloadTask
        {
            Status = DownloadStatus.Downloading,
            Speed = 2048,
            Eta = 30
        };

        task.Status = DownloadStatus.Completed;

        Assert.Equal(0, task.Speed);
        Assert.Equal(0, task.Eta);
    }

    [Fact]
    public void BatchDownloadXaml_ShowsTaskStatusTextIncludingAuthenticationPhase()
    {
        var xaml = File.ReadAllText(
            TestRepositoryPaths.GetViewPath("BatchDownloadView.xaml"));

        Assert.Contains(
            "Text=\"{Binding StatusText, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DisplayTitle}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelAll_WhenConfirmed_StopsUnfinishedAndKeepsFinishedTasksVisible()
    {
        var dbPath = CreateTempDatabasePath();
        using var history = new HistoryService(dbPath);
        var configService = new TestConfigService();
        var ytDlp = new YtDlpService(configService, new EnvironmentService());
        var manager = new DownloadManager(ytDlp, history, configService);
        var viewModel = new BatchDownloadViewModel(manager, configService, ytDlp);

        try
        {
            var cts1 = new CancellationTokenSource();
            var task1 = new DownloadTask { Status = DownloadStatus.Downloading, Url = "https://example.com/1", Cts = cts1 };
            var task2 = new DownloadTask { Status = DownloadStatus.Failed, Url = "https://example.com/2" };

            manager.Tasks.Add(task1);
            manager.Tasks.Add(task2);

            viewModel.ConfirmFunc = (msg, title) => true; // 确认取消

            viewModel.CancelAllCommand.Execute(null);

            // 验证任务 1 (Downloading) 仍保留在队列中，但其 Cts 被取消
            Assert.Contains(task1, manager.Tasks);
            Assert.True(cts1.IsCancellationRequested);

            // 已结束任务保留，交由“清理已结束”单独处理。
            Assert.Contains(task2, manager.Tasks);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public void CancelAll_WhenCancelled_KeepsTasks()
    {
        var dbPath = CreateTempDatabasePath();
        using var history = new HistoryService(dbPath);
        var configService = new TestConfigService();
        var ytDlp = new YtDlpService(configService, new EnvironmentService());
        var manager = new DownloadManager(ytDlp, history, configService);
        var viewModel = new BatchDownloadViewModel(manager, configService, ytDlp);

        try
        {
            var cts1 = new CancellationTokenSource();
            var task1 = new DownloadTask { Status = DownloadStatus.Downloading, Url = "https://example.com/1", Cts = cts1 };
            var task2 = new DownloadTask { Status = DownloadStatus.Failed, Url = "https://example.com/2" };

            manager.Tasks.Add(task1);
            manager.Tasks.Add(task2);

            viewModel.ConfirmFunc = (msg, title) => false; // 用户取消

            viewModel.CancelAllCommand.Execute(null);

            // 确认任务没有变化，没有被取消或清理
            Assert.Contains(task1, manager.Tasks);
            Assert.False(cts1.IsCancellationRequested);
            Assert.Contains(task2, manager.Tasks);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public void ImportText_WithValidAndInvalidUrls_ImportsValidAndRaisesNotificationForInvalid()
    {
        var dbPath = CreateTempDatabasePath();
        using var history = new HistoryService(dbPath);
        var configService = new TestConfigService();
        var ytDlp = new YtDlpService(configService, new EnvironmentService());
        var manager = new DownloadManager(ytDlp, history, configService);
        var viewModel = new BatchDownloadViewModel(manager, configService, ytDlp);

        try
        {
            string? receivedMsg = null;
            bool? receivedSuccess = null;
            viewModel.RequestShowNotification += (msg, success) =>
            {
                receivedMsg = msg;
                receivedSuccess = success;
            };

            string inputText = "https://example.com/video1\nthis is invalid line\nhttps://example.com/video2";
            viewModel.ImportText(inputText);

            // 验证有效的 2 个 URL 被导入，并以换行符连接
            Assert.Contains("https://example.com/video1", viewModel.UrlsText);
            Assert.Contains("https://example.com/video2", viewModel.UrlsText);
            Assert.Equal(2, viewModel.LinkCount);

            // 验证 1 个无效行触发了通知提示，且由于有成功导入的链接，应当是 success: true
            Assert.NotNull(receivedMsg);
            Assert.Contains("新增 2 个链接", receivedMsg);
            Assert.Contains("忽略 1 行无效文本", receivedMsg);
            Assert.True(receivedSuccess);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public void ImportText_WithOnlyValidUrls_ImportsAllAndRaisesSuccessNotification()
    {
        var dbPath = CreateTempDatabasePath();
        using var history = new HistoryService(dbPath);
        var configService = new TestConfigService();
        var ytDlp = new YtDlpService(configService, new EnvironmentService());
        var manager = new DownloadManager(ytDlp, history, configService);
        var viewModel = new BatchDownloadViewModel(manager, configService, ytDlp);

        try
        {
            string? receivedMsg = null;
            viewModel.RequestShowNotification += (msg, success) =>
            {
                receivedMsg = msg;
            };

            string inputText = "https://example.com/video1\nhttps://example.com/video2";
            viewModel.ImportText(inputText);

            Assert.Contains("https://example.com/video1", viewModel.UrlsText);
            Assert.Contains("https://example.com/video2", viewModel.UrlsText);
            Assert.Equal(2, viewModel.LinkCount);

            Assert.NotNull(receivedMsg);
            Assert.Contains("新增 2 个链接", receivedMsg);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public void ImportText_WithOnlyInvalidUrls_ImportsNoneAndRaisesErrorNotification()
    {
        var dbPath = CreateTempDatabasePath();
        using var history = new HistoryService(dbPath);
        var configService = new TestConfigService();
        var ytDlp = new YtDlpService(configService, new EnvironmentService());
        var manager = new DownloadManager(ytDlp, history, configService);
        var viewModel = new BatchDownloadViewModel(manager, configService, ytDlp);

        try
        {
            string? receivedMsg = null;
            bool? receivedSuccess = null;
            viewModel.RequestShowNotification += (msg, success) =>
            {
                receivedMsg = msg;
                receivedSuccess = success;
            };

            string inputText = "invalid line 1\ninvalid line 2";
            viewModel.ImportText(inputText);

            Assert.Equal(0, viewModel.LinkCount);
            Assert.NotNull(receivedMsg);
            Assert.Contains("新增 0 个链接", receivedMsg);
            Assert.Contains("忽略 2 行", receivedMsg);
            Assert.False(receivedSuccess);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public void ImportText_DeduplicatesExistingAndNewLinksWithExplicitFeedback()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var manager = new DownloadManager(ytDlp, history, config);
        var viewModel = new BatchDownloadViewModel(manager, config, ytDlp)
        {
            UrlsText = "https://example.com/one"
        };
        string? notification = null;
        viewModel.RequestShowNotification += (message, _) => notification = message;

        viewModel.ImportText("https://example.com/one\nhttps://example.com/two\nhttps://example.com/two");

        Assert.Equal(2, viewModel.LinkCount);
        Assert.Equal(1, viewModel.UrlsText.Split('\n').Count(url => url.EndsWith("/two", StringComparison.Ordinal)));
        Assert.Contains("跳过 2 个重复链接", notification, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenTaskFolderCommand_SelectsExistingOutputFileWithInjectedLauncher()
    {
        var dbPath = CreateTempDatabasePath();
        var outputDirectory = CreateTempOutputDirectory();
        using var history = new HistoryService(dbPath);
        var configService = new TestConfigService();
        var ytDlp = new YtDlpService(configService, new EnvironmentService());
        var manager = new DownloadManager(ytDlp, history, configService);
        var startedProcesses = new List<ProcessStartInfo>();
        var viewModel = new BatchDownloadViewModel(manager, configService, ytDlp, startedProcesses.Add);

        try
        {
            var outputPath = Path.Combine(outputDirectory, "clip.mp4");
            await File.WriteAllTextAsync(outputPath, "video");
            var task = new DownloadTask
            {
                OutputFilePath = outputPath,
                OutputDirectory = outputDirectory
            };
            manager.Tasks.Add(task);

            await viewModel.OpenTaskFolderCommand.ExecuteAsync(task.Id);

            var startInfo = Assert.Single(startedProcesses);
            Assert.Equal("explorer.exe", startInfo.FileName);
            Assert.Equal($"/select,\"{outputPath}\"", startInfo.Arguments);
            Assert.True(startInfo.UseShellExecute);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task OpenTaskFolderCommand_OpensOutputDirectoryWhenOutputFileIsMissing()
    {
        var dbPath = CreateTempDatabasePath();
        var outputDirectory = CreateTempOutputDirectory();
        using var history = new HistoryService(dbPath);
        var configService = new TestConfigService();
        var ytDlp = new YtDlpService(configService, new EnvironmentService());
        var manager = new DownloadManager(ytDlp, history, configService);
        var startedProcesses = new List<ProcessStartInfo>();
        var viewModel = new BatchDownloadViewModel(manager, configService, ytDlp, startedProcesses.Add);

        try
        {
            var task = new DownloadTask
            {
                OutputFilePath = Path.Combine(outputDirectory, "missing.mp4"),
                OutputDirectory = outputDirectory
            };
            manager.Tasks.Add(task);

            await viewModel.OpenTaskFolderCommand.ExecuteAsync(task.Id);

            var startInfo = Assert.Single(startedProcesses);
            Assert.Equal(outputDirectory, startInfo.FileName);
            Assert.True(startInfo.UseShellExecute);
            Assert.True(string.IsNullOrEmpty(startInfo.Arguments));
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteDirectory(outputDirectory);
        }
    }

    private sealed class BlockingYtDlpDownloadService : IYtDlpDownloadService
    {
        private readonly TaskCompletionSource _first = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeMetadataRequests;
        private int _maxConcurrentMetadataRequests;

        public Task FirstMetadataRequest => _first.Task;
        public int MaxConcurrentMetadataRequests => Volatile.Read(
            ref _maxConcurrentMetadataRequests);
        public Func<string, string> MetadataTitleFactory { get; set; } = url => url;
        public string MetadataPlatform { get; set; } = "Twitter";

        public async Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _activeMetadataRequests);
            UpdateMaximum(ref _maxConcurrentMetadataRequests, active);
            _first.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                return new VideoInfo
                {
                    Title = MetadataTitleFactory(url),
                    Platform = MetadataPlatform
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeMetadataRequests);
            }
        }

        public Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            task.Status = DownloadStatus.Completed;
            return Task.CompletedTask;
        }

        public void Release() => _release.TrySetResult();

        private static void UpdateMaximum(ref int maximum, int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref maximum);
                if (candidate <= current
                    || Interlocked.CompareExchange(ref maximum, candidate, current) == current)
                {
                    return;
                }
            }
        }
    }
}
