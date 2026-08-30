using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public sealed class CollectionDownloadStateTests
{
    [Theory]
    [InlineData(false, CollectionSubscriptionItemState.Downloaded)]
    [InlineData(true, CollectionSubscriptionItemState.Failed)]
    public async Task FinishedCollectionTask_PersistsTerminalItemState(
        bool fail,
        CollectionSubscriptionItemState expectedState)
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var outputDirectory = root.Path("downloads");
        Directory.CreateDirectory(outputDirectory);
        var subscription = await SeedSubscriptionAsync(history, outputDirectory);
        var config = new ConfigService(root.Path("config"));
        using var manager = new DownloadManager(new TerminalDownloadService(fail), history, config);
        var task = CreateTask(subscription, outputDirectory);
        var stateObservedAtFinished = new TaskCompletionSource<CollectionSubscriptionItemState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TaskFinished += finishedTask =>
        {
            if (!ReferenceEquals(task, finishedTask))
                return;

            try
            {
                var storedAtFinished = history.GetCollectionSubscriptionAsync(subscription.Id)
                    .GetAwaiter()
                    .GetResult();
                stateObservedAtFinished.TrySetResult(Assert.Single(storedAtFinished!.Items).State);
            }
            catch (Exception ex)
            {
                stateObservedAtFinished.TrySetException(ex);
            }
        };
        await history.UpdateCollectionSubscriptionItemStateAsync(
            subscription.Id,
            "entry-1",
            CollectionSubscriptionItemState.Queued,
            task.Id);

        await manager.EnqueueAsync(task, new VideoInfo
        {
            Url = task.Url,
            Title = task.Title,
            Platform = "Example"
        });
        await manager.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(
            expectedState,
            await stateObservedAtFinished.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        var stored = await history.GetCollectionSubscriptionAsync(subscription.Id);
        var item = Assert.Single(stored!.Items);
        Assert.Equal(expectedState, item.State);
        Assert.Empty(item.TaskId);
    }

    [Fact]
    public async Task CancelledCollectionTask_ReturnsItemToNewState()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var outputDirectory = root.Path("downloads");
        Directory.CreateDirectory(outputDirectory);
        var subscription = await SeedSubscriptionAsync(history, outputDirectory);
        var config = new ConfigService(root.Path("config"));
        var service = new CancellableDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var task = CreateTask(subscription, outputDirectory);
        await history.UpdateCollectionSubscriptionItemStateAsync(
            subscription.Id,
            "entry-1",
            CollectionSubscriptionItemState.Queued,
            task.Id);

        await manager.EnqueueAsync(task, new VideoInfo
        {
            Url = task.Url,
            Title = task.Title,
            Platform = "Example"
        });
        await service.Started.WaitAsync(TimeSpan.FromSeconds(3));
        manager.Cancel(task.Id);
        await manager.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        var stored = await history.GetCollectionSubscriptionAsync(subscription.Id);
        var item = Assert.Single(stored!.Items);
        Assert.Equal(CollectionSubscriptionItemState.New, item.State);
        Assert.Empty(item.TaskId);
    }

    [Fact]
    public async Task CancelFinishingPause_WaitsForCleanupAndReturnsItemToNewState()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var outputDirectory = root.Path("downloads");
        Directory.CreateDirectory(outputDirectory);
        var subscription = await SeedSubscriptionAsync(history, outputDirectory);
        var config = new ConfigService(root.Path("config"));
        var service = new PauseCleanupGateDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var task = CreateTask(subscription, outputDirectory);
        await history.UpdateCollectionSubscriptionItemStateAsync(
            subscription.Id,
            "entry-1",
            CollectionSubscriptionItemState.Queued,
            task.Id);

        await manager.EnqueueAsync(task, new VideoInfo
        {
            Url = task.Url,
            Title = task.Title,
            Platform = "Example"
        });
        await service.Started.WaitAsync(TimeSpan.FromSeconds(3));

        manager.Pause(task.Id);
        await service.CancellationCallbackStarted.WaitAsync(TimeSpan.FromSeconds(3));
        service.FinishAttempt();
        Assert.True(await WaitUntilAsync(() => task.Cts is null));

        var cancellation = manager.CancelAsync(task.Id);
        Assert.Equal(DownloadStatus.Cancelled, task.Status);
        await Task.Delay(50);
        Assert.False(cancellation.IsCompleted);

        service.ReleaseCancellation();
        await cancellation.WaitAsync(TimeSpan.FromSeconds(3));
        await manager.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        var stored = await history.GetCollectionSubscriptionAsync(subscription.Id);
        var item = Assert.Single(stored!.Items);
        Assert.Equal(CollectionSubscriptionItemState.New, item.State);
        Assert.Empty(item.TaskId);
    }

    [Fact]
    public async Task FinishedOldTask_DoesNotOverwriteNewTaskCorrelation()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var outputDirectory = root.Path("downloads");
        Directory.CreateDirectory(outputDirectory);
        var subscription = await SeedSubscriptionAsync(history, outputDirectory);
        var config = new ConfigService(root.Path("config"));
        var service = new CompletableDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var oldTask = CreateTask(subscription, outputDirectory);
        await history.UpdateCollectionSubscriptionItemStateAsync(
            subscription.Id,
            "entry-1",
            CollectionSubscriptionItemState.Queued,
            oldTask.Id);

        await manager.EnqueueAsync(oldTask, new VideoInfo
        {
            Url = oldTask.Url,
            Title = oldTask.Title,
            Platform = "Example"
        });
        await service.Started.WaitAsync(TimeSpan.FromSeconds(3));
        await history.UpdateCollectionSubscriptionItemStateAsync(
            subscription.Id,
            "entry-1",
            CollectionSubscriptionItemState.Queued,
            "newer-task");

        service.Release();
        await manager.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        var stored = await history.GetCollectionSubscriptionAsync(subscription.Id);
        var item = Assert.Single(stored!.Items);
        Assert.Equal(CollectionSubscriptionItemState.Queued, item.State);
        Assert.Equal("newer-task", item.TaskId);
    }

    [Fact]
    public async Task TerminalPersistence_CompletesBeforeTaskFinishedAndIdle()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var outputDirectory = root.Path("downloads");
        Directory.CreateDirectory(outputDirectory);
        var subscription = await SeedSubscriptionAsync(history, outputDirectory);
        var config = new ConfigService(root.Path("config"));
        var service = new ControlledFailureDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var task = CreateTask(subscription, outputDirectory);
        await history.UpdateCollectionSubscriptionItemStateAsync(
            subscription.Id,
            "entry-1",
            CollectionSubscriptionItemState.Queued,
            task.Id);
        var taskFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TaskFinished += finishedTask =>
        {
            if (ReferenceEquals(task, finishedTask))
                taskFinished.TrySetResult();
        };

        await manager.EnqueueAsync(task, new VideoInfo
        {
            Url = task.Url,
            Title = task.Title,
            Platform = "Example"
        });
        await service.Started.WaitAsync(TimeSpan.FromSeconds(3));
        var connectionGate = GetConnectionGate(history);
        await connectionGate.WaitAsync();
        var idle = manager.WaitForIdleAsync(CancellationToken.None);
        try
        {
            service.Fail();
            Assert.True(await WaitUntilAsync(
                () => task.Status == DownloadStatus.Failed && task.Cts is null));
            await Task.Delay(50);
            Assert.False(taskFinished.Task.IsCompleted);
            Assert.False(idle.IsCompleted);
        }
        finally
        {
            connectionGate.Release();
        }

        await taskFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await idle.WaitAsync(TimeSpan.FromSeconds(3));
        var stored = await history.GetCollectionSubscriptionAsync(subscription.Id);
        var item = Assert.Single(stored!.Items);
        Assert.Equal(CollectionSubscriptionItemState.Failed, item.State);
        Assert.Empty(item.TaskId);
    }

    [Fact]
    public async Task RetryAsync_MarksCollectionItemQueuedBeforeDownloadStarts()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var outputDirectory = root.Path("downloads");
        Directory.CreateDirectory(outputDirectory);
        var subscription = await SeedSubscriptionAsync(history, outputDirectory);
        var config = new ConfigService(root.Path("config"));
        var service = new CompletableDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var task = CreateTask(subscription, outputDirectory);
        task.Status = DownloadStatus.Failed;
        manager.Tasks.Add(task);
        await history.UpdateCollectionSubscriptionItemStateAsync(
            subscription.Id,
            "entry-1",
            CollectionSubscriptionItemState.Failed,
            "");

        await manager.RetryAsync(task.Id);
        await service.Started.WaitAsync(TimeSpan.FromSeconds(3));

        var queuedSubscription = await history.GetCollectionSubscriptionAsync(subscription.Id);
        var queuedItem = Assert.Single(queuedSubscription!.Items);
        Assert.Equal(CollectionSubscriptionItemState.Queued, queuedItem.State);
        Assert.Equal(task.Id, queuedItem.TaskId);

        service.Release();
        await manager.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        var completedSubscription = await history.GetCollectionSubscriptionAsync(subscription.Id);
        var completedItem = Assert.Single(completedSubscription!.Items);
        Assert.Equal(CollectionSubscriptionItemState.Downloaded, completedItem.State);
        Assert.Empty(completedItem.TaskId);
    }

    [Fact]
    public async Task RestoreAsync_ReconcilesOrphanedAndTerminalQueuedItems()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var outputDirectory = root.Path("downloads");
        Directory.CreateDirectory(outputDirectory);
        var subscription = await history.UpsertCollectionSubscriptionAsync(
            new CollectionSubscription
            {
                CanonicalKey = "extractor:example:collection-restore",
                SourceUrl = "https://example.test/collection/restore",
                Title = "Restore collection",
                OutputDirectory = outputDirectory
            },
            [
                new CollectionSubscriptionItemSnapshot
                {
                    EntryKey = "restored-entry",
                    EntryId = "restored-video",
                    Url = "https://example.test/video/restored",
                    Title = "Restored video",
                    Position = 1
                },
                new CollectionSubscriptionItemSnapshot
                {
                    EntryKey = "orphaned-entry",
                    EntryId = "orphaned-video",
                    Url = "https://example.test/video/orphaned",
                    Title = "Orphaned video",
                    Position = 2
                },
                new CollectionSubscriptionItemSnapshot
                {
                    EntryKey = "failed-entry",
                    EntryId = "failed-video",
                    Url = "https://example.test/video/failed",
                    Title = "Failed video",
                    Position = 3
                },
                new CollectionSubscriptionItemSnapshot
                {
                    EntryKey = "cancelled-entry",
                    EntryId = "cancelled-video",
                    Url = "https://example.test/video/cancelled",
                    Title = "Cancelled video",
                    Position = 4
                }
            ]);
        await history.UpdateCollectionSubscriptionItemStatesAsync(
            subscription.Id,
            [
                new CollectionSubscriptionItemStateUpdate
                {
                    EntryKey = "restored-entry",
                    State = CollectionSubscriptionItemState.Queued,
                    TaskId = "restored-task"
                },
                new CollectionSubscriptionItemStateUpdate
                {
                    EntryKey = "orphaned-entry",
                    State = CollectionSubscriptionItemState.Queued,
                    TaskId = "missing-task"
                },
                new CollectionSubscriptionItemStateUpdate
                {
                    EntryKey = "failed-entry",
                    State = CollectionSubscriptionItemState.Queued,
                    TaskId = "failed-task"
                },
                new CollectionSubscriptionItemStateUpdate
                {
                    EntryKey = "cancelled-entry",
                    State = CollectionSubscriptionItemState.Queued,
                    TaskId = "cancelled-task"
                }
            ]);
        var statePath = root.Path("queue-state.json");
        using (var seedPersistence = new TaskQueuePersistenceService(statePath, TimeSpan.Zero))
        {
            await seedPersistence.FlushAsync([
                new DownloadTask
                {
                    Id = "restored-task",
                    Url = "https://example.test/video/restored",
                    Status = DownloadStatus.Downloading,
                    CollectionSubscriptionId = subscription.Id,
                    CollectionEntryKey = "restored-entry"
                },
                new DownloadTask
                {
                    Id = "failed-task",
                    Url = "https://example.test/video/failed",
                    Status = DownloadStatus.Failed,
                    CollectionSubscriptionId = subscription.Id,
                    CollectionEntryKey = "failed-entry"
                },
                new DownloadTask
                {
                    Id = "cancelled-task",
                    Url = "https://example.test/video/cancelled",
                    Status = DownloadStatus.Cancelled,
                    CollectionSubscriptionId = subscription.Id,
                    CollectionEntryKey = "cancelled-entry"
                }
            ]);
        }

        using var persistence = new TaskQueuePersistenceService(statePath, TimeSpan.Zero);
        var config = new ConfigService(root.Path("config"));
        using var manager = new DownloadManager(
            new TerminalDownloadService(fail: false),
            history,
            config,
            taskQueuePersistence: persistence);

        Assert.Equal(3, await manager.RestoreAsync());

        var restored = await history.GetCollectionSubscriptionAsync(subscription.Id);
        Assert.NotNull(restored);
        var matchedItem = restored.Items.Single(candidate => candidate.EntryKey == "restored-entry");
        Assert.Equal(CollectionSubscriptionItemState.Queued, matchedItem.State);
        Assert.Equal("restored-task", matchedItem.TaskId);
        var orphanedItem = restored.Items.Single(candidate => candidate.EntryKey == "orphaned-entry");
        Assert.Equal(CollectionSubscriptionItemState.New, orphanedItem.State);
        Assert.Empty(orphanedItem.TaskId);
        var failedItem = restored.Items.Single(candidate => candidate.EntryKey == "failed-entry");
        Assert.Equal(CollectionSubscriptionItemState.Failed, failedItem.State);
        Assert.Empty(failedItem.TaskId);
        var cancelledItem = restored.Items.Single(candidate => candidate.EntryKey == "cancelled-entry");
        Assert.Equal(CollectionSubscriptionItemState.New, cancelledItem.State);
        Assert.Empty(cancelledItem.TaskId);

        var finished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TaskFinished += task =>
        {
            if (task.Id == "restored-task")
                finished.TrySetResult();
        };

        await manager.CancelAsync("restored-task");
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var cancelledAfterRestore = await history.GetCollectionSubscriptionAsync(subscription.Id);
        var restoredItem = cancelledAfterRestore!.Items.Single(candidate =>
            candidate.EntryKey == "restored-entry");
        Assert.Equal(CollectionSubscriptionItemState.New, restoredItem.State);
        Assert.Empty(restoredItem.TaskId);
    }

    [Fact]
    public async Task RestoreAsync_LockedQueueStateDoesNotReconcileCollectionItems()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var outputDirectory = root.Path("downloads");
        Directory.CreateDirectory(outputDirectory);
        var subscription = await SeedSubscriptionAsync(history, outputDirectory);
        const string taskId = "temporarily-unreadable-task";
        await history.UpdateCollectionSubscriptionItemStateAsync(
            subscription.Id,
            "entry-1",
            CollectionSubscriptionItemState.Queued,
            taskId);
        var statePath = root.Path("queue-state.json");
        using (var seedPersistence = new TaskQueuePersistenceService(statePath, TimeSpan.Zero))
        {
            await seedPersistence.FlushAsync([
                new DownloadTask
                {
                    Id = taskId,
                    Url = "https://example.test/video/1",
                    Status = DownloadStatus.Paused,
                    CollectionSubscriptionId = subscription.Id,
                    CollectionEntryKey = "entry-1"
                }
            ]);
        }

        await using var stateLock = new FileStream(
            statePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        using var persistence = new TaskQueuePersistenceService(statePath, TimeSpan.Zero);
        var config = new ConfigService(root.Path("config"));
        using var manager = new DownloadManager(
            new TerminalDownloadService(fail: false),
            history,
            config,
            taskQueuePersistence: persistence);

        Assert.Equal(0, await manager.RestoreAsync());

        var stored = await history.GetCollectionSubscriptionAsync(subscription.Id);
        var item = Assert.Single(stored!.Items);
        Assert.Equal(CollectionSubscriptionItemState.Queued, item.State);
        Assert.Equal(taskId, item.TaskId);
        Assert.True(File.Exists(statePath));

        await stateLock.DisposeAsync();
        manager.Dispose();

        var preservedTask = Assert.Single(await persistence.RestoreAsync());
        Assert.Equal(taskId, preservedTask.Id);
    }

    [Fact]
    public async Task Dispose_PreservesQueuedCollectionStateForPersistedPausedTask()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var outputDirectory = root.Path("downloads");
        Directory.CreateDirectory(outputDirectory);
        var subscription = await SeedSubscriptionAsync(history, outputDirectory);
        var task = CreateTask(subscription, outputDirectory);
        task.Status = DownloadStatus.Paused;
        await history.UpdateCollectionSubscriptionItemStateAsync(
            subscription.Id,
            "entry-1",
            CollectionSubscriptionItemState.Queued,
            task.Id);
        using var persistence = new TaskQueuePersistenceService(
            root.Path("queue-state.json"),
            TimeSpan.Zero);
        var config = new ConfigService(root.Path("config"));
        using var manager = new DownloadManager(
            new TerminalDownloadService(fail: false),
            history,
            config,
            taskQueuePersistence: persistence);
        manager.Tasks.Add(task);

        manager.Dispose();

        var stored = await history.GetCollectionSubscriptionAsync(subscription.Id);
        var item = Assert.Single(stored!.Items);
        Assert.Equal(CollectionSubscriptionItemState.Queued, item.State);
        Assert.Equal(task.Id, item.TaskId);
        var persistedTask = Assert.Single(await persistence.RestoreAsync());
        Assert.Equal(DownloadStatus.Paused, persistedTask.Status);
    }

    private static async Task<CollectionSubscription> SeedSubscriptionAsync(
        HistoryService history,
        string outputDirectory)
        => await history.UpsertCollectionSubscriptionAsync(
            new CollectionSubscription
            {
                CanonicalKey = "extractor:example:collection-1",
                SourceUrl = "https://example.test/collection/1",
                Title = "Collection",
                OutputDirectory = outputDirectory,
                BatchId = "batch-1",
                BatchName = "Collection"
            },
            [new CollectionSubscriptionItemSnapshot
            {
                EntryKey = "entry-1",
                EntryId = "video-1",
                Url = "https://example.test/video/1",
                Title = "Video 1",
                Position = 1
            }]);

    private static DownloadTask CreateTask(
        CollectionSubscription subscription,
        string outputDirectory)
        => new()
        {
            Url = "https://example.test/video/1",
            Title = "Video 1",
            OutputDirectory = outputDirectory,
            CollectionSubscriptionId = subscription.Id,
            CollectionEntryKey = "entry-1"
        };

    private static SemaphoreSlim GetConnectionGate(HistoryService history)
    {
        var field = typeof(HistoryService).GetField(
            "_connectionGate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<SemaphoreSlim>(field!.GetValue(history));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return true;
            await Task.Delay(20);
        }

        return false;
    }

    private sealed class TerminalDownloadService(bool fail) : IYtDlpDownloadService
    {
        public Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
            => Task.FromResult<VideoInfo?>(new VideoInfo { Url = url, Title = "Video 1" });

        public Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            if (fail)
                throw new InvalidOperationException("simulated failure");

            task.Status = DownloadStatus.Completed;
            return Task.CompletedTask;
        }
    }

    private sealed class CancellableDownloadService : IYtDlpDownloadService
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
            => Task.FromResult<VideoInfo?>(new VideoInfo { Url = url, Title = "Video 1" });

        public async Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            task.Status = DownloadStatus.Downloading;
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class PauseCleanupGateDownloadService : IYtDlpDownloadService
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationCallbackStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCancellation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finishAttempt = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task CancellationCallbackStarted => _cancellationCallbackStarted.Task;

        public Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
            => Task.FromResult<VideoInfo?>(new VideoInfo { Url = url, Title = "Video 1" });

        public async Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken.Register(() =>
            {
                _cancellationCallbackStarted.TrySetResult();
                _releaseCancellation.Task.GetAwaiter().GetResult();
            });
            task.Status = DownloadStatus.Downloading;
            _started.TrySetResult();
            await _finishAttempt.Task;
            throw new OperationCanceledException(cancellationToken);
        }

        public void FinishAttempt() => _finishAttempt.TrySetResult();

        public void ReleaseCancellation() => _releaseCancellation.TrySetResult();
    }

    private sealed class CompletableDownloadService : IYtDlpDownloadService
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
            => Task.FromResult<VideoInfo?>(new VideoInfo { Url = url, Title = "Video 1" });

        public async Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            task.Status = DownloadStatus.Downloading;
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            task.Status = DownloadStatus.Completed;
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class ControlledFailureDownloadService : IYtDlpDownloadService
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _fail = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
            => Task.FromResult<VideoInfo?>(new VideoInfo { Url = url, Title = "Video 1" });

        public async Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            task.Status = DownloadStatus.Downloading;
            _started.TrySetResult();
            await _fail.Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("simulated failure");
        }

        public void Fail() => _fail.TrySetResult();
    }
}
