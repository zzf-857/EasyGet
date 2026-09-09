using System.Windows.Threading;
using EasyGet.Services;
using EasyGet.ViewModels;
using Xunit;

namespace EasyGet.Tests;

public sealed class UiDispatcherTests
{
    [Fact]
    public Task Post_DoesNotBlockWorkerWhileUiThreadIsBusy()
        => RunOnDispatcherThreadAsync(dispatcher =>
        {
            var callbackThread = 0;
            var uiThread = Environment.CurrentManagedThreadId;
            var worker = Task.Run(() => UiDispatcher.Post(
                () => callbackThread = Environment.CurrentManagedThreadId,
                dispatcher));

            // The UI deliberately waits here without pumping messages. Synchronous
            // Dispatcher.Invoke would prevent the download worker from returning.
            Assert.True(worker.Wait(TimeSpan.FromSeconds(3)));
            Assert.Equal(0, callbackThread);
            Drain(dispatcher);
            Assert.Equal(uiThread, callbackThread);
        });

    [Fact]
    public Task Refresh_CoalescesWorkerBurstAndReadsLatestStateOnUiThread()
        => RunOnDispatcherThreadAsync(dispatcher =>
        {
            var latestProgress = 0;
            var displayedProgress = 0;
            var refreshCount = 0;
            var refresh = new CoalescedUiRefresh(() =>
            {
                displayedProgress = latestProgress;
                refreshCount++;
            }, dispatcher);
            var worker = Task.Run(() =>
            {
                for (var index = 1; index <= 1000; index++)
                {
                    latestProgress = index;
                    refresh.Request();
                }
            });

            Assert.True(worker.Wait(TimeSpan.FromSeconds(3)));
            Assert.Equal(0, refreshCount);
            Drain(dispatcher);
            Assert.Equal(1, refreshCount);
            Assert.Equal(1000, displayedProgress);
        });

    [Fact]
    public Task Refresh_DoesNotLoseChangesRequestedDuringRefresh()
        => RunOnDispatcherThreadAsync(dispatcher =>
        {
            var refreshCount = 0;
            CoalescedUiRefresh refresh = null!;
            refresh = new CoalescedUiRefresh(() =>
            {
                if (++refreshCount == 1)
                    refresh.Request();
            }, dispatcher);

            refresh.Request();
            Drain(dispatcher);

            Assert.Equal(2, refreshCount);
        });

    [Fact]
    public Task ClearLog_DiscardsPendingLinesAndOnlyShowsNewMessages()
        => RunOnDispatcherThreadAsync(dispatcher => WithDownloadViewModel(dispatcher, viewModel =>
        {
            viewModel.LogLines.Add("already displayed");
            viewModel.AppendLogLine("received before clear");

            viewModel.ClearLogCommand.Execute(null);
            Assert.Empty(viewModel.LogLines);
            viewModel.AppendLogLine("received after clear");
            Drain(dispatcher);

            Assert.Equal("received after clear", viewModel.LogText);
            Assert.Single(viewModel.LogLines);
        }));

    [Fact]
    public Task CopyLog_IncludesPendingMessagesWithoutDuplicatingNextRefresh()
        => RunOnDispatcherThreadAsync(dispatcher => WithDownloadViewModel(dispatcher, viewModel =>
        {
            viewModel.AppendLogLine("first");
            viewModel.AppendLogLine("second");
            Assert.Empty(viewModel.LogLines);

            var copied = viewModel.GetLogTextForCopy();
            Assert.Equal($"first{Environment.NewLine}second", copied);
            Drain(dispatcher);

            Assert.Equal(copied, viewModel.LogText);
            Assert.Equal(2, viewModel.LogLines.Count);
        }));

    [Fact]
    public Task PresentationFailure_DoesNotEscapeDispatcherOrDisableFutureRefreshes()
        => RunOnDispatcherThreadAsync(dispatcher =>
        {
            var unhandled = 0;
            dispatcher.UnhandledException += (_, args) =>
            {
                unhandled++;
                args.Handled = true;
            };
            var worker = Task.Run(() => UiDispatcher.Post(
                () => throw new InvalidOperationException("broken toast subscriber"), dispatcher));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(3)));
            var refreshCount = 0;
            var refresh = new CoalescedUiRefresh(() =>
            {
                if (++refreshCount == 1)
                    throw new InvalidOperationException("broken status subscriber");
            }, dispatcher);
            refresh.Request();
            Drain(dispatcher);

            refresh.Request();
            Drain(dispatcher);

            Assert.Equal(0, unhandled);
            Assert.Equal(2, refreshCount);
        });

    private static void WithDownloadViewModel(Dispatcher dispatcher, Action<DownloadViewModel> test)
    {
        using var root = new TestDirectory();
        var config = new ConfigService(root.Path("config"));
        using var history = new HistoryService(root.Path("history.db"));
        var service = new YtDlpService(config, new EnvironmentService());
        using var manager = new DownloadManager(service, history, config);
        var viewModel = new DownloadViewModel(
            manager, config, new YtDlpVideoInfoProvider(service), _ => { }, logDispatcher: dispatcher);
        test(viewModel);
    }

    private static void Drain(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        _ = dispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ContextIdle);
        Dispatcher.PushFrame(frame);
    }

    private static Task RunOnDispatcherThreadAsync(Action<Dispatcher> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                test(dispatcher);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                dispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
