using System.IO;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class HttpIdleReadTests
{
    [Fact]
    public void DefaultIdleTimeout_IsSixtySeconds()
        => Assert.Equal(TimeSpan.FromSeconds(60), HttpIdleRead.DefaultIdleTimeout);

    [Fact]
    public async Task ReadAsync_CompletesWhenStreamReturnsData()
    {
        await using var source = new MemoryStream([1, 2, 3]);
        var buffer = new byte[16];

        var read = await HttpIdleRead.ReadAsync(
            source,
            buffer,
            TimeSpan.FromMilliseconds(200),
            CancellationToken.None);

        Assert.Equal(3, read);
        Assert.Equal([1, 2, 3], buffer[..3]);
    }

    [Fact]
    public async Task ReadAsync_ThrowsTimeoutExceptionWhenStreamWritesOneByteThenHangs()
    {
        await using var source = new OneByteThenHangStream();
        var buffer = new byte[16];

        var first = await HttpIdleRead.ReadAsync(
            source,
            buffer,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        Assert.Equal(1, first);
        Assert.Equal(0x42, buffer[0]);

        var hungRead = HttpIdleRead.ReadAsync(
            source,
            buffer,
            TimeSpan.FromMilliseconds(200),
            CancellationToken.None).AsTask();
        var winner = await Task.WhenAny(hungRead, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(hungRead, winner);
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => hungRead);
        Assert.Contains("没有收到数据", timeout.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_PropagatesCallerCancellationInsteadOfTimeout()
    {
        await using var source = new OneByteThenHangStream();
        var buffer = new byte[16];
        _ = await HttpIdleRead.ReadAsync(
            source,
            buffer,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HttpIdleRead.ReadAsync(
                source,
                buffer,
                TimeSpan.FromSeconds(30),
                cts.Token).AsTask());
    }

    [Fact]
    public async Task ReadAsync_RejectsInvalidIdleTimeout()
    {
        await using var source = new MemoryStream([1]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            HttpIdleRead.ReadAsync(source, new byte[1], TimeSpan.Zero, CancellationToken.None).AsTask());
    }

    private sealed class OneByteThenHangStream : Stream
    {
        private bool _wroteOne;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (!_wroteOne && !buffer.IsEmpty)
            {
                _wroteOne = true;
                buffer.Span[0] = 0x42;
                return 1;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
