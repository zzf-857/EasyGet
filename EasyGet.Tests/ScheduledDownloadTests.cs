using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public sealed class ScheduledDownloadTests
{
    [Fact]
    public async Task ScheduleAsync_FutureOffsetTimeWaitsThenUsesExistingPipeline()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var downloads = new RecordingDownloadService();
        using var manager = new DownloadManager(downloads, history, config);
        var task = new DownloadTask { Url = "https://example.com/future" };

        await manager.ScheduleAsync(task, DateTimeOffset.Now.AddMilliseconds(300));

        Assert.Equal(DownloadStatus.Scheduled, task.Status);
        Assert.NotNull(task.ScheduledStartTimeUtc);
        Assert.Equal(TimeSpan.Zero, task.ScheduledStartTimeUtc!.Value.Offset);
        await Task.Delay(75);
        Assert.Equal(0, downloads.MetadataCalls);

        await downloads.DownloadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await manager.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, downloads.MetadataCalls);
        Assert.Equal(1, downloads.DownloadCalls);
        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Null(task.ScheduledStartTimeUtc);
    }

    [Fact]
    public async Task ScheduleAsync_PastTimeStartsPromptly()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var downloads = new RecordingDownloadService();
        using var manager = new DownloadManager(downloads, history, config);
        var task = new DownloadTask { Url = "https://example.com/overdue" };

        await manager.ScheduleAsync(task, DateTimeOffset.UtcNow.AddMinutes(-1));
        await downloads.DownloadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, downloads.MetadataCalls);
        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Null(task.ScheduledStartTimeUtc);
    }

    [Fact]
    public async Task EnqueueAsync_HonorsScheduledStartTimeOnTask()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var downloads = new RecordingDownloadService();
        using var manager = new DownloadManager(downloads, history, config);
        var task = new DownloadTask
        {
            Url = "https://example.com/model-schedule",
            ScheduledStartTimeUtc = DateTimeOffset.UtcNow.AddMilliseconds(250)
        };

        await manager.EnqueueAsync(task);
        Assert.Equal(DownloadStatus.Scheduled, task.Status);
        await Task.Delay(75);
        Assert.Equal(0, downloads.MetadataCalls);

        await downloads.DownloadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, downloads.MetadataCalls);
        Assert.Equal(DownloadStatus.Completed, task.Status);
    }

    [Fact]
    public async Task RescheduleExistingTaskToPastActivatesOnceWithoutDuplicatingQueueEntry()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var downloads = new RecordingDownloadService();
        using var manager = new DownloadManager(downloads, history, config);
        var task = new DownloadTask { Url = "https://example.com/reschedule" };

        await manager.ScheduleAsync(task, DateTimeOffset.UtcNow.AddHours(1));
        await manager.ScheduleAsync(task, DateTimeOffset.UtcNow.AddSeconds(-1));
        await downloads.DownloadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await manager.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Same(task, Assert.Single(manager.Tasks));
        Assert.Equal(1, downloads.MetadataCalls);
        Assert.Equal(1, downloads.DownloadCalls);
    }

    [Fact]
    public async Task Cancel_ScheduledTaskPreventsLaterActivation()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var downloads = new RecordingDownloadService();
        using var manager = new DownloadManager(downloads, history, config);
        var task = new DownloadTask { Url = "https://example.com/cancel" };

        await manager.ScheduleAsync(task, DateTimeOffset.UtcNow.AddMilliseconds(250));
        manager.Cancel(task.Id);
        await Task.Delay(400);

        Assert.Equal(DownloadStatus.Cancelled, task.Status);
        Assert.Null(task.ScheduledStartTimeUtc);
        Assert.Equal(0, downloads.MetadataCalls);
        Assert.Equal(0, downloads.DownloadCalls);
    }

    [Fact]
    public async Task RemovingScheduledTaskPreventsLaterActivation()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var downloads = new RecordingDownloadService();
        using var manager = new DownloadManager(downloads, history, config);
        var task = new DownloadTask { Url = "https://example.com/remove" };

        await manager.ScheduleAsync(task, DateTimeOffset.UtcNow.AddMilliseconds(250));
        Assert.True(manager.Tasks.Remove(task));
        await Task.Delay(400);

        Assert.Null(task.ScheduledStartTimeUtc);
        Assert.Equal(0, downloads.MetadataCalls);
        Assert.Equal(0, downloads.DownloadCalls);
    }

    [Fact]
    public async Task RestoreAsync_FutureScheduleSurvivesRestartAndRunsOnce()
    {
        using var root = new TestDirectory();
        var statePath = root.Path("queue-state.json");
        var dueAt = DateTimeOffset.UtcNow.AddMilliseconds(700);

        using (var firstPersistence = new TaskQueuePersistenceService(statePath, TimeSpan.Zero))
        using (var firstHistory = new HistoryService(root.Path("first-history.db")))
        using (var firstManager = new DownloadManager(
                   new RecordingDownloadService(),
                   firstHistory,
                   new ConfigService(root.Path("first-config")),
                   taskQueuePersistence: firstPersistence))
        {
            await firstManager.ScheduleAsync(
                new DownloadTask { Url = "https://example.com/restart" },
                dueAt);
            await firstManager.FlushAsync();
        }

        using var persistence = new TaskQueuePersistenceService(statePath, TimeSpan.Zero);
        using var history = new HistoryService(root.Path("second-history.db"));
        var downloads = new RecordingDownloadService();
        using var manager = new DownloadManager(
            downloads,
            history,
            new ConfigService(root.Path("second-config")),
            taskQueuePersistence: persistence);

        Assert.Equal(1, await manager.RestoreAsync());
        var restored = Assert.Single(manager.Tasks);
        Assert.Equal(DownloadStatus.Scheduled, restored.Status);
        Assert.Equal(0, downloads.MetadataCalls);

        await downloads.DownloadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await manager.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, downloads.MetadataCalls);
        Assert.Equal(1, downloads.DownloadCalls);
        Assert.Equal(DownloadStatus.Completed, restored.Status);
    }

    [Fact]
    public async Task RestoreAsync_OverdueScheduleRunsPromptly()
    {
        using var root = new TestDirectory();
        var statePath = root.Path("queue-state.json");
        using (var seed = new TaskQueuePersistenceService(statePath, TimeSpan.Zero))
        {
            await seed.FlushAsync([
                new DownloadTask
                {
                    Url = "https://example.com/restored-overdue",
                    Status = DownloadStatus.Scheduled,
                    ScheduledStartTimeUtc = DateTimeOffset.UtcNow.AddHours(-1),
                    ErrorMessage = "cookie=session-secret"
                }
            ]);
        }
        var persistedJson = await File.ReadAllTextAsync(statePath);
        Assert.DoesNotContain("session-secret", persistedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("errorMessage", persistedJson, StringComparison.OrdinalIgnoreCase);

        using var persistence = new TaskQueuePersistenceService(statePath, TimeSpan.Zero);
        using var history = new HistoryService(root.Path("history.db"));
        var downloads = new RecordingDownloadService();
        using var manager = new DownloadManager(
            downloads,
            history,
            new ConfigService(root.Path("config")),
            taskQueuePersistence: persistence);

        Assert.Equal(1, await manager.RestoreAsync());
        await downloads.DownloadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, downloads.MetadataCalls);
        Assert.Equal(1, downloads.DownloadCalls);
        Assert.Equal(DownloadStatus.Completed, Assert.Single(manager.Tasks).Status);
    }

    [Fact]
    public async Task CancelledScheduleIsPersistedWithoutAStartTimeAndIsNotRestoredAsScheduled()
    {
        using var root = new TestDirectory();
        var statePath = root.Path("queue-state.json");
        using (var persistence = new TaskQueuePersistenceService(statePath, TimeSpan.Zero))
        using (var history = new HistoryService(root.Path("first-history.db")))
        using (var manager = new DownloadManager(
                   new RecordingDownloadService(),
                   history,
                   new ConfigService(root.Path("first-config")),
                   taskQueuePersistence: persistence))
        {
            var task = new DownloadTask { Url = "https://example.com/persist-cancel" };
            await manager.ScheduleAsync(task, DateTimeOffset.UtcNow.AddHours(1));
            manager.Cancel(task.Id);
            await manager.FlushAsync();
        }

        using var restoredPersistence = new TaskQueuePersistenceService(statePath, TimeSpan.Zero);
        var restored = Assert.Single(await restoredPersistence.RestoreAsync());

        Assert.Equal(DownloadStatus.Cancelled, restored.Status);
        Assert.Null(restored.ScheduledStartTimeUtc);
    }

    [Fact]
    public void NormalizeScheduledStartTime_AcceptsUtcLocalAndUnspecifiedLocalValues()
    {
        var utc = new DateTime(2030, 5, 4, 12, 30, 0, DateTimeKind.Utc);
        var local = utc.ToLocalTime();
        var unspecifiedLocal = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        Assert.Equal(
            new DateTimeOffset(utc),
            DownloadManager.NormalizeScheduledStartTime(utc));
        Assert.Equal(
            new DateTimeOffset(utc),
            DownloadManager.NormalizeScheduledStartTime(local));
        Assert.Equal(
            new DateTimeOffset(utc),
            DownloadManager.NormalizeScheduledStartTime(unspecifiedLocal));
    }

    private sealed class RecordingDownloadService : IYtDlpDownloadService
    {
        private int _metadataCalls;
        private int _downloadCalls;

        public int MetadataCalls => Volatile.Read(ref _metadataCalls);
        public int DownloadCalls => Volatile.Read(ref _downloadCalls);
        public TaskCompletionSource DownloadCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _metadataCalls);
            return Task.FromResult<VideoInfo?>(null);
        }

        public Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _downloadCalls);
            task.Status = DownloadStatus.Downloading;
            task.Status = DownloadStatus.Completed;
            DownloadCompleted.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
