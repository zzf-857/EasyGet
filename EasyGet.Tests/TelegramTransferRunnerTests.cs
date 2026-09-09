using System.IO;
using EasyGet.Services;
using TL;
using Xunit;

namespace EasyGet.Tests;

public class TelegramTransferRunnerTests
{
    [Fact]
    public void DefaultIdleTimeout_IsTwoMinutes()
        => Assert.Equal(TimeSpan.FromSeconds(120), TelegramTransferRunner.DefaultIdleTimeout);

    [Fact]
    public async Task ClientAbort_ReusesOneTeardownTaskForCancellationAndShutdown()
    {
        using var session = new MemoryStream();
        var client = new WTelegram.Client(key => key switch
        {
            "api_id" => "1",
            "api_hash" => "0123456789abcdef0123456789abcdef",
            _ => null
        }, session);
        var adapter = new TelegramDownloadClient(client);

        var first = adapter.AbortAsync();
        var second = adapter.AbortAsync();
        Assert.Same(first, second);
        await Task.WhenAll(first, second);
        Assert.True(adapter.IsAborted);
        Assert.False(adapter.IsLoggedIn);
    }

    [Fact]
    public async Task SuccessfulTransfer_PreservesSeekingAndLeavesOutputAndClientOpen()
    {
        using var output = new MemoryStream();
        var aborts = 0;
        var reports = new List<long>();
        await TelegramTransferRunner.RunAsync(async (stream, progress) =>
        {
            Assert.True(stream.CanSeek);
            await stream.WriteAsync(new byte[] { 1, 2, 3 });
            stream.Seek(1, SeekOrigin.Begin);
            await stream.WriteAsync(new byte[] { 9 });
            await stream.FlushAsync();
            progress(3, 3);
        }, () => { aborts++; return Task.CompletedTask; }, output, (value, _) => reports.Add(value), CancellationToken.None);

        Assert.Equal(new byte[] { 1, 9, 3 }, output.ToArray());
        Assert.True(output.CanWrite);
        Assert.Equal(new long[] { 3 }, reports);
        Assert.Equal(0, aborts);
    }

    [Fact]
    public async Task CancelWithoutProgress_AbortsAndDrainsProducerBeforeReturning()
    {
        using var output = new MemoryStream();
        using var caller = new CancellationTokenSource();
        var started = Signal();
        var aborted = Signal();
        var producerStopped = false;
        var running = TelegramTransferRunner.RunAsync(async (_, _) =>
        {
            started.SetResult();
            await aborted.Task;
            await Task.Yield();
            producerStopped = true;
        }, () => { aborted.SetResult(); return Task.CompletedTask; }, output, (_, _) => { }, caller.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.True(producerStopped);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task IdleWithoutProgress_AbortsAndDrainsProducer()
    {
        using var output = new MemoryStream();
        var aborted = Signal();
        var producerStopped = false;
        var running = TelegramTransferRunner.RunAsync(async (_, _) =>
        {
            await aborted.Task;
            producerStopped = true;
        }, () => { aborted.SetResult(); return Task.CompletedTask; }, output, (_, _) => { }, CancellationToken.None,
            idleTimeout: TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAsync<TimeoutException>(() => running.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(producerStopped);
    }

    [Fact]
    public async Task Progress_ResetsIdleTimerForLongTransfers()
    {
        using var output = new MemoryStream();
        var aborts = 0;
        await TelegramTransferRunner.RunAsync(async (stream, progress) =>
        {
            for (var index = 1; index <= 6; index++)
            {
                await Task.Delay(80);
                await stream.WriteAsync(new byte[] { (byte)index });
                progress(index, 6);
            }
        }, () => { aborts++; return Task.CompletedTask; }, output, (_, _) => { }, CancellationToken.None,
            idleTimeout: TimeSpan.FromMilliseconds(300));

        Assert.Equal(6, output.Length);
        Assert.Equal(0, aborts);
    }

    [Theory]
    [InlineData(400, "FILE_REFERENCE_EXPIRED")]
    [InlineData(420, "FLOOD_WAIT_X")]
    public async Task TelegramRpcFailure_PreservesExceptionAndClientForRetry(int code, string message)
    {
        using var output = new MemoryStream();
        var aborts = 0;
        var expected = new RpcException(code, message);
        var error = await Assert.ThrowsAsync<RpcException>(() => TelegramTransferRunner.RunAsync(
            (_, _) => Task.FromException(expected),
            () => { aborts++; return Task.CompletedTask; }, output, (_, _) => { }, CancellationToken.None));

        Assert.Same(expected, error);
        Assert.Equal(0, aborts);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task CancelBeforeStart_DoesNotCreateProducerOrAbortClient()
    {
        using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TelegramTransferRunner.RunAsync(
            (_, _) => throw new Xunit.Sdk.XunitException("Transfer must not start"),
            () => throw new Xunit.Sdk.XunitException("Abort must not run"), output, (_, _) => { }, new CancellationToken(true)));
    }

    [Fact]
    public async Task LateProducerAfterDrainTimeout_CannotTouchClosedOutput()
    {
        var output = new TrackingStream();
        using var caller = new CancellationTokenSource();
        var started = Signal();
        var allowLateWrite = Signal();
        Task? producer = null;
        var running = TelegramTransferRunner.RunAsync((stream, _) => producer = ProduceAsync(stream),
            () => Task.CompletedTask, output, (_, _) => { }, caller.Token,
            drainTimeout: TimeSpan.FromMilliseconds(80));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(2)));
        output.Dispose();
        allowLateWrite.SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => producer!.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, output.WriteAttempts);

        async Task ProduceAsync(Stream stream)
        {
            started.SetResult();
            await allowLateWrite.Task;
            await stream.WriteAsync(new byte[] { 1 });
        }
    }

    [Fact]
    public async Task AbortFailure_IsObservedAndDoesNotReplaceCancellation()
    {
        using var output = new MemoryStream();
        using var caller = new CancellationTokenSource();
        var started = Signal();
        var release = Signal();
        var running = TelegramTransferRunner.RunAsync(async (_, _) =>
        {
            started.SetResult();
            await release.Task;
            throw new IOException("late transfer fault");
        }, () => { release.SetResult(); throw new IOException("abort fault"); }, output, (_, _) => { }, caller.Token);

        await started.Task;
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(caller.Token, error.CancellationToken);
    }

    [Fact]
    public async Task TransferFailure_SealsOutputBeforeCallerDeletesPartialFile()
    {
        using var output = new TrackingStream();
        Stream? captured = null;
        await Assert.ThrowsAsync<IOException>(() => TelegramTransferRunner.RunAsync((stream, _) =>
        {
            captured = stream;
            return Task.FromException(new IOException("chunk failure"));
        }, () => throw new Xunit.Sdk.XunitException("Ordinary failures must not abort the client"), output, (_, _) => { }, CancellationToken.None));

        Assert.Throws<ObjectDisposedException>(() => captured!.Seek(0, SeekOrigin.Begin));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => captured!.WriteAsync(new byte[] { 1 }, 0, 1));
        Assert.Equal(0, output.WriteAttempts);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task Cancel_WaitsForWriteAlreadyInFlightBeforeReturningOutput()
    {
        using var output = new BlockingWriteStream();
        using var caller = new CancellationTokenSource();
        var running = TelegramTransferRunner.RunAsync(async (stream, _) => await stream.WriteAsync(new byte[] { 1 }),
            () => Task.CompletedTask, output, (_, _) => { }, caller.Token);
        await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        caller.Cancel();
        Assert.False(running.IsCompleted);
        output.Release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(output.WriteFinished);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class TrackingStream : MemoryStream
    {
        public int WriteAttempts { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteAttempts++;
            return base.WriteAsync(buffer, cancellationToken);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WriteAttempts++;
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }

    private sealed class BlockingWriteStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public bool WriteFinished { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await Release.Task;
            await base.WriteAsync(buffer, cancellationToken);
            WriteFinished = true;
        }
    }
}
