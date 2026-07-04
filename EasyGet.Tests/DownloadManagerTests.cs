using EasyGet.Models;
using EasyGet.Services;
using System.Reflection;
using Xunit;

namespace EasyGet.Tests;

public class DownloadManagerTests
{
    [Fact]
    public async Task EnqueueAsync_DouyinNoteWithSpecialEngineEnabled_SkipsYtDlpInfoAndRunsSidecar()
    {
        var outputDir = CreateTempOutputDirectory();
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-douyin-note-route");
        try
        {
            var configService = CreateConfigService(outputDir, enableDouyinSpecialEngine: true);
            using var historyService = new HistoryService(dbPath);
            var ytDlp = new FakeYtDlpDownloadService();
            var sidecar = new FakeDouyinSpecialDownloadService
            {
                Handler = (downloadTask, _, _) =>
                {
                    downloadTask.Title = "sidecar note";
                    downloadTask.Platform = "Douyin";
                    downloadTask.OutputFilePath = Path.Combine(downloadTask.OutputDirectory, "sidecar-note.json");
                    downloadTask.Status = DownloadStatus.Completed;
                    return Task.CompletedTask;
                }
            };
            var manager = CreateManager(ytDlp, sidecar, historyService, configService);
            var task = new DownloadTask
            {
                Url = "https://www.douyin.com/note/7621772413184822582",
                OutputDirectory = outputDir,
                Format = "json"
            };

            var finished = await EnqueueAndWaitAsync(manager, task);

            Assert.Same(task, finished);
            Assert.Equal(0, ytDlp.GetVideoInfoCallCount);
            Assert.Equal(0, ytDlp.DownloadCallCount);
            Assert.Equal(1, sidecar.DownloadCallCount);
            Assert.Equal("Douyin_Note_7621772413184822582", sidecar.TitleAtCall);
            Assert.Equal("Douyin", sidecar.PlatformAtCall);
            Assert.Equal(Path.Combine(outputDir, "抖音"), sidecar.OutputDirectoryAtCall);
            Assert.Equal(DownloadStatus.Completed, task.Status);

            var history = Assert.Single(await historyService.GetAllAsync());
            Assert.Equal(task.OutputFilePath, history.FilePath);
            Assert.Equal("Douyin", history.Platform);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task EnqueueAsync_DouyinSidecarCompleted_SavesAttachmentHistoryExcludingPrimaryFile()
    {
        var outputDir = CreateTempOutputDirectory();
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-douyin-sidecar-attachments");
        try
        {
            var configService = CreateConfigService(outputDir, enableDouyinSpecialEngine: true);
            using var historyService = new HistoryService(dbPath);
            var ytDlp = new FakeYtDlpDownloadService();
            var sidecar = new FakeDouyinSpecialDownloadService
            {
                Handler = (downloadTask, _, _) =>
                {
                    var primaryPath = Path.Combine(downloadTask.OutputDirectory, "video.mp4");
                    var commentsPath = Path.Combine(downloadTask.OutputDirectory, "comments.json");
                    var metadataPath = Path.Combine(downloadTask.OutputDirectory, "metadata.json");

                    downloadTask.Title = "sidecar with attachments";
                    downloadTask.Platform = "Douyin";
                    downloadTask.OutputFilePath = primaryPath;
                    SetStringListProperty(downloadTask, "OutputFilePaths", [primaryPath, commentsPath, metadataPath, primaryPath]);
                    downloadTask.Status = DownloadStatus.Completed;
                    return Task.CompletedTask;
                }
            };
            var manager = CreateManager(ytDlp, sidecar, historyService, configService);
            var task = new DownloadTask
            {
                Url = "https://www.douyin.com/video/7621772413184822582",
                OutputDirectory = outputDir
            };

            await EnqueueAndWaitAsync(manager, task);

            var history = Assert.Single(await historyService.GetAllAsync());

            Assert.Equal(task.OutputFilePath, history.FilePath);
            Assert.Equal(
                [
                    Path.Combine(task.OutputDirectory, "comments.json"),
                    Path.Combine(task.OutputDirectory, "metadata.json")
                ],
                GetStringListProperty(history, "AttachmentFilePaths"));
        }
        finally
        {
            TryDeleteDirectory(outputDir);
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task EnqueueAsync_DouyinSidecarCompleted_DropsUnsafeAttachmentHistoryPaths()
    {
        var outputDir = CreateTempOutputDirectory();
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-douyin-sidecar-unsafe-attachments");
        try
        {
            var configService = CreateConfigService(outputDir, enableDouyinSpecialEngine: true);
            using var historyService = new HistoryService(dbPath);
            var ytDlp = new FakeYtDlpDownloadService();
            var sidecar = new FakeDouyinSpecialDownloadService
            {
                Handler = (downloadTask, _, _) =>
                {
                    var primaryPath = Path.Combine(downloadTask.OutputDirectory, "video.mp4");
                    var safeAttachmentPath = Path.Combine(downloadTask.OutputDirectory, "comments.json");
                    var unsafeAttachmentPath = Path.Combine(outputDir, "outside.json");

                    downloadTask.Title = "sidecar unsafe attachments";
                    downloadTask.Platform = "Douyin";
                    downloadTask.OutputFilePath = primaryPath;
                    SetStringListProperty(downloadTask, "OutputFilePaths", [primaryPath, safeAttachmentPath, unsafeAttachmentPath]);
                    downloadTask.Status = DownloadStatus.Completed;
                    return Task.CompletedTask;
                }
            };
            var manager = CreateManager(ytDlp, sidecar, historyService, configService);
            var task = new DownloadTask
            {
                Url = "https://www.douyin.com/video/7621772413184822582",
                OutputDirectory = outputDir
            };

            await EnqueueAndWaitAsync(manager, task);

            var history = Assert.Single(await historyService.GetAllAsync());

            Assert.Equal([Path.Combine(task.OutputDirectory, "comments.json")], GetStringListProperty(history, "AttachmentFilePaths"));
            Assert.DoesNotContain(Path.Combine(outputDir, "outside.json"), GetStringListProperty(history, "AttachmentFilePaths"));
        }
        finally
        {
            TryDeleteDirectory(outputDir);
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task EnqueueAsync_DouyinUserWithSpecialEngineEnabled_PassesConfigIntoSidecarRequest()
    {
        var outputDir = CreateTempOutputDirectory();
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-douyin-user-route");
        try
        {
            var configService = CreateConfigService(outputDir, enableDouyinSpecialEngine: true);
            configService.Config.CookieContent = "ttwid=abc; odin_tt=def";
            configService.Config.UseProxy = true;
            configService.Config.ProxyAddress = "http://127.0.0.1:7890";
            configService.Config.DouyinMode = "post";
            configService.Config.DouyinLimit = 12;
            configService.Config.DouyinDownloadCover = true;
            configService.Config.DouyinDownloadMusic = true;
            configService.Config.DouyinDownloadJson = true;
            using var historyService = new HistoryService(dbPath);
            var ytDlp = new FakeYtDlpDownloadService();
            var sidecar = new FakeDouyinSpecialDownloadService();
            var manager = CreateManager(ytDlp, sidecar, historyService, configService);
            var task = new DownloadTask
            {
                Url = "https://www.douyin.com/user/MS4wLjABAAAA_test",
                OutputDirectory = outputDir
            };

            await EnqueueAndWaitAsync(manager, task);

            Assert.Equal(0, ytDlp.GetVideoInfoCallCount);
            Assert.Equal(1, sidecar.DownloadCallCount);
            Assert.Same(configService.Config, sidecar.ConfigAtCall);
            Assert.Equal("ttwid=abc; odin_tt=def", sidecar.ConfigAtCall?.CookieContent);
            Assert.True(sidecar.ConfigAtCall?.UseProxy);
            Assert.Equal("http://127.0.0.1:7890", sidecar.ConfigAtCall?.ProxyAddress);
            Assert.Equal("post", sidecar.ConfigAtCall?.DouyinMode);
            Assert.Equal(12, sidecar.ConfigAtCall?.DouyinLimit);
            Assert.True(sidecar.ConfigAtCall?.DouyinDownloadCover);
            Assert.True(sidecar.ConfigAtCall?.DouyinDownloadMusic);
            Assert.True(sidecar.ConfigAtCall?.DouyinDownloadJson);
            Assert.Equal("Douyin_User_MS4wLjABAAAA_test", sidecar.TitleAtCall);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task EnqueueAsync_DouyinGallerySidecarUnavailable_FailsWithoutYtDlpFallback()
    {
        var outputDir = CreateTempOutputDirectory();
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-douyin-gallery-route");
        try
        {
            var configService = CreateConfigService(outputDir, enableDouyinSpecialEngine: true);
            using var historyService = new HistoryService(dbPath);
            var ytDlp = new FakeYtDlpDownloadService();
            var sidecar = new FakeDouyinSpecialDownloadService
            {
                ExceptionToThrow = new InvalidOperationException("Douyin sidecar was not found: sidecar.py")
            };
            var manager = CreateManager(ytDlp, sidecar, historyService, configService);
            var task = new DownloadTask
            {
                Url = "https://www.douyin.com/gallery/7621772413184822582",
                OutputDirectory = outputDir
            };

            await EnqueueAndWaitAsync(manager, task);

            Assert.Equal(0, ytDlp.GetVideoInfoCallCount);
            Assert.Equal(0, ytDlp.DownloadCallCount);
            Assert.Equal(1, sidecar.DownloadCallCount);
            Assert.Equal(DownloadStatus.Failed, task.Status);
            Assert.Contains("sidecar", task.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task EnqueueAsync_DouyinSpecialEngineDisabled_UsesLegacyYtDlpFlow()
    {
        var outputDir = CreateTempOutputDirectory();
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-douyin-disabled-route");
        try
        {
            var configService = CreateConfigService(outputDir, enableDouyinSpecialEngine: false);
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
            var sidecar = new FakeDouyinSpecialDownloadService();
            var manager = CreateManager(ytDlp, sidecar, historyService, configService);
            var task = new DownloadTask
            {
                Url = "https://www.douyin.com/note/7621772413184822582",
                OutputDirectory = outputDir
            };

            await EnqueueAndWaitAsync(manager, task);

            Assert.Equal(1, ytDlp.GetVideoInfoCallCount);
            Assert.Equal(1, ytDlp.DownloadCallCount);
            Assert.Equal(0, sidecar.DownloadCallCount);
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

    [Fact]
    public async Task EnqueueAsync_DouyinVideoSidecarStartupFailure_FallsBackToYtDlp()
    {
        var outputDir = CreateTempOutputDirectory();
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-douyin-video-fallback");
        try
        {
            var configService = CreateConfigService(outputDir, enableDouyinSpecialEngine: true);
            using var historyService = new HistoryService(dbPath);
            var ytDlp = new FakeYtDlpDownloadService();
            var sidecar = new FakeDouyinSpecialDownloadService
            {
                ExceptionToThrow = new InvalidOperationException("Failed to start Douyin sidecar: python")
            };
            var manager = CreateManager(ytDlp, sidecar, historyService, configService);
            var task = new DownloadTask
            {
                Url = "https://www.douyin.com/video/7621772413184822582",
                OutputDirectory = outputDir
            };

            await EnqueueAndWaitAsync(manager, task);

            Assert.Equal(0, ytDlp.GetVideoInfoCallCount);
            Assert.Equal(1, sidecar.DownloadCallCount);
            Assert.Equal(1, ytDlp.DownloadCallCount);
            Assert.Equal("Douyin_Video_7621772413184822582", sidecar.TitleAtCall);
            Assert.Equal(DownloadStatus.Completed, task.Status);
            Assert.Equal("", task.ErrorMessage);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task EnqueueAsync_DouyinVideoDownloaderRootMissing_FallsBackToYtDlp()
    {
        var outputDir = CreateTempOutputDirectory();
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-douyin-video-root-missing");
        try
        {
            var configService = CreateConfigService(outputDir, enableDouyinSpecialEngine: true);
            using var historyService = new HistoryService(dbPath);
            var ytDlp = new FakeYtDlpDownloadService();
            var sidecar = new FakeDouyinSpecialDownloadService
            {
                Handler = (downloadTask, _, _) =>
                {
                    downloadTask.Status = DownloadStatus.Failed;
                    downloadTask.ErrorMessage = "douyin-downloader-promax root not found";
                    downloadTask.DouyinSuccessCount = 1;
                    downloadTask.DouyinFailedCount = 1;
                    downloadTask.DouyinSkippedCount = 1;
                    downloadTask.DouyinTaskEventLog = "sidecar root missing";
                    return Task.CompletedTask;
                }
            };
            var manager = CreateManager(ytDlp, sidecar, historyService, configService);
            var task = new DownloadTask
            {
                Url = "https://www.douyin.com/video/7621772413184822582",
                OutputDirectory = outputDir
            };

            await EnqueueAndWaitAsync(manager, task);

            Assert.Equal(1, sidecar.DownloadCallCount);
            Assert.Equal(1, ytDlp.DownloadCallCount);
            Assert.Equal(DownloadStatus.Completed, task.Status);
            Assert.Equal("", task.ErrorMessage);
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

    [Fact]
    public async Task EnqueueAsync_DouyinVideoTerminalFailedDoesNotFallbackToYtDlp()
    {
        var outputDir = CreateTempOutputDirectory();
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-douyin-video-terminal-failed");
        try
        {
            var configService = CreateConfigService(outputDir, enableDouyinSpecialEngine: true);
            using var historyService = new HistoryService(dbPath);
            var ytDlp = new FakeYtDlpDownloadService();
            var sidecar = new FakeDouyinSpecialDownloadService
            {
                Handler = (downloadTask, _, _) =>
                {
                    downloadTask.Status = DownloadStatus.Failed;
                    downloadTask.ErrorMessage = "Douyin sidecar failed: cookie expired";
                    return Task.CompletedTask;
                }
            };
            var manager = CreateManager(ytDlp, sidecar, historyService, configService);
            var task = new DownloadTask
            {
                Url = "https://www.douyin.com/video/7621772413184822582",
                OutputDirectory = outputDir
            };

            await EnqueueAndWaitAsync(manager, task);

            Assert.Equal(1, sidecar.DownloadCallCount);
            Assert.Equal(0, ytDlp.DownloadCallCount);
            Assert.Equal(DownloadStatus.Failed, task.Status);
            Assert.Equal("Douyin sidecar failed: cookie expired", task.ErrorMessage);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task EnqueueAsync_DouyinVideoPromaxCookieFailureDoesNotFallbackToYtDlp()
    {
        var outputDir = CreateTempOutputDirectory();
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-douyin-video-promax-cookie-failed");
        const string error = "douyin-downloader-promax core api Cookie invalid";
        try
        {
            var configService = CreateConfigService(outputDir, enableDouyinSpecialEngine: true);
            using var historyService = new HistoryService(dbPath);
            var ytDlp = new FakeYtDlpDownloadService();
            var sidecar = new FakeDouyinSpecialDownloadService
            {
                Handler = (downloadTask, _, _) =>
                {
                    downloadTask.Status = DownloadStatus.Failed;
                    downloadTask.ErrorMessage = error;
                    return Task.CompletedTask;
                }
            };
            var manager = CreateManager(ytDlp, sidecar, historyService, configService);
            var task = new DownloadTask
            {
                Url = "https://www.douyin.com/video/7621772413184822582",
                OutputDirectory = outputDir
            };

            await EnqueueAndWaitAsync(manager, task);

            Assert.Equal(1, sidecar.DownloadCallCount);
            Assert.Equal(0, ytDlp.DownloadCallCount);
            Assert.Equal(DownloadStatus.Failed, task.Status);
            Assert.Equal(error, task.ErrorMessage);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task EnqueueAsync_DouyinShortLinkApiClientRateLimitFailureDoesNotFallbackToYtDlp()
    {
        var outputDir = CreateTempOutputDirectory();
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-douyin-shortlink-api-client-rate-limit");
        const string error = @"core\api_client.py signature/rate limit";
        try
        {
            var configService = CreateConfigService(outputDir, enableDouyinSpecialEngine: true);
            using var historyService = new HistoryService(dbPath);
            var ytDlp = new FakeYtDlpDownloadService();
            var sidecar = new FakeDouyinSpecialDownloadService
            {
                Handler = (downloadTask, _, _) =>
                {
                    downloadTask.Status = DownloadStatus.Failed;
                    downloadTask.ErrorMessage = error;
                    return Task.CompletedTask;
                }
            };
            var manager = CreateManager(ytDlp, sidecar, historyService, configService);
            var task = new DownloadTask
            {
                Url = "https://v.douyin.com/i6EpMYVJgA8/",
                OutputDirectory = outputDir
            };

            await EnqueueAndWaitAsync(manager, task);

            Assert.Equal(1, sidecar.DownloadCallCount);
            Assert.Equal(0, ytDlp.DownloadCallCount);
            Assert.Equal(DownloadStatus.Failed, task.Status);
            Assert.Equal(error, task.ErrorMessage);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task EnqueueAsync_DouyinShortLinkDownloaderRootMissing_FallsBackToYtDlp()
    {
        var outputDir = CreateTempOutputDirectory();
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-douyin-shortlink-root-missing");
        try
        {
            var configService = CreateConfigService(outputDir, enableDouyinSpecialEngine: true);
            using var historyService = new HistoryService(dbPath);
            var ytDlp = new FakeYtDlpDownloadService();
            var sidecar = new FakeDouyinSpecialDownloadService
            {
                Handler = (downloadTask, _, _) =>
                {
                    downloadTask.Status = DownloadStatus.Failed;
                    downloadTask.ErrorMessage = "downloader root missing";
                    return Task.CompletedTask;
                }
            };
            var manager = CreateManager(ytDlp, sidecar, historyService, configService);
            var task = new DownloadTask
            {
                Url = "https://v.douyin.com/i6EpMYVJgA8/",
                OutputDirectory = outputDir
            };

            await EnqueueAndWaitAsync(manager, task);

            Assert.Equal(1, sidecar.DownloadCallCount);
            Assert.Equal(1, ytDlp.DownloadCallCount);
            Assert.Equal(DownloadStatus.Completed, task.Status);
            Assert.Equal("", task.ErrorMessage);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Theory]
    [InlineData("https://www.douyin.com/collection/7621772413184822582", "Douyin_Collection_7621772413184822582")]
    [InlineData("https://www.douyin.com/mix/7621772413184822582", "Douyin_Mix_7621772413184822582")]
    [InlineData("https://www.douyin.com/music/7621772413184822582", "Douyin_Music_7621772413184822582")]
    public async Task EnqueueAsync_DouyinCollectionMixMusicWithSpecialEngineEnabled_UsesSidecar(
        string url,
        string expectedTitle)
    {
        var outputDir = CreateTempOutputDirectory();
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-douyin-single-link-route");
        try
        {
            var configService = CreateConfigService(outputDir, enableDouyinSpecialEngine: true);
            using var historyService = new HistoryService(dbPath);
            var ytDlp = new FakeYtDlpDownloadService();
            var sidecar = new FakeDouyinSpecialDownloadService();
            var manager = CreateManager(ytDlp, sidecar, historyService, configService);
            var task = new DownloadTask
            {
                Url = url,
                OutputDirectory = outputDir
            };

            await EnqueueAndWaitAsync(manager, task);

            Assert.Equal(0, ytDlp.GetVideoInfoCallCount);
            Assert.Equal(0, ytDlp.DownloadCallCount);
            Assert.Equal(1, sidecar.DownloadCallCount);
            Assert.Equal(expectedTitle, sidecar.TitleAtCall);
            Assert.Equal("Douyin", sidecar.PlatformAtCall);
            Assert.Equal(DownloadStatus.Completed, task.Status);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task EnqueueAsync_DouyinLiveWithSpecialEngineEnabled_UsesSidecar()
    {
        var outputDir = CreateTempOutputDirectory();
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-douyin-live-route");
        try
        {
            var configService = CreateConfigService(outputDir, enableDouyinSpecialEngine: true);
            using var historyService = new HistoryService(dbPath);
            var ytDlp = new FakeYtDlpDownloadService();
            var sidecar = new FakeDouyinSpecialDownloadService();
            var manager = CreateManager(ytDlp, sidecar, historyService, configService);
            var task = new DownloadTask
            {
                Url = "https://www.douyin.com/live/7621772413184822582",
                OutputDirectory = outputDir
            };

            await EnqueueAndWaitAsync(manager, task);

            Assert.Equal(0, ytDlp.GetVideoInfoCallCount);
            Assert.Equal(0, ytDlp.DownloadCallCount);
            Assert.Equal(1, sidecar.DownloadCallCount);
            Assert.Equal("Douyin_Live_7621772413184822582", sidecar.TitleAtCall);
            Assert.Equal("Douyin", sidecar.PlatformAtCall);
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
        var configService = new ConfigService();
        configService.Config.MaxConcurrentDownloads = 1;
        using var historyService = new HistoryService();
        var manager = new DownloadManager(
            new YtDlpService(configService, new EnvironmentService()),
            historyService,
            configService);
        var semaphore = GetSemaphore(manager);
        await semaphore.WaitAsync();

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
            manager.Cancel(task.Id);

            var completed = await Task.WhenAny(finished.Task, Task.Delay(1000));

            Assert.Same(finished.Task, completed);
            Assert.Same(task, await finished.Task);
            Assert.Equal(DownloadStatus.Cancelled, task.Status);
        }
        finally
        {
            semaphore.Release();
        }
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
            var sidecar = new FakeDouyinSpecialDownloadService();
            var manager = CreateManager(ytDlp, sidecar, historyService, configService);
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

    private static SemaphoreSlim GetSemaphore(DownloadManager manager)
    {
        var field = typeof(DownloadManager).GetField(
            "_semaphore",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        return (SemaphoreSlim)field!.GetValue(manager)!;
    }

    private static IReadOnlyList<string> GetStringListProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        var value = property!.GetValue(instance);
        var strings = Assert.IsAssignableFrom<IEnumerable<string>>(value);
        return strings.ToList();
    }

    private static void SetStringListProperty(object instance, string propertyName, IEnumerable<string> values)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);

        if (property!.CanWrite)
        {
            property.SetValue(instance, values.ToList());
            return;
        }

        var currentValue = property.GetValue(instance);
        var collection = Assert.IsAssignableFrom<ICollection<string>>(currentValue);
        collection.Clear();
        foreach (var value in values)
            collection.Add(value);
    }

    private static DownloadManager CreateManager(
        FakeYtDlpDownloadService ytDlp,
        FakeDouyinSpecialDownloadService sidecar,
        HistoryService historyService,
        ConfigService configService)
        => new(ytDlp, historyService, configService, douyinSpecialDownloadService: sidecar);

    private static ConfigService CreateConfigService(string outputDir, bool enableDouyinSpecialEngine)
    {
        var configService = new ConfigService();
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

    private sealed class FakeYtDlpDownloadService : IYtDlpDownloadService
    {
        public int GetVideoInfoCallCount { get; private set; }
        public int DownloadCallCount { get; private set; }
        public string? OutputDirectoryAtDownload { get; private set; }
        public VideoInfo? InfoToReturn { get; set; } = new()
        {
            Title = "legacy title",
            Platform = "Douyin"
        };

        public Task<VideoInfo?> GetVideoInfoAsync(string url, CancellationToken cancellationToken = default)
        {
            GetVideoInfoCallCount++;
            return Task.FromResult(InfoToReturn);
        }

        public Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCallCount++;
            OutputDirectoryAtDownload = task.OutputDirectory;
            task.Status = DownloadStatus.Completed;
            task.Progress = 100;
            task.ErrorMessage = "";
            task.OutputFilePath = Path.Combine(task.OutputDirectory, $"{task.Title}.mp4");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDouyinSpecialDownloadService : IDouyinSpecialDownloadService
    {
        public int DownloadCallCount { get; private set; }
        public string? TitleAtCall { get; private set; }
        public string? PlatformAtCall { get; private set; }
        public string? OutputDirectoryAtCall { get; private set; }
        public AppConfig? ConfigAtCall { get; private set; }
        public Exception? ExceptionToThrow { get; set; }
        public Func<DownloadTask, AppConfig, CancellationToken, Task>? Handler { get; set; }

        public Task DownloadAsync(
            DownloadTask task,
            AppConfig config,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCallCount++;
            TitleAtCall = task.Title;
            PlatformAtCall = task.Platform;
            OutputDirectoryAtCall = task.OutputDirectory;
            ConfigAtCall = config;

            if (ExceptionToThrow is not null)
                throw ExceptionToThrow;

            if (Handler is not null)
                return Handler(task, config, cancellationToken);

            task.Status = DownloadStatus.Completed;
            task.Progress = 100;
            task.ErrorMessage = "";
            task.OutputFilePath = Path.Combine(task.OutputDirectory, $"{task.Title}.json");
            return Task.CompletedTask;
        }
    }
}
