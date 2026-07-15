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
    public async Task StartBatchDownload_AddsAllTasksBeforeMetadataResolutionCompletes()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
        var blocker = new BlockingYtDlpDownloadService();
        var manager = new DownloadManager(blocker, history, config);
        var concreteYtDlp = new YtDlpService(config, new EnvironmentService());
        var viewModel = new BatchDownloadViewModel(manager, config, concreteYtDlp)
        {
            UrlsText = string.Join(
                '\n',
                Enumerable.Range(1, 20).Select(i => $"https://x.com/user/status/{i}"))
        };

        var command = viewModel.StartBatchDownloadCommand.ExecuteAsync(null);
        await blocker.FirstMetadataRequest.WaitAsync(TimeSpan.FromSeconds(2));
        var admittedTaskCount = manager.Tasks.Count;
        blocker.Release();
        await command.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(20, admittedTaskCount);
        Assert.InRange(blocker.MaxConcurrentMetadataRequests, 1, 4);
    }

    [Fact]
    public async Task StartBatchDownload_DeduplicatesInputAndExistingQueueUrls()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var config = new ConfigService(root.Path("config"));
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

        await viewModel.StartBatchDownloadCommand.ExecuteAsync(null);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(3, manager.Tasks.Count);
        Assert.Single(manager.Tasks, task => string.Equals(
            task.Url,
            "https://example.com/new",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BatchDownloadXaml_ShowsTaskStatusTextIncludingAuthenticationPhase()
    {
        var xaml = File.ReadAllText(
            TestRepositoryPaths.GetViewPath("BatchDownloadView.xaml"));

        Assert.Contains("Text=\"{Binding StatusText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DisplayTitle}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelAll_WhenConfirmed_CancelsAndClearsTasks()
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

            // 验证任务 2 (Failed) 已从列表中清除
            Assert.DoesNotContain(task2, manager.Tasks);
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
            Assert.Contains("已导入 2 个链接", receivedMsg);
            Assert.Contains("忽略了 1 行无效文本", receivedMsg);
            Assert.True(receivedSuccess);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public void ImportText_WithOnlyValidUrls_ImportsAllAndDoesNotRaiseNotification()
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

            // 验证没有触发忽略警告通知
            Assert.Null(receivedMsg);
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
            Assert.Contains("已导入 0 个链接", receivedMsg);
            Assert.Contains("忽略了 2 行", receivedMsg);
            Assert.False(receivedSuccess);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
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
                return new VideoInfo { Title = url, Platform = "Twitter" };
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
