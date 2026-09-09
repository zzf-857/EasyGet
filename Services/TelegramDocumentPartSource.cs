using TL;

namespace EasyGet.Services;

internal sealed class TelegramDocumentPartSource
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private readonly InputDocumentFileLocation _location;
    private readonly Func<int, CancellationToken, Task<WTelegram.Client>> _getMediaClient;
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private WTelegram.Client? _mediaClient;
    private int _dcId;

    internal TelegramDocumentPartSource(Document document,
        Func<int, CancellationToken, Task<WTelegram.Client>> getMediaClient)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(getMediaClient);
        if (document.size <= 0 || document.dc_id <= 0)
            throw new ArgumentException("Telegram 文件大小或数据中心标识无效。", nameof(document));

        _location = document.ToFileLocation((PhotoSizeBase)null!);
        _dcId = document.dc_id;
        _getMediaClient = getMediaClient;
    }

    internal async Task<byte[]> ReadAsync(long offset, int limit, CancellationToken ct)
    {
        for (var migrations = 0; ; migrations++)
        {
            ct.ThrowIfCancellationRequested();
            var client = await GetMediaClientAsync(ct);
            try
            {
                var result = await client.Upload_GetFile(_location, offset, limit, cdn_supported: false)
                    .WaitAsync(RequestTimeout, ct);
                ct.ThrowIfCancellationRequested();
                return result is Upload_File file
                    ? file.bytes
                    : throw new NotSupportedException($"Telegram 返回了不支持的文件分块类型：{result?.GetType().Name}。");
            }
            catch (RpcException ex) when (migrations < 2 && ex.Code == 303
                && ex.Message == "FILE_MIGRATE_X" && ex.X > 0)
            {
                await _clientGate.WaitAsync(ct);
                try
                {
                    // Ignore a stale migration response if another worker has
                    // already switched this document to the new media DC.
                    if (ReferenceEquals(_mediaClient, client) && _dcId != ex.X)
                    {
                        _dcId = ex.X;
                        _mediaClient = null;
                    }
                }
                finally
                {
                    _clientGate.Release();
                }
            }
        }
    }

    private async Task<WTelegram.Client> GetMediaClientAsync(CancellationToken ct)
    {
        await _clientGate.WaitAsync(ct);
        try
        {
            if (_mediaClient is null)
            {
                var client = await _getMediaClient(_dcId, ct).WaitAsync(RequestTimeout, ct);
                ct.ThrowIfCancellationRequested();
                // Media clients do not inherit this setting from the main client.
                // Surface every server wait (including zero) to the chunk runner.
                client.FloodRetryThreshold = -1;
                _mediaClient = client;
            }
            return _mediaClient;
        }
        finally
        {
            _clientGate.Release();
        }
    }
}
