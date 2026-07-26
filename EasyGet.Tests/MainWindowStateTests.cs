using System.Windows;
using Xunit;

namespace EasyGet.Tests;

public class MainWindowStateTests
{
    private static readonly Rect PrimaryWorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void EnsureRestoredBoundsVisible_KeepsPositionOnConnectedSecondaryScreen()
    {
        var requested = new Rect(2100, 120, 1280, 800);
        var virtualScreen = new Rect(0, 0, 3840, 1080);

        var result = MainWindow.EnsureRestoredBoundsVisible(
            requested,
            virtualScreen,
            PrimaryWorkArea);

        Assert.Equal(requested, result);
    }

    [Fact]
    public void EnsureRestoredBoundsVisible_CentersWindowAfterSecondaryScreenDisconnects()
    {
        var requested = new Rect(2200, 120, 1280, 800);

        var result = MainWindow.EnsureRestoredBoundsVisible(
            requested,
            new Rect(0, 0, 1920, 1080),
            PrimaryWorkArea);

        Assert.Equal(new Rect(320, 120, 1280, 800), result);
    }

    [Fact]
    public void EnsureRestoredBoundsVisible_KeepsWindowWithDraggableTitleBarArea()
    {
        var requested = new Rect(-1180, 0, 1280, 800);

        var result = MainWindow.EnsureRestoredBoundsVisible(
            requested,
            new Rect(0, 0, 1920, 1080),
            PrimaryWorkArea);

        Assert.Equal(requested, result);
    }

    [Fact]
    public void EnsureRestoredBoundsVisible_RecoversWindowWithHiddenTitleBar()
    {
        var requested = new Rect(100, 1100, 1280, 800);

        var result = MainWindow.EnsureRestoredBoundsVisible(
            requested,
            new Rect(0, 0, 1920, 1080),
            PrimaryWorkArea);

        Assert.Equal(new Rect(320, 120, 1280, 800), result);
    }
}
