using System.Collections.Concurrent;
using System.Diagnostics;
using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public sealed class CollectionRefreshCoordinatorTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"easyget-collection-refresh-coordinator-{Guid.NewGuid():N}");

    [Fact]
    public async Task CheckDueSubscriptionsAsync_RefreshesOnlyEnabledDueSubscriptions()
    {
        var now = new DateTimeOffset(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
        var config = CreateConfig();
        config.Config.CollectionRefreshIntervalHours = 24;
        CollectionSubscription[] subscriptions =
        [
            Subscription(1, nextCheckUtc: now.AddMinutes(-1)),
            Subscription(2, nextCheckUtc: now.AddMinutes(-1), autoCheckEnabled: false),
            Subscription(3, nextCheckUtc: now.AddMinutes(1)),
            Subscription(4, lastAttemptUtc: now.AddHours(-23)),
            Subscription(5, lastAttemptUtc: now.AddHours(-25))
        ];
        var refreshed = new ConcurrentBag<long>();
        using var coordinator = CreateCoordinator(
            config,
            subscriptions,
            (id, _) =>
            {
                refreshed.Add(id);
                return Task.FromResult(NoUpdates(id));
            },
            utcNow: () => now);

        await coordinator.CheckDueSubscriptionsAsync();

        Assert.Equal([1L, 5L], refreshed.Order().ToArray());
    }

    [Fact]
    public async Task CheckDueSubscriptionsAsync_DisabledGloballyDoesNotReadSubscriptions()
    {
        var config = CreateConfig();
        config.Config.AutomaticCollectionRefreshEnabled = false;
        var loadCalls = 0;
        using var coordinator = CreateCoordinator(
            config,
            _ =>
            {
                Interlocked.Increment(ref loadCalls);
                return Task.FromResult<IReadOnlyList<CollectionSubscription>>(
                    [Subscription(1)]);
            },
            (_, _) => Task.FromResult(NoUpdates(1)));

        await coordinator.CheckDueSubscriptionsAsync();

        Assert.Equal(0, loadCalls);
    }

    [Fact]
    public async Task CheckDueSubscriptionsAsync_ConcurrentRoundReturnsWithoutOverlap()
    {
        var config = CreateConfig();
        var loadCalls = 0;
        var refreshCalls = 0;
        var refreshStarted = NewSignal();
        var releaseRefresh = NewSignal();
        using var coordinator = CreateCoordinator(
            config,
            _ =>
            {
                Interlocked.Increment(ref loadCalls);
                return Task.FromResult<IReadOnlyList<CollectionSubscription>>(
                    [Subscription(1)]);
            },
            async (id, cancellationToken) =>
            {
                Interlocked.Increment(ref refreshCalls);
                refreshStarted.TrySetResult();
                await releaseRefresh.Task.WaitAsync(cancellationToken);
                return NoUpdates(id);
            });

        var firstRound = coordinator.CheckDueSubscriptionsAsync();
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coordinator.CheckDueSubscriptionsAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, loadCalls);
        Assert.Equal(1, refreshCalls);
        releaseRefresh.TrySetResult();
        await firstRound;
    }

    [Fact]
    public async Task CheckDueSubscriptionsAsync_BoundsConcurrencyAndIsolatesOneFailure()
    {
        var config = CreateConfig();
        var attempted = new ConcurrentBag<long>();
        var releaseRefreshes = NewSignal();
        var concurrencyLimitReached = NewSignal();
        var active = 0;
        var maximumActive = 0;
        var subscriptions = Enumerable.Range(1, 6)
            .Select(id => Subscription(id))
            .ToArray();
        using var coordinator = CreateCoordinator(
            config,
            subscriptions,
            async (id, cancellationToken) =>
            {
                attempted.Add(id);
                var currentActive = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, currentActive);
                if (currentActive == 2)
                    concurrencyLimitReached.TrySetResult();

                try
                {
                    await releaseRefreshes.Task.WaitAsync(cancellationToken);
                    if (id == 1)
                        throw new InvalidOperationException("one subscription failed");

                    return NoUpdates(id);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            maxConcurrency: 2);

        var scan = coordinator.CheckDueSubscriptionsAsync();
        await concurrencyLimitReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, Volatile.Read(ref maximumActive));

        releaseRefreshes.TrySetResult();
        await scan;

        Assert.Equal(Enumerable.Range(1, 6).Select(id => (long)id), attempted.Order());
        Assert.InRange(maximumActive, 1, 2);
    }

    [Fact]
    public async Task CheckDueSubscriptionsAsync_RaisesUpdatesFoundAndIsolatesSubscribers()
    {
        var config = CreateConfig();
        var subscription = Subscription(42);
        subscription.Title = "合集";
        CollectionSubscriptionItem[] newItems =
        [
            new() { SubscriptionId = 42, EntryKey = "bvid:one", Title = "新视频 1" },
            new() { SubscriptionId = 42, EntryKey = "bvid:two", Title = "新视频 2" }
        ];
        CollectionUpdatesFoundEventArgs? received = null;
        using var coordinator = CreateCoordinator(
            config,
            [subscription],
            (id, _) => Task.FromResult(
                CollectionRefreshOutcome.Success(id, subscription, newItems)));
        coordinator.UpdatesFound += (_, _) => throw new InvalidOperationException("bad handler");
        coordinator.UpdatesFound += (_, args) => received = args;

        await coordinator.CheckDueSubscriptionsAsync();

        Assert.NotNull(received);
        Assert.Same(subscription, received.Subscription);
        Assert.Equal(["新视频 1", "新视频 2"], received.NewItems.Select(item => item.Title));
    }

    [Fact]
    public async Task Start_ScansImmediatelyThenAgainAfterPeriodicDelay()
    {
        var config = CreateConfig();
        var firstRefresh = NewSignal();
        var secondRefresh = NewSignal();
        var firstDelayStarted = NewSignal();
        var releaseFirstDelay = NewSignal();
        var refreshCount = 0;
        var delayCount = 0;
        TimeSpan? requestedDelay = null;
        using var coordinator = CreateCoordinator(
            config,
            [Subscription(1)],
            (id, _) =>
            {
                var count = Interlocked.Increment(ref refreshCount);
                (count == 1 ? firstRefresh : secondRefresh).TrySetResult();
                return Task.FromResult(NoUpdates(id));
            },
            delayAsync: async (delay, cancellationToken) =>
            {
                requestedDelay = delay;
                if (Interlocked.Increment(ref delayCount) == 1)
                {
                    firstDelayStarted.TrySetResult();
                    await releaseFirstDelay.Task.WaitAsync(cancellationToken);
                }
                else
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        coordinator.Start();
        coordinator.Start();
        await firstRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await firstDelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CollectionRefreshCoordinator.ScanInterval, requestedDelay);

        releaseFirstDelay.TrySetResult();
        await secondRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, refreshCount);
    }

    [Fact]
    public async Task Dispose_CancelsAnActiveStartupRefresh()
    {
        var config = CreateConfig();
        var refreshStarted = NewSignal();
        var cancellationObserved = NewSignal();
        using var coordinator = CreateCoordinator(
            config,
            [Subscription(1)],
            async (_, cancellationToken) =>
            {
                refreshStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new UnreachableException();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }
            });

        coordinator.Start();
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.Dispose();

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void IsDue_UsesStoredNextCheckBeforeFallbackInterval(
        int nextCheckOffsetMinutes,
        bool expected)
    {
        var now = new DateTimeOffset(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
        var subscription = Subscription(
            1,
            nextCheckUtc: now.AddMinutes(nextCheckOffsetMinutes),
            lastAttemptUtc: now.AddDays(-30));

        var actual = CollectionRefreshCoordinator.IsDue(
            subscription,
            now,
            TimeSpan.FromHours(24));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsDue_GlobalIntervalChangeOverridesPreviouslyStoredSchedule()
    {
        var now = new DateTimeOffset(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
        var subscription = Subscription(
            1,
            nextCheckUtc: now.AddHours(18),
            lastAttemptUtc: now.AddHours(-6));
        subscription.CheckInterval = TimeSpan.FromHours(24);

        Assert.True(CollectionRefreshCoordinator.IsDue(
            subscription,
            now,
            TimeSpan.FromHours(1)));

        subscription.NextCheckUtc = now.AddHours(-1);
        Assert.False(CollectionRefreshCoordinator.IsDue(
            subscription,
            now,
            TimeSpan.FromHours(12)));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
        }
    }

    private ConfigService CreateConfig()
    {
        var config = new ConfigService(_tempDirectory);
        config.Config.AutomaticCollectionRefreshEnabled = true;
        config.Config.CollectionRefreshIntervalHours = 24;
        return config;
    }

    private static CollectionRefreshCoordinator CreateCoordinator(
        ConfigService config,
        IReadOnlyList<CollectionSubscription> subscriptions,
        Func<long, CancellationToken, Task<CollectionRefreshOutcome>> refreshAsync,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        int maxConcurrency = CollectionRefreshCoordinator.DefaultMaxConcurrency)
        => CreateCoordinator(
            config,
            _ => Task.FromResult(subscriptions),
            refreshAsync,
            utcNow,
            delayAsync,
            maxConcurrency);

    private static CollectionRefreshCoordinator CreateCoordinator(
        ConfigService config,
        Func<CancellationToken, Task<IReadOnlyList<CollectionSubscription>>>
            getSubscriptionsAsync,
        Func<long, CancellationToken, Task<CollectionRefreshOutcome>> refreshAsync,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        int maxConcurrency = CollectionRefreshCoordinator.DefaultMaxConcurrency)
        => new(
            config,
            getSubscriptionsAsync,
            refreshAsync,
            utcNow ?? (() => DateTimeOffset.UtcNow),
            delayAsync ?? Task.Delay,
            maxConcurrency);

    private static CollectionSubscription Subscription(
        long id,
        DateTimeOffset? nextCheckUtc = null,
        DateTimeOffset? lastAttemptUtc = null,
        bool autoCheckEnabled = true)
        => new()
        {
            Id = id,
            AutoCheckEnabled = autoCheckEnabled,
            NextCheckUtc = nextCheckUtc,
            LastAttemptUtc = lastAttemptUtc
        };

    private static CollectionRefreshOutcome NoUpdates(long subscriptionId)
    {
        var subscription = Subscription(subscriptionId);
        return CollectionRefreshOutcome.Success(subscriptionId, subscription, []);
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

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
}
