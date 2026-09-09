using System.Collections.Concurrent;
using System.IO;
using EasyGet.Services;
using TL;
using Xunit;

namespace EasyGet.Tests;

public class TelegramDocumentPartSourceTests
{
    private const int PartSize = 512 * 1024;
    private static readonly byte[] FileBytes = [1, 2, 3, 4];

    [Fact]
    public async Task Read_PreservesDocumentLocationAndChunkRequestWithoutRequestingCdn()
    {
        using var client = new FakeMediaClient();
        var document = Document();
        var source = new TelegramDocumentPartSource(document, (dc, _) =>
        {
            Assert.Equal(document.dc_id, dc);
            return Task.FromResult<WTelegram.Client>(client);
        });
        client.Handler = _ =>
        {
            Assert.Equal(-1, client.FloodRetryThreshold);
            return Task.FromResult<Upload_FileBase>(new Upload_File { bytes = FileBytes });
        };

        var bytes = await source.ReadAsync(PartSize, PartSize, default);

        Assert.Equal(FileBytes, bytes);
        var request = Assert.Single(client.Requests);
        var location = Assert.IsType<InputDocumentFileLocation>(request.location);
        Assert.Equal(document.id, location.id);
        Assert.Equal(document.access_hash, location.access_hash);
        Assert.Equal(document.file_reference, location.file_reference);
        Assert.True(string.IsNullOrEmpty(location.thumb_size));
        Assert.Equal(PartSize, request.offset);
        Assert.Equal(PartSize, request.limit);
        Assert.Equal((TL.Methods.Upload_GetFile.Flags)0, request.flags);
    }

    [Fact]
    public async Task ConcurrentReads_InitializeOnlyOneMediaConnectionAndReuseIt()
    {
        using var client = new FakeMediaClient();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = 0;
        var source = new TelegramDocumentPartSource(Document(), async (_, ct) =>
        {
            Interlocked.Increment(ref connections);
            started.TrySetResult();
            await ready.Task.WaitAsync(ct);
            return client;
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reads = Enumerable.Range(0, 4)
            .Select(index => source.ReadAsync(index * (long)PartSize, PartSize, deadline.Token)).ToArray();
        try
        {
            await started.Task.WaitAsync(deadline.Token);
            Assert.Equal(1, connections);
            Assert.Empty(client.Requests);
        }
        finally
        {
            ready.TrySetResult();
        }

        var results = await Task.WhenAll(reads);

        Assert.Equal(1, connections);
        Assert.Equal(4, client.Requests.Count);
        Assert.Equal(-1, client.FloodRetryThreshold);
        Assert.All(results, bytes => Assert.Equal(FileBytes, bytes));
        Assert.Equal(new long[] { 0, PartSize, 2L * PartSize, 3L * PartSize },
            client.Requests.Select(request => request.offset).Order());
    }

    [Fact]
    public async Task FileMigration_RetriesTheSameChunkAndReusesTheNewDataCenter()
    {
        using var originalClient = new FakeMediaClient();
        using var migratedClient = new FakeMediaClient();
        originalClient.Handler = _ => throw new RpcException(303, "FILE_MIGRATE_X", 4);
        var connections = new List<int>();
        var document = Document();
        var source = new TelegramDocumentPartSource(document, (dc, _) =>
        {
            connections.Add(dc);
            return Task.FromResult<WTelegram.Client>(dc == 2 ? originalClient : migratedClient);
        });

        await source.ReadAsync(PartSize, PartSize, default);
        await source.ReadAsync(2L * PartSize, PartSize, default);

        Assert.Equal(new[] { 2, 4 }, connections);
        var original = Assert.Single(originalClient.Requests);
        var migrated = migratedClient.Requests.ToArray();
        Assert.Equal(2, migrated.Length);
        Assert.Equal(original.offset, migrated[0].offset);
        Assert.Equal(original.limit, migrated[0].limit);
        Assert.Equal(document.file_reference, Assert.IsType<InputDocumentFileLocation>(migrated[0].location).file_reference);
        Assert.Equal(2L * PartSize, migrated[1].offset);
        Assert.Equal(-1, migratedClient.FloodRetryThreshold);
    }

    [Fact]
    public async Task RepeatedMigrations_StopAfterTwoRedirects()
    {
        using var first = new FakeMediaClient();
        using var second = new FakeMediaClient();
        using var third = new FakeMediaClient();
        first.Handler = _ => throw new RpcException(303, "FILE_MIGRATE_X", 3);
        second.Handler = _ => throw new RpcException(303, "FILE_MIGRATE_X", 4);
        var failure = new RpcException(303, "FILE_MIGRATE_X", 5);
        third.Handler = _ => throw failure;
        var connections = new List<int>();
        var clients = new Dictionary<int, WTelegram.Client> { [2] = first, [3] = second, [4] = third };
        var source = new TelegramDocumentPartSource(Document(), (dc, _) =>
        {
            connections.Add(dc);
            return Task.FromResult(clients[dc]);
        });

        var error = await Assert.ThrowsAsync<RpcException>(() => source.ReadAsync(0, PartSize, default));

        Assert.Same(failure, error);
        Assert.Equal(new[] { 2, 3, 4 }, connections);
        Assert.Single(first.Requests);
        Assert.Single(second.Requests);
        Assert.Single(third.Requests);
    }

    [Theory]
    [InlineData("FLOOD_WAIT_X", 0)]
    [InlineData("FLOOD_WAIT_X", 90)]
    [InlineData("FLOOD_PREMIUM_WAIT_X", 4)]
    public async Task ServerWait_IsReturnedUnchangedToTheChunkRunner(string message, int seconds)
    {
        using var client = new FakeMediaClient();
        var expected = new RpcException(420, message, seconds);
        client.Handler = _ => throw expected;
        var source = new TelegramDocumentPartSource(Document(), (_, _) => Task.FromResult<WTelegram.Client>(client));

        var error = await Assert.ThrowsAsync<RpcException>(() => source.ReadAsync(0, PartSize, default).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Same(expected, error);
        Assert.Equal(-1, client.FloodRetryThreshold);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task PreCancelledRead_DoesNotCreateAConnectionOrIssueARpc()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var connections = 0;
        var source = new TelegramDocumentPartSource(Document(), (_, _) =>
        {
            connections++;
            throw new InvalidOperationException("Should not connect");
        });

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadAsync(0, PartSize, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, connections);
    }

    [Fact]
    public async Task CancelledRpcWait_ReturnsPromptlyWithoutWaitingForTheResponse()
    {
        using var client = new FakeMediaClient();
        using var cancellation = new CancellationTokenSource();
        var response = new TaskCompletionSource<Upload_FileBase>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Handler = _ =>
        {
            started.TrySetResult();
            return response.Task;
        };
        var source = new TelegramDocumentPartSource(Document(), (_, _) => Task.FromResult<WTelegram.Client>(client));
        var read = source.ReadAsync(0, PartSize, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();

            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Single(client.Requests);
            Assert.False(response.Task.IsCompleted);
        }
        finally
        {
            response.TrySetResult(new Upload_File { bytes = FileBytes });
        }
    }

    [Fact]
    public async Task ConnectionFailure_PropagatesAndAllowsTheNextReadToConnect()
    {
        using var client = new FakeMediaClient();
        var expected = new IOException("Media DC unavailable");
        var attempts = 0;
        var source = new TelegramDocumentPartSource(Document(), (_, _) => ++attempts == 1
            ? Task.FromException<WTelegram.Client>(expected)
            : Task.FromResult<WTelegram.Client>(client));

        var error = await Assert.ThrowsAsync<IOException>(() => source.ReadAsync(0, PartSize, default));
        var bytes = await source.ReadAsync(0, PartSize, default);

        Assert.Same(expected, error);
        Assert.Equal(2, attempts);
        Assert.Equal(FileBytes, bytes);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task UnexpectedCdnRedirect_IsRejectedWithoutReturningAnEmptyChunk()
    {
        using var client = new FakeMediaClient();
        client.Handler = _ => Task.FromResult<Upload_FileBase>(new Upload_FileCdnRedirect());
        var source = new TelegramDocumentPartSource(Document(), (_, _) => Task.FromResult<WTelegram.Client>(client));

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => source.ReadAsync(0, PartSize, default));

        Assert.Contains(nameof(Upload_FileCdnRedirect), error.Message, StringComparison.Ordinal);
        Assert.Single(client.Requests);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(-1, 2)]
    [InlineData(PartSize, 0)]
    [InlineData(PartSize, -2)]
    public void InvalidDocument_IsRejectedBeforeRequestingAConnection(long size, int dcId)
    {
        var document = Document();
        document.size = size;
        document.dc_id = dcId;

        Assert.Throws<ArgumentException>(() => new TelegramDocumentPartSource(document, (_, _) =>
            throw new InvalidOperationException("Should not connect")));
    }

    private static Document Document() => new()
    {
        id = 987654321,
        access_hash = 123456789,
        file_reference = [7, 8, 9],
        dc_id = 2,
        size = 4L * PartSize,
        mime_type = "video/mp4",
        attributes = []
    };

    private sealed class FakeMediaClient : WTelegram.Client
    {
        public FakeMediaClient() : base(key => key switch
        {
            "api_id" => "1",
            "api_hash" => "0123456789abcdef0123456789abcdef",
            _ => null
        }, new MemoryStream()) { }

        public ConcurrentQueue<TL.Methods.Upload_GetFile> Requests { get; } = new();
        public Func<TL.Methods.Upload_GetFile, Task<Upload_FileBase>> Handler { get; set; }
            = _ => Task.FromResult<Upload_FileBase>(new Upload_File { bytes = FileBytes });

        public override async Task<T> Invoke<T>(IMethod<T> query)
        {
            var request = Assert.IsType<TL.Methods.Upload_GetFile>(query);
            Requests.Enqueue(request);
            return (T)(object)await Handler(request);
        }
    }
}
