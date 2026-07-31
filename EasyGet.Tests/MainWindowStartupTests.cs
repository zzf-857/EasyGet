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

    [Fact]
    public void ShouldHideWindowOnClose_HidesForOrdinaryCloseWhenTrayIsAvailable()
    {
        var shouldHide = MainWindow.ShouldHideWindowOnClose(
            explicitExitRequested: false,
            trayAvailable: true);

        Assert.True(shouldHide);
    }

    [Fact]
    public void ShouldHideWindowOnClose_DoesNotHideForExplicitTrayExit()
    {
        var shouldHide = MainWindow.ShouldHideWindowOnClose(
            explicitExitRequested: true,
            trayAvailable: true);

        Assert.False(shouldHide);
    }

    [Fact]
    public void ShouldHideWindowOnClose_DoesNotHideWhenTrayIsUnavailable()
    {
        var shouldHide = MainWindow.ShouldHideWindowOnClose(
            explicitExitRequested: false,
            trayAvailable: false);

        Assert.False(shouldHide);
    }

    [Fact]
    public void TrayExit_RequestsExplicitApplicationShutdown()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath("MainWindow.xaml.cs"));

        Assert.Contains("_exitRequested = true;", source, StringComparison.Ordinal);
        Assert.Contains("application.Shutdown();", source, StringComparison.Ordinal);
    }
}
