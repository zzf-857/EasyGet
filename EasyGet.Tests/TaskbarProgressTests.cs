using EasyGet.ViewModels;
using EasyGet.Services;
using EasyGet.Models;
using System.Windows.Shell;
using Xunit;

namespace EasyGet.Tests;

public class TaskbarProgressTests
{
    [Fact]
    public void TaskbarProgressFollowsLifecycleStates()
    {
        using var root = new TestDirectory();
        var config = new ConfigService(root.Path("config"));
        var environment = new EnvironmentService();
        using var history = new HistoryService(root.Path("history.db"));
        var ytDlp = new YtDlpService(config, environment);
        using var manager = new DownloadManager(ytDlp, history, config);
        using var telegram = new TelegramDownloadService(config);
        var batch = new BatchDownloadViewModel(manager, config, ytDlp);
        var settings = new SettingsViewModel(config, environment, manager, telegram);
        var download = new DownloadViewModel(manager, config, new YtDlpVideoInfoProvider(ytDlp));
        var historyVm = new HistoryViewModel(history, config);

        var main = new MainViewModel(
            environment,
            manager,
            download,
            batch,
            historyVm,
            settings
        );

        // 1. 初始状态：无任务，None
        Assert.Equal(TaskbarItemProgressState.None, main.TaskbarState);
        Assert.Equal(0.0, main.TaskbarValue);

        var scheduledTask = new DownloadTask
        {
            Status = DownloadStatus.Scheduled,
            ScheduledStartTimeUtc = DateTimeOffset.UtcNow.AddHours(1)
        };
        manager.Tasks.Add(scheduledTask);

        Assert.Equal(1, main.ScheduledTaskCount);
        Assert.Contains("1 计划", main.TaskStatusText, StringComparison.Ordinal);
        Assert.Equal(TaskbarItemProgressState.Normal, main.TaskbarState);
        Assert.Equal(0.0, main.TaskbarValue);

        // 2. 插入下载任务：分母含计划任务，Completed 口径与 OverallProgress 一致
        var task1 = new DownloadTask { Status = DownloadStatus.Downloading, Progress = 40.0 };
        manager.Tasks.Add(task1);

        Assert.Equal(TaskbarItemProgressState.Normal, main.TaskbarState);
        Assert.Equal(0.2, main.TaskbarValue); // (0 + 40) / 2 / 100

        var task2 = new DownloadTask { Status = DownloadStatus.Downloading, Progress = 60.0 };
        manager.Tasks.Add(task2);

        Assert.Equal(TaskbarItemProgressState.Normal, main.TaskbarState);
        Assert.Equal(1.0 / 3.0, main.TaskbarValue, precision: 6); // (0 + 40 + 60) / 3 / 100

        var failedTask = new DownloadTask { Status = DownloadStatus.Failed, Progress = 20.0 };
        manager.Tasks.Add(failedTask);

        Assert.Equal(TaskbarItemProgressState.Error, main.TaskbarState);
        Assert.Equal(0.3, main.TaskbarValue); // (0 + 40 + 60 + 20) / 4 / 100

        task1.Status = DownloadStatus.Completed;
        task2.Status = DownloadStatus.Completed;

        Assert.Equal(TaskbarItemProgressState.Error, main.TaskbarState);
        Assert.Equal(0.55, main.TaskbarValue); // (0 + 100 + 100 + 20) / 4 / 100

        scheduledTask.Status = DownloadStatus.Completed;
        Assert.Equal(TaskbarItemProgressState.None, main.TaskbarState);
        Assert.Equal(0.0, main.TaskbarValue);
    }

    [Fact]
    public void TaskbarProgress_DoesNotJumpBackwardWhenOneTaskCompletes()
    {
        using var root = new TestDirectory();
        var config = new ConfigService(root.Path("config"));
        var environment = new EnvironmentService();
        using var history = new HistoryService(root.Path("history.db"));
        var ytDlp = new YtDlpService(config, environment);
        using var manager = new DownloadManager(ytDlp, history, config);
        using var telegram = new TelegramDownloadService(config);
        var batch = new BatchDownloadViewModel(manager, config, ytDlp);
        var settings = new SettingsViewModel(config, environment, manager, telegram);
        var download = new DownloadViewModel(manager, config, new YtDlpVideoInfoProvider(ytDlp));
        var historyVm = new HistoryViewModel(history, config);
        var main = new MainViewModel(environment, manager, download, batch, historyVm, settings);

        var nearlyDone = new DownloadTask { Status = DownloadStatus.Downloading, Progress = 90.0 };
        var justStarted = new DownloadTask { Status = DownloadStatus.Downloading, Progress = 10.0 };
        manager.Tasks.Add(nearlyDone);
        manager.Tasks.Add(justStarted);

        Assert.Equal(TaskbarItemProgressState.Normal, main.TaskbarState);
        Assert.Equal(0.5, main.TaskbarValue);

        nearlyDone.Status = DownloadStatus.Completed;

        Assert.Equal(TaskbarItemProgressState.Normal, main.TaskbarState);
        Assert.Equal(0.55, main.TaskbarValue);
    }

    [Fact]
    public void TaskbarProgress_CountsPausedTasksAndShowsPausedState()
    {
        using var root = new TestDirectory();
        var config = new ConfigService(root.Path("config"));
        var environment = new EnvironmentService();
        using var history = new HistoryService(root.Path("history.db"));
        var ytDlp = new YtDlpService(config, environment);
        using var manager = new DownloadManager(ytDlp, history, config);
        using var telegram = new TelegramDownloadService(config);
        var batch = new BatchDownloadViewModel(manager, config, ytDlp);
        var settings = new SettingsViewModel(config, environment, manager, telegram);
        var download = new DownloadViewModel(manager, config, new YtDlpVideoInfoProvider(ytDlp));
        var historyVm = new HistoryViewModel(history, config);
        var main = new MainViewModel(environment, manager, download, batch, historyVm, settings);

        manager.Tasks.Add(new DownloadTask { Status = DownloadStatus.Paused, Progress = 30.0 });
        manager.Tasks.Add(new DownloadTask { Status = DownloadStatus.Waiting, Progress = 0.0 });

        Assert.Equal(TaskbarItemProgressState.Paused, main.TaskbarState);
        Assert.Equal(0.15, main.TaskbarValue);
    }
}
