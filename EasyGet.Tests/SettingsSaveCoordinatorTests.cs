using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public sealed class SettingsSaveCoordinatorTests
{
    private static readonly TimeSpan DeferredSave = TimeSpan.FromHours(1);

    [Fact]
    public async Task Flush_CoalescesPendingEditsAndDoesNotRepeatCompletedWrites()
    {
        var saveCount = 0;
        var coordinator = new SettingsSaveCoordinator(_ =>
        {
            saveCount++;
            return Task.FromResult(true);
        }, DeferredSave);

        Assert.True(await coordinator.FlushAsync());
        Assert.Equal(0, saveCount);
        for (var index = 0; index < 50; index++)
            coordinator.RequestAutoSave();

        Assert.True(await coordinator.FlushAsync());
        Assert.Equal(1, saveCount);
        Assert.True(await coordinator.FlushAsync());
        Assert.Equal(1, saveCount);
    }

    [Fact]
    public async Task Flush_RetriesFailedWritesWithoutLosingPendingEdits()
    {
        var saveCount = 0;
        var coordinator = new SettingsSaveCoordinator(
            _ => Task.FromResult(++saveCount > 1), DeferredSave);
        coordinator.RequestAutoSave();

        Assert.False(await coordinator.FlushAsync());
        Assert.True(await coordinator.FlushAsync());
        Assert.Equal(2, saveCount);
        Assert.True(await coordinator.FlushAsync());
        Assert.Equal(2, saveCount);
    }

    [Fact]
    public async Task Flush_PersistsEditsRequestedDuringTheWrite()
    {
        var saveCount = 0;
        SettingsSaveCoordinator coordinator = null!;
        coordinator = new SettingsSaveCoordinator(_ =>
        {
            if (++saveCount == 1)
                coordinator.RequestAutoSave();
            return Task.FromResult(true);
        }, DeferredSave);
        coordinator.RequestAutoSave();

        Assert.True(await coordinator.FlushAsync());
        Assert.Equal(2, saveCount);
        Assert.True(await coordinator.FlushAsync());
        Assert.Equal(2, saveCount);
    }

    [Fact]
    public async Task ExplicitSave_DoesNotAcknowledgeEditsRequestedDuringTheWrite()
    {
        var intents = new List<SettingsSaveIntent>();
        SettingsSaveCoordinator coordinator = null!;
        coordinator = new SettingsSaveCoordinator(intent =>
        {
            intents.Add(intent);
            if (intent == SettingsSaveIntent.Explicit)
                coordinator.RequestAutoSave();
            return Task.FromResult(true);
        }, DeferredSave);
        coordinator.RequestAutoSave();

        await coordinator.SaveExplicitAsync();
        Assert.True(await coordinator.FlushAsync());
        Assert.Equal([SettingsSaveIntent.Explicit, SettingsSaveIntent.Automatic], intents);
    }

    [Fact]
    public async Task ExplicitSave_CancelsRedundantAutomaticSave()
    {
        var intents = new List<SettingsSaveIntent>();
        var coordinator = new SettingsSaveCoordinator(intent =>
        {
            intents.Add(intent);
            return Task.FromResult(true);
        }, DeferredSave);
        coordinator.RequestAutoSave();

        await coordinator.SaveExplicitAsync();
        Assert.True(await coordinator.FlushAsync());
        Assert.Equal([SettingsSaveIntent.Explicit], intents);
    }

    [Fact]
    public async Task SaveRequests_DoNotRunPersistenceConcurrently()
    {
        var releaseFirstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCount = 0;
        var coordinator = new SettingsSaveCoordinator(async _ =>
        {
            if (++saveCount == 1)
                await releaseFirstSave.Task;
            return true;
        }, DeferredSave);

        var firstSave = coordinator.SaveExplicitAsync();
        var secondSave = coordinator.SaveExplicitAsync();
        Assert.Equal(1, saveCount);
        Assert.False(secondSave.IsCompleted);

        releaseFirstSave.SetResult();
        await Task.WhenAll(firstSave, secondSave).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, saveCount);
    }
}
