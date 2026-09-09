using System.Diagnostics;
using System.IO;

namespace EasyGet.Services;

internal static class TelegramTransferRunner
{
    internal static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(15);

    internal static async Task RunAsync(
        Func<Stream, WTelegram.Client.ProgressCallback, Task> transfer,
        Func<Task> abort,
        Stream output,
        WTelegram.Client.ProgressCallback progress,
        CancellationToken ct,
        TimeSpan? idleTimeout = null,
        TimeSpan? drainTimeout = null)
    {
        ct.ThrowIfCancellationRequested();
        var idleLimit = idleTimeout ?? DefaultIdleTimeout;
        var drainLimit = drainTimeout ?? DefaultDrainTimeout;
        if (idleLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        if (drainLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(drainTimeout));

        using var idle = new CancellationTokenSource(idleLimit);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
        var stream = new GuardedTransferStream(output);
        var progressGate = new object();
        var running = true;
        Task? downloading = null;
        try
        {
            downloading = transfer(stream, (downloaded, total) =>
            {
                lock (progressGate)
                {
                    if (!running)
                        throw new OperationCanceledException("Telegram 文件传输已停止。");
                    stop.Token.ThrowIfCancellationRequested();
                    idle.CancelAfter(idleLimit);
                    progress(downloaded, total);
                }
            });
            await downloading.WaitAsync(stop.Token).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            lock (progressGate)
                running = false;

            // Close access to the caller-owned file before disconnecting. Even if
            // a library chunk outlives its parent Task, it cannot write later.
            await stream.StopAsync().ConfigureAwait(false);

            // WTelegram 4.4.6 DisposeAsync aborts pending RPCs on every DC;
            // ResetAsync alone does not. Do not replace it with WaitAsync(ct).
            var aborting = Task.Run(abort);
            var drained = Task.WhenAll(ObserveAsync(aborting), ObserveAsync(downloading ?? Task.CompletedTask));
            try
            {
                await drained.WaitAsync(drainLimit).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Both tasks already have observers. The sealed stream remains
                // safe if a third-party transfer is late completing its teardown.
                Debug.WriteLine("[Telegram] 中止传输后等待后台任务结束超时；输出文件已隔离。");
            }

            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"Telegram 下载超过 {idleLimit.TotalSeconds:0} 秒未收到数据，请检查代理或网络后重试。");
        }
        finally
        {
            lock (progressGate)
                running = false;
            await stream.StopAsync().ConfigureAwait(false);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) { Debug.WriteLine($"[Telegram] 已观察中止传输任务: {ex.GetType().Name}"); }
    }

    /// <summary>
    /// WTelegram can have outstanding parallel chunks when its transfer Task
    /// fails. This wrapper serializes file access with StopAsync, preserves
    /// seeking/parallel transfer support, and never owns/disposes the real file.
    /// </summary>
    private sealed class GuardedTransferStream(Stream output) : Stream
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _stopped;

        public async Task StopAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { _stopped = true; }
            finally { _gate.Release(); }
        }

        public override bool CanRead => false;
        public override bool CanSeek => output.CanSeek;
        public override bool CanWrite => output.CanWrite;
        public override long Length => Access(() => output.Length);
        public override long Position
        {
            get => Access(() => output.Position);
            set => Access(() => { output.Position = value; return 0; });
        }

        public override void Flush() => Access(() => { output.Flush(); return 0; });
        public override Task FlushAsync(CancellationToken cancellationToken)
            => AccessAsync(() => output.FlushAsync(cancellationToken), cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => Access(() => output.Seek(offset, origin));
        public override void SetLength(long value) => Access(() => { output.SetLength(value); return 0; });
        public override void Write(byte[] buffer, int offset, int count)
            => Access(() => { output.Write(buffer, offset, count); return 0; });
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => AccessAsync(() => output.WriteAsync(buffer, offset, count, cancellationToken), cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => new(AccessAsync(() => output.WriteAsync(buffer, cancellationToken).AsTask(), cancellationToken));
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private T Access<T>(Func<T> operation)
        {
            _gate.Wait();
            try
            {
                ObjectDisposedException.ThrowIf(_stopped, this);
                return operation();
            }
            finally { _gate.Release(); }
        }

        private async Task AccessAsync(Func<Task> operation, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_stopped, this);
                await operation().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
    }
}
