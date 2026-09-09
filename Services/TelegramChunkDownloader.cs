using System.IO;
using System.Runtime.ExceptionServices;
using TL;

namespace EasyGet.Services;

internal static class TelegramChunkDownloader
{
    internal const int ChunkSize = 512 * 1024;

    internal static Task DownloadAsync(long fileSize, Stream output,
        Func<long, int, CancellationToken, Task<byte[]>> fetchPart,
        WTelegram.Client.ProgressCallback progress, Action<string>? log, CancellationToken ct)
        => DownloadAsync(fileSize, output, fetchPart, progress, log, ct, new TelegramDownloadThrottle());

    internal static async Task DownloadAsync(long fileSize, Stream output,
        Func<long, int, CancellationToken, Task<byte[]>> fetchPart,
        WTelegram.Client.ProgressCallback progress, Action<string>? log, CancellationToken ct,
        TelegramDownloadThrottle throttle)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(fetchPart);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(throttle);
        if (!output.CanWrite)
            throw new ArgumentException("Telegram 下载输出流不可写。", nameof(output));
        if (output.CanSeek && output.Position != 0)
            throw new ArgumentException("Telegram 分块下载必须从空文件起始位置开始。", nameof(output));
        ct.ThrowIfCancellationRequested();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new Queue<Task<byte[]>>();
        var firstFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressGate = new object();
        long written = 0;
        long nextOffset = 0;
        try
        {
            log?.Invoke($"[Telegram] 使用 512 KiB 分块、最多 4 个请求并按顺序写入文件（当前会话并发：{throttle.ConcurrencyLimit}）。");
            Report();
            while (pending.Count < TelegramDownloadThrottle.MaximumConcurrency && nextOffset < fileSize)
                Enqueue();

            while (pending.Count != 0)
            {
                var bytes = await pending.Peek().ConfigureAwait(false);
                stop.Token.ThrowIfCancellationRequested();
                // There is exactly one writer. Retain this slot until its write
                // completes so buffered + in-flight blocks never exceed four.
                await output.WriteAsync(bytes, stop.Token).ConfigureAwait(false);
                lock (progressGate)
                {
                    written += bytes.Length;
                    progress(written, fileSize);
                }
                bytes = null!; // release this buffer before admitting the next block
                _ = pending.Dequeue();
                if (nextOffset < fileSize)
                    Enqueue();
            }
            ct.ThrowIfCancellationRequested();
        }
        catch
        {
            ct.ThrowIfCancellationRequested();
            if (firstFailure.Task.IsCompletedSuccessfully)
                ExceptionDispatchInfo.Capture(firstFailure.Task.Result).Throw();
            throw;
        }
        finally
        {
            stop.Cancel();
            // Every still-owned worker is awaited, including a failed worker
            // behind an earlier block. Workers never access the output stream.
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch { /* all task failures are observed; preserve the original error */ }
        }

        void Report()
        {
            lock (progressGate)
                progress(written, fileSize);
        }

        void Enqueue()
        {
            stop.Token.ThrowIfCancellationRequested();
            var offset = nextOffset;
            nextOffset += Math.Min(ChunkSize, fileSize - nextOffset);
            pending.Enqueue(FetchAsync(offset));
        }

        async Task<byte[]> FetchAsync(long offset)
        {
            try
            {
                var floodCount = 0;
                while (true)
                {
                    await throttle.WaitAsync(Report, stop.Token).ConfigureAwait(false);
                    try
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        var bytes = await fetchPart(offset, ChunkSize, stop.Token).ConfigureAwait(false);
                        var expected = (int)Math.Min(ChunkSize, fileSize - offset);
                        if (bytes is null || bytes.Length != expected)
                            throw new IOException($"Telegram 分块不完整：偏移 {offset}，预期 {expected} 字节，实际 {bytes?.Length ?? 0} 字节。");
                        return bytes;
                    }
                    catch (RpcException error) when (TelegramDownloadThrottle.IsFloodWait(error))
                    {
                        // Record even excessive waits/retry exhaustion so a
                        // subsequent file/client cannot bypass this cooldown.
                        throttle.RecordFlood(error);
                        if (++floodCount > 3 || error.X > TelegramDownloadThrottle.MaximumWaitSeconds)
                            throw;
                        log?.Invoke($"[Telegram] 服务器要求等待 {error.X} 秒；已降为单请求，将重试当前分块并保留已下载内容。");
                    }
                    finally
                    {
                        throttle.Release();
                    }
                }
            }
            catch (Exception error)
            {
                if (error is not OperationCanceledException || !stop.IsCancellationRequested)
                    firstFailure.TrySetResult(error);
                stop.Cancel();
                throw;
            }
        }
    }
}
