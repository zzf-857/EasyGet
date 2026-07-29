using Xunit;

namespace EasyGet.Tests;

public class MainWindowStartupTests
{
    [Fact]
    public async Task InitializeLoadedServicesAsync_InitializesMainBeforeTray()
    {
        var calls = new List<string>();

        await MainWindow.InitializeLoadedServicesAsync(
            () =>
            {
                calls.Add("main");
                return Task.CompletedTask;
            },
            () =>
            {
                calls.Add("tray");
                return true;
            });

        Assert.Equal(["main", "tray"], calls);
    }

    [Fact]
    public async Task InitializeLoadedServicesAsync_IgnoresTrayFailureAfterMainInitialization()
    {
        var mainInitialized = false;

        await MainWindow.InitializeLoadedServicesAsync(
            () =>
            {
                mainInitialized = true;
                return Task.CompletedTask;
            },
            () => throw new EntryPointNotFoundException("tray unavailable"));

        Assert.True(mainInitialized);
    }
}
