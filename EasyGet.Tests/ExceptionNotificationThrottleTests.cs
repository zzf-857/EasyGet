using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public sealed class ExceptionNotificationThrottleTests
{
    [Fact]
    public void ShouldNotify_SuppressesIdenticalExceptionInsideWindow()
    {
        var throttle = new ExceptionNotificationThrottle(TimeSpan.FromMinutes(1));
        var now = DateTime.UnixEpoch;
        var error = new InvalidOperationException(
            "Cannot bind read-only DownloadTask.StatusText",
            new InvalidOperationException("TwoWay binding is not allowed"));

        var first = throttle.ShouldNotify(error, "DispatcherUnhandledException", now);
        var duplicate = throttle.ShouldNotify(
            error,
            "DispatcherUnhandledException",
            now.AddSeconds(5));
        var afterWindow = throttle.ShouldNotify(
            error,
            "DispatcherUnhandledException",
            now.AddMinutes(1));

        Assert.True(first);
        Assert.False(duplicate);
        Assert.True(afterWindow);
    }

    [Fact]
    public void ShouldNotify_AllowsDistinctErrorsAndSources()
    {
        var throttle = new ExceptionNotificationThrottle(TimeSpan.FromMinutes(1));
        var now = DateTime.UnixEpoch;

        Assert.True(throttle.ShouldNotify(
            new InvalidOperationException("first"),
            "DispatcherUnhandledException",
            now));
        Assert.True(throttle.ShouldNotify(
            new InvalidOperationException("second"),
            "DispatcherUnhandledException",
            now));
        Assert.True(throttle.ShouldNotify(
            new InvalidOperationException("first"),
            "Startup Error",
            now));
    }

    [Fact]
    public async Task ShouldNotify_IsThreadSafeForExceptionBurst()
    {
        var throttle = new ExceptionNotificationThrottle(TimeSpan.FromMinutes(1));
        var now = DateTime.UnixEpoch;
        var notifications = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(_ => Task.Run(() =>
                throttle.ShouldNotify(
                    new InvalidOperationException("same binding failure"),
                    "DispatcherUnhandledException",
                    now))));

        Assert.Single(notifications, shouldNotify => shouldNotify);
    }
}
