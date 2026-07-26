using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class DynamicConcurrencyGateTests
{
    [Fact]
    public async Task LowerLimit_WaitsForActiveCountToFallBelowLimit()
    {
        var gate = new DynamicConcurrencyGate(initialLimit: 3);
        await gate.WaitAsync();
        await gate.WaitAsync();
        await gate.WaitAsync();

        gate.UpdateLimit(1);
        var waiting = gate.WaitAsync();

        gate.Release();
        Assert.False(waiting.IsCompleted);
        gate.Release();
        Assert.False(waiting.IsCompleted);
        gate.Release();
        Assert.True(waiting.IsCompletedSuccessfully);

        await waiting;
        gate.Release();
    }

    [Fact]
    public async Task RaiseLimit_ImmediatelyGrantsAvailableSlots()
    {
        var gate = new DynamicConcurrencyGate(initialLimit: 1);
        await gate.WaitAsync();
        var second = gate.WaitAsync();
        var third = gate.WaitAsync();
        var fourth = gate.WaitAsync();

        gate.UpdateLimit(3);

        Assert.True(second.IsCompletedSuccessfully);
        Assert.True(third.IsCompletedSuccessfully);
        Assert.False(fourth.IsCompleted);

        gate.Release();
        Assert.True(fourth.IsCompletedSuccessfully);

        await Task.WhenAll(second, third, fourth);
        gate.Release();
        gate.Release();
        gate.Release();
    }

    [Fact]
    public async Task RapidLimitChanges_DoNotOverGrant()
    {
        var gate = new DynamicConcurrencyGate(initialLimit: 1);
        await gate.WaitAsync();
        var waiters = Enumerable.Range(0, 111)
            .Select(_ => gate.WaitAsync())
            .ToArray();

        for (var cycle = 1; cycle <= 10; cycle++)
        {
            gate.UpdateLimit(12);
            Assert.Equal(cycle * 11, waiters.Count(task => task.IsCompletedSuccessfully));

            gate.UpdateLimit(1);
            for (var release = 0; release < 11; release++)
                gate.Release();

            Assert.Equal(cycle * 11, waiters.Count(task => task.IsCompletedSuccessfully));
        }

        Assert.False(waiters[^1].IsCompleted);
        gate.Release();
        Assert.True(waiters[^1].IsCompletedSuccessfully);

        await Task.WhenAll(waiters);
        gate.Release();
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotConsumeSlot()
    {
        var gate = new DynamicConcurrencyGate(initialLimit: 1);
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        var cancelledWaiter = gate.WaitAsync(cancellation.Token);
        var nextWaiter = gate.WaitAsync();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await cancelledWaiter);
        gate.Release();
        Assert.True(nextWaiter.IsCompletedSuccessfully);

        await nextWaiter;
        gate.Release();
    }
}
