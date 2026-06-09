using EasyGet.Models;
using EasyGet.Services;
using System.Reflection;
using Xunit;

namespace EasyGet.Tests;

public class DownloadManagerTests
{
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
}
