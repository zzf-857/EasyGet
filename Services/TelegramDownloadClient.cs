using System.IO;
using TL;

namespace EasyGet.Services;

// Keep Telegram RPCs behind a small boundary so private-dialog pagination and
// file failures can be exercised without a real account or session file.
internal interface ITelegramDownloadClient
{
    bool IsLoggedIn { get; }
    bool HasSavedSession { get; }
    Task RestoreSessionAsync(CancellationToken ct);
    Task<IPeerInfo> ResolveUsernameAsync(string username, CancellationToken ct);
    Task<Messages_DialogsBase> GetDialogsAsync(int folderId, DateTime offsetDate,
        int offsetId, InputPeer offsetPeer, bool excludePinned, CancellationToken ct);
    Task<MessageBase?> GetMessageAsync(InputPeer peer, int messageId, CancellationToken ct);
    Task DownloadMediaAsync(MessageMedia media, Stream output, WTelegram.Client.ProgressCallback progress,
        CancellationToken ct, Action<string>? log = null);
}

internal sealed class TelegramDownloadClient(WTelegram.Client client, TelegramDownloadThrottle? throttle = null) : ITelegramDownloadClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private readonly TelegramDownloadThrottle _throttle = throttle ?? new();
    private readonly object _abortGate = new();
    private Task? _abortTask;
    internal bool IsAborted { get; private set; }
    public bool IsLoggedIn => !IsAborted && client.User is not null;
    public bool HasSavedSession => client.UserId != 0;

    public async Task RestoreSessionAsync(CancellationToken ct)
    {
        if (!HasSavedSession)
            throw new InvalidOperationException("请先在设置中完成 Telegram 授权");
        // Never start an interactive login / send another verification code here.
        await client.LoginUserIfNeeded(reloginOnFailedResume: false).WaitAsync(RequestTimeout, ct);
    }

    public async Task<IPeerInfo> ResolveUsernameAsync(string username, CancellationToken ct)
        => (await client.Contacts_ResolveUsername(username).WaitAsync(RequestTimeout, ct)).UserOrChat;

    public Task<Messages_DialogsBase> GetDialogsAsync(int folderId, DateTime offsetDate,
        int offsetId, InputPeer offsetPeer, bool excludePinned, CancellationToken ct)
        => client.Messages_GetDialogs(offsetDate, offsetId, offsetPeer, limit: 100,
            folder_id: folderId, exclude_pinned: excludePinned).WaitAsync(RequestTimeout, ct);

    public async Task<MessageBase?> GetMessageAsync(InputPeer peer, int messageId, CancellationToken ct)
    {
        var ids = new InputMessageID { id = messageId };
        var result = peer is InputPeerChannel channel
            ? await client.Channels_GetMessages(new InputChannel(channel.channel_id, channel.access_hash), ids).WaitAsync(RequestTimeout, ct)
            : await client.Messages_GetMessages(ids).WaitAsync(RequestTimeout, ct);
        return result.Messages.FirstOrDefault(message => message.ID == messageId);
    }

    public async Task DownloadMediaAsync(MessageMedia media, Stream output,
        WTelegram.Client.ProgressCallback progress, CancellationToken ct, Action<string>? log = null)
    {
        ct.ThrowIfCancellationRequested();
        if (IsAborted)
            throw new InvalidOperationException("Telegram 连接已中止，请重新恢复会话后下载。");
        await TelegramTransferRunner.RunAsync(DownloadAsync, AbortAsync, output, progress, ct);

        Task DownloadAsync(Stream guardedOutput, WTelegram.Client.ProgressCallback guardedProgress) => media switch
        {
            MessageMediaDocument { document: Document { size: > 0, dc_id: > 0 } document }
                => DownloadDocumentAsync(document, guardedOutput, guardedProgress, log, ct),
            MessageMediaDocument { document: Document document }
                => client.DownloadFileAsync(document, guardedOutput, (PhotoSizeBase)null!, guardedProgress),
            MessageMediaPhoto { photo: Photo photo }
                => client.DownloadFileAsync(photo, guardedOutput, (PhotoSizeBase)null!, guardedProgress),
            _ => throw new NotSupportedException("这条 Telegram 消息没有可下载的文件或照片。")
        };
    }

    internal static bool UsesChunkDownloader(MessageMedia media)
        => media is MessageMediaDocument { document: Document { size: > 0, dc_id: > 0 } };

    private async Task DownloadDocumentAsync(Document document, Stream output,
        WTelegram.Client.ProgressCallback progress, Action<string>? log, CancellationToken ct)
    {
        var source = new TelegramDocumentPartSource(document, (dcId, token) =>
            client.GetClientForDC(-dcId, true).WaitAsync(RequestTimeout, token));
        await TelegramChunkDownloader.DownloadAsync(document.size, output, source.ReadAsync,
            progress, log, ct, _throttle);
    }

    internal Task AbortAsync()
    {
        lock (_abortGate)
        {
            IsAborted = true;
            // Cancellation and application shutdown can race. WTelegram.Dispose
            // is not idempotent, so both callers must await the same teardown.
            return _abortTask ??= client.DisposeAsync().AsTask();
        }
    }
}
