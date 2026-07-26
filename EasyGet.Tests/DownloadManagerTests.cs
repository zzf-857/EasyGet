using EasyGet.Models;
using EasyGet.Services;
using EasyGet.Services.Cookies;
using System.Reflection;
using Xunit;

namespace EasyGet.Tests;

public class DownloadManagerTests
{
    [Fact]
    public void DownloadTask_StatusText_DescribesMetadataAuthenticationWithoutNewStatus()
    {
        var task = new DownloadTask { Status = DownloadStatus.Resolving };

        Assert.Equal(DownloadStatus.Resolving, task.Status);
        Assert.Contains("认证", task.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadTask_DisplayTitleShowsUsefulPlaceholderBeforeMetadataArrives()
    {
        var task = new DownloadTask
        {
            Url = "https://x.com/user/status/1",
            Title = ""
        };

        Assert.Contains("等待解析", task.DisplayTitle, StringComparison.Ordinal);
        task.Title = "已解析标题";
        Assert.Equal("已解析标题", task.DisplayTitle);
    }

    [Theory]
    [InlineData(DownloadStatus.Downloading, DownloadStatus.Cancelled)]
    [InlineData(DownloadStatus.Waiting, DownloadStatus.Cancelled)]
    [InlineData(DownloadStatus.Paused, DownloadStatus.Paused)]
    public void DownloadTask_MarkCancelledUnlessPausedPreservesPause(
        DownloadStatus initialStatus,
        DownloadStatus expectedStatus)
    {
        var task = new DownloadTask { Status = initialStatus };

        task.MarkCancelledUnlessPaused();

        Assert.Equal(expectedStatus, task.Status);
    }

    [Fact]
    public async Task EnqueueAsync_MetadataWorkersContinueWhileDownloadsWaitForConcurrency()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        config.Config.MaxConcurrentDownloads = 1;
        var service = new DownloadBlockingYtDlpDownloadService(expectedTaskCount: 20);
        var manager = new DownloadManager(service, history, config);

        foreach (var index in Enumerable.Range(1, 20))
        {
            await manager.EnqueueAsync(new DownloadTask
            {
                Url = $"https://x.com/user/status/{index}"
            });
        }

        var metadataResult = await Task.WhenAny(
            service.AllMetadataResolved.Task,
            Task.Delay(TimeSpan.FromSeconds(1)));
        var downloadsQueued = await WaitUntilAsync(
            () => manager.Tasks.Count(task => task.Status == DownloadStatus.Waiting) == 19,
            TimeSpan.FromSeconds(1));
        var waitingForDownloadCount = manager.Tasks.Count(
            task => task.Status == DownloadStatus.Waiting);
        service.ReleaseDownloads();
        await service.AllDownloadsCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Same(service.AllMetadataResolved.Task, metadataResult);
        Assert.True(downloadsQueued);
        Assert.Equal(20, service.MetadataCallCount);
        Assert.Equal(19, waitingForDownloadCount);
    }

    [Fact]
    public async Task WaitForIdleAsync_CompletesAfterMetadataAndDownloadsFinish()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var service = new FakeYtDlpDownloadService();
        var manager = new DownloadManager(service, history, config);
        var tasks = Enumerable.Range(1, 6)
            .Select(index => new DownloadTask
            {
                Url = $"https://example.com/watch/{index}"
            })
            .ToArray();

        foreach (var task in tasks)
            await manager.EnqueueAsync(task);

        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.All(tasks, task => Assert.Equal(DownloadStatus.Completed, task.Status));
        Assert.Equal(6, service.GetVideoInfoCallCount);
        Assert.Equal(6, service.DownloadCallCount);
    }

    [Fact]
    public async Task Cancel_QueuedMetadataTaskUpdatesImmediatelyAndSkipsResolution()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var service = new QueueBlockingYtDlpDownloadService();
        var manager = new DownloadManager(service, history, config);
        var tasks = Enumerable.Range(1, 5)
            .Select(index => new DownloadTask
            {
                Url = $"https://example.com/watch/{index}"
            })
            .ToArray();
        foreach (var task in tasks)
            await manager.EnqueueAsync(task);
        await service.FourMetadataRequestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queuedSource = Assert.IsType<CancellationTokenSource>(tasks[4].Cts);

        manager.Cancel(tasks[4].Id);

        var statusAfterCancel = tasks[4].Status;
        service.Release();
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(DownloadStatus.Cancelled, statusAfterCancel);
        Assert.DoesNotContain(tasks[4].Url, service.ResolvedUrls);
        Assert.Null(tasks[4].Cts);
        AssertCancellationTokenSourceDisposed(queuedSource);
    }

    [Fact]
    public async Task WaitForIdleAsync_IncludesResumedDownloads()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var service = new ResumeBlockingYtDlpDownloadService();
        var manager = new DownloadManager(service, history, config);
        var task = new DownloadTask
        {
            Url = "https://example.com/resume",
            Status = DownloadStatus.Paused
        };
        manager.Tasks.Add(task);

        await manager.ResumeAsync(task.Id);
        await service.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var idleTask = manager.WaitForIdleAsync(CancellationToken.None);
        var completedBeforeRelease = idleTask.IsCompleted;
        service.Release();
        await idleTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(completedBeforeRelease);
        Assert.Equal(DownloadStatus.Completed, task.Status);
    }

    [Fact]
    public async Task EnqueueAsync_TwentySamePlatformTasksReuseOneManagedAuthentication()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var managedLogin = new CountingManagedLoginSessionService();
        var coordinator = new CookieAcquisitionCoordinator(
            config,
            new PlatformCookieVault(root.Path("config")),
            new EmptyBrowserProfileDiscoveryService(),
            new CookieHealthStore(root.Path("config")),
            managedLogin,
            root.Path("temp"));
        var service = new CoordinatorBackedYtDlpDownloadService(coordinator);
        var manager = new DownloadManager(service, history, config);

        foreach (var index in Enumerable.Range(1, 20))
        {
            await manager.EnqueueAsync(new DownloadTask
            {
                Url = $"https://x.com/user/status/{index}"
            });
        }

        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, managedLogin.CallCount);
        Assert.All(manager.Tasks, task => Assert.Equal(DownloadStatus.Completed, task.Status));
    }

    [Fact]
    public async Task Dispose_CancelsActiveAndQueuedMetadataTasks()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var service = new QueueBlockingYtDlpDownloadService();
        var manager = new DownloadManager(service, history, config);
        var tasks = Enumerable.Range(1, 5)
            .Select(index => new DownloadTask
            {
                Url = $"https://example.com/dispose/{index}"
            })
            .ToArray();
        foreach (var task in tasks)
            await manager.EnqueueAsync(task);
        await service.FourMetadataRequestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        manager.Dispose();
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.All(tasks, task => Assert.Equal(DownloadStatus.Cancelled, task.Status));
    }

    [Fact]
    public async Task MetadataFailure_DoesNotExposeCookieOrProfileDetails()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var manager = new DownloadManager(
            new ThrowingMetadataYtDlpDownloadService(
                @"SID=secret-value in C:\Users\me\Secret Profile\Cookies"),
            history,
            config);
        var logs = new List<string>();
        manager.LogReceived += logs.Add;
        var task = new DownloadTask { Url = "https://example.com/private" };

        await manager.EnqueueAsync(task);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.Contains("解析失败", task.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", task.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Profile", task.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(logs, log => log.Contains("secret-value", StringComparison.Ordinal));
        Assert.Null(task.Cts);
    }

    [Fact]
    public async Task DownloadFailure_DoesNotExposeCookieOrProfileDetails()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var manager = new DownloadManager(
            new ThrowingDownloadYtDlpDownloadService(
                @"auth_token=secret-value in C:\Users\me\Secret Profile"),
            history,
            config);
        var logs = new List<string>();
        manager.LogReceived += logs.Add;
        var task = new DownloadTask { Url = "https://example.com/private" };

        await manager.EnqueueAsync(task);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.Contains("下载失败", task.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", task.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Profile", task.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(logs, log => log.Contains("secret-value", StringComparison.Ordinal));
        Assert.Null(task.Cts);
    }

    [Fact]
    public async Task TaskFinishedSubscriberFailure_DoesNotStopMetadataWorkers()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var manager = new DownloadManager(
            new ThrowingMetadataYtDlpDownloadService("metadata failed"),
            history,
            config);
        manager.TaskFinished += _ => throw new InvalidOperationException("subscriber failed");
        var tasks = Enumerable.Range(1, 6)
            .Select(index => new DownloadTask
            {
                Url = $"https://example.com/failure/{index}"
            })
            .ToArray();

        foreach (var task in tasks)
            await manager.EnqueueAsync(task);
        var idleTask = manager.WaitForIdleAsync(CancellationToken.None);
        var completed = await Task.WhenAny(
            idleTask,
            Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.Same(idleTask, completed);
        Assert.All(tasks, task => Assert.Equal(DownloadStatus.Failed, task.Status));
    }

    [Fact]
    public async Task EnqueueAsync_DouyinLinkAlwaysUsesYtDlpFlow()
    {
        var outputDir = CreateTempOutputDirectory();
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-douyin-disabled-route");
        try
        {
            var configService = CreateConfigService(outputDir, enableDouyinSpecialEngine: true);
            using var historyService = new HistoryService(dbPath);
            var ytDlp = new FakeYtDlpDownloadService
            {
                InfoToReturn = new VideoInfo
                {
                    Title = "legacy douyin note",
                    Platform = "Douyin",
                    Url = "https://www.douyin.com/note/7621772413184822582"
                }
            };
            var manager = new DownloadManager(ytDlp, historyService, configService);
            var task = new DownloadTask
            {
                Url = "https://www.douyin.com/note/7621772413184822582",
                OutputDirectory = outputDir
            };

            await EnqueueAndWaitAsync(manager, task);

            Assert.Equal(1, ytDlp.GetVideoInfoCallCount);
            Assert.Equal(1, ytDlp.DownloadCallCount);
            Assert.Equal("legacy douyin note", task.Title);
            Assert.Equal("Douyin", task.Platform);
            Assert.Equal(Path.Combine(outputDir, "抖音"), ytDlp.OutputDirectoryAtDownload);
            Assert.Equal(DownloadStatus.Completed, task.Status);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }
    [Theory]
    [InlineData(0, AppConfig.MinConcurrentDownloadLimit)]
    [InlineData(-5, AppConfig.MinConcurrentDownloadLimit)]
    [InlineData(3, 3)]
    [InlineData(99, AppConfig.MaxConcurrentDownloadLimit)]
    public void NormalizeConcurrencyLimit_ClampsToSupportedRange(int value, int expected)
    {
        Assert.Equal(expected, DownloadManager.NormalizeConcurrencyLimit(value));
    }

    [Fact]
    public void ApplyProgress_ClampsOutOfRangeValuesForUiState()
    {
        var task = new DownloadTask();
        var progress = new DownloadProgress
        {
            Percent = 135,
            Speed = -42,
            Eta = -8,
            Downloaded = -256
        };

        ApplyProgress(task, progress);

        Assert.Equal(100, task.Progress);
        Assert.Equal(0, task.Speed);
        Assert.Equal(0, task.Eta);
        Assert.Equal(0, task.DownloadedSize);
    }

    [Fact]
    public void ApplyProgress_ReplacesNonFiniteNumbersWithZero()
    {
        var task = new DownloadTask();
        var progress = new DownloadProgress
        {
            Percent = double.NaN,
            Speed = double.PositiveInfinity,
            Eta = double.NegativeInfinity,
            Downloaded = 128
        };

        ApplyProgress(task, progress);

        Assert.Equal(0, task.Progress);
        Assert.Equal(0, task.Speed);
        Assert.Equal(0, task.Eta);
        Assert.Equal(128, task.DownloadedSize);
    }

    [Fact]
    public async Task ResumeAsync_CancelWhileWaitingForConcurrencyMarksTaskCancelled()
    {
        var configService = new TestConfigService();
        configService.Config.MaxConcurrentDownloads = 1;
        using var historyService = new HistoryService();
        var manager = new DownloadManager(
            new YtDlpService(configService, new EnvironmentService()),
            historyService,
            configService);
        var downloadGate = GetDownloadGate(manager);
        await downloadGate.WaitAsync();

        try
        {
            var task = new DownloadTask
            {
                Url = "https://example.com/video",
                Title = "paused",
                Status = DownloadStatus.Paused
            };
            var finished = new TaskCompletionSource<DownloadTask>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            manager.TaskFinished += finishedTask => finished.TrySetResult(finishedTask);
            manager.Tasks.Add(task);

            await manager.ResumeAsync(task.Id);
            var source = Assert.IsType<CancellationTokenSource>(task.Cts);
            manager.Cancel(task.Id);

            var completed = await Task.WhenAny(finished.Task, Task.Delay(1000));

            Assert.Same(finished.Task, completed);
            Assert.Same(task, await finished.Task);
            Assert.Equal(DownloadStatus.Cancelled, task.Status);
            Assert.Null(task.Cts);
            AssertCancellationTokenSourceDisposed(source);
        }
        finally
        {
            downloadGate.Release();
        }
    }

    [Fact]
    public async Task Pause_DownloadCancellationKeepsTaskPaused()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var service = new ResumeBlockingYtDlpDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var task = new DownloadTask { Url = "https://example.com/video" };

        await manager.EnqueueAsync(task);
        await service.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var source = Assert.IsType<CancellationTokenSource>(task.Cts);
        manager.Pause(task.Id);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DownloadStatus.Paused, task.Status);
        Assert.Null(task.Cts);
        AssertCancellationTokenSourceDisposed(source);
    }

    [Fact]
    public async Task CompletedDownload_ClearsAndDisposesAttemptSource()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var service = new ResumeBlockingYtDlpDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var task = new DownloadTask { Url = "https://example.com/completed" };

        await manager.EnqueueAsync(task);
        await service.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var source = Assert.IsType<CancellationTokenSource>(task.Cts);

        service.Release();
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Null(task.Cts);
        AssertCancellationTokenSourceDisposed(source);
    }

    [Fact]
    public async Task ResumeAsync_WaitsForOldCleanupBeforeStartingNewAttempt()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        config.Config.MaxConcurrentDownloads = 2;
        var service = new PauseResumeIsolationYtDlpDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var task = new DownloadTask { Url = "https://example.com/resume-isolation" };
        var finished = new TaskCompletionSource<DownloadTask>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishedCount = 0;
        manager.TaskFinished += finishedTask =>
        {
            Interlocked.Increment(ref finishedCount);
            finished.TrySetResult(finishedTask);
        };

        await manager.EnqueueAsync(task);
        await service.FirstDownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var firstSource = Assert.IsType<CancellationTokenSource>(task.Cts);

        manager.Pause(task.Id);
        await service.FirstCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var resumeTask = manager.ResumeAsync(task.Id);

        Assert.False(resumeTask.IsCompleted);
        Assert.Same(firstSource, task.Cts);
        Assert.False(service.ResumedDownloadStarted.Task.IsCompleted);
        Assert.False(finished.Task.IsCompleted);

        service.AllowFirstAttemptToExit();
        await resumeTask.WaitAsync(TimeSpan.FromSeconds(2));
        var resumedSource = Assert.IsType<CancellationTokenSource>(task.Cts);
        await service.ResumedDownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        AssertCancellationTokenSourceDisposed(firstSource);
        Assert.Equal(0, Volatile.Read(ref finishedCount));
        Assert.Same(resumedSource, task.Cts);
        Assert.False(resumedSource.IsCancellationRequested);
        Assert.Equal(DownloadStatus.Downloading, task.Status);

        service.CompleteResumedDownload();
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(task, await finished.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(1, Volatile.Read(ref finishedCount));
        Assert.Null(task.Cts);
        AssertCancellationTokenSourceDisposed(resumedSource);
    }

    [Fact]
    public async Task Cancel_UpgradesFinishingPauseAndNotifiesOnlyOnce()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var service = new ResumeBlockingYtDlpDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var task = new DownloadTask { Url = "https://example.com/pause-cancel" };
        var finishedCount = 0;
        manager.TaskFinished += _ => Interlocked.Increment(ref finishedCount);

        await manager.EnqueueAsync(task);
        await service.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var source = Assert.IsType<CancellationTokenSource>(task.Cts);
        var attemptsField = typeof(DownloadManager).GetField(
            "_activeAttempts",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var attempts = Assert.IsAssignableFrom<System.Collections.IDictionary>(
            attemptsField?.GetValue(manager));
        var attempt = Assert.IsAssignableFrom<object>(attempts[task]);
        var updateSync = Assert.IsAssignableFrom<object>(
            attempt.GetType().GetProperty("UpdateSync")?.GetValue(attempt));
        var isFinishingProperty = attempt.GetType().GetProperty("IsFinishing");
        var wasRegisteredProperty = attempt.GetType().GetProperty("WasRegistered");

        Monitor.Enter(updateSync);
        try
        {
            manager.Pause(task.Id);
            Assert.True(SpinWait.SpinUntil(
                () => isFinishingProperty?.GetValue(attempt) is true,
                TimeSpan.FromSeconds(2)));
            Assert.Equal(true, wasRegisteredProperty?.GetValue(attempt));

            manager.Cancel(task.Id);

            Assert.Equal(DownloadStatus.Cancelled, task.Status);
            Assert.Equal(0, Volatile.Read(ref finishedCount));
        }
        finally
        {
            Monitor.Exit(updateSync);
        }

        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DownloadStatus.Cancelled, task.Status);
        Assert.Equal(1, Volatile.Read(ref finishedCount));
        Assert.Null(task.Cts);
        AssertCancellationTokenSourceDisposed(source);
    }

    [Fact]
    public void Cancel_PausedTaskMarksTaskCancelled()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        using var cancellation = new CancellationTokenSource();
        using var manager = new DownloadManager(new FakeYtDlpDownloadService(), history, config);
        var task = new DownloadTask
        {
            Url = "https://example.com/video",
            Status = DownloadStatus.Paused,
            Cts = cancellation
        };
        manager.Tasks.Add(task);

        manager.Cancel(task.Id);

        Assert.Equal(DownloadStatus.Cancelled, task.Status);
        Assert.True(task.Cts.IsCancellationRequested);
    }

    [Fact]
    public void CancelAndPause_DisposedSourcesDoNotThrow()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        using var manager = new DownloadManager(new FakeYtDlpDownloadService(), history, config);
        var cancelSource = new CancellationTokenSource();
        var pauseSource = new CancellationTokenSource();
        cancelSource.Dispose();
        pauseSource.Dispose();
        var cancelTask = new DownloadTask
        {
            Url = "https://example.com/cancel",
            Status = DownloadStatus.Paused,
            Cts = cancelSource
        };
        var pauseTask = new DownloadTask
        {
            Url = "https://example.com/pause",
            Status = DownloadStatus.Downloading,
            Cts = pauseSource
        };
        manager.Tasks.Add(cancelTask);
        manager.Tasks.Add(pauseTask);

        var cancelException = Record.Exception(() => manager.Cancel(cancelTask.Id));
        var pauseException = Record.Exception(() => manager.Pause(pauseTask.Id));

        Assert.Null(cancelException);
        Assert.Null(pauseException);
        Assert.Equal(DownloadStatus.Cancelled, cancelTask.Status);
        Assert.Equal(DownloadStatus.Paused, pauseTask.Status);
        Assert.Null(cancelTask.Cts);
        Assert.Null(pauseTask.Cts);
    }

    [Fact]
    public async Task MetadataPropertyCallback_CanCancelWithoutWaitingForAttemptLock()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var service = new FakeYtDlpDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var task = new DownloadTask { Url = "https://example.com/property-callback" };
        var cancelCompletedInsideCallback = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        task.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(DownloadTask.Title))
                return;

            var cancelTask = Task.Run(() => manager.Cancel(task.Id));
            cancelCompletedInsideCallback.TrySetResult(
                cancelTask.Wait(TimeSpan.FromSeconds(1)));
        };

        await manager.EnqueueAsync(task);

        Assert.True(await cancelCompletedInsideCallback.Task
            .WaitAsync(TimeSpan.FromSeconds(2)));
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DownloadStatus.Cancelled, task.Status);
        Assert.Null(task.Cts);
    }

    [Fact]
    public async Task MetadataWaitingSubscriberFailure_CleansAttemptAndKeepsWorkersAlive()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var service = new FakeYtDlpDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var failingTask = new DownloadTask { Url = "https://example.com/waiting-subscriber" };
        failingTask.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadTask.Status)
                && failingTask.Status == DownloadStatus.Waiting)
            {
                throw new InvalidOperationException("waiting subscriber failed");
            }
        };

        await manager.EnqueueAsync(failingTask);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DownloadStatus.Failed, failingTask.Status);
        Assert.Null(failingTask.Cts);

        var nextTask = new DownloadTask { Url = "https://example.com/next-metadata" };
        await manager.EnqueueAsync(nextTask);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DownloadStatus.Completed, nextTask.Status);
        Assert.Null(nextTask.Cts);
    }

    [Fact]
    public async Task FinishAttempt_PropertySubscriberFailureStillCleansUpAndAllowsRetry()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var service = new ResumeBlockingYtDlpDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var task = new DownloadTask { Url = "https://example.com/subscriber-failure" };

        await manager.EnqueueAsync(task);
        await service.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var firstSource = Assert.IsType<CancellationTokenSource>(task.Cts);
        System.ComponentModel.PropertyChangedEventHandler throwingHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadTask.Status)
                && task.Status == DownloadStatus.Cancelled)
            {
                throw new InvalidOperationException("subscriber failed");
            }
        };
        task.PropertyChanged += throwingHandler;

        manager.Cancel(task.Id);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        task.PropertyChanged -= throwingHandler;
        Assert.Equal(DownloadStatus.Cancelled, task.Status);
        Assert.Null(task.Cts);
        AssertCancellationTokenSourceDisposed(firstSource);

        await manager.RetryAsync(task.Id);
        Assert.True(await WaitUntilAsync(
            () => task.Status == DownloadStatus.Downloading && task.Cts is not null,
            TimeSpan.FromSeconds(2)));
        var retrySource = Assert.IsType<CancellationTokenSource>(task.Cts);

        service.Release();
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Null(task.Cts);
        AssertCancellationTokenSourceDisposed(retrySource);
    }

    [Fact]
    public async Task DisposeDuringResumeRemoval_CancelsUnlistedAttemptBeforeDownloadStarts()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var service = new ResumeBlockingYtDlpDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var task = new DownloadTask
        {
            Url = "https://example.com/dispose-resume",
            Status = DownloadStatus.Paused
        };
        manager.Tasks.Add(task);
        CancellationTokenSource? removedSource = null;
        manager.Tasks.CollectionChanged += (_, e) =>
        {
            if (e.OldItems?.Cast<DownloadTask>().Contains(task) != true)
                return;

            removedSource = task.Cts;
            manager.Dispose();
        };

        await manager.ResumeAsync(task.Id);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(removedSource);
        AssertCancellationTokenSourceDisposed(removedSource);
        Assert.False(service.DownloadStarted.Task.IsCompleted);
        Assert.Equal(DownloadStatus.Cancelled, task.Status);
        Assert.Null(task.Cts);
        Assert.Contains(task, manager.Tasks);
    }

    [Fact]
    public async Task DisposeDuringRetryRemoval_RestoresCancelledTaskWithoutStartingMetadata()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var service = new FakeYtDlpDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var task = new DownloadTask
        {
            Url = "https://example.com/dispose-retry",
            Status = DownloadStatus.Failed
        };
        manager.Tasks.Add(task);
        CancellationTokenSource? removedSource = null;
        manager.Tasks.CollectionChanged += (_, e) =>
        {
            if (e.OldItems?.Cast<DownloadTask>().Contains(task) != true)
                return;

            removedSource = task.Cts;
            manager.Dispose();
        };

        await manager.RetryAsync(task.Id);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(removedSource);
        AssertCancellationTokenSourceDisposed(removedSource);
        Assert.Equal(DownloadStatus.Cancelled, task.Status);
        Assert.Null(task.Cts);
        Assert.Contains(task, manager.Tasks);
        Assert.Equal(0, service.GetVideoInfoCallCount);
        Assert.Equal(0, service.DownloadCallCount);
    }

    [Fact]
    public async Task ResumeAndRetryAfterDispose_DoNotCreateAttempts()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var service = new FakeYtDlpDownloadService();
        using var manager = new DownloadManager(service, history, config);
        manager.Dispose();
        var pausedTask = new DownloadTask
        {
            Url = "https://example.com/disposed-resume",
            Status = DownloadStatus.Paused
        };
        var failedTask = new DownloadTask
        {
            Url = "https://example.com/disposed-retry",
            Status = DownloadStatus.Failed
        };
        manager.Tasks.Add(pausedTask);
        manager.Tasks.Add(failedTask);

        await manager.ResumeAsync(pausedTask.Id);
        await manager.RetryAsync(failedTask.Id);

        Assert.Null(pausedTask.Cts);
        Assert.Null(failedTask.Cts);
        Assert.Equal(0, service.GetVideoInfoCallCount);
        Assert.Equal(0, service.DownloadCallCount);
    }

    [Fact]
    public async Task RetryAsync_ClearsDouyinTaskOutcomeAndEventLog()
    {
        var outputDir = CreateTempOutputDirectory();
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-douyin-retry-reset");
        try
        {
            var configService = CreateConfigService(outputDir, enableDouyinSpecialEngine: false);
            using var historyService = new HistoryService(dbPath);
            var ytDlp = new FakeYtDlpDownloadService();
            var manager = new DownloadManager(ytDlp, historyService, configService);
            var task = new DownloadTask
            {
                Url = "https://example.com/video",
                OutputDirectory = outputDir,
                Status = DownloadStatus.Failed,
                Progress = 80,
                ErrorMessage = "old failure",
                DouyinSuccessCount = 4,
                DouyinFailedCount = 1,
                DouyinSkippedCount = 2,
                DouyinTaskEventLog = "old event"
            };
            manager.Tasks.Add(task);

            var finished = await RetryAndWaitAsync(manager, task);

            Assert.Same(task, finished);
            Assert.Equal(DownloadStatus.Completed, task.Status);
            Assert.Equal(0, task.DouyinSuccessCount);
            Assert.Equal(0, task.DouyinFailedCount);
            Assert.Equal(0, task.DouyinSkippedCount);
            Assert.Equal("", task.DouyinTaskEventLog);
            Assert.False(task.HasDouyinTaskOutcome);
            Assert.False(task.HasDouyinTaskEventLog);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    private static void ApplyProgress(DownloadTask task, DownloadProgress progress)
    {
        var method = typeof(DownloadManager).GetMethod(
            "ApplyProgress",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, [task, progress]);
    }

    private static DynamicConcurrencyGate GetDownloadGate(DownloadManager manager)
    {
        var field = typeof(DownloadManager).GetField(
            "_downloadGate",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        return (DynamicConcurrencyGate)field!.GetValue(manager)!;
    }

    private static ConfigService CreateConfigService(string outputDir, bool enableDouyinSpecialEngine)
    {
        var configService = new TestConfigService();
        configService.Config.DefaultDownloadPath = outputDir;
        configService.Config.AutoCategorizeByPlatform = true;
        configService.Config.EnableDouyinSpecialEngine = enableDouyinSpecialEngine;
        configService.Config.MaxConcurrentDownloads = 1;
        return configService;
    }

    private static async Task<DownloadTask> EnqueueAndWaitAsync(DownloadManager manager, DownloadTask task)
    {
        var finished = new TaskCompletionSource<DownloadTask>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnTaskFinished(DownloadTask finishedTask)
        {
            if (ReferenceEquals(task, finishedTask))
                finished.TrySetResult(finishedTask);
        }

        manager.TaskFinished += OnTaskFinished;
        try
        {
            await manager.EnqueueAsync(task);
            var completed = await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(finished.Task, completed);
            return await finished.Task;
        }
        finally
        {
            manager.TaskFinished -= OnTaskFinished;
        }
    }

    private static async Task<DownloadTask> RetryAndWaitAsync(DownloadManager manager, DownloadTask task)
    {
        var finished = new TaskCompletionSource<DownloadTask>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnTaskFinished(DownloadTask finishedTask)
        {
            if (ReferenceEquals(task, finishedTask))
                finished.TrySetResult(finishedTask);
        }

        manager.TaskFinished += OnTaskFinished;
        try
        {
            await manager.RetryAsync(task.Id);
            var completed = await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(finished.Task, completed);
            return await finished.Task;
        }
        finally
        {
            manager.TaskFinished -= OnTaskFinished;
        }
    }

    private static string CreateTempOutputDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easyget-douyin-route-{Guid.NewGuid():N}");
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

    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(10);
        }

        return condition();
    }

    private static bool IsCancellationTokenSourceDisposed(CancellationTokenSource source)
    {
        try
        {
            _ = source.Token;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static void AssertCancellationTokenSourceDisposed(CancellationTokenSource source)
        => Assert.True(IsCancellationTokenSourceDisposed(source));

    private sealed class FakeYtDlpDownloadService : IYtDlpDownloadService
    {
        private int _getVideoInfoCallCount;
        private int _downloadCallCount;

        public int GetVideoInfoCallCount => Volatile.Read(ref _getVideoInfoCallCount);
        public int DownloadCallCount => Volatile.Read(ref _downloadCallCount);
        public string? OutputDirectoryAtDownload { get; private set; }
        public VideoInfo? InfoToReturn { get; set; } = new()
        {
            Title = "legacy title",
            Platform = "Douyin"
        };

        public Task<VideoInfo?> GetVideoInfoAsync(string url, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _getVideoInfoCallCount);
            return Task.FromResult(InfoToReturn);
        }

        public Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _downloadCallCount);
            OutputDirectoryAtDownload = task.OutputDirectory;
            task.Status = DownloadStatus.Completed;
            task.Progress = 100;
            task.ErrorMessage = "";
            task.OutputFilePath = Path.Combine(task.OutputDirectory, $"{task.Title}.mp4");
            return Task.CompletedTask;
        }
    }

    private sealed class DownloadBlockingYtDlpDownloadService(int expectedTaskCount)
        : IYtDlpDownloadService
    {
        private readonly TaskCompletionSource _releaseDownloads = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _metadataCallCount;
        private int _downloadCallCount;

        public int MetadataCallCount => Volatile.Read(ref _metadataCallCount);
        public TaskCompletionSource AllMetadataResolved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllDownloadsCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _metadataCallCount) == expectedTaskCount)
                AllMetadataResolved.TrySetResult();
            return Task.FromResult<VideoInfo?>(new VideoInfo
            {
                Title = url,
                Platform = "Twitter"
            });
        }

        public async Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                task.Status = DownloadStatus.Downloading;
                await _releaseDownloads.Task.WaitAsync(cancellationToken);
                task.Status = DownloadStatus.Completed;
            }
            finally
            {
                if (Interlocked.Increment(ref _downloadCallCount) == expectedTaskCount)
                    AllDownloadsCompleted.TrySetResult();
            }
        }

        public void ReleaseDownloads() => _releaseDownloads.TrySetResult();
    }

    private sealed class QueueBlockingYtDlpDownloadService : IYtDlpDownloadService
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _resolvedUrlsLock = new();
        private readonly List<string> _resolvedUrls = [];

        public TaskCompletionSource FourMetadataRequestsStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> ResolvedUrls
        {
            get
            {
                lock (_resolvedUrlsLock)
                    return _resolvedUrls.ToArray();
            }
        }

        public async Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_resolvedUrlsLock)
            {
                _resolvedUrls.Add(url);
                if (_resolvedUrls.Count == 4)
                    FourMetadataRequestsStarted.TrySetResult();
            }
            await _release.Task.WaitAsync(cancellationToken);
            return new VideoInfo { Title = url, Platform = "Generic" };
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
    }

    private sealed class ResumeBlockingYtDlpDownloadService : IYtDlpDownloadService
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DownloadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
            => Task.FromResult<VideoInfo?>(null);

        public async Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            task.Status = DownloadStatus.Downloading;
            DownloadStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            task.Status = DownloadStatus.Completed;
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class PauseResumeIsolationYtDlpDownloadService : IYtDlpDownloadService
    {
        private readonly TaskCompletionSource _allowFirstAttemptToExit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completeResumedDownload = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _downloadCallCount;

        public TaskCompletionSource FirstDownloadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumedDownloadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
            => Task.FromResult<VideoInfo?>(null);

        public async Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            task.Status = DownloadStatus.Downloading;
            if (Interlocked.Increment(ref _downloadCallCount) == 1)
            {
                FirstDownloadStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstCancellationObserved.TrySetResult();
                    await _allowFirstAttemptToExit.Task;
                    throw;
                }
            }

            ResumedDownloadStarted.TrySetResult();
            await _completeResumedDownload.Task.WaitAsync(cancellationToken);
            task.Status = DownloadStatus.Completed;
        }

        public void AllowFirstAttemptToExit() => _allowFirstAttemptToExit.TrySetResult();

        public void CompleteResumedDownload() => _completeResumedDownload.TrySetResult();
    }

    private sealed class CoordinatorBackedYtDlpDownloadService(
        CookieAcquisitionCoordinator coordinator) : IYtDlpDownloadService
    {
        public async Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            var platform = MediaPlatformResolver.Resolve(url);
            await using var lease = await coordinator.AcquireArgumentsAsync(
                new CookieAttempt(CookieSourceKind.ManagedSession, platform),
                url,
                cancellationToken);
            return new VideoInfo { Title = url, Platform = "Twitter" };
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
    }

    private sealed class EmptyBrowserProfileDiscoveryService
        : IBrowserProfileDiscoveryService
    {
        public IReadOnlyList<BrowserProfile> Discover() => [];
    }

    private sealed class CountingManagedLoginSessionService
        : IManagedLoginSessionService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
            MediaPlatformDefinition platform,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken);
            return [new BrowserCookie(".x.com", "/", "auth_token", "value", true, 0)];
        }

        public Task ClearAsync(string platformId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class ThrowingMetadataYtDlpDownloadService(string message)
        : IYtDlpDownloadService
    {
        public Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
            => Task.FromException<VideoInfo?>(new InvalidOperationException(message));

        public Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ThrowingDownloadYtDlpDownloadService(string message)
        : IYtDlpDownloadService
    {
        public Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
            => Task.FromResult<VideoInfo?>(new VideoInfo
            {
                Title = "private",
                Platform = "Generic"
            });

        public Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException(message));
    }

}
