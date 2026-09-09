using System.IO;
using TL;

namespace EasyGet.Services;

internal static class TelegramPeerResolver
{
    internal static async Task<Channel> ResolvePrivateChannelAsync(
        ITelegramDownloadClient client, long channelId, CancellationToken ct)
    {
        // Folder 0 is the main list; folder 1 contains archived conversations.
        foreach (var folderId in new[] { 0, 1 })
        {
            var offsetDate = default(DateTime);
            var offsetId = 0;
            InputPeer offsetPeer = null!; // Telegram serializes the initial cursor as inputPeerEmpty.
            var seenOffsets = new HashSet<(long PeerId, int MessageId, DateTime Date)>();
            var excludePinned = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await client.GetDialogsAsync(folderId, offsetDate, offsetId, offsetPeer, excludePinned, ct);
                ct.ThrowIfCancellationRequested();
                if (page is not Messages_Dialogs dialogs)
                    throw new IOException("Telegram 未返回有效会话列表，请重试。");

                // A basic group can have the same numeric ID as a channel. /c/
                // links always refer to a channel/supergroup, never that group.
                if (dialogs.chats.TryGetValue(channelId, out var chat))
                {
                    if (chat is Channel channel && !channel.flags.HasFlag(Channel.Flags.min))
                        return channel;
                    if (chat is ChannelForbidden)
                        throw new InvalidOperationException("当前 Telegram 账号无权访问该私有频道或群组，请确认绑定的是已加入该会话的账号。");
                }

                if (page is not Messages_DialogsSlice || dialogs.dialogs.Length == 0)
                    break;

                // A page may end with a deleted top message or a folder row.
                // Walk back to a usable cursor instead of stopping at that row.
                var advanced = false;
                foreach (var dialog in dialogs.dialogs.Reverse())
                {
                    if (dialog is not Dialog || dialog.Peer is null)
                        continue;
                    var message = dialogs.messages.LastOrDefault(message =>
                        message.ID == dialog.TopMessage && message.Peer?.GetType() == dialog.Peer.GetType()
                        && message.Peer.ID == dialog.Peer.ID && message.Date != default);
                    if (message is null)
                        continue;
                    var cursor = (dialog.Peer.ID, dialog.TopMessage, message.Date);
                    if (!seenOffsets.Add(cursor))
                        continue;
                    offsetPeer = dialogs.UserOrChat(dialog).ToInputPeer();
                    offsetDate = message.Date;
                    offsetId = dialog.TopMessage;
                    advanced = true;
                    break;
                }
                if (!advanced)
                    throw new IOException("Telegram 会话列表分页未能继续，无法确认频道是否存在，请重试。");
                excludePinned = true;
            }
        }

        throw new InvalidOperationException($"在当前账号的全部会话（含归档）中未找到私有频道或群组 {channelId}。请确认 EasyGet 绑定的账号已加入该会话，并使用具体消息的链接。");
    }
}
