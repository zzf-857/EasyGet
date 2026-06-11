using System;
using System.IO;
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
        => Path.Combine(
            Path.GetTempPath(),
            $"easyget-batch-vm-{Guid.NewGuid():N}.db");

    private static void TryDeleteDatabase(string dbPath)
    {
        foreach (var path in new[] { dbPath, $"{dbPath}-wal", $"{dbPath}-shm" })
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }

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
}
