using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class LongRunningSessionServiceTests
{
    [Fact]
    public void SetActive_IsIdempotentAndDisposeClearsState()
    {
        var calls = new List<uint>();
        var service = new LongRunningSessionService(state =>
        {
            calls.Add(state);
            return state;
        });

        service.SetActive(true);
        service.SetActive(true);
        Assert.True(service.IsActive);

        service.Dispose();
        Assert.False(service.IsActive);

        if (OperatingSystem.IsWindows())
            Assert.Equal(2, calls.Count);
        else
            Assert.Empty(calls);
    }

    [Fact]
    public void SetActive_AfterDisposeThrows()
    {
        var service = new LongRunningSessionService(state => state);
        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.SetActive(true));
    }
}
