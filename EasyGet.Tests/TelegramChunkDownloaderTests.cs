using System.Collections.Concurrent;
using System.IO;
using EasyGet.Services;
using TL;
using Xunit;

namespace EasyGet.Tests;

public class TelegramChunkDownloaderTests
{
    private const int Chunk = TelegramChunkDownloader.ChunkSize;

    [Fact]
    public async Task OutOfOrderRequests_UseFourSlotsAndWriteCorrectOrderedContent()
    {
        using var output = new MemoryStream();
        var parts = Enumerable.Range(0, 6).Select(_ => Signal<byte[]>()).ToArray();
        var calls = new ConcurrentQueue<long>();
        var active = 0;
        var maximumActive = 0;
        var progress = new List<long>();
        var running = TelegramChunkDownloader.DownloadAsync(6L * Chunk, output, async (offset, limit, ct) =>
        {
            Assert.Equal(Chunk, limit);
            Assert.Equal(0, offset % Chunk);
            calls.Enqueue(offset);
            var current = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, current);
            try { return await parts[offset / Chunk].Task.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref active); }
        }, (done, _) => progress.Add(done), null, CancellationToken.None);

        await UntilAsync(() => calls.Count == 4);
        Assert.Equal(4, active);
        for (var index = 3; index > 0; index--)
            parts[index].SetResult(Block(index));
        await UntilAsync(() => active == 1);
        Assert.Equal(0, output.Length);
        Assert.Equal(4, calls.Count);
        parts[0].SetResult(Block(0));
        await UntilAsync(() => calls.Count == 6);
        parts[5].SetResult(Block(5));
        parts[4].SetResult(Block(4));
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(4, maximumActive);
        Assert.Equal(0, active);
        var result = output.ToArray();
        for (var index = 0; index < 6; index++)
            Assert.All(result.AsSpan(index * Chunk, Chunk).ToArray(), value => Assert.Equal((byte)index, value));
        Assert.Equal(progress.Order(), progress);
        Assert.Equal(6L * Chunk, progress[^1]);
    }

    [Fact]
    public async Task SlowWriter_KeepsPrefetchWindowAtFourIncludingCurrentWrite()
    {
        using var output = new BlockingFirstWriteStream();
        var calls = 0;
        var running = TelegramChunkDownloader.DownloadAsync(8L * Chunk, output,
            (offset, _, _) => { Interlocked.Increment(ref calls); return Task.FromResult(Block((int)(offset / Chunk))); },
            (_, _) => { }, null, CancellationToken.None);
        await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(4, calls);
        output.Release.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(8, calls);
        Assert.Equal(8L * Chunk, output.Length);
        Assert.Equal(0, output.SeekCalls);
    }

    [Fact]
    public async Task FinalPartialBlock_StillRequestsAlignedLimitAndWritesExactLength()
    {
        using var output = new MemoryStream();
        var calls = new List<(long Offset, int Limit)>();
        await TelegramChunkDownloader.DownloadAsync(Chunk + 123L, output, (offset, limit, _) =>
        {
            calls.Add((offset, limit));
            return Task.FromResult(new byte[offset == 0 ? Chunk : 123]);
        }, (_, _) => { }, null, CancellationToken.None);
        Assert.Equal(new[] { (0L, Chunk), ((long)Chunk, Chunk) }, calls);
        Assert.Equal(Chunk + 123L, output.Length);
    }

    [Theory]
    [InlineData(122)]
    [InlineData(124)]
    [InlineData(0)]
    public async Task IncorrectBlockLength_FailsWithoutWritingThatBlock(int received)
    {
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => TelegramChunkDownloader.DownloadAsync(123, output,
            (_, _, _) => Task.FromResult(new byte[received]), (_, _) => { }, null, CancellationToken.None));
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData("FLOOD_WAIT_X")]
    [InlineData("FLOOD_PREMIUM_WAIT_X")]
    public async Task FloodWait_RetriesOnlyFailedOffsetAfterFullCooldownAndLowersConcurrency(string message)
    {
        using var output = new MemoryStream();
        var clock = new ManualClock();
        var throttle = new TelegramDownloadThrottle(clock);
        var firstWave = Enumerable.Range(0, 4).Select(_ => Signal<byte[]>()).ToArray();
        var subsequent = new ConcurrentQueue<TaskCompletionSource<byte[]>>();
        var calls = new ConcurrentQueue<(long Offset, DateTimeOffset Time)>();
        var counts = new ConcurrentDictionary<long, int>();
        var reports = new List<long>();
        var running = TelegramChunkDownloader.DownloadAsync(6L * Chunk, output, async (offset, _, ct) =>
        {
            calls.Enqueue((offset, clock.UtcNow));
            var call = counts.AddOrUpdate(offset, 1, (_, previous) => previous + 1);
            if (offset < 4L * Chunk && call == 1)
                return await firstWave[offset / Chunk].Task.WaitAsync(ct);
            var pending = Signal<byte[]>();
            subsequent.Enqueue(pending);
            return await pending.Task.WaitAsync(ct);
        }, (done, _) => reports.Add(done), null, CancellationToken.None, throttle);

        await UntilAsync(() => calls.Count == 4);
        firstWave[1].SetException(new RpcException(420, message, 3));
        await UntilAsync(() => throttle.ConcurrencyLimit == 1);
        firstWave[2].SetResult(Block(2));
        firstWave[3].SetResult(Block(3));
        firstWave[0].SetResult(Block(0));
        await UntilAsync(() => clock.PendingDelays == 1);
        for (var second = 1; second <= 2; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await UntilAsync(() => clock.PendingDelays == 1);
            Assert.Equal(4, calls.Count);
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        // The failed block and the next prefetched block may enter the shared
        // gate in either order. Verify cooldown and one-at-a-time requests,
        // rather than imposing a thread-pool scheduling order.
        for (var expectedCalls = 5; expectedCalls <= 7; expectedCalls++)
        {
            await UntilAsync(() => subsequent.Count == 1);
            Assert.Equal(expectedCalls, calls.Count);
            var request = calls.Last();
            Assert.True(request.Time >= clock.Start + TimeSpan.FromSeconds(3));
            Assert.True(subsequent.TryDequeue(out var pending));
            pending.SetResult(Block((int)(request.Offset / Chunk)));
        }
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, counts[Chunk]);
        Assert.All(counts.Where(pair => pair.Key != Chunk), pair => Assert.Equal(1, pair.Value));
        Assert.Equal(reports.Order(), reports);
        Assert.Contains(reports.Zip(reports.Skip(1)), pair => pair.First == pair.Second);
        Assert.Equal(Enumerable.Range(0, 6).SelectMany(Block).ToArray(), output.ToArray());
    }

    [Fact]
    public async Task ExcessiveFloodWait_PersistsAcrossFilesAndNeverSendsEarlyRequest()
    {
        var clock = new ManualClock();
        var throttle = new TelegramDownloadThrottle(clock);
        var calls = 0;
        using var first = new MemoryStream();
        await Assert.ThrowsAsync<RpcException>(() => TelegramChunkDownloader.DownloadAsync(1, first,
            (_, _, _) => { calls++; throw new RpcException(420, "FLOOD_PREMIUM_WAIT_X", 601); },
            (_, _) => { }, null, CancellationToken.None, throttle));
        using var second = new MemoryStream();
        var error = await Assert.ThrowsAsync<RpcException>(() => TelegramChunkDownloader.DownloadAsync(1, second,
            (_, _, _) => { calls++; return Task.FromResult(new byte[1]); },
            (_, _) => { }, null, CancellationToken.None, throttle));
        Assert.Equal(601, error.X);
        Assert.Equal(1, calls);
        Assert.Equal(0, clock.PendingDelays);
    }

    [Fact]
    public async Task FourthFloodResponse_StopsRetryingButRetainsCooldownForNextFile()
    {
        var clock = new ManualClock();
        var throttle = new TelegramDownloadThrottle(clock);
        using var first = new MemoryStream();
        var calls = 0;
        var running = TelegramChunkDownloader.DownloadAsync(1, first,
            (_, _, _) => { calls++; throw new RpcException(420, "FLOOD_WAIT_X", 1); },
            (_, _) => { }, null, CancellationToken.None, throttle);
        for (var retry = 0; retry < 3; retry++)
        {
            await UntilAsync(() => clock.PendingDelays == 1);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        await Assert.ThrowsAsync<RpcException>(() => running.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(4, calls);

        using var second = new MemoryStream();
        var nextFileCalls = 0;
        var nextFile = TelegramChunkDownloader.DownloadAsync(1, second,
            (_, _, _) => { nextFileCalls++; return Task.FromResult(new byte[1]); },
            (_, _) => { }, null, CancellationToken.None, throttle);
        await UntilAsync(() => clock.PendingDelays == 1);
        Assert.Equal(0, nextFileCalls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await nextFile.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, nextFileCalls);
    }

    [Theory]
    [InlineData(420, "SLOWMODE_WAIT_X")]
    [InlineData(420, "FLOOD_TEST_PHONE_WAIT_X")]
    [InlineData(401, "AUTH_KEY_UNREGISTERED")]
    [InlineData(400, "CHANNEL_PRIVATE")]
    public async Task OtherRpcErrors_AreNotRetried(int code, string message)
    {
        using var output = new MemoryStream();
        var calls = 0;
        var expected = new RpcException(code, message, 1);
        var error = await Assert.ThrowsAsync<RpcException>(() => TelegramChunkDownloader.DownloadAsync(1, output,
            (_, _, _) => { calls++; throw expected; }, (_, _) => { }, null, CancellationToken.None));
        Assert.Same(expected, error);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LaterBlockFailure_CancelsAndDrainsEarlierAndOtherWorkers()
    {
        using var output = new MemoryStream();
        var allStarted = Signal();
        var failing = Signal<byte[]>();
        var started = 0;
        var finished = 0;
        var expected = new IOException("part failed");
        var running = TelegramChunkDownloader.DownloadAsync(8L * Chunk, output, async (offset, _, ct) =>
        {
            if (Interlocked.Increment(ref started) == 4)
                allStarted.SetResult();
            try
            {
                if (offset == 2L * Chunk)
                    return await failing.Task;
                await Task.Delay(Timeout.Infinite, ct);
                return Block(0);
            }
            finally { Interlocked.Increment(ref finished); }
        }, (_, _) => { }, null, CancellationToken.None);
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        failing.SetException(expected);
        var error = await Assert.ThrowsAsync<IOException>(() => running.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Same(expected, error);
        Assert.Equal(4, finished);
        Assert.Equal(4, started);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task CallerCancellation_CancelsAndDrainsAllWorkers()
    {
        using var output = new MemoryStream();
        using var caller = new CancellationTokenSource();
        var started = 0;
        var finished = 0;
        var running = TelegramChunkDownloader.DownloadAsync(8L * Chunk, output, async (_, _, ct) =>
        {
            Interlocked.Increment(ref started);
            try { await Task.Delay(Timeout.Infinite, ct); return Block(0); }
            finally { Interlocked.Increment(ref finished); }
        }, (_, _) => { }, null, caller.Token);
        await UntilAsync(() => started == 4);
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(4, finished);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task CancellationDuringCooldown_DoesNotClearSharedDeadline()
    {
        var clock = new ManualClock();
        var throttle = new TelegramDownloadThrottle(clock);
        using var caller = new CancellationTokenSource();
        using var first = new MemoryStream();
        var running = TelegramChunkDownloader.DownloadAsync(1, first,
            (_, _, _) => throw new RpcException(420, "FLOOD_WAIT_X", 2), (_, _) => { }, null, caller.Token, throttle);
        await UntilAsync(() => clock.PendingDelays == 1);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        using var second = new MemoryStream();
        var calls = 0;
        var next = TelegramChunkDownloader.DownloadAsync(1, second,
            (_, _, _) => { calls++; return Task.FromResult(new byte[1]); }, (_, _) => { }, null, CancellationToken.None, throttle);
        await UntilAsync(() => clock.PendingDelays == 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => clock.PendingDelays == 1);
        Assert.Equal(0, calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await next.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, calls);
    }

    private static byte[] Block(int value) => Enumerable.Repeat((byte)value, Chunk).ToArray();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(1, timeout.Token);
        }
    }

    private sealed class ManualClock : ITelegramDownloadClock
    {
        private readonly object _gate = new();
        private readonly List<(DateTimeOffset Due, TaskCompletionSource Signal)> _pending = [];
        private DateTimeOffset _now;
        public DateTimeOffset Start { get; } = DateTimeOffset.Parse("2026-09-10T00:00:00Z");
        public ManualClock() => _now = Start;
        public DateTimeOffset UtcNow { get { lock (_gate) return _now; } }
        public int PendingDelays { get { lock (_gate) return _pending.Count(item => !item.Signal.Task.IsCompleted); } }
        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            var signal = Signal();
            lock (_gate)
                _pending.Add((_now + delay, signal));
            ct.Register(() => signal.TrySetCanceled(ct));
            return signal.Task;
        }
        public void Advance(TimeSpan duration)
        {
            lock (_gate)
            {
                _now += duration;
                foreach (var item in _pending.Where(item => item.Due <= _now))
                    item.Signal.TrySetResult();
                _pending.RemoveAll(item => item.Signal.Task.IsCompleted);
            }
        }
    }

    private sealed class BlockingFirstWriteStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public int SeekCalls { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Length == 0)
            {
                Started.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
        public override long Seek(long offset, SeekOrigin loc)
        {
            SeekCalls++;
            return base.Seek(offset, loc);
        }
    }
}
