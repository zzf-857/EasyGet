using System.Globalization;
using System.Windows;
using System.Xml.Linq;
using EasyGet.Models;
using EasyGet.Services;
using Xunit;
using ConfigWindowState = EasyGet.Models.WindowState;

namespace EasyGet.Tests;

public class MainWindowStateTests
{
    private static readonly Rect PrimaryWorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void EnsureRestoredBoundsVisible_KeepsPositionOnConnectedSecondaryScreen()
    {
        var requested = new Rect(2100, 120, 1280, 800);
        Rect[] workAreas =
        [
            PrimaryWorkArea,
            new Rect(1920, 0, 1920, 1040)
        ];

        var result = WindowPlacementManager.EnsureRestoredBoundsVisible(
            requested,
            workAreas,
            PrimaryWorkArea);

        Assert.Equal(requested, result);
    }

    [Fact]
    public void EnsureRestoredBoundsVisible_KeepsNegativeCoordinateSecondaryScreen()
    {
        var requested = new Rect(-2200, 0, 1280, 800);
        Rect[] workAreas =
        [
            PrimaryWorkArea,
            new Rect(-2560, -200, 2560, 1400)
        ];

        var result = WindowPlacementManager.EnsureRestoredBoundsVisible(
            requested,
            workAreas,
            PrimaryWorkArea);

        Assert.Equal(requested, result);
    }

    [Fact]
    public void EnsureRestoredBoundsVisible_CentersWindowAfterSecondaryScreenDisconnects()
    {
        var requested = new Rect(2200, 120, 1280, 800);

        var result = WindowPlacementManager.EnsureRestoredBoundsVisible(
            requested,
            [PrimaryWorkArea],
            PrimaryWorkArea);

        Assert.Equal(new Rect(320, 120, 1280, 800), result);
    }

    [Fact]
    public void EnsureRestoredBoundsVisible_RecoversFromGapBetweenOffsetScreens()
    {
        var requested = new Rect(100, -1200, 1280, 800);
        Rect[] workAreas =
        [
            PrimaryWorkArea,
            new Rect(1920, -1440, 2560, 1400)
        ];

        var result = WindowPlacementManager.EnsureRestoredBoundsVisible(
            requested,
            workAreas,
            PrimaryWorkArea);

        Assert.Equal(new Rect(320, 120, 1280, 800), result);
    }

    [Theory]
    [InlineData(-1064, true)]
    [InlineData(-1065, false)]
    public void EnsureRestoredBoundsVisible_RequiresUsableDraggableTitleBar(
        double left,
        bool shouldKeepPosition)
    {
        var requested = new Rect(left, 0, 1280, 800);

        var result = WindowPlacementManager.EnsureRestoredBoundsVisible(
            requested,
            [PrimaryWorkArea],
            PrimaryWorkArea);

        Assert.Equal(
            shouldKeepPosition ? requested : new Rect(320, 120, 1280, 800),
            result);
    }

    [Theory]
    [InlineData(-1596, true)]
    [InlineData(-1597, false)]
    public void EnsureRestoredBoundsVisible_ScalesDraggableThresholdWithDpi(
        double left,
        bool shouldKeepPosition)
    {
        var workArea = new Rect(0, 0, 2880, 1560);
        var requested = new Rect(left, 0, 1920, 1200);

        var result = WindowPlacementManager.EnsureRestoredBoundsVisible(
            requested,
            [workArea],
            workArea,
            dpiScale: 1.5);

        Assert.Equal(
            shouldKeepPosition ? requested : new Rect(480, 180, 1920, 1200),
            result);
    }

    [Fact]
    public void EnsureRestoredBoundsVisible_UsesWorkAreaInsteadOfMonitorBounds()
    {
        var workAreaBelowTopTaskbar = new Rect(0, 48, 1920, 992);
        var requested = new Rect(100, 0, 1280, 800);

        var result = WindowPlacementManager.EnsureRestoredBoundsVisible(
            requested,
            [workAreaBelowTopTaskbar],
            workAreaBelowTopTaskbar);

        Assert.Equal(new Rect(320, 144, 1280, 800), result);
    }

    [Fact]
    public void EnsureRestoredBoundsVisible_FitsOversizedWindowToFallbackWorkArea()
    {
        var requested = new Rect(2200, 100, 3000, 1800);

        var result = WindowPlacementManager.EnsureRestoredBoundsVisible(
            requested,
            [PrimaryWorkArea],
            PrimaryWorkArea);

        Assert.Equal(PrimaryWorkArea, result);
    }

    [Fact]
    public void EnsureRestoredBoundsVisible_FitsVisibleOversizedWindowAfterDpiMove()
    {
        var requested = new Rect(0, 0, 3000, 1800);

        var result = WindowPlacementManager.EnsureRestoredBoundsVisible(
            requested,
            [PrimaryWorkArea],
            PrimaryWorkArea,
            fitOversizedBounds: true);

        Assert.Equal(PrimaryWorkArea, result);
    }

    [Fact]
    public void StartupLoadsConfigurationBeforeCreatingAndShowingMainWindow()
    {
        var appSource = File.ReadAllText(
            TestRepositoryPaths.GetRootPath("App.xaml.cs"));
        var loadIndex = appSource.IndexOf(
            "await configService.LoadAsync()",
            StringComparison.Ordinal);
        var resolveIndex = appSource.IndexOf(
            "GetRequiredService<MainWindow>()",
            StringComparison.Ordinal);
        var showIndex = appSource.IndexOf("mainWindow.Show()", StringComparison.Ordinal);
        var themeIndex = appSource.IndexOf(
            "ThemeManager.ApplyTheme(configService.Config.ThemeColor)",
            StringComparison.Ordinal);

        Assert.True(loadIndex >= 0);
        Assert.True(themeIndex > loadIndex);
        Assert.True(resolveIndex > loadIndex);
        Assert.True(resolveIndex > themeIndex);
        Assert.True(showIndex > resolveIndex);

        var appDocument = XDocument.Load(
            TestRepositoryPaths.GetRootPath("App.xaml"));
        Assert.Null(appDocument.Root?.Attribute("StartupUri"));

        var viewModelSource = File.ReadAllText(
            TestRepositoryPaths.GetRootPath(Path.Combine("ViewModels", "MainViewModel.cs")));
        Assert.DoesNotContain(
            "_configService.LoadAsync()",
            viewModelSource,
            StringComparison.Ordinal);

        var windowSource = File.ReadAllText(
            TestRepositoryPaths.GetRootPath("MainWindow.xaml.cs"));
        var prepareIndex = windowSource.IndexOf(
            "_windowPlacement.PrepareInitialBounds()",
            StringComparison.Ordinal);
        var loadedIndex = windowSource.IndexOf("Loaded +=", StringComparison.Ordinal);
        Assert.True(prepareIndex >= 0);
        Assert.True(loadedIndex > prepareIndex);
    }

    [Fact]
    public void MainWindowUsesManualStartupAndSharedConfiguredDimensions()
    {
        var document = XDocument.Load(
            TestRepositoryPaths.GetRootPath("MainWindow.xaml"));
        var window = document.Root!;

        Assert.Equal("Manual", window.Attribute("WindowStartupLocation")?.Value);
        Assert.Equal(
            ConfigWindowState.DefaultWidth.ToString(CultureInfo.InvariantCulture),
            window.Attribute("Width")?.Value);
        Assert.Equal(
            ConfigWindowState.DefaultHeight.ToString(CultureInfo.InvariantCulture),
            window.Attribute("Height")?.Value);
        Assert.Equal(
            ConfigWindowState.MinWidth.ToString(CultureInfo.InvariantCulture),
            window.Attribute("MinWidth")?.Value);
        Assert.Equal(
            ConfigWindowState.MinHeight.ToString(CultureInfo.InvariantCulture),
            window.Attribute("MinHeight")?.Value);

        var project = XDocument.Load(
            TestRepositoryPaths.GetRootPath("EasyGet.csproj"));
        var highDpiMode = project.Descendants("ApplicationHighDpiMode").Single();
        Assert.Equal("PerMonitorV2", highDpiMode.Value);
    }

    [Fact]
    public void WindowPlacementManagerHooksAndReleasesDisplayTopologyNotifications()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "WindowPlacementManager.cs")));

        Assert.Contains("WM_DISPLAYCHANGE", source, StringComparison.Ordinal);
        Assert.Contains("WM_DPICHANGED", source, StringComparison.Ordinal);
        Assert.Contains("SPI_SETWORKAREA", source, StringComparison.Ordinal);
        Assert.Contains("AddHook", source, StringComparison.Ordinal);
        Assert.Contains("RemoveHook", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherTimer", source, StringComparison.Ordinal);
        Assert.Contains(
            "ScheduleValidation(fitOversizedBounds: true)",
            source,
            StringComparison.Ordinal);
        var initialValidationIndex = source.IndexOf(
            "if (!EnsureVisibleOnCurrentDisplays(",
            StringComparison.Ordinal);
        Assert.True(initialValidationIndex >= 0);
        var initialResizeIndex = source.IndexOf(
            "resizeImmediately: true",
            initialValidationIndex,
            StringComparison.Ordinal);
        var initialFitIndex = source.IndexOf(
            "fitOversizedBounds: true",
            initialValidationIndex,
            StringComparison.Ordinal);
        Assert.True(initialResizeIndex > initialValidationIndex);
        Assert.True(initialFitIndex > initialResizeIndex);

        var windowSource = File.ReadAllText(
            TestRepositoryPaths.GetRootPath("MainWindow.xaml.cs"));
        Assert.Contains(
            "Closed += (_, _) => _windowPlacement.Dispose()",
            windowSource,
            StringComparison.Ordinal);
        Assert.Contains("_windowPlacement.Save()", windowSource, StringComparison.Ordinal);
        Assert.Contains("_window.RestoreBounds", source, StringComparison.Ordinal);
        Assert.Contains("placement.NormalPosition", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeRuntimeConfig_DropsInvalidNativeWindowPlacement()
    {
        var config = new AppConfig
        {
            Window = new ConfigWindowState
            {
                NativePlacement = new NativeWindowPlacement
                {
                    Left = 100,
                    Top = 100,
                    Right = 100,
                    Bottom = 500
                }
            }
        };

        ConfigService.NormalizeRuntimeConfig(config);

        Assert.Null(config.Window.NativePlacement);
    }

    [Fact]
    public async Task ConfigService_RoundTripsValidNativeWindowPlacement()
    {
        using var root = new TestDirectory();
        var service = new ConfigService(root.DirectoryPath);
        service.Config.DefaultDownloadPath = root.Path("downloads");
        service.Config.Window.NativePlacement = new NativeWindowPlacement
        {
            Left = -1800,
            Top = 40,
            Right = -440,
            Bottom = 880
        };

        Assert.True(await service.SaveAsync());

        var reloaded = new ConfigService(root.DirectoryPath);
        await reloaded.LoadAsync();

        var placement = Assert.IsType<NativeWindowPlacement>(
            reloaded.Config.Window.NativePlacement);
        Assert.Equal(-1800, placement.Left);
        Assert.Equal(40, placement.Top);
        Assert.Equal(-440, placement.Right);
        Assert.Equal(880, placement.Bottom);
    }
}
