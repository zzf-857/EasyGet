using System.Reflection;
using System.Windows.Threading;
using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class DownloadManagerDispatcherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttemptUpdates_WaitForUiBeforeLockingAndRejectCancelledQueuedUpdates(bool cancelWhileQueued)
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        using var ui = new DispatcherThread();
        using var manager = new DownloadManager(new UnusedDownloadService(), history,
            new ConfigService(root.Path("config")), null, null, null, null, ui.Dispatcher);
        var task = new DownloadTask { Status = DownloadStatus.Resolving };
        manager.Tasks.Add(task);
        var begin = typeof(DownloadManager).GetMethod("BeginAttemptAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var beginTask = (Task)begin.Invoke(manager, [task])!;
        await beginTask;
        var attempt = beginTask.GetType().GetProperty("Result")!.GetValue(beginTask)!;
        var updateSync = attempt.GetType().GetProperty("UpdateSync")!.GetValue(attempt)!;
        var update = typeof(DownloadManager).GetMethod("TryUpdateCurrentAttempt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var finish = typeof(DownloadManager).GetMethod("FinishAttempt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var uiEntered = new ManualResetEventSlim();
        using var releaseUi = new ManualResetEventSlim();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherHookEventHandler onPosted = (_, _) => posted.TrySetResult();
        Task<bool>? updateTask = null;
        try
        {
            _ = ui.Dispatcher.InvokeAsync(() =>
            {
                uiEntered.Set();
                releaseUi.Wait(TimeSpan.FromSeconds(10));
            });
            Assert.True(uiEntered.Wait(TimeSpan.FromSeconds(2)));
            ui.Dispatcher.Hooks.OperationPosted += onPosted;

            updateTask = Task.Run(() => (bool)update.Invoke(manager,
                [attempt, (Action<DownloadTask>)(current =>
                    ui.Dispatcher.Invoke(() => current.Progress = 75))])!);
            await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // The UI may finish/cancel this attempt while the progress update is queued.
            // Holding UpdateSync here would invert that wait and freeze both threads.
            var lockAvailable = Monitor.TryEnter(updateSync);
            if (lockAvailable)
                Monitor.Exit(updateSync);
            Assert.True(lockAvailable, "A queued UI update must not hold the attempt update lock.");

            if (cancelWhileQueued)
            {
                await manager.CancelAsync(task.Id);
                Assert.True((bool)finish.Invoke(manager, [attempt, null])!);
            }
            releaseUi.Set();

            Assert.Equal(!cancelWhileQueued, await updateTask.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(cancelWhileQueued ? 0 : 75, task.Progress);
            if (cancelWhileQueued)
                Assert.Equal(DownloadStatus.Cancelled, task.Status);
        }
        finally
        {
            ui.Dispatcher.Hooks.OperationPosted -= onPosted;
            releaseUi.Set();
            if (updateTask is not null)
                await updateTask.WaitAsync(TimeSpan.FromSeconds(3));
            finish.Invoke(manager, [attempt, null]);
        }
    }

    private sealed class UnusedDownloadService : IYtDlpDownloadService
    {
        public Task<VideoInfo?> GetVideoInfoAsync(string url, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("This test only exercises attempt updates.");

        public Task DownloadAsync(DownloadTask task, IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("This test only exercises attempt updates.");
    }

    private sealed class DispatcherThread : IDisposable
    {
        private readonly Thread _thread;
        internal Dispatcher Dispatcher { get; }

        internal DispatcherThread()
        {
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() =>
            {
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Dispatcher = ready.Task.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            Assert.True(_thread.Join(TimeSpan.FromSeconds(3)));
        }
    }
}
