using EasyGet.ViewModels;
using EasyGet.Services;
using EasyGet.Models;
using System.Windows.Shell;
using Xunit;
using System.IO;

namespace EasyGet.Tests;

public class TaskbarProgressTests
{
    [Fact]
    public void TaskbarProgressFollowsLifecycleStates()
    {
        var config = new ConfigService();
        var environment = new EnvironmentService();
        var historyPath = Path.Combine(
            Path.GetTempPath(),
            "EasyGetTests",
            Guid.NewGuid().ToString("N"),
            "history.db");
        var history = new HistoryService(historyPath);
        var ytDlp = new YtDlpService(config, environment);
        var manager = new DownloadManager(ytDlp, history, config);
        var batch = new BatchDownloadViewModel(manager, config, ytDlp);
        var settings = new SettingsViewModel(config, environment, manager);
        var download = new DownloadViewModel(manager, config, new YtDlpVideoInfoProvider(ytDlp));
        var historyVm = new HistoryViewModel(history, config);

        var main = new MainViewModel(
            config,
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

        // 2. 插入活跃任务：Normal 状态，进度对应
        var task1 = new DownloadTask { Status = DownloadStatus.Downloading, Progress = 40.0 };
        manager.Tasks.Add(task1);

        Assert.Equal(TaskbarItemProgressState.Normal, main.TaskbarState);
        Assert.Equal(0.4, main.TaskbarValue);

        // 3. 多个活跃任务：Normal 状态，进度取平均
        var task2 = new DownloadTask { Status = DownloadStatus.Downloading, Progress = 60.0 };
        manager.Tasks.Add(task2);

        Assert.Equal(TaskbarItemProgressState.Normal, main.TaskbarState);
        Assert.Equal(0.5, main.TaskbarValue); // (40 + 60) / 2 / 100 = 0.5

        // 4. 有失败任务加入，状态变为 Error
        var failedTask = new DownloadTask { Status = DownloadStatus.Failed };
        manager.Tasks.Add(failedTask);

        Assert.Equal(TaskbarItemProgressState.Error, main.TaskbarState);
        Assert.Equal(0.5, main.TaskbarValue); // 活跃任务依然是 task1 和 task2，平均值不变

        // 5. 活跃任务全部完成，清除任务栏进度
        task1.Status = DownloadStatus.Completed;
        task2.Status = DownloadStatus.Completed;

        Assert.Equal(TaskbarItemProgressState.None, main.TaskbarState);
        Assert.Equal(0.0, main.TaskbarValue);

        history.Dispose();
    }
}
