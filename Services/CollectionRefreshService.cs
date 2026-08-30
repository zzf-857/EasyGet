using System.Diagnostics;
using EasyGet.Models;

namespace EasyGet.Services;

public sealed class CollectionRefreshOutcome
{
    private CollectionRefreshOutcome(
        long subscriptionId,
        bool isSuccess,
        CollectionSubscription? subscription,
        IReadOnlyList<CollectionSubscriptionItem> newItems,
        string errorMessage)
    {
        SubscriptionId = subscriptionId;
        IsSuccess = isSuccess;
        Subscription = subscription;
        NewItems = newItems;
        ErrorMessage = errorMessage;
    }

    public long SubscriptionId { get; }

    public bool IsSuccess { get; }

    public CollectionSubscription? Subscription { get; }

    public IReadOnlyList<CollectionSubscriptionItem> NewItems { get; }

    public string ErrorMessage { get; }

    public static CollectionRefreshOutcome Success(
        long subscriptionId,
        CollectionSubscription subscription,
        IReadOnlyList<CollectionSubscriptionItem> newItems)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(newItems);

        return new CollectionRefreshOutcome(
            subscriptionId,
            isSuccess: true,
            subscription,
            newItems.ToArray(),
            "");
    }

    public static CollectionRefreshOutcome Failure(
        long subscriptionId,
        CollectionSubscription? subscription,
        string errorMessage)
        => new(
            subscriptionId,
            isSuccess: false,
            subscription,
            [],
            string.IsNullOrWhiteSpace(errorMessage) ? "合集刷新失败。" : errorMessage.Trim());
}

/// <summary>
/// Fetches a complete remote collection snapshot and applies it atomically to a tracked collection.
/// </summary>
public sealed class CollectionRefreshService
{
    private const int DefaultMetadataConcurrency = 4;

    private readonly ICollectionPlaylistClient _playlistClient;
    private readonly ICollectionRefreshStore _store;
    private readonly int _maxMetadataConcurrency;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _inFlightGate = new();
    private readonly Dictionary<long, InFlightRefresh> _inFlight = [];

    public CollectionRefreshService(
        YtDlpService ytDlpService,
        HistoryService historyService,
        int maxMetadataConcurrency = DefaultMetadataConcurrency)
        : this(
            new YtDlpCollectionPlaylistClient(ytDlpService),
            new HistoryCollectionRefreshStore(historyService),
            maxMetadataConcurrency,
            () => DateTimeOffset.UtcNow)
    {
    }

    internal CollectionRefreshService(
        ICollectionPlaylistClient playlistClient,
        ICollectionRefreshStore store,
        int maxMetadataConcurrency = DefaultMetadataConcurrency,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(playlistClient);
        ArgumentNullException.ThrowIfNull(store);
        if (maxMetadataConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxMetadataConcurrency));

        _playlistClient = playlistClient;
        _store = store;
        _maxMetadataConcurrency = maxMetadataConcurrency;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Concurrent callers for the same subscription share one remote refresh. Cancelling one waiter
    /// only cancels the shared operation when no other callers are still waiting for it.
    /// </summary>
    public Task<CollectionRefreshOutcome> RefreshAsync(
        long subscriptionId,
        CancellationToken cancellationToken = default)
    {
        if (subscriptionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(subscriptionId));

        cancellationToken.ThrowIfCancellationRequested();

        InFlightRefresh operation;
        var shouldStart = false;
        lock (_inFlightGate)
        {
            if (!_inFlight.TryGetValue(subscriptionId, out operation!))
            {
                operation = new InFlightRefresh();
                _inFlight.Add(subscriptionId, operation);
                shouldStart = true;
            }

            operation.WaiterCount++;
        }

        if (shouldStart)
            _ = RunSharedRefreshAsync(subscriptionId, operation);

        return AwaitSharedRefreshAsync(subscriptionId, operation, cancellationToken);
    }

    private async Task RunSharedRefreshAsync(long subscriptionId, InFlightRefresh operation)
    {
        try
        {
            var result = await RefreshCoreAsync(subscriptionId, operation.Cancellation.Token)
                .ConfigureAwait(false);
            operation.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            operation.Completion.TrySetCanceled(operation.Cancellation.Token);
        }
        catch (Exception ex)
        {
            operation.Completion.TrySetException(ex);
        }
        finally
        {
            lock (_inFlightGate)
            {
                if (_inFlight.TryGetValue(subscriptionId, out var current)
                    && ReferenceEquals(current, operation))
                {
                    _inFlight.Remove(subscriptionId);
                }
            }

            operation.Cancellation.Dispose();
        }
    }

    private async Task<CollectionRefreshOutcome> AwaitSharedRefreshAsync(
        long subscriptionId,
        InFlightRefresh operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var cancelSharedOperation = false;
            lock (_inFlightGate)
            {
                operation.WaiterCount--;
                if (operation.WaiterCount == 0 && !operation.Completion.Task.IsCompleted)
                {
                    cancelSharedOperation = true;
                }
            }

            if (cancelSharedOperation)
            {
                try
                {
                    operation.Cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The shared operation completed between the waiter-count check and cancellation.
                }
            }
        }
    }

    private async Task<CollectionRefreshOutcome> RefreshCoreAsync(
        long subscriptionId,
        CancellationToken cancellationToken)
    {
        var subscription = await _store.GetAsync(subscriptionId).ConfigureAwait(false);
        if (subscription is null)
        {
            return CollectionRefreshOutcome.Failure(
                subscriptionId,
                null,
                "未找到合集订阅。");
        }

        var attemptedAtUtc = _utcNow().ToUniversalTime();
        if (string.IsNullOrWhiteSpace(subscription.SourceUrl))
        {
            return await RecordFailureAsync(
                    subscription,
                    "合集链接为空。",
                    attemptedAtUtc)
                .ConfigureAwait(false);
        }

        PlaylistFetchResult fetchResult;
        try
        {
            fetchResult = await _playlistClient.FetchPlaylistInfoAsync(
                    subscription.SourceUrl,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await RecordFailureAsync(subscription, ex.Message, attemptedAtUtc)
                .ConfigureAwait(false);
        }

        if (!fetchResult.IsSuccess)
        {
            return await RecordFailureAsync(
                    subscription,
                    fetchResult.ErrorMessage,
                    attemptedAtUtc)
                .ConfigureAwait(false);
        }

        var snapshots = BuildSnapshots(subscription, fetchResult.Info);
        await ResolveMissingNewTitlesAsync(subscription, snapshots, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var stored = await _store.ApplySnapshotAsync(
                subscription.Id,
                snapshots,
                fetchResult.Info.Title,
                attemptedAtUtc)
            .ConfigureAwait(false);

        return CollectionRefreshOutcome.Success(
            subscription.Id,
            stored.Subscription,
            stored.NewItems);
    }

    private async Task<CollectionRefreshOutcome> RecordFailureAsync(
        CollectionSubscription subscription,
        string errorMessage,
        DateTimeOffset attemptedAtUtc)
    {
        var normalizedMessage = string.IsNullOrWhiteSpace(errorMessage)
            ? "合集刷新失败。"
            : errorMessage.Trim();
        var stored = await _store.RecordFailureAsync(
                subscription.Id,
                normalizedMessage,
                attemptedAtUtc)
            .ConfigureAwait(false);

        return CollectionRefreshOutcome.Failure(
            subscription.Id,
            stored,
            normalizedMessage);
    }

    private static List<CollectionSubscriptionItemSnapshot> BuildSnapshots(
        CollectionSubscription subscription,
        PlaylistInfo playlist)
    {
        var existingByKey = subscription.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.EntryKey))
            .GroupBy(item => item.EntryKey.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var snapshotsByKey = new Dictionary<string, CollectionSubscriptionItemSnapshot>(
            StringComparer.Ordinal);
        var snapshots = new List<CollectionSubscriptionItemSnapshot>();

        var entries = playlist.Entries.Count > 0
            ? playlist.Entries
            : playlist.Urls.Select((url, index) => new PlaylistEntryInfo
            {
                Url = url,
                OriginalIndex = index + 1
            }).ToList();

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var entryKey = entry.StableKey.Trim();
            if (string.IsNullOrWhiteSpace(entryKey))
                continue;

            var title = entry.OriginalTitle.Trim();
            if (string.IsNullOrWhiteSpace(title)
                && existingByKey.TryGetValue(entryKey, out var existing))
            {
                title = existing.Title;
            }

            if (snapshotsByKey.TryGetValue(entryKey, out var duplicate))
            {
                if (string.IsNullOrWhiteSpace(duplicate.Title) && !string.IsNullOrWhiteSpace(title))
                    duplicate.Title = title;
                if (string.IsNullOrWhiteSpace(duplicate.Url) && !string.IsNullOrWhiteSpace(entry.Url))
                    duplicate.Url = entry.Url.Trim();
                if (string.IsNullOrWhiteSpace(duplicate.EntryId) && !string.IsNullOrWhiteSpace(entry.Id))
                    duplicate.EntryId = entry.Id.Trim();
                continue;
            }

            var snapshot = new CollectionSubscriptionItemSnapshot
            {
                EntryKey = entryKey,
                EntryId = entry.Id.Trim(),
                Url = entry.Url.Trim(),
                Title = title,
                Position = entry.OriginalIndex > 0 ? entry.OriginalIndex : index + 1
            };
            snapshotsByKey.Add(entryKey, snapshot);
            snapshots.Add(snapshot);
        }

        return snapshots;
    }

    private async Task ResolveMissingNewTitlesAsync(
        CollectionSubscription subscription,
        IReadOnlyList<CollectionSubscriptionItemSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var existingByKey = subscription.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.EntryKey))
            .GroupBy(item => item.EntryKey.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var unresolved = snapshots
            .Where(snapshot => string.IsNullOrWhiteSpace(snapshot.Title)
                               && !string.IsNullOrWhiteSpace(snapshot.Url)
                               && (!existingByKey.TryGetValue(snapshot.EntryKey, out var existing)
                                   || existing.State is CollectionSubscriptionItemState.New
                                       or CollectionSubscriptionItemState.Failed))
            .ToArray();
        if (unresolved.Length == 0)
            return;

        await Parallel.ForEachAsync(
            unresolved,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _maxMetadataConcurrency
            },
            async (snapshot, itemCancellationToken) =>
            {
                try
                {
                    var info = await _playlistClient.GetVideoInfoAsync(
                            snapshot.Url,
                            itemCancellationToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(info?.Title))
                        snapshot.Title = info.Title.Trim();
                }
                catch (OperationCanceledException) when (itemCancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[CollectionRefreshService] Metadata lookup failed for {snapshot.Url}: {ex.Message}");
                }
            }).ConfigureAwait(false);
    }

    private sealed class InFlightRefresh
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public TaskCompletionSource<CollectionRefreshOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int WaiterCount { get; set; }
    }
}

internal interface ICollectionPlaylistClient
{
    Task<PlaylistFetchResult> FetchPlaylistInfoAsync(
        string url,
        CancellationToken cancellationToken = default);

    Task<VideoInfo?> GetVideoInfoAsync(
        string url,
        CancellationToken cancellationToken = default);
}

internal sealed class YtDlpCollectionPlaylistClient(YtDlpService ytDlpService)
    : ICollectionPlaylistClient
{
    private readonly YtDlpService _ytDlpService =
        ytDlpService ?? throw new ArgumentNullException(nameof(ytDlpService));

    public Task<PlaylistFetchResult> FetchPlaylistInfoAsync(
        string url,
        CancellationToken cancellationToken = default)
        => _ytDlpService.FetchPlaylistInfoAsync(url, cancellationToken);

    public Task<VideoInfo?> GetVideoInfoAsync(
        string url,
        CancellationToken cancellationToken = default)
        => _ytDlpService.GetVideoInfoAsync(url, cancellationToken);
}

internal interface ICollectionRefreshStore
{
    Task<CollectionSubscription?> GetAsync(long subscriptionId);

    Task<CollectionSubscriptionRefreshResult> ApplySnapshotAsync(
        long subscriptionId,
        IReadOnlyCollection<CollectionSubscriptionItemSnapshot> snapshot,
        string? title,
        DateTimeOffset attemptedAtUtc);

    Task<CollectionSubscription> RecordFailureAsync(
        long subscriptionId,
        string errorMessage,
        DateTimeOffset attemptedAtUtc);
}

internal sealed class HistoryCollectionRefreshStore(HistoryService historyService)
    : ICollectionRefreshStore
{
    private readonly HistoryService _historyService =
        historyService ?? throw new ArgumentNullException(nameof(historyService));

    public Task<CollectionSubscription?> GetAsync(long subscriptionId)
        => _historyService.GetCollectionSubscriptionAsync(subscriptionId);

    public Task<CollectionSubscriptionRefreshResult> ApplySnapshotAsync(
        long subscriptionId,
        IReadOnlyCollection<CollectionSubscriptionItemSnapshot> snapshot,
        string? title,
        DateTimeOffset attemptedAtUtc)
        => _historyService.ApplyCollectionSubscriptionSnapshotAsync(
            subscriptionId,
            snapshot,
            title,
            attemptedAtUtc);

    public Task<CollectionSubscription> RecordFailureAsync(
        long subscriptionId,
        string errorMessage,
        DateTimeOffset attemptedAtUtc)
        => _historyService.RecordCollectionSubscriptionRefreshFailureAsync(
            subscriptionId,
            errorMessage,
            attemptedAtUtc);
}
