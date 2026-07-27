using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyGet.Models;
using EasyGet.Services;
using EasyGet.ViewModels;
using Xunit;

namespace EasyGet.Tests;

public class NotificationTests
{
    [Fact]
    public async Task NotificationItem_SelfDestructsAfter4Seconds()
    {
        var expired = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new NotificationItem("Test Msg", true);

        item.Expired += _ => expired.TrySetResult();

        // 异步等待计时器，避免阻塞测试工作线程后反过来饿死线程池计时回调。
        await expired.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, item.RemainingRatio);
    }

    [Fact]
    public async Task FailureNotification_PersistsUntilClosed()
    {
        var expired = false;
        var item = new NotificationItem("Download failed", false);
        item.Expired += _ => expired = true;

        await Task.Delay(TimeSpan.FromMilliseconds(4300));

        Assert.False(expired);
        Assert.Equal(1, item.RemainingRatio);
        item.Close();
    }

    [Fact]
    public void NotificationKindsUseDesignerDismissDurations()
    {
        var success = new NotificationItem("Done", NotificationKind.Success);
        var info = new NotificationItem("Detected", NotificationKind.Info);
        var failure = new NotificationItem("Failed", NotificationKind.Failure);

        Assert.Equal(TimeSpan.FromSeconds(4), success.AutoDismissAfter);
        Assert.Equal(TimeSpan.FromSeconds(5), info.AutoDismissAfter);
        Assert.Null(failure.AutoDismissAfter);
        Assert.True(success.IsSuccess);
        Assert.True(info.IsInfo);
        Assert.True(failure.IsFailure);

        success.Close();
        info.Close();
        failure.Close();
    }

    [Fact]
    public void FailureNotification_ExecutesRecoveryActionAndCloses()
    {
        var actionExecuted = false;
        var closed = false;
        var item = new NotificationItem("Download failed", false, "查看队列", () => actionExecuted = true);
        item.Closed += _ => closed = true;

        item.ExecuteActionCommand.Execute(null);

        Assert.True(actionExecuted);
        Assert.True(closed);
        Assert.True(item.HasAction);
        Assert.Equal("查看队列", item.ActionLabel);
    }

    [Fact]
    public void TaskFailureToastIncludesReasonAndNextStepCanRemainActionable()
    {
        var task = new DownloadTask
        {
            Title = "Example",
            Url = "https://example.com/video",
            ErrorMessage = "Cookie 已失效，请重新登录。"
        };

        var message = MainViewModel.BuildTaskFailureMessage(task);

        Assert.Contains("下载失败: Example", message, StringComparison.Ordinal);
        Assert.Contains("Cookie 已失效，请重新登录。", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotificationItem_PauseAndResumeTimer()
    {
        var item = new NotificationItem("Test Msg", true);

        // 等待计时器确实完成至少一次 tick，避免 CI runner 调度抖动导致固定延迟不稳定。
        Assert.True(await WaitUntilAsync(
            () => item.RemainingRatio < 1.0,
            TimeSpan.FromSeconds(5)));
        double initialRatio = item.RemainingRatio;

        // 暂停
        item.Pause();
        await Task.Delay(200);
        double pausedRatio = item.RemainingRatio;

        // 验证暂停后比例没有显着变动
        Assert.Equal(initialRatio, pausedRatio, 2);

        // 恢复
        item.Resume();
        Assert.True(await WaitUntilAsync(
            () => item.RemainingRatio < pausedRatio,
            TimeSpan.FromSeconds(5)));
        double resumedRatio = item.RemainingRatio;

        // 验证恢复后比例确实减少了
        Assert.True(resumedRatio < pausedRatio);

        item.Close();
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
            await Task.Delay(25);
        }

        return condition();
    }

    [Fact]
    public void MainViewModel_LimitsStackToThreeToasts()
    {
        var configService = new TestConfigService();
        var dbPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"easyget-notif-vm-{Guid.NewGuid():N}.db");

        try
        {
            using var history = new HistoryService(dbPath);
            var env = new EnvironmentService();
            var ytDlp = new YtDlpService(configService, env);
            var manager = new DownloadManager(ytDlp, history, configService);
            var downloadVM = new DownloadViewModel(manager, configService, new YtDlpVideoInfoProvider(ytDlp));
            var batchDownloadVM = new BatchDownloadViewModel(manager, configService, ytDlp);
            var historyVM = new HistoryViewModel(history);
            var settingsVM = new SettingsViewModel(configService, env, manager, new TelegramDownloadService(configService));
            var mainVM = new MainViewModel(
                env,
                manager,
                downloadVM,
                batchDownloadVM,
                historyVM,
                settingsVM
            );

            // 验证初始状态
            Assert.Empty(mainVM.Notifications);

            // 添加 4 条 Toast
            mainVM.ShowToast("1", true);
            mainVM.ShowToast("2", true);
            mainVM.ShowToast("3", true);
            mainVM.ShowToast("4", true);

            // 限制最大堆叠数为 3
            Assert.Equal(3, mainVM.Notifications.Count);

            // 验证第 1 条被移出，当前剩下 "2", "3", "4"
            Assert.Equal("2", mainVM.Notifications[0].Message);
            Assert.Equal("3", mainVM.Notifications[1].Message);
            Assert.Equal("4", mainVM.Notifications[2].Message);

            // 释放计时器
            foreach (var item in mainVM.Notifications.ToList())
            {
                item.Close();
            }
        }
        finally
        {
            foreach (var path in new[] { dbPath, $"{dbPath}-wal", $"{dbPath}-shm" })
            {
                try
                {
                    if (System.IO.File.Exists(path))
                        System.IO.File.Delete(path);
                }
                catch { }
            }
        }
    }

    [Fact]
    public void ClipboardDetectionCreatesActionableInfoToastWithoutChangingPage()
    {
        var configService = new TestConfigService();
        var dbPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"easyget-clipboard-toast-{Guid.NewGuid():N}.db");

        try
        {
            using var history = new HistoryService(dbPath);
            var env = new EnvironmentService();
            var ytDlp = new YtDlpService(configService, env);
            var manager = new DownloadManager(ytDlp, history, configService);
            var downloadVM = new DownloadViewModel(
                manager,
                configService,
                new YtDlpVideoInfoProvider(ytDlp));
            var batchDownloadVM = new BatchDownloadViewModel(manager, configService, ytDlp);
            var historyVM = new HistoryViewModel(history);
            var settingsVM = new SettingsViewModel(
                configService,
                env,
                manager,
                new TelegramDownloadService(configService));
            var mainVM = new MainViewModel(
                env,
                manager,
                downloadVM,
                batchDownloadVM,
                historyVM,
                settingsVM);
            mainVM.NavigateCommand.Execute("settings");

            downloadVM.CheckClipboardAndPrompt("https://example.com/video");

            var notification = Assert.Single(mainVM.Notifications);
            Assert.Equal(NotificationKind.Info, notification.Kind);
            Assert.Equal("立即解析", notification.ActionLabel);
            Assert.True(notification.HasAction);
            Assert.Equal(3, mainVM.SelectedNavIndex);
            notification.Close();
        }
        finally
        {
            foreach (var path in new[] { dbPath, $"{dbPath}-wal", $"{dbPath}-shm" })
            {
                try
                {
                    if (System.IO.File.Exists(path))
                        System.IO.File.Delete(path);
                }
                catch { }
            }
        }
    }

    [Fact]
    public void NotificationItem_MultipleCloseCallsAreSafeAndIdempotent()
    {
        var item = new NotificationItem("Concurrent Close Test", true);

        var exceptionCount = 0;
        var threads = Enumerable.Range(0, 10).Select(_ => new Thread(() =>
        {
            try
            {
                item.Pause();
                item.Resume();
                item.Close();
            }
            catch (Exception)
            {
                Interlocked.Increment(ref exceptionCount);
            }
        })).ToList();

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        Assert.Equal(0, exceptionCount);
    }
}
