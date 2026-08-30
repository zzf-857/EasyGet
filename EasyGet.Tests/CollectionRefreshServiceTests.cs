using System.Collections.Concurrent;
using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public sealed class CollectionRefreshServiceTests
{
    private static readonly DateTimeOffset AttemptedAt =
        new(2026, 8, 30, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RefreshAsync_DiffsStableKeysAndResolvesOnlyUntitledNewEntries()
    {
        var existingEntry = Entry("existing", title: "");
        var removedEntry = Entry("removed", title: "Old removed title");
        var subscription = Subscription(
            StoredItem(existingEntry, "Stored existing title"),
            StoredItem(removedEntry, "Old removed title"));
        var store = new FakeCollectionRefreshStore(subscription);
        var client = new FakeCollectionPlaylistClient
        {
            FetchHandler = (_, _) => Task.FromResult(PlaylistFetchResult.Success(new PlaylistInfo
            {
                Title = "Updated collection title",
                Entries =
                [
                    Entry("existing", title: "", index: 3),
                    Entry("titled-new", title: "Remote title", index: 1),
                    Entry("untitled-new", title: "", index: 2),
                    Entry("untitled-new", title: "", index: 4)
                ]
            })),
            MetadataHandler = (url, _) => Task.FromResult<VideoInfo?>(new VideoInfo
            {
                Title = url.Contains("untitled-new", StringComparison.Ordinal)
                    ? "Resolved title"
                    : "Unexpected lookup"
            })
        };
        var service = new CollectionRefreshService(client, store, utcNow: () => AttemptedAt);

        var outcome = await service.RefreshAsync(subscription.Id);

        Assert.True(outcome.IsSuccess);
        Assert.Equal("Updated collection title", outcome.Subscription!.Title);
        Assert.Equal(2, outcome.NewItems.Count);
        Assert.Equal(
            ["Remote title", "Resolved title"],
            outcome.NewItems.OrderBy(item => item.Position).Select(item => item.Title));
        Assert.Equal(["https://example.test/untitled-new"], client.MetadataUrls);
        Assert.Equal(3, store.LastSnapshot.Count);
        Assert.Equal(
            "Stored existing title",
            store.LastSnapshot.Single(item => item.EntryKey == existingEntry.StableKey).Title);
        Assert.False(outcome.Subscription.Items.Single(
            item => item.EntryKey == removedEntry.StableKey).IsPresent);
        Assert.Equal(AttemptedAt, store.LastAttemptedAtUtc);
    }

    [Theory]
    [InlineData(CollectionSubscriptionItemState.New)]
    [InlineData(CollectionSubscriptionItemState.Failed)]
    public async Task RefreshAsync_RetriesMissingTitleForPendingExistingEntry(
        CollectionSubscriptionItemState state)
    {
        var entry = Entry("pending", title: "");
        var storedItem = StoredItem(entry, "");
        storedItem.State = state;
        var subscription = Subscription(storedItem);
        var store = new FakeCollectionRefreshStore(subscription);
        var client = new FakeCollectionPlaylistClient
        {
            FetchHandler = (_, _) => Task.FromResult(PlaylistFetchResult.Success(new PlaylistInfo
            {
                Entries = [Entry("pending", title: "")]
            })),
            MetadataHandler = (_, _) => Task.FromResult<VideoInfo?>(new VideoInfo
            {
                Title = "Resolved pending title"
            })
        };
        var service = new CollectionRefreshService(client, store, utcNow: () => AttemptedAt);

        var outcome = await service.RefreshAsync(subscription.Id);

        Assert.True(outcome.IsSuccess);
        Assert.Empty(outcome.NewItems);
        Assert.Equal(["https://example.test/pending"], client.MetadataUrls);
        Assert.Equal(
            "Resolved pending title",
            Assert.Single(outcome.Subscription!.Items).Title);
    }

    [Fact]
    public async Task RefreshAsync_ReorderRenameRemovalAndReappearanceDoNotCreateNewItems()
    {
        var first = Entry("first", title: "Original first", index: 1);
        var second = Entry("second", title: "Original second", index: 2);
        var temporarilyRemoved = Entry("temporarily-removed", title: "Old title", index: 3);
        var subscription = Subscription(
            StoredItem(first, first.OriginalTitle),
            StoredItem(second, second.OriginalTitle),
            StoredItem(temporarilyRemoved, temporarilyRemoved.OriginalTitle, isPresent: false));
        var store = new FakeCollectionRefreshStore(subscription);
        var client = new FakeCollectionPlaylistClient
        {
            FetchHandler = (_, _) => Task.FromResult(PlaylistFetchResult.Success(new PlaylistInfo
            {
                Entries =
                [
                    Entry("second", title: "Renamed second", index: 1),
                    Entry("temporarily-removed", title: "Returned title", index: 2)
                ]
            }))
        };
        var service = new CollectionRefreshService(client, store);

        var outcome = await service.RefreshAsync(subscription.Id);

        Assert.True(outcome.IsSuccess);
        Assert.Empty(outcome.NewItems);
        Assert.Empty(client.MetadataUrls);
        Assert.False(outcome.Subscription!.Items.Single(item => item.EntryKey == first.StableKey).IsPresent);
        Assert.Equal(
            "Renamed second",
            outcome.Subscription.Items.Single(item => item.EntryKey == second.StableKey).Title);
        Assert.True(outcome.Subscription.Items.Single(
            item => item.EntryKey == temporarilyRemoved.StableKey).IsPresent);
    }

    [Fact]
    public async Task RefreshAsync_FailedFetchRecordsFailureWithoutApplyingPartialSnapshot()
    {
        var existing = Entry("existing", title: "Existing title");
        var subscription = Subscription(StoredItem(existing, existing.OriginalTitle));
        var store = new FakeCollectionRefreshStore(subscription);
        var client = new FakeCollectionPlaylistClient
        {
            FetchHandler = (_, _) => Task.FromResult(PlaylistFetchResult.Failure(
                new PlaylistInfo
                {
                    Entries = [Entry("partial-new", title: "Partial result")]
                },
                "remote fetch failed",
                exitCode: 1))
        };
        var service = new CollectionRefreshService(client, store, utcNow: () => AttemptedAt);

        var outcome = await service.RefreshAsync(subscription.Id);

        Assert.False(outcome.IsSuccess);
        Assert.Equal("remote fetch failed", outcome.ErrorMessage);
        Assert.Equal(0, store.ApplyCalls);
        Assert.Equal(1, store.FailureCalls);
        Assert.Single(outcome.Subscription!.Items);
        Assert.Equal(existing.StableKey, outcome.Subscription.Items[0].EntryKey);
        Assert.Equal(AttemptedAt, outcome.Subscription.LastAttemptUtc);
    }

    [Fact]
    public async Task RefreshAsync_SuccessfulEmptyFetchAppliesEmptySnapshot()
    {
        var existing = Entry("existing", title: "Existing title");
        var subscription = Subscription(StoredItem(existing, existing.OriginalTitle));
        var store = new FakeCollectionRefreshStore(subscription);
        var client = new FakeCollectionPlaylistClient
        {
            FetchHandler = (_, _) => Task.FromResult(PlaylistFetchResult.Success(new PlaylistInfo()))
        };
        var service = new CollectionRefreshService(client, store, utcNow: () => AttemptedAt);

        var outcome = await service.RefreshAsync(subscription.Id);

        Assert.True(outcome.IsSuccess);
        Assert.Empty(outcome.NewItems);
        Assert.Equal(1, store.ApplyCalls);
        Assert.Empty(store.LastSnapshot);
        Assert.False(outcome.Subscription!.Items[0].IsPresent);
        Assert.Equal(AttemptedAt, outcome.Subscription.LastSuccessUtc);
    }

    [Fact]
    public async Task RefreshAsync_ConcurrentCallsForSameSubscriptionShareOneFetch()
    {
        var subscription = Subscription();
        var store = new FakeCollectionRefreshStore(subscription);
        var fetchStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFetch = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeCollectionPlaylistClient
        {
            FetchHandler = async (_, cancellationToken) =>
            {
                fetchStarted.TrySetResult(true);
                await releaseFetch.Task.WaitAsync(cancellationToken);
                return PlaylistFetchResult.Success(new PlaylistInfo());
            }
        };
        var service = new CollectionRefreshService(client, store);

        var first = service.RefreshAsync(subscription.Id);
        await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = service.RefreshAsync(subscription.Id);
        releaseFetch.TrySetResult(true);

        var firstOutcome = await first.WaitAsync(TimeSpan.FromSeconds(2));
        var secondOutcome = await second.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(firstOutcome, secondOutcome);
        Assert.Equal(1, client.FetchCalls);
        Assert.Equal(1, store.ApplyCalls);
    }

    [Fact]
    public async Task RefreshAsync_CancellingOneWaiterDoesNotCancelAnotherWaiter()
    {
        var subscription = Subscription();
        var store = new FakeCollectionRefreshStore(subscription);
        var fetchStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFetch = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeCollectionPlaylistClient
        {
            FetchHandler = async (_, cancellationToken) =>
            {
                fetchStarted.TrySetResult(true);
                await releaseFetch.Task.WaitAsync(cancellationToken);
                return PlaylistFetchResult.Success(new PlaylistInfo());
            }
        };
        var service = new CollectionRefreshService(client, store);
        using var firstCancellation = new CancellationTokenSource();

        var first = service.RefreshAsync(subscription.Id, firstCancellation.Token);
        await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = service.RefreshAsync(subscription.Id);
        firstCancellation.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(async () => await first);
        releaseFetch.TrySetResult(true);
        var secondOutcome = await second.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(secondOutcome.IsSuccess);
        Assert.Equal(1, client.FetchCalls);
        Assert.Equal(1, store.ApplyCalls);
    }

    [Fact]
    public async Task RefreshAsync_BoundsConcurrentMetadataResolution()
    {
        var subscription = Subscription();
        var store = new FakeCollectionRefreshStore(subscription);
        var twoLookupsStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLookups = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var activeLookups = 0;
        var maximumActiveLookups = 0;
        var startedLookups = 0;
        var client = new FakeCollectionPlaylistClient
        {
            FetchHandler = (_, _) => Task.FromResult(PlaylistFetchResult.Success(new PlaylistInfo
            {
                Entries = Enumerable.Range(1, 6)
                    .Select(index => Entry($"new-{index}", title: "", index: index))
                    .ToList()
            })),
            MetadataHandler = async (url, cancellationToken) =>
            {
                var active = Interlocked.Increment(ref activeLookups);
                UpdateMaximum(ref maximumActiveLookups, active);
                if (Interlocked.Increment(ref startedLookups) == 2)
                    twoLookupsStarted.TrySetResult(true);

                try
                {
                    await releaseLookups.Task.WaitAsync(cancellationToken);
                    return new VideoInfo { Title = $"Resolved {url}" };
                }
                finally
                {
                    Interlocked.Decrement(ref activeLookups);
                }
            }
        };
        var service = new CollectionRefreshService(client, store, maxMetadataConcurrency: 2);

        var refresh = service.RefreshAsync(subscription.Id);
        await twoLookupsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, Volatile.Read(ref startedLookups));
        Assert.Equal(2, Volatile.Read(ref maximumActiveLookups));
        releaseLookups.TrySetResult(true);

        var outcome = await refresh.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(outcome.IsSuccess);
        Assert.Equal(6, client.MetadataUrls.Count);
        Assert.Equal(6, outcome.NewItems.Count);
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (candidate <= observed
                || Interlocked.CompareExchange(ref maximum, candidate, observed) == observed)
            {
                return;
            }
        }
    }

    private static CollectionSubscription Subscription(params CollectionSubscriptionItem[] items)
        => new()
        {
            Id = 17,
            CanonicalKey = "extractor:test:collection",
            SourceUrl = "https://example.test/collection",
            Title = "Collection",
            CheckInterval = TimeSpan.FromHours(24),
            Items = items.ToList()
        };

    private static PlaylistEntryInfo Entry(
        string id,
        string title,
        int index = 1)
        => new()
        {
            Id = id,
            IeKey = "Test",
            Url = $"https://example.test/{id}",
            OriginalTitle = title,
            OriginalIndex = index,
            Kind = PlaylistEntryKind.Video
        };

    private static CollectionSubscriptionItem StoredItem(
        PlaylistEntryInfo entry,
        string title,
        bool isPresent = true)
        => new()
        {
            SubscriptionId = 17,
            EntryKey = entry.StableKey,
            EntryId = entry.Id,
            Url = entry.Url,
            Title = title,
            Position = entry.OriginalIndex,
            LocalSequence = entry.OriginalIndex,
            State = CollectionSubscriptionItemState.Known,
            IsPresent = isPresent
        };

    private sealed class FakeCollectionPlaylistClient : ICollectionPlaylistClient
    {
        private int _fetchCalls;

        public Func<string, CancellationToken, Task<PlaylistFetchResult>> FetchHandler { get; init; } =
            (_, _) => Task.FromResult(PlaylistFetchResult.Success(new PlaylistInfo()));

        public Func<string, CancellationToken, Task<VideoInfo?>> MetadataHandler { get; init; } =
            (_, _) => Task.FromResult<VideoInfo?>(null);

        public int FetchCalls => Volatile.Read(ref _fetchCalls);

        public ConcurrentQueue<string> MetadataUrls { get; } = new();

        public Task<PlaylistFetchResult> FetchPlaylistInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _fetchCalls);
            return FetchHandler(url, cancellationToken);
        }

        public Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            MetadataUrls.Enqueue(url);
            return MetadataHandler(url, cancellationToken);
        }
    }

    private sealed class FakeCollectionRefreshStore(CollectionSubscription subscription)
        : ICollectionRefreshStore
    {
        private readonly CollectionSubscription _subscription = subscription;

        public int ApplyCalls { get; private set; }

        public int FailureCalls { get; private set; }

        public IReadOnlyList<CollectionSubscriptionItemSnapshot> LastSnapshot { get; private set; } = [];

        public DateTimeOffset? LastAttemptedAtUtc { get; private set; }

        public Task<CollectionSubscription?> GetAsync(long subscriptionId)
            => Task.FromResult<CollectionSubscription?>(
                subscriptionId == _subscription.Id ? _subscription : null);

        public Task<CollectionSubscriptionRefreshResult> ApplySnapshotAsync(
            long subscriptionId,
            IReadOnlyCollection<CollectionSubscriptionItemSnapshot> snapshot,
            string? title,
            DateTimeOffset attemptedAtUtc)
        {
            ApplyCalls++;
            LastSnapshot = snapshot.ToArray();
            LastAttemptedAtUtc = attemptedAtUtc;
            var presentKeys = snapshot.Select(item => item.EntryKey).ToHashSet(StringComparer.Ordinal);
            foreach (var item in _subscription.Items)
                item.IsPresent = presentKeys.Contains(item.EntryKey);

            var newItems = new List<CollectionSubscriptionItem>();
            foreach (var itemSnapshot in snapshot)
            {
                var existing = _subscription.Items.FirstOrDefault(item =>
                    string.Equals(item.EntryKey, itemSnapshot.EntryKey, StringComparison.Ordinal));
                if (existing is null)
                {
                    existing = new CollectionSubscriptionItem
                    {
                        SubscriptionId = subscriptionId,
                        EntryKey = itemSnapshot.EntryKey,
                        LocalSequence = _subscription.Items.Count + 1,
                        State = CollectionSubscriptionItemState.New
                    };
                    _subscription.Items.Add(existing);
                    newItems.Add(existing);
                }

                existing.EntryId = itemSnapshot.EntryId;
                existing.Url = itemSnapshot.Url;
                existing.Title = itemSnapshot.Title;
                existing.Position = itemSnapshot.Position;
                existing.IsPresent = true;
            }

            if (!string.IsNullOrWhiteSpace(title))
                _subscription.Title = title.Trim();
            _subscription.LastAttemptUtc = attemptedAtUtc;
            _subscription.LastSuccessUtc = attemptedAtUtc;
            _subscription.FailureMessage = "";

            return Task.FromResult(new CollectionSubscriptionRefreshResult
            {
                Subscription = _subscription,
                NewItems = newItems
            });
        }

        public Task<CollectionSubscription> RecordFailureAsync(
            long subscriptionId,
            string errorMessage,
            DateTimeOffset attemptedAtUtc)
        {
            FailureCalls++;
            LastAttemptedAtUtc = attemptedAtUtc;
            _subscription.LastAttemptUtc = attemptedAtUtc;
            _subscription.FailureMessage = errorMessage;
            return Task.FromResult(_subscription);
        }
    }
}
