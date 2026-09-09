using System.IO;
using EasyGet.Models;
using EasyGet.Services;
using TL;
using Xunit;
using Message = TL.Message;

namespace EasyGet.Tests;

public class TelegramDownloadPipelineTests
{
    private const long TargetChannelId = 1234567890;
    private static readonly byte[] MediaBytes = [1, 2, 3, 4];

    [Fact]
    public async Task PrivateChannelBeyondFirstPage_IsResolvedWithItsAccessHash()
    {
        var client = new FakeTelegramClient();
        var olderChannel = Channel(99, "Other");
        var target = Channel(TargetChannelId, "Private", 7654321);
        var date = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        client.QueueDialogs(0, Slice(olderChannel, 321, date), Complete(target));

        var resolved = await TelegramPeerResolver.ResolvePrivateChannelAsync(client, TargetChannelId, default);

        Assert.Same(target, resolved);
        Assert.Equal(7654321, resolved.access_hash);
        Assert.Equal(2, client.DialogRequests.Count);
        var nextPage = client.DialogRequests[1];
        Assert.Equal(0, nextPage.FolderId);
        Assert.Equal(321, nextPage.OffsetId);
        Assert.Equal(date, nextPage.OffsetDate);
        Assert.Equal(olderChannel.id, Assert.IsType<InputPeerChannel>(nextPage.OffsetPeer).channel_id);
    }

    [Fact]
    public async Task PrivateChannelMinRecord_DoesNotHideTheCompleteRecordOnALaterPage()
    {
        var client = new FakeTelegramClient();
        var firstPage = Slice(Channel(99, "Other"), 321, DateTime.UtcNow);
        var partial = Channel(TargetChannelId, "Partial", 1);
        partial.flags |= TL.Channel.Flags.min;
        firstPage.chats[TargetChannelId] = partial;
        var complete = Channel(TargetChannelId, "Private", 7654321);
        client.QueueDialogs(0, firstPage, Complete(complete));

        var resolved = await TelegramPeerResolver.ResolvePrivateChannelAsync(client, TargetChannelId, default);

        Assert.Same(complete, resolved);
        Assert.Equal(2, client.DialogRequests.Count);
    }

    [Fact]
    public async Task PrivateChannelWithSameIdAsBasicGroup_ResolvesTheChannel()
    {
        var client = new FakeTelegramClient();
        var firstPage = Slice(Channel(99, "Other"), 321, DateTime.UtcNow);
        firstPage.chats[TargetChannelId] = new Chat { id = TargetChannelId, title = "Different basic group" };
        var target = Channel(TargetChannelId, "Private");
        client.QueueDialogs(0, firstPage, Complete(target));

        var resolved = await TelegramPeerResolver.ResolvePrivateChannelAsync(client, TargetChannelId, default);

        Assert.Same(target, resolved);
        Assert.Equal(2, client.DialogRequests.Count);
    }

    [Fact]
    public async Task ArchivedPrivateChannel_IsFoundAfterTheMainFolder()
    {
        var client = new FakeTelegramClient();
        var target = Channel(TargetChannelId, "Archived");
        client.QueueDialogs(0, Complete(Channel(99, "Other")));
        client.QueueDialogs(1, Complete(target));

        var resolved = await TelegramPeerResolver.ResolvePrivateChannelAsync(client, TargetChannelId, default);

        Assert.Same(target, resolved);
        Assert.Equal(new[] { 0, 1 }, client.DialogRequests.Select(request => request.FolderId));
        Assert.Equal(0, client.DialogRequests[1].OffsetId);
    }

    [Fact]
    public async Task MissingPrivateChannel_IsReportedAfterSearchingBothFolders()
    {
        var client = new FakeTelegramClient();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TelegramPeerResolver.ResolvePrivateChannelAsync(client, TargetChannelId, default));

        Assert.Contains("未找到", error.Message, StringComparison.Ordinal);
        Assert.Contains(TargetChannelId.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { 0, 1 }, client.DialogRequests.Select(request => request.FolderId));
    }

    [Fact]
    public async Task ForbiddenPrivateChannel_ReportsMembershipInsteadOfPretendingItIsMissing()
    {
        var client = new FakeTelegramClient();
        client.QueueDialogs(0, Complete(new ChannelForbidden
        {
            id = TargetChannelId,
            access_hash = 7654321,
            title = "Private"
        }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TelegramPeerResolver.ResolvePrivateChannelAsync(client, TargetChannelId, default));

        Assert.Contains("无权访问", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.MessageRequests);
    }

    [Fact]
    public async Task DownloadWithSavedSession_RestoresLoginAndDownloadsWithoutOpeningSettings()
    {
        using var directory = new TestDirectory();
        var client = ClientForMessages(DocumentMessage(456));
        client.IsLoggedIn = false;
        client.HasSavedSession = true;
        using var service = new TelegramDownloadService(new TestConfigService(), client);
        var task = Download(directory, "456");

        await service.DownloadAsync(task);

        Assert.Equal(1, client.RestoreCalls);
        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(directory.Path("Private_456", "video.mp4")));
        Assert.Equal(MediaBytes.Length, task.DownloadedSize);
        var request = Assert.Single(client.MessageRequests);
        Assert.Equal(TargetChannelId, Assert.IsType<InputPeerChannel>(request.Peer).channel_id);
        Assert.Empty(Directory.EnumerateFiles(directory.DirectoryPath, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DownloadWithoutSavedSession_DoesNotAttemptInteractiveLoginOrReadMessages()
    {
        using var directory = new TestDirectory();
        var client = ClientForMessages(DocumentMessage(456));
        client.IsLoggedIn = false;
        client.HasSavedSession = false;
        using var service = new TelegramDownloadService(new TestConfigService(), client);
        var task = Download(directory, "456");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(task));

        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.Equal(0, client.RestoreCalls);
        Assert.Empty(client.DialogRequests);
        Assert.Empty(client.MessageRequests);
    }

    [Fact]
    public async Task LoginStatusWithoutSavedSession_RequestsPhoneNumberWithoutStartingLogin()
    {
        var client = new FakeTelegramClient { IsLoggedIn = false, HasSavedSession = false };
        using var service = new TelegramDownloadService(new TestConfigService(), client);

        var firstStatus = await service.CheckLoginStatusAsync();
        var secondStatus = await service.CheckLoginStatusAsync();

        Assert.Equal("phone_number", firstStatus);
        Assert.Equal("phone_number", secondStatus);
        Assert.Equal(0, client.RestoreCalls);
        Assert.Empty(client.DialogRequests);
        Assert.Empty(client.MessageRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptedOrTruncatedMedia_PreservesExistingFileAndRemovesPartialOutput(bool interrupt)
    {
        using var directory = new TestDirectory();
        var finalPath = directory.Path("Private_456", "video.mp4");
        await directory.WriteAsync("Private_456/video.mp4", "previous complete download");
        var existing = await File.ReadAllBytesAsync(finalPath);
        var client = ClientForMessages(DocumentMessage(456));
        client.DownloadHandler = async (_, output, _, ct) =>
        {
            await output.WriteAsync(new byte[] { 1, 2 }, ct);
            if (interrupt)
                throw new IOException("Connection interrupted");
        };
        using var service = new TelegramDownloadService(new TestConfigService(), client);
        var task = Download(directory, "456");

        var error = await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(task));

        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.Equal(existing, await File.ReadAllBytesAsync(finalPath));
        Assert.Empty(Directory.EnumerateFiles(directory.DirectoryPath, "*.part", SearchOption.AllDirectories));
        Assert.DoesNotContain(finalPath, task.OutputFilePaths);
        Assert.Contains(interrupt ? "Connection interrupted" : "文件不完整", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledMedia_PreservesExistingFileAndRemovesPartialOutput()
    {
        using var directory = new TestDirectory();
        using var cancellation = new CancellationTokenSource();
        var finalPath = directory.Path("Private_456", "video.mp4");
        await directory.WriteAsync("Private_456/video.mp4", "previous complete download");
        var existing = await File.ReadAllBytesAsync(finalPath);
        var client = ClientForMessages(DocumentMessage(456));
        client.DownloadHandler = async (_, output, progress, ct) =>
        {
            await output.WriteAsync(new byte[] { 1, 2 }, ct);
            cancellation.Cancel();
            progress(2, MediaBytes.Length);
        };
        using var service = new TelegramDownloadService(new TestConfigService(), client);
        var task = Download(directory, "456");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(task, ct: cancellation.Token));

        Assert.Equal(DownloadStatus.Cancelled, task.Status);
        Assert.Equal(existing, await File.ReadAllBytesAsync(finalPath));
        Assert.Empty(Directory.EnumerateFiles(directory.DirectoryPath, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellingADownloadWaitingForTheClient_DoesNotInterruptTheActiveDownload()
    {
        using var directory = new TestDirectory();
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = ClientForMessages(DocumentMessage(456), DocumentMessage(457));
        client.DownloadHandler = async (_, output, progress, ct) =>
        {
            downloadStarted.TrySetResult();
            await releaseDownload.Task.WaitAsync(ct);
            await output.WriteAsync(MediaBytes, ct);
            progress(MediaBytes.Length, MediaBytes.Length);
        };
        using var service = new TelegramDownloadService(new TestConfigService(), client);
        var activeTask = Download(directory, "456");
        var waitingTask = Download(directory, "457");
        var activeDownload = service.DownloadAsync(activeTask, ct: deadline.Token);
        try
        {
            await downloadStarted.Task.WaitAsync(deadline.Token);
            var waitingDownload = service.DownloadAsync(waitingTask, ct: cancellation.Token);
            Assert.False(waitingDownload.IsCompleted);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingDownload.WaitAsync(deadline.Token));

            Assert.Equal(DownloadStatus.Cancelled, waitingTask.Status);
            Assert.Equal(DownloadStatus.Downloading, activeTask.Status);
            Assert.False(activeDownload.IsCompleted);
            Assert.Equal(1, client.MediaCalls);
            Assert.Equal(456, Assert.Single(client.MessageRequests).MessageId);
        }
        finally
        {
            releaseDownload.TrySetResult();
            await activeDownload.WaitAsync(deadline.Token);
        }

        Assert.Equal(DownloadStatus.Completed, activeTask.Status);
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(Assert.Single(activeTask.OutputFilePaths)));
        Assert.False(Directory.Exists(directory.Path("Private_457")));
        Assert.Empty(Directory.EnumerateFiles(directory.DirectoryPath, "*.part", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposingTheService_CancelsActiveAndQueuedDownloadsWithoutDisposedGateErrors(bool queueAnotherDownload)
    {
        using var directory = new TestDirectory();
        using var emergencyCancellation = new CancellationTokenSource();
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = ClientForMessages(DocumentMessage(456), DocumentMessage(457));
        client.DownloadHandler = async (_, output, _, ct) =>
        {
            await output.WriteAsync(new byte[] { 1, 2 }, ct);
            downloadStarted.TrySetResult();
            await blockedDownload.Task.WaitAsync(ct);
        };
        using var service = new TelegramDownloadService(new TestConfigService(), client);
        var activeTask = Download(directory, "456");
        var queuedTask = Download(directory, "457");
        var activeDownload = service.DownloadAsync(activeTask, ct: emergencyCancellation.Token);
        try
        {
            await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queuedDownload = queueAnotherDownload
                ? service.DownloadAsync(queuedTask, ct: emergencyCancellation.Token)
                : null;
            if (queuedDownload is not null)
                Assert.False(queuedDownload.IsCompleted);

            service.Dispose();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activeDownload.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(DownloadStatus.Cancelled, activeTask.Status);
            if (queuedDownload is not null)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedDownload.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(DownloadStatus.Cancelled, queuedTask.Status);
                Assert.False(Directory.Exists(directory.Path("Private_457")));
            }
            Assert.False(emergencyCancellation.IsCancellationRequested);
            Assert.Equal(1, client.MediaCalls);
            Assert.Equal(456, Assert.Single(client.MessageRequests).MessageId);
            Assert.Empty(activeTask.OutputFilePaths);
            Assert.Empty(Directory.EnumerateFiles(directory.DirectoryPath, "*.part", SearchOption.AllDirectories));
        }
        finally
        {
            emergencyCancellation.Cancel();
        }
    }

    [Fact]
    public async Task RangeWithOneFailedDownload_IsFailedAndKeepsSuccessfulFiles()
    {
        using var directory = new TestDirectory();
        var client = ClientForMessages(DocumentMessage(41), DocumentMessage(42), DocumentMessage(43));
        client.DownloadHandler = async (media, output, progress, ct) =>
        {
            var document = Assert.IsType<Document>(Assert.IsType<MessageMediaDocument>(media).document);
            if (document.id == 42)
                throw new IOException("Download failed for message 42");
            await output.WriteAsync(MediaBytes, ct);
            progress(MediaBytes.Length, MediaBytes.Length);
        };
        using var service = new TelegramDownloadService(new TestConfigService(), client);
        var task = Download(directory, "41-43");

        var error = await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(task));

        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.Contains("42", error.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { 41, 42, 43 }, client.MessageRequests.Select(request => request.MessageId));
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(directory.Path("Private_41-43", "41_video.mp4")));
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(directory.Path("Private_41-43", "43_video.mp4")));
        Assert.False(File.Exists(directory.Path("Private_41-43", "42_video.mp4")));
        Assert.Equal(2, task.OutputFilePaths.Count);
        Assert.True(task.Progress < 100);
        Assert.Empty(Directory.EnumerateFiles(directory.DirectoryPath, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RangeEndingAtIntMaxValue_DownloadsExactlyTheRequestedMessages()
    {
        using var directory = new TestDirectory();
        var client = ClientForMessages(TextMessage(int.MaxValue - 1), TextMessage(int.MaxValue));
        using var service = new TelegramDownloadService(new TestConfigService(), client);
        var task = Download(directory, $"{int.MaxValue - 1}-{int.MaxValue}");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await service.DownloadAsync(task, ct: cancellation.Token);

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(new[] { int.MaxValue - 1, int.MaxValue }, client.MessageRequests.Select(request => request.MessageId));
        Assert.Equal(2, task.OutputFilePaths.Count);
        Assert.All(task.OutputFilePaths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task VideoWithoutRemoteFileName_GetsUsableMp4Extension()
    {
        using var directory = new TestDirectory();
        var client = ClientForMessages(DocumentMessage(456, fileName: null));
        using var service = new TelegramDownloadService(new TestConfigService(), client);
        var task = Download(directory, "456");

        await service.DownloadAsync(task);

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal("mp4", task.Format);
        Assert.Equal("media_456.mp4", Path.GetFileName(Assert.Single(task.OutputFilePaths)));
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(task.OutputFilePaths[0]));
    }

    [Fact]
    public async Task MediaWithLongValidFileName_DownloadsWithoutExceedingFileNameLimits()
    {
        using var directory = new TestDirectory();
        var fileName = new string('a', 226) + ".mp4";
        var client = ClientForMessages(DocumentMessage(456, fileName));
        using var service = new TelegramDownloadService(new TestConfigService(), client);
        var task = Download(directory, "456");

        await service.DownloadAsync(task);

        Assert.Equal(DownloadStatus.Completed, task.Status);
        var output = Assert.Single(task.OutputFilePaths);
        Assert.Equal(fileName, Path.GetFileName(output));
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(output));
        Assert.Empty(Directory.EnumerateFiles(directory.DirectoryPath, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MediaNamedLikeCaptionFile_PreservesBothTheCaptionAndDownloadedBytes()
    {
        using var directory = new TestDirectory();
        var message = DocumentMessage(456, "message_text.txt");
        message.message = "A caption that must survive the media download.";
        var client = ClientForMessages(message);
        using var service = new TelegramDownloadService(new TestConfigService(), client);
        var task = Download(directory, "456");

        await service.DownloadAsync(task);

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(2, task.OutputFilePaths.Count);
        Assert.Equal(2, task.OutputFilePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var captionPath = directory.Path("Private_456", "message_text.txt");
        Assert.Equal(message.message, await File.ReadAllTextAsync(captionPath));
        var mediaPath = Assert.Single(task.OutputFilePaths, path => !path.Equals(captionPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(mediaPath));
        Assert.Equal(2, Directory.EnumerateFiles(directory.DirectoryPath, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task UnsupportedMediaWithText_SavesTextWithoutCreatingAnEmptyMediaFile()
    {
        using var directory = new TestDirectory();
        var message = TextMessage(456);
        message.media = new MessageMediaUnsupported();
        var client = ClientForMessages(message);
        using var service = new TelegramDownloadService(new TestConfigService(), client);
        var task = Download(directory, "456");

        await service.DownloadAsync(task);

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(0, client.MediaCalls);
        var textPath = Assert.Single(Directory.EnumerateFiles(directory.DirectoryPath, "*", SearchOption.AllDirectories));
        Assert.Equal("message_text.txt", Path.GetFileName(textPath));
        Assert.Equal(message.message, await File.ReadAllTextAsync(textPath));
        Assert.Equal(textPath, Assert.Single(task.OutputFilePaths));
    }

    [Fact]
    public async Task ExpiredMediaReference_IsRefetchedAndRetriedWithoutLeavingPartialFiles()
    {
        using var directory = new TestDirectory();
        var original = DocumentMessage(456);
        var refreshed = DocumentMessage(456);
        var refreshedDocument = Assert.IsType<Document>(Assert.IsType<MessageMediaDocument>(refreshed.media).document);
        refreshedDocument.file_reference = [9, 8, 7];
        var client = ClientForMessages(original);
        client.DownloadHandler = async (media, output, progress, ct) =>
        {
            if (client.MediaCalls == 1)
            {
                await output.WriteAsync(new byte[] { 1 }, ct);
                client.Messages[456] = refreshed;
                throw new RpcException(400, "FILE_REFERENCE_EXPIRED", 0);
            }
            Assert.Same(refreshed.media, media);
            await output.WriteAsync(MediaBytes, ct);
            progress(MediaBytes.Length, MediaBytes.Length);
        };
        using var service = new TelegramDownloadService(new TestConfigService(), client);
        var task = Download(directory, "456");

        await service.DownloadAsync(task);

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(2, client.MediaCalls);
        Assert.Equal(2, client.MessageRequests.Count);
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(Assert.Single(task.OutputFilePaths)));
        Assert.Empty(Directory.EnumerateFiles(directory.DirectoryPath, "*.part", SearchOption.AllDirectories));
    }

    private static Channel Channel(long id, string title, long accessHash = 654321) => new()
    {
        id = id,
        title = title,
        access_hash = accessHash,
        flags = TL.Channel.Flags.has_access_hash
    };

    private static Messages_Dialogs Complete(params ChatBase[] chats) => new()
    {
        chats = chats.ToDictionary(chat => chat.ID),
        users = [],
        dialogs = [],
        messages = []
    };

    private static Messages_DialogsSlice Slice(Channel channel, int messageId, DateTime date) => new()
    {
        count = 200,
        chats = new Dictionary<long, ChatBase> { [channel.id] = channel },
        users = [],
        dialogs = [new Dialog { peer = new PeerChannel { channel_id = channel.id }, top_message = messageId }],
        messages = [new Message { id = messageId, peer_id = new PeerChannel { channel_id = channel.id }, date = date }]
    };

    private static Message DocumentMessage(int id, string? fileName = "video.mp4") => new()
    {
        id = id,
        peer_id = new PeerChannel { channel_id = TargetChannelId },
        media = new MessageMediaDocument
        {
            document = new Document
            {
                id = id,
                mime_type = "video/mp4",
                size = MediaBytes.Length,
                file_reference = [1],
                attributes = fileName is null ? [] : [new DocumentAttributeFilename { file_name = fileName }]
            }
        }
    };

    private static Message TextMessage(int id) => new()
    {
        id = id,
        peer_id = new PeerChannel { channel_id = TargetChannelId },
        message = $"Saved message {id}"
    };

    private static FakeTelegramClient ClientForMessages(params Message[] messages)
    {
        var client = new FakeTelegramClient();
        client.QueueDialogs(0, Complete(Channel(TargetChannelId, "Private")));
        foreach (var message in messages)
            client.Messages[message.id] = message;
        return client;
    }

    private static DownloadTask Download(TestDirectory directory, string messageId) => new()
    {
        Url = $"https://t.me/c/{TargetChannelId}/{messageId}",
        OutputDirectory = directory.DirectoryPath
    };

    private sealed record DialogRequest(int FolderId, DateTime OffsetDate, int OffsetId, InputPeer OffsetPeer, bool ExcludePinned);
    private sealed record MessageRequest(InputPeer Peer, int MessageId);

    private sealed class FakeTelegramClient : ITelegramDownloadClient
    {
        private readonly Dictionary<int, Queue<Messages_DialogsBase>> _pages = [];
        public bool IsLoggedIn { get; set; } = true;
        public bool HasSavedSession { get; set; } = true;
        public int RestoreCalls { get; private set; }
        public int MediaCalls { get; private set; }
        public List<DialogRequest> DialogRequests { get; } = [];
        public List<MessageRequest> MessageRequests { get; } = [];
        public Dictionary<int, MessageBase?> Messages { get; } = [];
        public Func<MessageMedia, Stream, WTelegram.Client.ProgressCallback, CancellationToken, Task>? DownloadHandler { get; set; }

        public void QueueDialogs(int folderId, params Messages_DialogsBase[] pages)
            => _pages[folderId] = new Queue<Messages_DialogsBase>(pages);

        public Task RestoreSessionAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RestoreCalls++;
            IsLoggedIn = true;
            return Task.CompletedTask;
        }

        public Task<IPeerInfo> ResolveUsernameAsync(string username, CancellationToken ct)
            => throw new InvalidOperationException("Private message links must not resolve a public username.");

        public Task<Messages_DialogsBase> GetDialogsAsync(int folderId, DateTime offsetDate,
            int offsetId, InputPeer offsetPeer, bool excludePinned, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.True(IsLoggedIn, "The saved session must be restored before resolving a private channel.");
            DialogRequests.Add(new DialogRequest(folderId, offsetDate, offsetId, offsetPeer, excludePinned));
            return Task.FromResult(_pages.TryGetValue(folderId, out var pages) && pages.Count > 0
                ? pages.Dequeue()
                : (Messages_DialogsBase)Complete());
        }

        public Task<MessageBase?> GetMessageAsync(InputPeer peer, int messageId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            MessageRequests.Add(new MessageRequest(peer, messageId));
            return Task.FromResult(Messages.GetValueOrDefault(messageId));
        }

        public async Task DownloadMediaAsync(MessageMedia media, Stream output,
            WTelegram.Client.ProgressCallback progress, CancellationToken ct, Action<string>? log = null)
        {
            ct.ThrowIfCancellationRequested();
            MediaCalls++;
            if (DownloadHandler is not null)
                await DownloadHandler(media, output, progress, ct);
            else
            {
                await output.WriteAsync(MediaBytes, ct);
                progress(MediaBytes.Length, MediaBytes.Length);
            }
        }
    }
}
