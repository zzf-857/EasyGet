using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public sealed class TaskQueuePersistenceServiceTests
{
    [Fact]
    public async Task FlushAndRestore_PersistsOnlyWhitelistedRecoverableState()
    {
        using var root = new TestDirectory();
        var statePath = root.Path("state", "queue-state.json");
        using var persistence = new TaskQueuePersistenceService(statePath, TimeSpan.FromMilliseconds(10));
        using var runtimeSource = new CancellationTokenSource();
        var task = new DownloadTask
        {
            Id = "task0001",
            Url = "https://example.com/video/1",
            Title = "Example video",
            Platform = "Example",
            Duration = 123,
            FileSize = 456_789,
            ThumbnailUrl = "https://example.com/thumb.jpg",
            Format = "mkv",
            Quality = "1080",
            Subtitle = "auto",
            OutputDirectory = root.Path("downloads"),
            BatchId = "batch-1",
            BatchName = "Batch one",
            BatchDirectory = root.Path("downloads", "batch"),
            CollectionTitle = "Collection",
            CollectionItemIndex = 2,
            CollectionItemCount = 5,
            OutputFilePath = root.Path("downloads", "video.mkv"),
            OutputFilePaths = [root.Path("downloads", "video.zh.srt")],
            Progress = 42.5,
            DownloadedSize = 123_456,
            Status = DownloadStatus.Downloading,
            ErrorMessage = "cookie=session-secret token=access-secret",
            DouyinTaskEventLog = "authorization: bearer-secret",
            Cts = runtimeSource
        };

        await persistence.FlushAsync([task]);

        var json = await File.ReadAllTextAsync(statePath);
        Assert.Contains("Example video", json, StringComparison.Ordinal);
        Assert.DoesNotContain("session-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("access-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("errorMessage", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("douyinTaskEventLog", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cts", json, StringComparison.OrdinalIgnoreCase);

        var restored = await persistence.RestoreAsync();
        var restoredTask = Assert.Single(restored);
        Assert.Equal(task.Id, restoredTask.Id);
        Assert.Equal(task.Url, restoredTask.Url);
        Assert.Equal(task.OutputFilePaths, restoredTask.OutputFilePaths);
        Assert.Equal(42.5, restoredTask.Progress);
        Assert.Equal(123_456, restoredTask.DownloadedSize);
        Assert.Equal(DownloadStatus.Paused, restoredTask.Status);
        Assert.True(restoredTask.WasRestoredFromPreviousSession);
        Assert.Contains("会话恢复", restoredTask.StatusText, StringComparison.Ordinal);
        Assert.Null(restoredTask.Cts);
        Assert.Equal("", restoredTask.ErrorMessage);
        Assert.Equal(0, restoredTask.Speed);
        Assert.Equal(0, restoredTask.Eta);
        Assert.Empty(Directory.GetFiles(root.Path("state"), "*.tmp"));
    }

    [Fact]
    public async Task Restore_NormalizesOperationalStatesAndSkipsCompletedTasks()
    {
        using var root = new TestDirectory();
        var statePath = root.Path("queue-state.json");
        using var persistence = new TaskQueuePersistenceService(statePath, TimeSpan.Zero);
        var statuses = new[]
        {
            DownloadStatus.Waiting,
            DownloadStatus.Paused,
            DownloadStatus.Failed,
            DownloadStatus.Cancelled,
            DownloadStatus.Downloading,
            DownloadStatus.Resolving,
            DownloadStatus.Merging,
            DownloadStatus.Completed
        };
        var tasks = statuses.Select((status, index) => new DownloadTask
        {
            Id = $"task{index:0000}",
            Url = $"https://example.com/{index}",
            Status = status
        }).ToArray();

        await persistence.FlushAsync(tasks);
        var restored = await persistence.RestoreAsync();

        Assert.Equal(7, restored.Count);
        Assert.DoesNotContain(restored, task => task.Status == DownloadStatus.Completed);
        Assert.Equal(4, restored.Count(task => task.Status == DownloadStatus.Paused));
        Assert.Single(restored, task => task.Status == DownloadStatus.Waiting);
        Assert.Single(restored, task => task.Status == DownloadStatus.Failed);
        Assert.Single(restored, task => task.Status == DownloadStatus.Cancelled);
        Assert.Equal(3, restored.Count(task => task.WasRestoredFromPreviousSession));
    }

    [Fact]
    public async Task Restore_QuarantinesMalformedStateAndDoesNotBlockStartup()
    {
        using var root = new TestDirectory();
        var statePath = root.Path("queue-state.json");
        await File.WriteAllTextAsync(statePath, "{ not valid json");
        using var persistence = new TaskQueuePersistenceService(statePath, TimeSpan.Zero);

        var restored = await persistence.RestoreAsync();

        Assert.Empty(restored);
        Assert.False(File.Exists(statePath));
        Assert.Single(Directory.GetFiles(root.DirectoryPath, "queue-state.corrupt-*.json"));
    }

    [Fact]
    public async Task DownloadManager_QueueAndStatusChangesAreDebouncedAndRestorable()
    {
        using var root = new TestDirectory();
        var statePath = root.Path("queue-state.json");
        using var persistence = new TaskQueuePersistenceService(
            statePath,
            TimeSpan.FromMilliseconds(20));
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        using var manager = new DownloadManager(
            new NoOpDownloadService(),
            history,
            config,
            taskQueuePersistence: persistence);
        var task = new DownloadTask
        {
            Id = "task0001",
            Url = "https://example.com/queue",
            Status = DownloadStatus.Paused
        };

        manager.Tasks.Add(task);
        await WaitUntilAsync(async () => (await persistence.RestoreAsync()).Count == 1);

        task.Status = DownloadStatus.Failed;
        await WaitUntilAsync(async () =>
        {
            var restored = await persistence.RestoreAsync();
            return restored.Count == 1 && restored[0].Status == DownloadStatus.Failed;
        });

        manager.Tasks.Remove(task);
        await WaitUntilAsync(async () => (await persistence.RestoreAsync()).Count == 0);
    }

    [Fact]
    public async Task DownloadManager_RestoreAsyncAddsSnapshotWithoutStartingDownloads()
    {
        using var root = new TestDirectory();
        var statePath = root.Path("queue-state.json");
        using (var seed = new TaskQueuePersistenceService(statePath, TimeSpan.Zero))
        {
            await seed.FlushAsync([
                new DownloadTask
                {
                    Id = "task0001",
                    Url = "https://example.com/recover",
                    Status = DownloadStatus.Downloading
                }
            ]);
        }

        using var persistence = new TaskQueuePersistenceService(statePath, TimeSpan.Zero);
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var downloads = new NoOpDownloadService();
        using var manager = new DownloadManager(
            downloads,
            history,
            config,
            taskQueuePersistence: persistence);

        var count = await manager.RestoreAsync();

        Assert.Equal(1, count);
        var restored = Assert.Single(manager.Tasks);
        Assert.Equal(DownloadStatus.Paused, restored.Status);
        Assert.True(restored.WasRestoredFromPreviousSession);
        Assert.Equal(0, downloads.MetadataCalls);
        Assert.Equal(0, downloads.DownloadCalls);
        await manager.FlushAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!await predicate())
            await Task.Delay(20, timeout.Token);
    }

    private sealed class NoOpDownloadService : IYtDlpDownloadService
    {
        public int MetadataCalls { get; private set; }
        public int DownloadCalls { get; private set; }

        public Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            MetadataCalls++;
            return Task.FromResult<VideoInfo?>(null);
        }

        public Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            return Task.CompletedTask;
        }
    }
}
