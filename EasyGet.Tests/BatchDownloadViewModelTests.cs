using System;
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

    [Fact]
    public void CancelAll_WhenConfirmed_CancelsAndClearsTasks()
    {
        var dbPath = CreateTempDatabasePath();
        using var history = new HistoryService(dbPath);
        var configService = new ConfigService();
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
        var configService = new ConfigService();
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
        var configService = new ConfigService();
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
        var configService = new ConfigService();
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
        var configService = new ConfigService();
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
}
