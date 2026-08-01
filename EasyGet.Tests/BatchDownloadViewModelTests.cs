using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
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
    public void QueueFilterOptions_MatchDesignerSegments()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var manager = new DownloadManager(ytDlp, history, config);
        var viewModel = new BatchDownloadViewModel(manager, config, ytDlp);

        Assert.Equal(
            new[] { "全部", "进行中", "等待", "计划", "已暂停", "失败", "已完成" },
            viewModel.QueueFilterOptions);
    }

    [Fact]
    public void QueueSummary_TracksAggregateProgressAndDesignerFilters()
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
        Assert.Equal(3, viewModel.VisibleQueueTasks.Count);
        Assert.Equal(160d / 3d, viewModel.OverallProgress, precision: 6);
        Assert.Equal(1024, viewModel.AggregateSpeed);
        Assert.Contains("已完成 1/3", viewModel.QueueSummaryText, StringComparison.Ordinal);

        viewModel.SetQueueFilterCommand.Execute("进行中");
        Assert.Single(viewModel.VisibleQueueTasks);
        Assert.Equal("https://example.com/running", viewModel.VisibleQueueTasks[0].Url);

        viewModel.SetQueueFilterCommand.Execute("失败");
        Assert.Single(viewModel.VisibleQueueTasks);
        Assert.Equal("https://example.com/failed", viewModel.VisibleQueueTasks[0].Url);

        viewModel.SetQueueFilterCommand.Execute("已完成");
        Assert.Single(viewModel.VisibleQueueTasks);
        Assert.Equal("https://example.com/done", viewModel.VisibleQueueTasks[0].Url);

        viewModel.ClearFinishedCommand.Execute(null);
        Assert.Single(manager.Tasks);
        Assert.Equal(0, viewModel.FinishedTaskCount);
    }

    [Fact]
    public async Task ScheduledTask_IsCountedFilteredAndStoppedAsUnfinished()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        using var manager = new DownloadManager(ytDlp, history, config);
        var viewModel = new BatchDownloadViewModel(manager, config, ytDlp)
        {
            ConfirmFunc = (_, _) => true
        };
        var task = new DownloadTask { Url = "https://example.com/scheduled" };

        await manager.ScheduleAsync(task, DateTimeOffset.UtcNow.AddHours(1));

        Assert.Equal(1, viewModel.ScheduledTaskCount);
        Assert.Equal(1, viewModel.RemainingTaskCount);
        Assert.Equal(0, viewModel.FinishedTaskCount);
        Assert.True(viewModel.CanStopAll);
        Assert.Contains("计划 1", viewModel.QueueSummaryText, StringComparison.Ordinal);

        viewModel.SetQueueFilterCommand.Execute("计划");
        Assert.Same(task, Assert.Single(viewModel.VisibleQueueTasks));

        viewModel.CancelAllCommand.Execute(null);

        Assert.Equal(DownloadStatus.Cancelled, task.Status);
        Assert.Null(task.ScheduledStartTimeUtc);
        Assert.Equal(0, viewModel.ScheduledTaskCount);
        Assert.Equal(0, viewModel.RemainingTaskCount);
        Assert.Equal(1, viewModel.FinishedTaskCount);
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
    public async Task ResolveBatchNames_UsesBoundedConcurrencyAndWaitsForConfirmationBeforeEnqueue()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        config.Config.DefaultDownloadPath = root.Path("downloads");
        var blocker = new BlockingYtDlpDownloadService();
        var manager = new DownloadManager(blocker, history, config);
        var concreteYtDlp = new YtDlpService(config, new EnvironmentService());
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            concreteYtDlp,
            videoInfoProvider: blocker)
        {
            UrlsText = string.Join(
                '\n',
                Enumerable.Range(1, 85).Select(i =>
                    $"https://www.bilibili.com/video/BV1ddN76xEQY/?p={i}"))
        };

        var command = viewModel.ResolveBatchNamesCommand.ExecuteAsync(null);
        await blocker.FirstMetadataRequest.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(manager.Tasks);
        Assert.Equal(85, viewModel.PendingItems.Count);
        blocker.Release();
        await command.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(manager.Tasks);
        Assert.True(viewModel.IsNameConfirmationStep);
        Assert.False(viewModel.IsResolvingNames);
        Assert.Equal(85, blocker.MetadataRequestCount);

        await viewModel.StartBatchDownloadCommand.ExecuteAsync(null);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(85, manager.Tasks.Select(task => task.Url).Distinct().Count());
        Assert.Equal(4, blocker.MaxConcurrentMetadataRequests);
        Assert.Equal(85, blocker.MetadataRequestCount);
        Assert.All(manager.Tasks, task =>
        {
            Assert.Equal("", task.BatchId);
            Assert.Equal("", task.BatchDirectory);
            Assert.Equal("", task.CollectionTitle);
            Assert.Equal(Path.GetFullPath(config.Config.DefaultDownloadPath), task.OutputDirectory);
        });

        var savedHistory = await history.GetAllAsync();
        Assert.Equal(85, savedHistory.Count);
        Assert.All(savedHistory, item =>
        {
            Assert.Equal("", item.BatchId);
            Assert.Equal("", item.BatchDirectory);
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
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            concreteYtDlp,
            videoInfoProvider: service)
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

        await viewModel.ResolveBatchNamesCommand.ExecuteAsync(null);
        Assert.Single(manager.Tasks);
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
    public async Task ResolveBatchNames_RecognizesTitleFirstAndLegacyFormatsWithoutMetadataRequests()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        config.Config.DefaultDownloadPath = root.Path("downloads");
        var service = new BlockingYtDlpDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            ytDlp,
            videoInfoProvider: service)
        {
            UrlsText = string.Join('\n',
            [
                "片头---https://example.com/video---one",
                "https://example.com/two | 旧格式标题"
            ])
        };

        await viewModel.ResolveBatchNamesCommand.ExecuteAsync(null);

        Assert.Empty(manager.Tasks);
        Assert.Equal(0, service.MetadataRequestCount);
        Assert.Equal(
            ["https://example.com/video---one", "https://example.com/two"],
            viewModel.PendingItems.Select(item => item.Url).ToArray());
        Assert.Equal(
            ["片头", "旧格式标题"],
            viewModel.PendingItems.Select(item => item.Title).ToArray());

        viewModel.PendingItems[0].Title = "修改后的片头";
        await viewModel.StartBatchDownloadCommand.ExecuteAsync(null);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(
            ["修改后的片头", "旧格式标题"],
            manager.Tasks.Select(task => task.Title).ToArray());
        Assert.Equal(0, service.MetadataRequestCount);
    }

    [Fact]
    public async Task ResolveBatchNames_PureUrlReusesMetadataAndKeepsEditedTitle()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        config.Config.DefaultDownloadPath = root.Path("downloads");
        var service = new BlockingYtDlpDownloadService
        {
            MetadataTitleFactory = _ => "解析得到的标题"
        };
        service.Release();
        using var manager = new DownloadManager(service, history, config);
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            ytDlp,
            videoInfoProvider: service)
        {
            UrlsText = "https://example.com/video"
        };

        await viewModel.ResolveBatchNamesCommand.ExecuteAsync(null);

        Assert.Empty(manager.Tasks);
        Assert.Equal(1, service.MetadataRequestCount);
        var draft = Assert.Single(viewModel.PendingItems);
        Assert.Equal("解析得到的标题", draft.Title);
        draft.Title = "用户确认的标题";

        await viewModel.StartBatchDownloadCommand.ExecuteAsync(null);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("用户确认的标题", Assert.Single(manager.Tasks).Title);
        Assert.Equal(1, service.MetadataRequestCount);
    }

    [Fact]
    public async Task ResolveBatchNames_FailedItemCanContinueAfterUserSuppliesTitle()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        config.Config.DefaultDownloadPath = root.Path("downloads");
        var service = new BlockingYtDlpDownloadService
        {
            MetadataResultFactory = url => url.EndsWith("/missing", StringComparison.Ordinal)
                ? null
                : new VideoInfo { Url = url, Title = "可用标题", Platform = "generic" }
        };
        service.Release();
        using var manager = new DownloadManager(service, history, config);
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            ytDlp,
            videoInfoProvider: service)
        {
            UrlsText = "https://example.com/missing\nhttps://example.com/available"
        };

        await viewModel.ResolveBatchNamesCommand.ExecuteAsync(null);

        var missing = Assert.Single(
            viewModel.PendingItems,
            item => item.Url.EndsWith("/missing", StringComparison.Ordinal));
        Assert.Equal("", missing.Title);
        Assert.True(missing.HasResolutionMessage);
        Assert.False(viewModel.StartBatchDownloadCommand.CanExecute(null));

        missing.Title = "手动补充标题";
        Assert.True(viewModel.StartBatchDownloadCommand.CanExecute(null));
        await viewModel.StartBatchDownloadCommand.ExecuteAsync(null);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(2, manager.Tasks.Count);
        Assert.Equal("手动补充标题", manager.Tasks.Single(task => task.Url == missing.Url).Title);
        Assert.Equal(2, service.MetadataRequestCount);
    }

    [Fact]
    public async Task UrlsTextChange_CancelsAndInvalidatesPendingNameResolution()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var service = new BlockingYtDlpDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            ytDlp,
            videoInfoProvider: service)
        {
            UrlsText = "https://example.com/old"
        };

        var resolution = viewModel.ResolveBatchNamesCommand.ExecuteAsync(null);
        await service.FirstMetadataRequest.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.UrlsText = "https://example.com/new";
        service.Release();
        await resolution.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsBatchInputStep);
        Assert.Empty(viewModel.PendingItems);
        Assert.Empty(manager.Tasks);
        Assert.Equal("https://example.com/new", viewModel.UrlsText);
    }

    [Fact]
    public async Task PlaylistImport_DuringNameResolution_CannotReplacePendingDrafts()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var service = new BlockingYtDlpDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            ytDlp,
            videoInfoProvider: service)
        {
            UrlsText = "https://example.com/original",
            PlaylistUrl = "https://example.com/replacement-playlist"
        };
        var replacement = new PlaylistInfo
        {
            Title = "不应覆盖的合集",
            SourceUrl = viewModel.PlaylistUrl,
            Urls = ["https://example.com/replacement"]
        };

        var resolution = viewModel.ResolveBatchNamesCommand.ExecuteAsync(null);
        await service.FirstMetadataRequest.WaitAsync(TimeSpan.FromSeconds(2));

        var originalDraft = Assert.Single(viewModel.PendingItems);
        Assert.False(viewModel.ImportPlaylistCommand.CanExecute(null));
        Assert.False(viewModel.ApplyPlaylistImport(replacement));
        Assert.Equal("https://example.com/original", viewModel.UrlsText);
        Assert.Same(originalDraft, Assert.Single(viewModel.PendingItems));

        service.Release();
        await resolution.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsNameConfirmationStep);
        Assert.False(viewModel.ImportPlaylistCommand.CanExecute(null));
        Assert.Same(originalDraft, Assert.Single(viewModel.PendingItems));
    }

    [Fact]
    public async Task PlaylistImport_InProgress_DisablesNameResolutionUntilImportFinishes()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var service = new BlockingYtDlpDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var importStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var importResult = new TaskCompletionSource<PlaylistInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            ytDlp,
            _ => { },
            videoInfoProvider: service,
            getPlaylistInfoAsync: async (_, cancellationToken) =>
            {
                importStarted.TrySetResult();
                return await importResult.Task.WaitAsync(cancellationToken);
            })
        {
            UrlsText = "https://example.com/original",
            PlaylistUrl = "https://example.com/playlist"
        };

        var import = viewModel.ImportPlaylistCommand.ExecuteAsync(null);
        await importStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsImportingPlaylist);
        Assert.False(viewModel.ResolveBatchNamesCommand.CanExecute(null));

        importResult.SetResult(new PlaylistInfo
        {
            Title = "新合集",
            SourceUrl = "https://example.com/playlist",
            Urls = ["https://example.com/playlist-item"]
        });
        await import.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(viewModel.IsImportingPlaylist);
        Assert.Equal("https://example.com/playlist-item", viewModel.UrlsText);
        Assert.True(viewModel.ResolveBatchNamesCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartBatchDownload_UsesImmutableTitlesAndRejectsPlaylistImportWhileAwaitingHistory()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        config.Config.DefaultDownloadPath = root.Path("downloads");
        var service = new BlockingYtDlpDownloadService();
        service.Release();
        using var manager = new DownloadManager(service, history, config);
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var historyReadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHistoryRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            ytDlp,
            _ => { },
            historyService: history,
            duplicateDetector: new DownloadDuplicateDetector(_ => false),
            videoInfoProvider: service,
            loadDownloadHistoryAsync: async () =>
            {
                historyReadStarted.TrySetResult();
                await releaseHistoryRead.Task;
                return [];
            })
        {
            UrlsText = string.Join('\n',
            [
                "确认标题一---https://example.com/one",
                "确认标题二---https://example.com/two"
            ]),
            PlaylistUrl = "https://example.com/replacement-playlist"
        };

        await viewModel.ResolveBatchNamesCommand.ExecuteAsync(null);
        var firstDraft = viewModel.PendingItems[0];
        var secondDraft = viewModel.PendingItems[1];

        var start = viewModel.StartBatchDownloadCommand.ExecuteAsync(null);
        await historyReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsDownloading);
        Assert.False(viewModel.CanEditPendingItems);
        Assert.False(viewModel.RemovePendingItemCommand.CanExecute(firstDraft));
        Assert.False(viewModel.ImportPlaylistCommand.CanExecute(null));
        Assert.False(viewModel.ApplyPlaylistImport(new PlaylistInfo
        {
            Title = "不应覆盖的合集",
            Urls = ["https://example.com/replacement"]
        }));

        firstDraft.Title = "等待期间修改一";
        secondDraft.Title = "等待期间修改二";
        releaseHistoryRead.SetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(3));
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(
            ["确认标题一", "确认标题二"],
            manager.Tasks.Select(task => task.Title).ToArray());
    }

    [Fact]
    public async Task ImportedPlaylist_UsesActualTitleForFolderAndCollectionTasks()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        config.Config.DefaultDownloadPath = root.Path("downloads");
        const string collectionTitle = "【大模型RAG】2026年系统教程！全程干货！";
        var collectionDirectory = root.Path("chosen-rag-collection");
        Directory.CreateDirectory(collectionDirectory);
        var service = new BlockingYtDlpDownloadService();
        service.MetadataPlatform = "Bilibili";
        service.MetadataTitleFactory = url => url.Contains("p=1", StringComparison.Ordinal)
            ? $"{collectionTitle} p01 00.【指南】完整路径"
            : $"{collectionTitle} p02 01.环境安装";
        service.Release();
        var manager = new DownloadManager(service, history, config);
        var concreteYtDlp = new YtDlpService(config, new EnvironmentService());
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            concreteYtDlp,
            videoInfoProvider: service)
        {
            SelectedCollectionFolder = new ExistingCollectionFolder
            {
                BatchId = "batch-rag",
                Name = collectionTitle,
                Directory = collectionDirectory
            }
        };
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
        await viewModel.ResolveBatchNamesCommand.ExecuteAsync(null);
        Assert.Empty(manager.Tasks);
        await viewModel.StartBatchDownloadCommand.ExecuteAsync(null);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, manager.Tasks.Count);
        Assert.All(manager.Tasks, task =>
        {
            Assert.Equal(collectionTitle, task.CollectionTitle);
            Assert.Equal(collectionTitle, task.BatchName);
            Assert.Equal(Path.GetFullPath(collectionDirectory), task.BatchDirectory);
            Assert.Equal(Path.GetFullPath(collectionDirectory), task.OutputDirectory);
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
        var collectionDirectory = root.Path("rag-series");
        Directory.CreateDirectory(collectionDirectory);
        var service = new BlockingYtDlpDownloadService
        {
            MetadataPlatform = "Bilibili"
        };
        service.Release();
        var manager = new DownloadManager(service, history, config);
        var concreteYtDlp = new YtDlpService(config, new EnvironmentService());
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            concreteYtDlp,
            videoInfoProvider: service)
        {
            SelectedCollectionFolder = new ExistingCollectionFolder
            {
                BatchId = "batch-rag-series",
                Name = "RAG 系列课程",
                Directory = collectionDirectory
            }
        };
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
        await viewModel.ResolveBatchNamesCommand.ExecuteAsync(null);
        Assert.Single(manager.Tasks);
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
    public async Task InitializeAsync_LoadsExistingCollectionsAndSelectionFillsDirectory()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var defaultDirectory = root.Path("downloads");
        var collectionDirectory = root.Path("downloads", "RAG 课程");
        Directory.CreateDirectory(defaultDirectory);
        Directory.CreateDirectory(collectionDirectory);
        await history.AddAsync(new DownloadHistory
        {
            Url = "https://example.com/rag/1",
            BatchId = "batch-rag",
            BatchName = "RAG 课程",
            BatchDirectory = collectionDirectory,
            DownloadTime = new DateTime(2026, 7, 30, 10, 0, 0)
        });
        await history.AddAsync(new DownloadHistory
        {
            Url = "https://example.com/rag/2",
            BatchId = "batch-rag",
            BatchName = "RAG 课程",
            BatchDirectory = collectionDirectory,
            DownloadTime = new DateTime(2026, 7, 30, 11, 0, 0)
        });
        await history.AddAsync(new DownloadHistory
        {
            Url = "https://example.com/missing",
            BatchId = "batch-missing",
            BatchName = "已删除合集",
            BatchDirectory = root.Path("missing")
        });
        var config = new ConfigService(root.Path("config"));
        config.Config.DefaultDownloadPath = defaultDirectory;
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var manager = new DownloadManager(ytDlp, history, config);
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            ytDlp,
            historyService: history);

        await viewModel.InitializeAsync();

        var option = Assert.Single(viewModel.ExistingCollectionFolders);
        Assert.Equal("batch-rag", option.BatchId);
        Assert.Equal(2, option.ExistingItemCount);
        viewModel.SelectedCollectionFolder = option;
        Assert.Equal(collectionDirectory, viewModel.DownloadDirectory);

        viewModel.ClearSelectedCollectionFolderCommand.Execute(null);
        Assert.Null(viewModel.SelectedCollectionFolder);
        Assert.Equal(defaultDirectory, viewModel.DownloadDirectory);
    }

    [Fact]
    public async Task InitializeAsync_RestoresConfiguredCollectionWithoutDownloadHistory()
    {
        using var root = new TestDirectory();
        var collectionDirectory = root.Path("saved-collection");
        Directory.CreateDirectory(collectionDirectory);
        var initialConfig = new ConfigService(root.Path("config"));
        initialConfig.Config.DefaultDownloadPath = root.Path("temporary");
        Assert.True(await initialConfig.UpdateSelectedCollectionDirectoryAsync(
            collectionDirectory));

        var reloadedConfig = new ConfigService(root.Path("config"));
        await reloadedConfig.LoadAsync();
        using var history = new HistoryService(root.Path("history.db"));
        var ytDlp = new YtDlpService(reloadedConfig, new EnvironmentService());
        using var manager = new DownloadManager(ytDlp, history, reloadedConfig);
        var viewModel = new BatchDownloadViewModel(
            manager,
            reloadedConfig,
            ytDlp,
            historyService: history);

        await viewModel.InitializeAsync();

        var selected = Assert.IsType<ExistingCollectionFolder>(
            viewModel.SelectedCollectionFolder);
        Assert.Equal(Path.GetFullPath(collectionDirectory), selected.Directory);
        Assert.Equal(Path.GetFullPath(collectionDirectory), viewModel.DownloadDirectory);
        Assert.Contains(
            viewModel.ExistingCollectionFolders,
            folder => ExistingCollectionFolderStore.PathsEqual(
                folder.Directory,
                collectionDirectory));
    }

    [Fact]
    public async Task RefreshCollections_DoesNotPersistTransientComboBoxNullSelection()
    {
        using var root = new TestDirectory();
        var collectionDirectory = root.Path("kept-collection");
        Directory.CreateDirectory(collectionDirectory);
        var config = new ConfigService(root.Path("config"));
        config.Config.DefaultDownloadPath = root.Path("temporary");
        Assert.True(await config.UpdateSelectedCollectionDirectoryAsync(
            collectionDirectory));
        using var history = new HistoryService(root.Path("history.db"));
        using var store = new ExistingCollectionFolderStore(history, config);
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        using var manager = new DownloadManager(ytDlp, history, config);
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            ytDlp,
            historyService: history,
            collectionFolderStore: store);
        await viewModel.InitializeAsync();
        Assert.NotNull(viewModel.SelectedCollectionFolder);
        store.FoldersRefreshing += (_, _) =>
        {
            // WPF clears a TwoWay SelectedItem while its ItemsSource is rebuilt.
            viewModel.SelectedCollectionFolder = null;
        };

        await store.RefreshAsync();

        Assert.NotNull(viewModel.SelectedCollectionFolder);
        Assert.Equal(
            Path.GetFullPath(collectionDirectory),
            viewModel.SelectedCollectionFolder!.Directory);
        Assert.Equal(
            Path.GetFullPath(collectionDirectory),
            config.Config.SelectedCollectionDirectory);
    }

    [Fact]
    public async Task BrowseDirectory_RegistersAndSelectsNewCollection()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        config.Config.DefaultDownloadPath = root.Path("default");
        var selectedDirectory = root.Path("selected-collection");
        var browsedDirectory = root.Path("custom-root");
        Directory.CreateDirectory(selectedDirectory);
        Directory.CreateDirectory(browsedDirectory);
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var manager = new DownloadManager(ytDlp, history, config);
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            ytDlp,
            _ => { },
            selectDirectory: _ => browsedDirectory)
        {
            SelectedCollectionFolder = new ExistingCollectionFolder
            {
                BatchId = "batch-existing",
                Name = "已有合集",
                Directory = selectedDirectory
            }
        };

        await viewModel.BrowseDirectoryCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.SelectedCollectionFolder);
        Assert.Equal(Path.GetFullPath(browsedDirectory), viewModel.SelectedCollectionFolder!.Directory);
        Assert.Equal(browsedDirectory, viewModel.DownloadDirectory);
        Assert.Equal(Path.GetFullPath(browsedDirectory), config.Config.SelectedCollectionDirectory);
        Assert.Equal(root.Path("default"), config.Config.DefaultDownloadPath);
    }

    [Fact]
    public async Task BrowseDirectory_SynchronizesBothPagesAndPersistsLastSelection()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        config.Config.DefaultDownloadPath = root.Path("initial");
        var singleSelection = root.Path("single-selection");
        var batchSelection = root.Path("batch-selection");
        Directory.CreateDirectory(singleSelection);
        Directory.CreateDirectory(batchSelection);
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        using var manager = new DownloadManager(ytDlp, history, config);
        var single = new DownloadViewModel(
            manager,
            config,
            new YtDlpVideoInfoProvider(ytDlp),
            _ => { },
            selectDirectory: _ => singleSelection);
        var batch = new BatchDownloadViewModel(
            manager,
            config,
            ytDlp,
            _ => { },
            selectDirectory: _ => batchSelection);

        await single.BrowseDirectoryCommand.ExecuteAsync(null);

        Assert.Equal(singleSelection, single.DownloadDirectory);
        Assert.Equal(singleSelection, batch.DownloadDirectory);
        Assert.Equal(root.Path("initial"), config.Config.DefaultDownloadPath);
        Assert.Equal(Path.GetFullPath(singleSelection), config.Config.SelectedCollectionDirectory);

        await batch.BrowseDirectoryCommand.ExecuteAsync(null);

        Assert.Equal(batchSelection, single.DownloadDirectory);
        Assert.Equal(batchSelection, batch.DownloadDirectory);
        Assert.Equal(root.Path("initial"), config.Config.DefaultDownloadPath);
        Assert.Equal(Path.GetFullPath(batchSelection), config.Config.SelectedCollectionDirectory);
        var reloaded = new ConfigService(config.ConfigDirectory);
        await reloaded.LoadAsync();
        Assert.Equal(root.Path("initial"), reloaded.Config.DefaultDownloadPath);
        Assert.Equal(Path.GetFullPath(batchSelection), reloaded.Config.SelectedCollectionDirectory);
        Assert.Contains(
            reloaded.Config.CollectionDirectories,
            path => ExistingCollectionFolderStore.PathsEqual(path, batchSelection));

        batch.ClearSelectedCollectionFolderCommand.Execute(null);
        Assert.Null(single.SelectedCollectionFolder);
        Assert.Null(batch.SelectedCollectionFolder);
        Assert.Equal(root.Path("initial"), single.DownloadDirectory);
        Assert.Equal(root.Path("initial"), batch.DownloadDirectory);
        Assert.Equal("", config.Config.SelectedCollectionDirectory);
        Assert.True(await config.SaveAsync());
        var temporaryReload = new ConfigService(config.ConfigDirectory);
        await temporaryReload.LoadAsync();
        Assert.Equal("", temporaryReload.Config.SelectedCollectionDirectory);
    }

    [Fact]
    public void SharedRootChange_PreservesCollectionOverrideUntilItIsCleared()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var initialRoot = root.Path("initial-root");
        var updatedRoot = root.Path("updated-root");
        var collectionDirectory = root.Path("initial-root", "existing-collection");
        Directory.CreateDirectory(collectionDirectory);
        var config = new ConfigService(root.Path("config"));
        config.Config.DefaultDownloadPath = initialRoot;
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        using var manager = new DownloadManager(ytDlp, history, config);
        var viewModel = new BatchDownloadViewModel(manager, config, ytDlp)
        {
            SelectedCollectionFolder = new ExistingCollectionFolder
            {
                BatchId = "batch-existing",
                Name = "已有合集",
                Directory = collectionDirectory
            }
        };

        config.UpdateDefaultDownloadPath(updatedRoot);

        Assert.Equal(collectionDirectory, viewModel.DownloadDirectory);
        Assert.Equal(updatedRoot, config.Config.DefaultDownloadPath);

        viewModel.ClearSelectedCollectionFolderCommand.Execute(null);

        Assert.Null(viewModel.SelectedCollectionFolder);
        Assert.Equal(updatedRoot, viewModel.DownloadDirectory);
    }

    [Fact]
    public void DestinationControls_AreLockedWhileBatchIsBeingCreated()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var collectionDirectory = root.Path("collection");
        Directory.CreateDirectory(collectionDirectory);
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var manager = new DownloadManager(ytDlp, history, config);
        var viewModel = new BatchDownloadViewModel(manager, config, ytDlp)
        {
            SelectedCollectionFolder = new ExistingCollectionFolder
            {
                BatchId = "batch-existing",
                Name = "已有合集",
                Directory = collectionDirectory
            },
            IsDownloading = true
        };

        Assert.False(viewModel.CanEditBatchDestination);
        Assert.False(viewModel.CanSelectExistingCollectionFolder);
        Assert.False(viewModel.BrowseDirectoryCommand.CanExecute(null));
        Assert.False(viewModel.RefreshExistingCollectionFoldersCommand.CanExecute(null));
        Assert.False(viewModel.ClearSelectedCollectionFolderCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartBatchDownload_TemporaryModeUsesDefaultDirectoryWithoutSubfolders()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var defaultDirectory = root.Path("default");
        config.Config.DefaultDownloadPath = defaultDirectory;
        var service = new BlockingYtDlpDownloadService();
        service.Release();
        var manager = new DownloadManager(service, history, config);
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            ytDlp,
            videoInfoProvider: service)
        {
            UrlsText = "https://example.com/one\nhttps://example.com/two"
        };

        await viewModel.ResolveBatchNamesCommand.ExecuteAsync(null);
        Assert.Empty(manager.Tasks);
        await viewModel.StartBatchDownloadCommand.ExecuteAsync(null);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.All(manager.Tasks, task =>
        {
            Assert.Equal(Path.GetFullPath(defaultDirectory), task.OutputDirectory);
            Assert.Equal("", task.BatchId);
            Assert.Equal("", task.BatchDirectory);
            Assert.Equal("", task.CollectionTitle);
        });
        Assert.Equal(defaultDirectory, config.Config.DefaultDownloadPath);
        Assert.Equal(defaultDirectory, viewModel.DownloadDirectory);
    }

    [Fact]
    public async Task StartBatchDownload_ReusesExistingCollectionWithoutNestedDirectory()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var collectionDirectory = root.Path("downloads", "RAG 课程");
        Directory.CreateDirectory(collectionDirectory);
        var service = new BlockingYtDlpDownloadService();
        service.Release();
        var manager = new DownloadManager(service, history, config);
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            ytDlp,
            videoInfoProvider: service)
        {
            UrlsText = "https://example.com/new-part",
            SelectedCollectionFolder = new ExistingCollectionFolder
            {
                BatchId = "batch-rag",
                Name = "RAG 课程",
                Directory = collectionDirectory,
                ExistingItemCount = 5
            }
        };

        await viewModel.ResolveBatchNamesCommand.ExecuteAsync(null);
        Assert.Empty(manager.Tasks);
        await viewModel.StartBatchDownloadCommand.ExecuteAsync(null);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        var task = Assert.Single(manager.Tasks);
        Assert.Equal("batch-rag", task.BatchId);
        Assert.Equal("RAG 课程", task.BatchName);
        Assert.Equal(Path.GetFullPath(collectionDirectory), task.BatchDirectory);
        Assert.Equal(Path.GetFullPath(collectionDirectory), task.OutputDirectory);
        Assert.Equal(0, task.CollectionItemIndex);
        Assert.Equal(0, task.CollectionItemCount);
        Assert.Equal(5, viewModel.SelectedCollectionFolder!.ExistingItemCount);
    }

    [Fact]
    public async Task StartBatchDownload_WhenSelectedCollectionWasDeleted_DoesNotRecreateIt()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var collectionDirectory = root.Path("deleted-collection");
        Directory.CreateDirectory(collectionDirectory);
        var service = new BlockingYtDlpDownloadService();
        service.Release();
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var manager = new DownloadManager(service, history, config);
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            ytDlp,
            videoInfoProvider: service)
        {
            UrlsText = "https://example.com/new-part",
            SelectedCollectionFolder = new ExistingCollectionFolder
            {
                BatchId = "batch-deleted",
                Name = "已删除合集",
                Directory = collectionDirectory
            }
        };
        string? notification = null;
        viewModel.RequestShowNotification += (message, _) => notification = message;
        Directory.Delete(collectionDirectory);

        await viewModel.ResolveBatchNamesCommand.ExecuteAsync(null);
        await viewModel.StartBatchDownloadCommand.ExecuteAsync(null);

        Assert.Empty(manager.Tasks);
        Assert.False(Directory.Exists(collectionDirectory));
        Assert.Contains("已不存在", notification, StringComparison.Ordinal);
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
        Assert.Contains("Text=\"{Binding Title, UpdateSourceTrigger=PropertyChanged}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("视频标题---视频链接", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ResolveBatchNamesCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding StartBatchDownloadCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DataContext.CanEditPendingItems", xaml, StringComparison.Ordinal);
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
    public void ImportText_PreservesTitleFirstEntryAndDeduplicatesByUrl()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        using var manager = new DownloadManager(ytDlp, history, config);
        var viewModel = new BatchDownloadViewModel(manager, config, ytDlp);

        viewModel.ImportText(string.Join('\n',
        [
            "演示视频---https://example.com/demo",
            "https://example.com/demo"
        ]));

        Assert.Equal("演示视频---https://example.com/demo", viewModel.UrlsText);
        Assert.Equal(1, viewModel.LinkCount);
    }

    [Theory]
    [InlineData("", "https://example.com/demo\n演示视频---https://example.com/demo")]
    [InlineData("https://example.com/demo", "演示视频---https://example.com/demo")]
    public void ImportText_WhenBareUrlPrecedesExplicitTitle_PrefersExplicitTitle(
        string existingText,
        string importedText)
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        using var manager = new DownloadManager(ytDlp, history, config);
        var viewModel = new BatchDownloadViewModel(manager, config, ytDlp)
        {
            UrlsText = existingText
        };

        viewModel.ImportText(importedText);

        Assert.Equal("演示视频---https://example.com/demo", viewModel.UrlsText);
        Assert.Equal(1, viewModel.LinkCount);
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
    public void PasteUrlsCommand_WhenClipboardIsBusy_LeavesCurrentUrlsUnchanged()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var ytDlp = new YtDlpService(config, new EnvironmentService());
        var manager = new DownloadManager(ytDlp, history, config);
        var viewModel = new BatchDownloadViewModel(
            manager,
            config,
            ytDlp,
            _ => { },
            () => throw new COMException("Clipboard is busy"))
        {
            UrlsText = "https://example.com/current"
        };

        var exception = Record.Exception(() =>
            viewModel.PasteUrlsCommand.Execute(null));

        Assert.Null(exception);
        Assert.Equal("https://example.com/current", viewModel.UrlsText);
        Assert.Equal(1, viewModel.LinkCount);
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

    private sealed class BlockingYtDlpDownloadService : IYtDlpDownloadService, IVideoInfoProvider
    {
        private readonly TaskCompletionSource _first = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeMetadataRequests;
        private int _maxConcurrentMetadataRequests;
        private int _metadataRequestCount;

        public Task FirstMetadataRequest => _first.Task;
        public int MaxConcurrentMetadataRequests => Volatile.Read(
            ref _maxConcurrentMetadataRequests);
        public int MetadataRequestCount => Volatile.Read(ref _metadataRequestCount);
        public Func<string, string> MetadataTitleFactory { get; set; } = url => url;
        public Func<string, VideoInfo?>? MetadataResultFactory { get; set; }
        public string MetadataPlatform { get; set; } = "Twitter";

        public async Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _metadataRequestCount);
            var active = Interlocked.Increment(ref _activeMetadataRequests);
            UpdateMaximum(ref _maxConcurrentMetadataRequests, active);
            _first.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                if (MetadataResultFactory is not null)
                    return MetadataResultFactory(url);

                return new VideoInfo
                {
                    Title = MetadataTitleFactory(url),
                    Platform = MetadataPlatform,
                    Url = url
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
