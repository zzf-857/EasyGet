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
        var expiredEvent = new AutoResetEvent(false);
        var item = new NotificationItem("Test Msg", true);

        item.Expired += (x) =>
        {
            expiredEvent.Set();
        };

        // 等待倒计时完成，由于是 4 秒，我们等待最多 4.5 秒
        bool signaled = expiredEvent.WaitOne(4500);

        Assert.True(signaled);
        Assert.Equal(0, item.RemainingRatio);
    }

    [Fact]
    public async Task NotificationItem_PauseAndResumeTimer()
    {
        var item = new NotificationItem("Test Msg", true);

        // 初始运行一会
        await Task.Delay(200);
        double initialRatio = item.RemainingRatio;
        Assert.True(initialRatio < 1.0);

        // 暂停
        item.Pause();
        await Task.Delay(200);
        double pausedRatio = item.RemainingRatio;

        // 验证暂停后比例没有显着变动
        Assert.Equal(initialRatio, pausedRatio, 2);

        // 恢复
        item.Resume();
        await Task.Delay(200);
        double resumedRatio = item.RemainingRatio;

        // 验证恢复后比例确实减少了
        Assert.True(resumedRatio < pausedRatio);

        item.Close();
    }

    [Fact]
    public void MainViewModel_LimitsStackToThreeToasts()
    {
        var configService = new ConfigService();
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
            var settingsVM = new SettingsViewModel(configService, env, manager);

            var mainVM = new MainViewModel(
                configService,
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
