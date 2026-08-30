using System.Diagnostics;
using EasyGet.Models;

namespace EasyGet.Services;

public sealed class CollectionUpdatesFoundEventArgs : EventArgs
{
    public CollectionUpdatesFoundEventArgs(
        CollectionSubscription subscription,
        IReadOnlyList<CollectionSubscriptionItem> newItems)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(newItems);

        Subscription = subscription;
        NewItems = newItems;
    }

    public CollectionSubscription Subscription { get; }

    public IReadOnlyList<CollectionSubscriptionItem> NewItems { get; }
}

/// <summary>
/// Refreshes due collection subscriptions while the EasyGet process remains alive.
/// Closing the main window to the tray does not stop this coordinator; disposing the app's
/// service provider does.
/// </summary>
public sealed class CollectionRefreshCoordinator : IDisposable
{
    internal static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(15);
    internal const int DefaultMaxConcurrency = 3;

    private readonly ConfigService _configService;
    private readonly Func<CancellationToken, Task<IReadOnlyList<CollectionSubscription>>>
        _getSubscriptionsAsync;
    private readonly Func<long, CancellationToken, Task<CollectionRefreshOutcome>> _refreshAsync;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly int _maxConcurrency;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _lifecycleLock = new();

    private Task? _runTask;
    private bool _disposed;

    public CollectionRefreshCoordinator(
        ConfigService configService,
        HistoryService historyService,
        CollectionRefreshService refreshService)
        : this(
            configService,
            async cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var subscriptions = await historyService.GetCollectionSubscriptionSchedulesAsync()
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return subscriptions;
            },
            refreshService.RefreshAsync,
            () => DateTimeOffset.UtcNow,
            Task.Delay,
            DefaultMaxConcurrency)
    {
    }

    internal CollectionRefreshCoordinator(
        ConfigService configService,
        Func<CancellationToken, Task<IReadOnlyList<CollectionSubscription>>>
            getSubscriptionsAsync,
        Func<long, CancellationToken, Task<CollectionRefreshOutcome>> refreshAsync,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        int maxConcurrency = DefaultMaxConcurrency)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(getSubscriptionsAsync);
        ArgumentNullException.ThrowIfNull(refreshAsync);
        ArgumentNullException.ThrowIfNull(utcNow);
        ArgumentNullException.ThrowIfNull(delayAsync);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);

        _configService = configService;
        _getSubscriptionsAsync = getSubscriptionsAsync;
        _refreshAsync = refreshAsync;
        _utcNow = utcNow;
        _delayAsync = delayAsync;
        _maxConcurrency = maxConcurrency;
    }

    public event EventHandler<CollectionUpdatesFoundEventArgs>? UpdatesFound;

    /// <summary>Starts the immediate startup scan and the periodic process-lifetime loop.</summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _runTask ??= RunAsync(_lifetimeCancellation.Token);
        }
    }

    /// <summary>
    /// Checks all subscriptions currently due for an automatic refresh. A concurrent scan wins;
    /// subsequent callers return without starting a second round.
    /// </summary>
    public async Task CheckDueSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        await CheckDueSubscriptionsCoreAsync(linkedCancellation.Token).ConfigureAwait(false);
    }

    internal static bool IsDue(
        CollectionSubscription subscription,
        DateTimeOffset nowUtc,
        TimeSpan configuredInterval)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (!subscription.AutoCheckEnabled)
            return false;

        nowUtc = nowUtc.ToUniversalTime();
        if (subscription.NextCheckUtc is { } nextCheckUtc
            && subscription.CheckInterval == configuredInterval)
        {
            return nextCheckUtc.ToUniversalTime() <= nowUtc;
        }

        var lastAttemptUtc = subscription.LastAttemptUtc ?? subscription.LastSuccessUtc;
        if (lastAttemptUtc is null
            && subscription.CreatedAtUtc != default)
        {
            lastAttemptUtc = subscription.CreatedAtUtc;
        }

        return lastAttemptUtc is null
            || lastAttemptUtc.Value.ToUniversalTime() + configuredInterval <= nowUtc;
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CheckDueSubscriptionsCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CollectionRefreshCoordinator] Scan failed: {ex.Message}");
            }

            try
            {
                await _delayAsync(ScanInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CollectionRefreshCoordinator] Scan delay failed: {ex.Message}");
            }
        }
    }

    private async Task CheckDueSubscriptionsCoreAsync(CancellationToken cancellationToken)
    {
        if (!await _scanGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            var config = _configService.Config;
            if (!config.AutomaticCollectionRefreshEnabled)
                return;

            var intervalHours = Math.Clamp(
                config.CollectionRefreshIntervalHours,
                AppConfig.MinCollectionRefreshIntervalHours,
                AppConfig.MaxCollectionRefreshIntervalHours);
            var configuredInterval = TimeSpan.FromHours(intervalHours);
            var nowUtc = _utcNow().ToUniversalTime();
            var subscriptions = await _getSubscriptionsAsync(cancellationToken)
                .ConfigureAwait(false);
            var dueSubscriptions = subscriptions
                .Where(subscription => IsDue(subscription, nowUtc, configuredInterval))
                .Select(subscription => subscription.Id)
                .Distinct()
                .ToArray();

            await Parallel.ForEachAsync(
                    dueSubscriptions,
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = _maxConcurrency
                    },
                    RefreshOneAsync)
                .ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async ValueTask RefreshOneAsync(long subscriptionId, CancellationToken cancellationToken)
    {
        CollectionRefreshOutcome outcome;
        try
        {
            outcome = await _refreshAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[CollectionRefreshCoordinator] Subscription {subscriptionId} failed: {ex.Message}");
            return;
        }

        if (!outcome.IsSuccess
            || outcome.Subscription is null
            || outcome.NewItems.Count == 0)
        {
            return;
        }

        RaiseUpdatesFound(new CollectionUpdatesFoundEventArgs(
            outcome.Subscription,
            outcome.NewItems.ToArray()));
    }

    private void RaiseUpdatesFound(CollectionUpdatesFoundEventArgs eventArgs)
    {
        var handlers = UpdatesFound;
        if (handlers is null)
            return;

        foreach (EventHandler<CollectionUpdatesFoundEventArgs> handler
                 in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[CollectionRefreshCoordinator] UpdatesFound subscriber failed: {ex.Message}");
            }
        }
    }
}
