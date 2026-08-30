using EasyGet.Models;
using EasyGet.Services;
using EasyGet.ViewModels;
using Xunit;

namespace EasyGet.Tests;

public sealed class CollectionUpdatesViewModelTests
{
    [Fact]
    public async Task InitializeAsync_LoadsOnlyPresentNewAndFailedItems()
    {
        await using var fixture = new Fixture();
        var outputDirectory = fixture.CreateDirectory("downloads");
        await fixture.SeedSubscriptionAsync(
            "collection:primary",
            "Primary",
            outputDirectory,
            new SeedItem("known", "Known", 1, CollectionSubscriptionItemState.Known),
            new SeedItem("new", "New video", 2, CollectionSubscriptionItemState.New),
            new SeedItem("failed", "Failed video", 3, CollectionSubscriptionItemState.Failed),
            new SeedItem("ignored", "Ignored", 4, CollectionSubscriptionItemState.Ignored),
            new SeedItem("removed", "Removed", 5, CollectionSubscriptionItemState.New, IsPresent: false));
        await fixture.SeedSubscriptionAsync(
            "collection:secondary",
            "Secondary",
            outputDirectory,
            new SeedItem("secondary-new", "Another update", 1, CollectionSubscriptionItemState.New));
        var viewModel = fixture.CreateViewModel();

        await viewModel.InitializeAsync();

        Assert.Equal(2, viewModel.Subscriptions.Count);
        Assert.Equal("Primary", viewModel.SelectedSubscription?.Title);
        Assert.Equal(3, viewModel.PendingCount);
        Assert.Equal(
            ["New video", "Failed video"],
            viewModel.PendingItems.Select(item => item.Title));
        Assert.Equal(["新增", "上次失败"], viewModel.PendingItems.Select(item => item.StateText));
        Assert.All(viewModel.PendingItems, item => Assert.True(item.IsSelected));
        Assert.Equal(2, viewModel.SelectedCount);
    }

    [Fact]
    public async Task DownloadSelected_UsesSavedDirectoryAndPersistsQueueCorrelation()
    {
        await using var fixture = new Fixture();
        var outputDirectory = fixture.CreateDirectory("saved-output");
        var subscription = await fixture.SeedSubscriptionAsync(
            "collection:download",
            "Download collection",
            outputDirectory,
            new SeedItem("old", "Old video", 1, CollectionSubscriptionItemState.Known),
            new SeedItem("selected", "Selected video", 2, CollectionSubscriptionItemState.New),
            new SeedItem("not-selected", "Not selected", 3, CollectionSubscriptionItemState.New));
        var notifications = new List<(string Message, bool Success)>();
        var viewModel = fixture.CreateViewModel();
        viewModel.RequestShowNotification += (message, success) =>
            notifications.Add((message, success));
        await viewModel.InitializeAsync();
        viewModel.PendingItems.Single(item => item.Item.EntryKey == "not-selected").IsSelected = false;

        await viewModel.DownloadSelectedCommand.ExecuteAsync(null);

        var task = Assert.Single(fixture.DownloadManager.Tasks);
        Assert.Equal("https://example.test/video/selected", task.Url);
        Assert.Equal("02. Selected video", task.Title);
        Assert.Equal(Path.GetFullPath(outputDirectory), task.OutputDirectory);
        Assert.Equal(Path.GetFullPath(outputDirectory), task.BatchDirectory);
        Assert.Equal(subscription.Id, task.CollectionSubscriptionId);
        Assert.Equal("selected", task.CollectionEntryKey);
        Assert.Equal("collection-batch", task.BatchId);
        Assert.Equal("Download collection", task.BatchName);
        Assert.Equal("Download collection", task.CollectionTitle);
        Assert.Equal(2, task.CollectionItemIndex);
        Assert.Equal(3, task.CollectionItemCount);
        Assert.Equal("mkv", task.Format);
        Assert.Equal("1080", task.Quality);
        Assert.Equal("all", task.Subtitle);

        var stored = await fixture.History.GetCollectionSubscriptionAsync(subscription.Id);
        Assert.NotNull(stored);
        var queued = stored.Items.Single(item => item.EntryKey == "selected");
        Assert.Equal(CollectionSubscriptionItemState.Queued, queued.State);
        Assert.Equal(task.Id, queued.TaskId);
        var notSelected = stored.Items.Single(item => item.EntryKey == "not-selected");
        Assert.Equal(CollectionSubscriptionItemState.New, notSelected.State);
        Assert.Empty(notSelected.TaskId);
        Assert.Single(viewModel.PendingItems);
        Assert.Equal("not-selected", viewModel.PendingItems[0].Item.EntryKey);
        Assert.Contains(notifications, item =>
            item.Success && item.Message.Contains("已加入 1 个", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadSelected_MissingOriginalDirectoryDoesNotCreateOrEnqueue()
    {
        await using var fixture = new Fixture();
        var missingDirectory = fixture.Path("deleted-original-directory");
        var subscription = await fixture.SeedSubscriptionAsync(
            "collection:missing-output",
            "Missing output",
            missingDirectory,
            new SeedItem("new", "New video", 1, CollectionSubscriptionItemState.New));
        var notifications = new List<(string Message, bool Success)>();
        var viewModel = fixture.CreateViewModel();
        viewModel.RequestShowNotification += (message, success) =>
            notifications.Add((message, success));
        await viewModel.InitializeAsync();

        await viewModel.DownloadSelectedCommand.ExecuteAsync(null);

        Assert.False(Directory.Exists(missingDirectory));
        Assert.Empty(fixture.DownloadManager.Tasks);
        var stored = await fixture.History.GetCollectionSubscriptionAsync(subscription.Id);
        Assert.NotNull(stored);
        var item = Assert.Single(stored.Items);
        Assert.Equal(CollectionSubscriptionItemState.New, item.State);
        Assert.Empty(item.TaskId);
        Assert.Contains(notifications, notification =>
            !notification.Success
            && notification.Message.Contains("原下载目录", StringComparison.Ordinal)
            && notification.Message.Contains("重新选择目录", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RelinkDirectory_PersistsReplacementDirectory()
    {
        await using var fixture = new Fixture();
        var missingDirectory = fixture.Path("missing-output");
        var replacementDirectory = fixture.CreateDirectory("replacement-output");
        var subscription = await fixture.SeedSubscriptionAsync(
            "collection:relink",
            "Relink collection",
            missingDirectory,
            new SeedItem("new", "New video", 1, CollectionSubscriptionItemState.New));
        string? directoryPassedToPicker = null;
        var notifications = new List<(string Message, bool Success)>();
        var viewModel = fixture.CreateViewModel(currentDirectory =>
        {
            directoryPassedToPicker = currentDirectory;
            return replacementDirectory;
        });
        viewModel.RequestShowNotification += (message, success) =>
            notifications.Add((message, success));
        await viewModel.InitializeAsync();

        await viewModel.RelinkDirectoryCommand.ExecuteAsync(null);

        Assert.Equal(missingDirectory, directoryPassedToPicker);
        Assert.Equal(Path.GetFullPath(replacementDirectory), viewModel.SavedDirectory);
        Assert.True(viewModel.IsSavedDirectoryAvailable);
        var stored = await fixture.History.GetCollectionSubscriptionAsync(subscription.Id);
        Assert.NotNull(stored);
        Assert.Equal(Path.GetFullPath(replacementDirectory), stored.OutputDirectory);
        Assert.Contains(notifications, item =>
            item.Success && item.Message.Contains("已更新合集下载目录", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IgnoreSelected_PersistsIgnoredStateAndLeavesUnselectedPending()
    {
        await using var fixture = new Fixture();
        var outputDirectory = fixture.CreateDirectory("downloads");
        var subscription = await fixture.SeedSubscriptionAsync(
            "collection:ignore",
            "Ignore collection",
            outputDirectory,
            new SeedItem("ignored", "Ignore me", 1, CollectionSubscriptionItemState.New),
            new SeedItem("remaining", "Keep me", 2, CollectionSubscriptionItemState.Failed));
        var notifications = new List<(string Message, bool Success)>();
        var viewModel = fixture.CreateViewModel();
        viewModel.RequestShowNotification += (message, success) =>
            notifications.Add((message, success));
        await viewModel.InitializeAsync();
        viewModel.PendingItems.Single(item => item.Item.EntryKey == "remaining").IsSelected = false;

        await viewModel.IgnoreSelectedCommand.ExecuteAsync(null);

        var stored = await fixture.History.GetCollectionSubscriptionAsync(subscription.Id);
        Assert.NotNull(stored);
        var ignored = stored.Items.Single(item => item.EntryKey == "ignored");
        Assert.Equal(CollectionSubscriptionItemState.Ignored, ignored.State);
        Assert.Empty(ignored.TaskId);
        Assert.Equal(
            CollectionSubscriptionItemState.Failed,
            stored.Items.Single(item => item.EntryKey == "remaining").State);
        var pending = Assert.Single(viewModel.PendingItems);
        Assert.Equal("remaining", pending.Item.EntryKey);
        Assert.Contains(notifications, item =>
            item.Success && item.Message.Contains("已忽略 1 个", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadCommands_ShareOneOperationGate()
    {
        await using var fixture = new Fixture();
        var outputDirectory = fixture.CreateDirectory("operation-gate");
        await fixture.SeedSubscriptionAsync(
            "collection:operation-gate",
            "Operation gate",
            outputDirectory,
            new SeedItem("selected", "Selected", 1, CollectionSubscriptionItemState.New),
            new SeedItem("not-selected", "Not selected", 2, CollectionSubscriptionItemState.New));
        var updateEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpdate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var updateCalls = 0;
        var viewModel = fixture.CreateViewModel(
            updateItemStates: async (subscriptionId, updates) =>
            {
                if (Interlocked.Increment(ref updateCalls) == 1)
                {
                    updateEntered.TrySetResult();
                    await releaseUpdate.Task;
                }

                return await fixture.History.UpdateCollectionSubscriptionItemStatesAsync(
                    subscriptionId,
                    updates);
            });
        await viewModel.InitializeAsync();
        viewModel.PendingItems.Single(item => item.Item.EntryKey == "not-selected").IsSelected = false;

        var selectedDownload = viewModel.DownloadSelectedCommand.ExecuteAsync(null);
        await updateEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.IsOperating);

        await viewModel.DownloadAllCommand.ExecuteAsync(null);
        releaseUpdate.TrySetResult();
        await selectedDownload;

        var task = Assert.Single(fixture.DownloadManager.Tasks);
        Assert.Equal("selected", task.CollectionEntryKey);
        Assert.False(viewModel.IsOperating);
        Assert.Equal(1, updateCalls);
    }

    [Fact]
    public async Task DownloadSelected_RetriesMatchingFailedTaskWithCurrentSettings()
    {
        await using var fixture = new Fixture();
        var outputDirectory = fixture.CreateDirectory("retry-output");
        var subscription = await fixture.SeedSubscriptionAsync(
            "collection:retry",
            "Retry collection",
            outputDirectory,
            new SeedItem("retry", "Retry video", 1, CollectionSubscriptionItemState.Failed));
        var failedTask = new DownloadTask
        {
            Url = "https://stale.example/video",
            Title = "Stale title",
            Format = "mp4",
            Quality = "worst",
            Subtitle = "none",
            OutputDirectory = fixture.CreateDirectory("stale-output"),
            BatchId = "stale-batch",
            BatchName = "Stale batch",
            CollectionSubscriptionId = subscription.Id,
            CollectionEntryKey = "retry",
            Status = DownloadStatus.Failed
        };
        fixture.DownloadManager.Tasks.Add(failedTask);
        var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync();

        await viewModel.DownloadSelectedCommand.ExecuteAsync(null);

        var retriedTask = Assert.Single(fixture.DownloadManager.Tasks);
        Assert.Same(failedTask, retriedTask);
        Assert.Equal("https://example.test/video/retry", retriedTask.Url);
        Assert.Equal("01. Retry video", retriedTask.Title);
        Assert.Equal(Path.GetFullPath(outputDirectory), retriedTask.OutputDirectory);
        Assert.Equal(Path.GetFullPath(outputDirectory), retriedTask.BatchDirectory);
        Assert.Equal("mkv", retriedTask.Format);
        Assert.Equal("1080", retriedTask.Quality);
        Assert.Equal("all", retriedTask.Subtitle);
        Assert.Equal("collection-batch", retriedTask.BatchId);
        Assert.Equal("Retry collection", retriedTask.BatchName);
        Assert.Equal(subscription.Id, retriedTask.CollectionSubscriptionId);
        Assert.Equal("retry", retriedTask.CollectionEntryKey);
        Assert.DoesNotContain(
            retriedTask.Status,
            new[] { DownloadStatus.Failed, DownloadStatus.Cancelled });

        var stored = await fixture.History.GetCollectionSubscriptionAsync(subscription.Id);
        var queuedItem = Assert.Single(stored!.Items);
        Assert.Equal(CollectionSubscriptionItemState.Queued, queuedItem.State);
        Assert.Equal(failedTask.Id, queuedItem.TaskId);
    }

    [Fact]
    public async Task InitializeAsync_PreservesCheckboxSelectionAcrossReload()
    {
        await using var fixture = new Fixture();
        var outputDirectory = fixture.CreateDirectory("selection-reload");
        await fixture.SeedSubscriptionAsync(
            "collection:selection-reload",
            "Selection reload",
            outputDirectory,
            new SeedItem("keep-unchecked", "Keep unchecked", 1, CollectionSubscriptionItemState.New));
        var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync();
        viewModel.PendingItems.Single().IsSelected = false;

        await viewModel.InitializeAsync();

        Assert.False(viewModel.PendingItems.Single().IsSelected);
        Assert.False(viewModel.HasSelectedItems);
    }

    [Fact]
    public async Task InitializeAsync_CoalescesConcurrentReloadAndSelectsRequestedSubscription()
    {
        await using var fixture = new Fixture();
        var outputDirectory = fixture.CreateDirectory("coalesced-load");
        await fixture.SeedSubscriptionAsync(
            "collection:first-load",
            "First load",
            outputDirectory,
            new SeedItem("first", "First", 1, CollectionSubscriptionItemState.New));
        var firstSnapshot = await fixture.History.GetCollectionSubscriptionsAsync();
        var target = await fixture.SeedSubscriptionAsync(
            "collection:final-load",
            "Final load",
            outputDirectory,
            new SeedItem("final", "Final", 1, CollectionSubscriptionItemState.New));
        var firstReadEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;
        var viewModel = fixture.CreateViewModel(
            loadSubscriptions: async () =>
            {
                if (Interlocked.Increment(ref readCount) == 1)
                {
                    firstReadEntered.TrySetResult();
                    await releaseFirstRead.Task;
                    return firstSnapshot;
                }

                return await fixture.History.GetCollectionSubscriptionsAsync();
            });

        var firstLoad = viewModel.InitializeAsync();
        await firstReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var selectTarget = viewModel.SelectSubscriptionAsync(target.Id);
        releaseFirstRead.TrySetResult();
        await Task.WhenAll(firstLoad, selectTarget);

        Assert.Equal(2, readCount);
        Assert.Equal(2, viewModel.Subscriptions.Count);
        Assert.Equal(target.Id, viewModel.SelectedSubscription?.Id);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task RelinkDirectory_PersistenceFailureRollsBackAndNotifies()
    {
        await using var fixture = new Fixture();
        var originalDirectory = fixture.CreateDirectory("relink-original");
        var replacementDirectory = fixture.CreateDirectory("relink-failure");
        await fixture.SeedSubscriptionAsync(
            "collection:relink-failure",
            "Relink failure",
            originalDirectory,
            new SeedItem("new", "New", 1, CollectionSubscriptionItemState.New));
        var notifications = new List<(string Message, bool Success)>();
        var viewModel = fixture.CreateViewModel(
            _ => replacementDirectory,
            updateSubscription: _ => throw new IOException("database unavailable"));
        viewModel.RequestShowNotification += (message, success) =>
            notifications.Add((message, success));
        await viewModel.InitializeAsync();

        await viewModel.RelinkDirectoryCommand.ExecuteAsync(null);

        Assert.Equal(Path.GetFullPath(originalDirectory), viewModel.SavedDirectory);
        Assert.Contains(notifications, item =>
            !item.Success
            && item.Message.Contains("更新合集下载目录失败", StringComparison.Ordinal)
            && item.Message.Contains("database unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadSelected_CasConflictDoesNotStartTaskAndNotifies()
    {
        await using var fixture = new Fixture();
        var outputDirectory = fixture.CreateDirectory("cas-conflict");
        await fixture.SeedSubscriptionAsync(
            "collection:cas-conflict",
            "CAS conflict",
            outputDirectory,
            new SeedItem("new", "New", 1, CollectionSubscriptionItemState.New));
        var notifications = new List<(string Message, bool Success)>();
        var viewModel = fixture.CreateViewModel(
            updateItemStates: (_, _) => Task.FromResult(0));
        viewModel.RequestShowNotification += (message, success) =>
            notifications.Add((message, success));
        await viewModel.InitializeAsync();

        await viewModel.DownloadSelectedCommand.ExecuteAsync(null);

        Assert.Empty(fixture.DownloadManager.Tasks);
        Assert.Single(viewModel.PendingItems);
        Assert.Contains(notifications, item =>
            !item.Success && item.Message.Contains("状态已变化", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadSelected_StatePersistenceExceptionIsNotified()
    {
        await using var fixture = new Fixture();
        var outputDirectory = fixture.CreateDirectory("queue-exception");
        await fixture.SeedSubscriptionAsync(
            "collection:queue-exception",
            "Queue exception",
            outputDirectory,
            new SeedItem("new", "New", 1, CollectionSubscriptionItemState.New));
        var notifications = new List<(string Message, bool Success)>();
        var viewModel = fixture.CreateViewModel(
            updateItemStates: (_, _) => throw new IOException("write failed"));
        viewModel.RequestShowNotification += (message, success) =>
            notifications.Add((message, success));
        await viewModel.InitializeAsync();

        await viewModel.DownloadSelectedCommand.ExecuteAsync(null);

        Assert.Empty(fixture.DownloadManager.Tasks);
        Assert.False(viewModel.IsOperating);
        Assert.Contains(notifications, item =>
            !item.Success
            && item.Message.Contains("下载合集更新失败", StringComparison.Ordinal)
            && item.Message.Contains("write failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadSelected_UnrelatedActiveUrlIsSkippedAndRemainsPending()
    {
        await using var fixture = new Fixture();
        var outputDirectory = fixture.CreateDirectory("active-url");
        var subscription = await fixture.SeedSubscriptionAsync(
            "collection:active-url",
            "Active URL",
            outputDirectory,
            new SeedItem("same-url", "Same URL", 1, CollectionSubscriptionItemState.New));
        var unrelatedTask = new DownloadTask
        {
            Url = "https://example.test/video/same-url",
            Status = DownloadStatus.Downloading
        };
        fixture.DownloadManager.Tasks.Add(unrelatedTask);
        var notifications = new List<(string Message, bool Success)>();
        var viewModel = fixture.CreateViewModel();
        viewModel.RequestShowNotification += (message, success) =>
            notifications.Add((message, success));
        await viewModel.InitializeAsync();

        await viewModel.DownloadSelectedCommand.ExecuteAsync(null);

        Assert.Same(unrelatedTask, Assert.Single(fixture.DownloadManager.Tasks));
        Assert.Single(viewModel.PendingItems);
        var stored = await fixture.History.GetCollectionSubscriptionAsync(subscription.Id);
        Assert.Equal(CollectionSubscriptionItemState.New, Assert.Single(stored!.Items).State);
        Assert.Contains(notifications, item =>
            !item.Success && item.Message.Contains("已跳过 1 个", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TaskFinished_WithStaleTaskIdDoesNotChangeInMemoryItem()
    {
        await using var fixture = new Fixture();
        var outputDirectory = fixture.CreateDirectory("stale-finish");
        var subscription = await fixture.SeedSubscriptionAsync(
            "collection:stale-finish",
            "Stale finish",
            outputDirectory,
            new SeedItem("item", "Item", 1, CollectionSubscriptionItemState.New));
        await fixture.History.UpdateCollectionSubscriptionItemStatesAsync(
            subscription.Id,
            [new CollectionSubscriptionItemStateUpdate
            {
                EntryKey = "item",
                State = CollectionSubscriptionItemState.New,
                TaskId = "newer-task"
            }]);
        var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync();
        var staleTask = new DownloadTask
        {
            CollectionSubscriptionId = subscription.Id,
            CollectionEntryKey = "item",
            Status = DownloadStatus.Completed
        };
        var handler = typeof(CollectionUpdatesViewModel).GetMethod(
            "OnDownloadTaskFinished",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(handler);
        handler.Invoke(viewModel, [staleTask]);

        var item = Assert.Single(viewModel.PendingItems).Item;
        Assert.Equal(CollectionSubscriptionItemState.New, item.State);
        Assert.Equal("newer-task", item.TaskId);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TestDirectory _root = new();
        private readonly HoldingDownloadService _downloadService = new();

        public Fixture()
        {
            History = new HistoryService(Path("history.db"));
            Config = new ConfigService(Path("config"));
            Config.Config.MaxConcurrentDownloads = 1;
            DownloadManager = new DownloadManager(_downloadService, History, Config);
            var ytDlpService = new YtDlpService(Config, new EnvironmentService());
            RefreshService = new CollectionRefreshService(ytDlpService, History);
        }

        public HistoryService History { get; }

        public ConfigService Config { get; }

        public DownloadManager DownloadManager { get; }

        public CollectionRefreshService RefreshService { get; }

        public string Path(params string[] segments) => _root.Path(segments);

        public string CreateDirectory(params string[] segments)
        {
            var directory = Path(segments);
            Directory.CreateDirectory(directory);
            return directory;
        }

        public CollectionUpdatesViewModel CreateViewModel(
            Func<string, string?>? selectDirectory = null,
            Func<Task<List<CollectionSubscription>>>? loadSubscriptions = null,
            Func<long, IReadOnlyCollection<CollectionSubscriptionItemStateUpdate>, Task<int>>?
                updateItemStates = null,
            Func<CollectionSubscription, Task<CollectionSubscription>>? updateSubscription = null,
            Func<long, Task<bool>>? deleteSubscription = null)
            => new(
                History,
                RefreshService,
                DownloadManager,
                new DownloadPreflightService(),
                selectDirectory ?? (_ => null),
                loadSubscriptions,
                updateItemStates,
                updateSubscription,
                deleteSubscription);

        public async Task<CollectionSubscription> SeedSubscriptionAsync(
            string canonicalKey,
            string title,
            string outputDirectory,
            params SeedItem[] items)
        {
            var subscription = await History.UpsertCollectionSubscriptionAsync(
                new CollectionSubscription
                {
                    CanonicalKey = canonicalKey,
                    SourceUrl = $"https://example.test/collection/{canonicalKey}",
                    Platform = "Example",
                    Title = title,
                    OutputDirectory = outputDirectory,
                    Format = "mkv",
                    Quality = "1080",
                    Subtitle = "all",
                    BatchId = "collection-batch",
                    BatchName = title,
                    AutoCheckEnabled = true,
                    CheckInterval = TimeSpan.FromHours(24)
                },
                items.Select(ToSnapshot).ToArray());

            var updates = items
                .Where(item => item.State != CollectionSubscriptionItemState.Known)
                .Select(item => new CollectionSubscriptionItemStateUpdate
                {
                    EntryKey = item.Key,
                    State = item.State,
                    TaskId = ""
                })
                .ToArray();
            if (updates.Length > 0)
            {
                await History.UpdateCollectionSubscriptionItemStatesAsync(
                    subscription.Id,
                    updates);
            }

            if (items.Any(item => !item.IsPresent))
            {
                await History.ApplyCollectionSubscriptionSnapshotAsync(
                    subscription.Id,
                    items.Where(item => item.IsPresent).Select(ToSnapshot).ToArray());
            }

            return await History.GetCollectionSubscriptionAsync(subscription.Id)
                   ?? throw new InvalidOperationException("Seeded subscription was not found.");
        }

        public async ValueTask DisposeAsync()
        {
            _downloadService.Release();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await DownloadManager.WaitForIdleAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
            }

            DownloadManager.Dispose();
            History.Dispose();
            _root.Dispose();
        }

        private static CollectionSubscriptionItemSnapshot ToSnapshot(SeedItem item)
            => new()
            {
                EntryKey = item.Key,
                EntryId = item.Key,
                Url = $"https://example.test/video/{item.Key}",
                Title = item.Title,
                Position = item.Position,
                LocalSequence = item.Position
            };
    }

    private sealed class HoldingDownloadService : IYtDlpDownloadService
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
            => Task.FromResult<VideoInfo?>(new VideoInfo { Url = url, Title = url });

        public Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
            => _release.Task.WaitAsync(cancellationToken);

        public void Release() => _release.TrySetResult();
    }

    private sealed record SeedItem(
        string Key,
        string Title,
        int Position,
        CollectionSubscriptionItemState State,
        bool IsPresent = true);
}
