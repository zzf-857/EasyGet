using System.IO;

namespace EasyGet.Services;

internal static class HttpIdleRead
{
    internal static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    internal static ValueTask<int> ReadAsync(
        Stream source,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
        => ReadAsync(source, buffer, DefaultIdleTimeout, cancellationToken);

    internal static async ValueTask<int> ReadAsync(
        Stream source,
        Memory<byte> buffer,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (idleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(idleTimeout);
        try
        {
            return await source.ReadAsync(buffer, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"下载超过 {FormatIdleTimeout(idleTimeout)} 没有收到数据。");
        }
    }

    private static string FormatIdleTimeout(TimeSpan idleTimeout)
        => idleTimeout.TotalSeconds >= 1
            ? $"{idleTimeout.TotalSeconds:0} 秒"
            : $"{idleTimeout.TotalMilliseconds:0} 毫秒";
}
