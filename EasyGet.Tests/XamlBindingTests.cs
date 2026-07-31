using System.Xml.Linq;
using Xunit;

namespace EasyGet.Tests;

public class XamlBindingTests
{
    [Theory]
    [InlineData("DownloadView.xaml")]
    [InlineData("BatchDownloadView.xaml")]
    [InlineData("HistoryView.xaml")]
    [InlineData("SettingsView.xaml")]
    public void PrimaryViewsUseDesignerWorkbenchSurfacesWithoutLegacyPanelCards(string viewFileName)
    {
        var source = File.ReadAllText(GetViewPath(viewFileName));

        Assert.True(
            source.Contains("BgPrimaryBrush", StringComparison.Ordinal)
            || source.Contains("BgSurfaceBrush", StringComparison.Ordinal)
            || source.Contains("BgSidebarBrush", StringComparison.Ordinal));
        Assert.DoesNotContain("ToolPanelBorder", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DownloadView.xaml")]
    [InlineData("BatchDownloadView.xaml")]
    [InlineData("HistoryView.xaml")]
    [InlineData("SettingsView.xaml")]
    public void InlineRunOutputBindingsAreExplicitlyOneWay(string viewFileName)
    {
        var document = XDocument.Load(GetViewPath(viewFileName));
        var unsafeBindings = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Run")
            .Select(element => element.Attribute("Text")?.Value ?? "")
            .Where(value => value.StartsWith("{Binding ", StringComparison.Ordinal))
            .Where(value => !value.Contains("Mode=OneWay", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            unsafeBindings.Count == 0,
            $"Run.Text output bindings in {viewFileName} must be one-way: "
                + string.Join("; ", unsafeBindings));
    }

    [Theory]
    [InlineData("DownloadView.xaml")]
    [InlineData("BatchDownloadView.xaml")]
    [InlineData("HistoryView.xaml")]
    [InlineData("SettingsView.xaml")]
    public void PrimaryPageRootsDoNotReplayEntryMotionOnNavigation(string viewFileName)
    {
        var document = XDocument.Load(GetViewPath(viewFileName));

        Assert.Equal("UserControl", document.Root?.Name.LocalName);
        Assert.DoesNotContain(document.Root!.Attributes(), attribute =>
            attribute.Name.LocalName == "Motion.PageEnter");
    }

    [Fact]
    public void MainWindowMatchesDesignerShellDimensionsAndTruthfulStatusBindings()
    {
        var document = XDocument.Load(GetRootPath("MainWindow.xaml"));
        var window = document.Root!;
        var source = document.ToString(SaveOptions.DisableFormatting);

        Assert.Equal("1360", window.Attribute("Width")?.Value);
        Assert.Equal("840", window.Attribute("Height")?.Value);
        Assert.Equal("1080", window.Attribute("MinWidth")?.Value);
        Assert.Equal("680", window.Attribute("MinHeight")?.Value);
        Assert.Equal("None", window.Attribute("WindowStyle")?.Value);

        var rowHeights = document.Descendants()
            .Where(element => element.Name.LocalName == "RowDefinition")
            .Select(element => element.Attribute("Height")?.Value)
            .ToList();
        Assert.Contains("48", rowHeights);
        Assert.Contains("32", rowHeights);

        Assert.Contains("Width=\"{Binding SidebarWidth}\"", source, StringComparison.Ordinal);
        Assert.Contains("TaskStatusText", source, StringComparison.Ordinal);
        Assert.Contains("AggregateSpeedText", source, StringComparison.Ordinal);
        Assert.Contains("DiskStatusText", source, StringComparison.Ordinal);
        Assert.Contains("EngineVersionText", source, StringComparison.Ordinal);
        Assert.Contains("ToolStatusText", source, StringComparison.Ordinal);
        Assert.Contains("AppVersion", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowUsesResponsive216And56PixelSidebarAt1280Breakpoint()
    {
        var viewModel = File.ReadAllText(GetRootPath(Path.Combine("ViewModels", "MainViewModel.cs")));
        var codeBehind = File.ReadAllText(GetRootPath("MainWindow.xaml.cs"));

        Assert.Contains("IsCompactLayout ? 56 : 216", viewModel, StringComparison.Ordinal);
        Assert.Contains("ActualWidth < 1280", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Width < 1280", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowCachesPrimaryPagesAndKeepsLayoutDiagnosticsOptIn()
    {
        var document = XDocument.Load(GetRootPath("MainWindow.xaml"));
        var window = document.Root!;
        var cachedHost = document.Descendants().Single(element =>
            element.Name.LocalName == "CachedPageHost");

        Assert.Equal("False", window.Attributes().Single(attribute =>
            attribute.Name.LocalName == "LayoutDiagnostics.IsEnabled").Value);
        Assert.Equal("{Binding CurrentPage}", cachedHost.Attribute("Page")?.Value);
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName == "ContentControl"
            && element.Attribute("Content")?.Value == "{Binding CurrentPage}");
    }

    [Fact]
    public void MainWindowSidebarUsesSubtleDividerAndRealEngineFooter()
    {
        var document = XDocument.Load(GetRootPath("MainWindow.xaml"));
        var sidebar = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Border"
            && element.Attribute("Background")?.Value == "{StaticResource BgSidebarBrush}"
            && element.Attribute("BorderThickness")?.Value == "0,0,1,0");

        Assert.NotNull(sidebar);
        Assert.Equal("{StaticResource BorderSubtleBrush}", sidebar!.Attribute("BorderBrush")?.Value);
        Assert.DoesNotContain(sidebar.Descendants().Attributes("Text"), attribute =>
            attribute.Value.Contains("Power User", StringComparison.OrdinalIgnoreCase));

        var statusRow = document.Descendants().First(element =>
            element.Name.LocalName == "Grid"
            && element.Attribute("Grid.Row")?.Value == "2");
        var engineFooter = statusRow.Elements().First(element =>
            element.Name.LocalName == "Border"
            && element.Attribute("Grid.Column")?.Value == "0");

        Assert.Contains(engineFooter.Descendants().Attributes("Text"), attribute =>
            attribute.Value.Contains("ToolStatusText", StringComparison.Ordinal));
        Assert.Contains(engineFooter.Descendants(), element =>
            element.Name.LocalName == "Button"
            && element.Attribute("CommandParameter")?.Value == "settings");
    }

    [Fact]
    public void MainWindowTitleBarUsesApplicationBrandAsset()
    {
        var document = XDocument.Load(GetRootPath("MainWindow.xaml"));
        var image = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Image"
            && element.Attribute("Source")?.Value == "/Assets/app.png");

        Assert.NotNull(image);
        Assert.Equal("Uniform", image!.Attribute("Stretch")?.Value);
        Assert.Contains(document.Descendants().Attributes("Text"), attribute => attribute.Value == "EasyGet");
    }

    [Fact]
    public void MainWindowNavigationContainsOnlyFourOrderedDestinations()
    {
        var document = XDocument.Load(GetRootPath("MainWindow.xaml"));
        var navItems = document.Descendants()
            .Where(element => element.Name.LocalName == "RadioButton")
            .Where(element => element.Attribute("CommandParameter") is not null)
            .Select(element => new
            {
                Page = element.Attribute("CommandParameter")?.Value ?? "",
                Binding = element.Attribute("IsChecked")?.Value ?? ""
            })
            .ToList();

        var expected = new[]
        {
            ("download", "ConverterParameter=0"),
            ("batch", "ConverterParameter=1"),
            ("history", "ConverterParameter=2"),
            ("settings", "ConverterParameter=3")
        };

        Assert.Equal(expected.Length, navItems.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Item1, navItems[i].Page);
            Assert.Contains(expected[i].Item2, navItems[i].Binding);
        }
    }

    [Theory]
    [InlineData("download")]
    [InlineData("batch")]
    [InlineData("history")]
    [InlineData("settings")]
    public void MainWindowNavigationItemsExposeTooltipAndAutomationName(string commandParameter)
    {
        var document = XDocument.Load(GetRootPath("MainWindow.xaml"));
        var navItem = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "RadioButton"
            && element.Attribute("CommandParameter")?.Value == commandParameter);

        Assert.NotNull(navItem);
        Assert.False(string.IsNullOrWhiteSpace(navItem!.Attribute("ToolTip")?.Value));
        Assert.False(string.IsNullOrWhiteSpace(AutomationName(navItem)));
    }

    [Fact]
    public void MainWindowToastStackIsTopRightAndSupportsRecoveryActions()
    {
        var document = XDocument.Load(GetRootPath("MainWindow.xaml"));
        var itemsControl = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "ItemsControl"
            && element.Attribute("ItemsSource")?.Value == "{Binding Notifications}");

        Assert.NotNull(itemsControl);
        Assert.Equal("Right", itemsControl!.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("Top", itemsControl.Attribute("VerticalAlignment")?.Value);
        Assert.Contains("ExecuteActionCommand", itemsControl.ToString(), StringComparison.Ordinal);
        Assert.Contains("ActionLabel", itemsControl.ToString(), StringComparison.Ordinal);
        Assert.Contains("CloseCommand", itemsControl.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowRequestsDarkSystemTitleBar()
    {
        var source = File.ReadAllText(GetRootPath("MainWindow.xaml.cs"));

        Assert.Contains("SourceInitialized", source, StringComparison.Ordinal);
        Assert.Contains("DwmSetWindowAttribute", source, StringComparison.Ordinal);
        Assert.Contains("DWMWA_USE_IMMERSIVE_DARK_MODE", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadViewUsesProgressiveSingleWorkspaceStates()
    {
        var source = File.ReadAllText(GetViewPath("DownloadView.xaml"));

        Assert.Contains("IsIdle", source, StringComparison.Ordinal);
        Assert.Contains("IsParsing", source, StringComparison.Ordinal);
        Assert.Contains("IsReady", source, StringComparison.Ordinal);
        Assert.Contains("IsScheduled", source, StringComparison.Ordinal);
        Assert.Contains("IsDownloadActive", source, StringComparison.Ordinal);
        Assert.Contains("IsCompleted", source, StringComparison.Ordinal);
        Assert.Contains("IsFailed", source, StringComparison.Ordinal);
        Assert.Contains("StartDownloadCommand", source, StringComparison.Ordinal);
        Assert.Contains("OpenCurrentFolderCommand", source, StringComparison.Ordinal);
        Assert.Contains("PlayCurrentFileCommand", source, StringComparison.Ordinal);
        Assert.Contains("RetryCurrentDownloadCommand", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadViewUsesReadableParameterToolbarAndGlobalClipboardState()
    {
        var document = XDocument.Load(GetViewPath("DownloadView.xaml"));
        var source = document.ToString(SaveOptions.DisableFormatting);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var optionsPanel = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Border"
            && element.Attribute(x + "Name")?.Value == "DownloadOptionsPanel");

        Assert.NotNull(optionsPanel);
        Assert.Null(optionsPanel!.Attribute("Height"));
        Assert.True(optionsPanel.Descendants().Count(element =>
            element.Name.LocalName == "RowDefinition") >= 2);
        Assert.DoesNotContain(optionsPanel.Descendants(), element =>
            element.Name.LocalName == "DockPanel");
        Assert.Contains("ConcurrentFragmentsText", source, StringComparison.Ordinal);
        Assert.Contains("ProxyStatusText", source, StringComparison.Ordinal);
        Assert.Contains("DownloadDirectory", source, StringComparison.Ordinal);
        Assert.Contains("SourceFormatOptions", source, StringComparison.Ordinal);
        Assert.Contains("SelectedSourceFormat", source, StringComparison.Ordinal);
        Assert.Contains("IsScheduledDownloadEnabled", source, StringComparison.Ordinal);
        Assert.Contains("ScheduledStartText", source, StringComparison.Ordinal);
        Assert.Contains("ScheduleValidationMessage", source, StringComparison.Ordinal);
        Assert.Contains("ClipboardMonitoringEnabled", source, StringComparison.Ordinal);
        Assert.Contains("RunPrimaryActionCommand", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowClipboardPrompt", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowToastSupportsInformationNotificationsAndActions()
    {
        var source = File.ReadAllText(GetRootPath("MainWindow.xaml"));

        Assert.Contains("IsInfo", source, StringComparison.Ordinal);
        Assert.Contains("检测到链接", source, StringComparison.Ordinal);
        Assert.Contains("ExecuteActionCommand", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadViewLogDrawerSupportsSelectionCopyAndClear()
    {
        var document = XDocument.Load(GetViewPath("DownloadView.xaml"));
        var source = document.ToString(SaveOptions.DisableFormatting);
        var logTextBox = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "TextBox"
            && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == "LogTextBox"));

        Assert.NotNull(logTextBox);
        Assert.Equal("True", logTextBox!.Attribute("IsReadOnly")?.Value);
        Assert.Equal("Auto", logTextBox.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.Equal("Auto", logTextBox.Attribute("HorizontalScrollBarVisibility")?.Value);
        Assert.Contains("LogExpander", source, StringComparison.Ordinal);
        Assert.Contains("CopyLogCommand", source, StringComparison.Ordinal);
        Assert.Contains("ClearLogCommand", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadViewRecentHistoryOpenActionPassesAvailableFilePath()
    {
        var document = XDocument.Load(GetViewPath("DownloadView.xaml"));
        var button = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Button"
            && (element.Attribute("Command")?.Value ?? "")
                .Contains("HistoryVM.OpenFolderCommand", StringComparison.Ordinal));

        Assert.NotNull(button);
        Assert.Equal("{Binding AvailableFilePath}", button!.Attribute("CommandParameter")?.Value);
    }

    [Theory]
    [InlineData("PasteUrlCommand")]
    [InlineData("StartDownloadCommand")]
    [InlineData("BrowseDirectoryCommand")]
    [InlineData("CancelDownloadCommand")]
    [InlineData("CopyLogCommand")]
    [InlineData("ClearLogCommand")]
    public void DownloadViewActionButtonsExposeTooltipAndAutomationName(string commandName)
    {
        var document = XDocument.Load(GetViewPath("DownloadView.xaml"));
        var button = FindButtonByCommand(document, commandName);

        Assert.NotNull(button);
        Assert.False(string.IsNullOrWhiteSpace(button!.Attribute("ToolTip")?.Value));
        Assert.False(string.IsNullOrWhiteSpace(AutomationName(button)));
    }

    [Fact]
    public void BatchDownloadViewUsesResizableImportRailAndReadableQueueRows()
    {
        var document = XDocument.Load(GetViewPath("BatchDownloadView.xaml"));
        var source = document.ToString(SaveOptions.DisableFormatting);
        var rootGrid = document.Root!.Elements()
            .Single(element => element.Name.LocalName == "Grid");
        var columns = rootGrid.Elements()
            .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .Where(element => element.Name.LocalName == "ColumnDefinition")
            .ToArray();

        Assert.Collection(
            columns,
            column =>
            {
                Assert.Equal("400", column.Attribute("Width")?.Value);
                Assert.Equal("320", column.Attribute("MinWidth")?.Value);
                Assert.Equal("520", column.Attribute("MaxWidth")?.Value);
            },
            column => Assert.Equal("6", column.Attribute("Width")?.Value),
            column =>
            {
                Assert.Equal("*", column.Attribute("Width")?.Value);
                Assert.Equal("560", column.Attribute("MinWidth")?.Value);
            });

        var splitter = rootGrid.Elements()
            .Single(element => element.Name.LocalName == "GridSplitter");
        Assert.Equal("1", splitter.Attribute("Grid.Column")?.Value);
        Assert.Equal("Columns", splitter.Attribute("ResizeDirection")?.Value);
        Assert.Equal("PreviousAndNext", splitter.Attribute("ResizeBehavior")?.Value);
        Assert.Equal("True", splitter.Attribute("ShowsPreview")?.Value);
        Assert.Equal("16", splitter.Attribute("KeyboardIncrement")?.Value);
        Assert.Equal("True", splitter.Attribute("IsTabStop")?.Value);

        var queueSurface = rootGrid.Elements()
            .Single(element => element.Name.LocalName == "Grid"
                               && element.Attribute("Grid.Column")?.Value == "2");
        Assert.NotNull(queueSurface);
        var overlay = rootGrid.Elements()
            .Single(element => element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "DragDropOverlay");
        Assert.Equal("3", overlay.Attribute("Grid.ColumnSpan")?.Value);
        Assert.Contains("VisibleQueueTasks", source, StringComparison.Ordinal);
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "Setter"
            && element.Attribute("Property")?.Value == "Height"
            && element.Attribute("Value")?.Value == "68");

        var queueSummary = document.Descendants().Single(element =>
            element.Name.LocalName == "TextBlock"
            && element.Attribute("Text")?.Value == "{Binding QueueSummaryText}");
        Assert.Equal("NoWrap", queueSummary.Attribute("TextWrapping")?.Value);
        Assert.Equal("CharacterEllipsis", queueSummary.Attribute("TextTrimming")?.Value);
        Assert.Equal("{Binding QueueSummaryText}", queueSummary.Attribute("ToolTip")?.Value);
        Assert.NotEqual("Horizontal", queueSummary.Parent?.Attribute("Orientation")?.Value);
    }

    [Fact]
    public void BatchDownloadViewExposesTargetDirectoryAndExistingCollectionPicker()
    {
        var source = File.ReadAllText(GetViewPath("BatchDownloadView.xaml"));

        Assert.Contains("{Binding DownloadDirectory}", source, StringComparison.Ordinal);
        Assert.Contains("{Binding BrowseDirectoryCommand}", source, StringComparison.Ordinal);
        Assert.Contains("{Binding ExistingCollectionFolders}", source, StringComparison.Ordinal);
        Assert.Contains("{Binding SelectedCollectionFolder}", source, StringComparison.Ordinal);
        Assert.Contains("{Binding RefreshExistingCollectionFoldersCommand}", source, StringComparison.Ordinal);
        Assert.Contains("{Binding ClearSelectedCollectionFolderCommand}", source, StringComparison.Ordinal);
        Assert.Contains("DisplayMemberPath=\"DisplayName\"", source, StringComparison.Ordinal);
        Assert.Contains("{Binding CanSelectExistingCollectionFolder}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchDownloadQueueUsesRecyclingAndDedicatedThumbnailColumn()
    {
        var document = XDocument.Load(GetViewPath("BatchDownloadView.xaml"));
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var queue = document.Descendants()
            .Single(element => element.Name.LocalName == "ListBox"
                               && element.Attribute(x + "Name")?.Value == "QueueList");

        Assert.Equal("True", queue.Attribute("VirtualizingPanel.IsVirtualizing")?.Value);
        Assert.Equal("Recycling", queue.Attribute("VirtualizingPanel.VirtualizationMode")?.Value);
        Assert.Equal("True", queue.Attribute("ScrollViewer.CanContentScroll")?.Value);

        var itemGrid = queue.Descendants()
            .First(element => element.Name.LocalName == "Grid"
                              && element.Elements().Any(child => child.Name.LocalName == "Grid.ColumnDefinitions"));
        var columns = itemGrid.Elements()
            .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .Where(element => element.Name.LocalName == "ColumnDefinition")
            .Select(element => element.Attribute("Width")?.Value)
            .ToArray();
        Assert.Equal(new[] { "20", "92", "*", "168", "120" }, columns);

        var thumbnail = itemGrid.Descendants()
            .Single(element => element.Name.LocalName == "Image"
                               && element.Attribute("Source")?.Value == "{Binding ThumbnailUrl}");
        Assert.Equal("80", thumbnail.Attribute("Width")?.Value);
        Assert.Equal("45", thumbnail.Attribute("Height")?.Value);
        Assert.Equal("UniformToFill", thumbnail.Attribute("Stretch")?.Value);
        Assert.Equal("Center", thumbnail.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("Center", thumbnail.Attribute("VerticalAlignment")?.Value);
        Assert.Equal("HighQuality", thumbnail.Attribute("RenderOptions.BitmapScalingMode")?.Value);
        Assert.Equal("1", thumbnail.Parent?.Parent?.Attribute("Grid.Column")?.Value);
        Assert.Equal("True", thumbnail.Parent?.Attribute("ClipToBounds")?.Value);
        Assert.Equal("True", thumbnail.Parent?.Parent?.Attribute("ClipToBounds")?.Value);

        var progressHost = itemGrid.Elements().Single(element =>
            element.Name.LocalName == "Grid"
            && element.Attribute("Grid.Column")?.Value == "3");
        var progressColumns = progressHost.Elements()
            .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .Select(element => element.Attribute("Width")?.Value)
            .ToArray();
        var progressRows = progressHost.Elements()
            .Single(element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .Select(element => element.Attribute("Height")?.Value)
            .ToArray();
        Assert.Equal(new[] { "*", "44" }, progressColumns);
        Assert.Equal(new[] { "18", "*" }, progressRows);

        var progressBar = progressHost.Elements().Single(element => element.Name.LocalName == "ProgressBar");
        Assert.Equal("1", progressBar.Attribute("Grid.Row")?.Value);
        Assert.Equal("2", progressBar.Attribute("Grid.ColumnSpan")?.Value);
        Assert.Equal("Stretch", progressBar.Attribute("HorizontalAlignment")?.Value);

        var actionHost = itemGrid.Elements().Single(element =>
            element.Name.LocalName == "StackPanel"
            && element.Attribute("Grid.Column")?.Value == "4");
        Assert.Equal("Right", actionHost.Attribute("HorizontalAlignment")?.Value);
    }

    [Fact]
    public void BatchDownloadViewExposesAllDesignerQueueFilters()
    {
        var document = XDocument.Load(GetViewPath("BatchDownloadView.xaml"));
        var filters = document.Descendants()
            .Where(element => element.Name.LocalName == "RadioButton")
            .Where(element => (element.Attribute("Command")?.Value ?? "")
                .Contains("SetQueueFilterCommand", StringComparison.Ordinal))
            .Select(element => element.Attribute("CommandParameter")?.Value)
            .ToList();

        Assert.Equal(new[] { "全部", "进行中", "等待", "计划", "已暂停", "失败", "已完成" }, filters);
    }

    [Fact]
    public void BatchDownloadViewUsesStatusRelevantRowActions()
    {
        var source = File.ReadAllText(GetViewPath("BatchDownloadView.xaml"));

        Assert.Contains("OpenTaskFolderCommand", source, StringComparison.Ordinal);
        Assert.Contains("PauseTaskCommand", source, StringComparison.Ordinal);
        Assert.Contains("ResumeTaskCommand", source, StringComparison.Ordinal);
        Assert.Contains("RetryTaskCommand", source, StringComparison.Ordinal);
        Assert.Contains("CancelTaskCommand", source, StringComparison.Ordinal);
        Assert.Contains("Value=\"Downloading\"", source, StringComparison.Ordinal);
        Assert.Contains("Value=\"Paused\"", source, StringComparison.Ordinal);
        Assert.Contains("Value=\"Failed\"", source, StringComparison.Ordinal);
        Assert.Contains("Value=\"Completed\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchDownloadViewShowsAggregateProgressAndOverflowCleanupActions()
    {
        var source = File.ReadAllText(GetViewPath("BatchDownloadView.xaml"));

        Assert.Contains("OverallProgress", source, StringComparison.Ordinal);
        Assert.Contains("QueueSummaryText", source, StringComparison.Ordinal);
        Assert.Contains("AggregateSpeedText", source, StringComparison.Ordinal);
        Assert.Contains("RetryFailedCommand", source, StringComparison.Ordinal);
        Assert.Contains("CancelAllCommand", source, StringComparison.Ordinal);
        Assert.Contains("ClearFinishedCommand", source, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding OverallProgress, Mode=OneWay}\"", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BatchDownloadView.xaml")]
    [InlineData("HistoryView.xaml")]
    public void PlatformLabelsHideWhenPlatformIsEmpty(string viewFileName)
    {
        var document = XDocument.Load(GetViewPath(viewFileName));
        var bindings = document.Descendants()
            .Attributes("Visibility")
            .Select(attribute => attribute.Value)
            .Where(value => value.Contains("Binding Platform", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(bindings);
        Assert.All(bindings, binding => Assert.Contains("StringToVisibility", binding));
    }

    [Fact]
    public void HistoryViewUsesReadableFolderRailAndCompactDropdownFallback()
    {
        var document = XDocument.Load(GetViewPath("HistoryView.xaml"));
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var source = document.ToString(SaveOptions.DisableFormatting);
        var codeBehind = File.ReadAllText(GetViewPath("HistoryView.xaml.cs"));

        var rail = document.Descendants().Single(element =>
            element.Name.LocalName == "Border"
            && element.Attribute(x + "Name")?.Value == "HistoryFolderRailHost");
        Assert.Equal("268", rail.Attribute("Width")?.Value);
        Assert.Equal("240", rail.Attribute("MinWidth")?.Value);
        Assert.Equal("420", rail.Attribute("MaxWidth")?.Value);
        Assert.Contains("HistoryFolderRail", rail.Attribute("Style")?.Value ?? "", StringComparison.Ordinal);

        var resizeThumb = document.Descendants().Single(element =>
            element.Name.LocalName == "Thumb"
            && element.Attribute(x + "Name")?.Value == "HistoryFolderRailResizeThumb");
        Assert.Equal("6", resizeThumb.Attribute("Width")?.Value);
        Assert.Equal("HistoryFolderRailResizeThumb_DragDelta", resizeThumb.Attribute("DragDelta")?.Value);
        Assert.Equal("HistoryFolderRailResizeThumb_PreviewKeyDown", resizeThumb.Attribute("PreviewKeyDown")?.Value);
        Assert.Equal("True", resizeThumb.Attribute("IsTabStop")?.Value);
        Assert.Contains("HistoryFolderRailResizeThumb", resizeThumb.Attribute("Style")?.Value ?? "", StringComparison.Ordinal);
        Assert.Contains("Cursor=\"SizeWE\"", source, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp", codeBehind, StringComparison.Ordinal);
        Assert.Contains("HistoryFolderRailMinWidth = 240", codeBehind, StringComparison.Ordinal);
        Assert.Contains("HistoryFolderRailMaxWidth = 420", codeBehind, StringComparison.Ordinal);

        var compactFolders = document.Descendants().Single(element =>
            element.Name.LocalName == "Grid"
            && (element.Attribute("Style")?.Value ?? "").Contains("CompactHistoryFolders", StringComparison.Ordinal));
        var compactColumns = compactFolders.Elements()
            .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .Where(element => element.Name.LocalName == "ColumnDefinition")
            .Select(element => element.Attribute("Width")?.Value)
            .ToArray();
        Assert.Equal(new[] { "Auto", "16", "*" }, compactColumns);

        var compactTitle = compactFolders.Elements().Single(element =>
            element.Name.LocalName == "TextBlock"
            && element.Attribute("Text")?.Value == "{Binding SelectedFolderTitle}");
        Assert.Equal("2", compactTitle.Attribute("Grid.Column")?.Value);
        Assert.Equal("CharacterEllipsis", compactTitle.Attribute("TextTrimming")?.Value);
        Assert.Equal("False", compactTitle.Attribute("IsHitTestVisible")?.Value);

        var historyContentHost = document.Descendants().Single(element =>
            element.Name.LocalName == "Grid"
            && element.Attribute(x + "Name")?.Value == "HistoryContentHost");
        var historyContentStyle = historyContentHost.Elements()
            .Single(element => element.Name.LocalName == "Grid.Style")
            .Elements()
            .Single(element => element.Name.LocalName == "Style");
        Assert.Contains(historyContentStyle.Elements(), element =>
            element.Name.LocalName == "Setter"
            && element.Attribute("Property")?.Value == "Margin"
            && element.Attribute("Value")?.Value == "16,0,0,0");
        Assert.Contains(historyContentStyle.Descendants(), element =>
            element.Name.LocalName == "DataTrigger"
            && (element.Attribute("Binding")?.Value ?? "").Contains("IsCompactLayout", StringComparison.Ordinal)
            && element.Descendants().Any(setter =>
                setter.Name.LocalName == "Setter"
                && setter.Attribute("Property")?.Value == "Margin"
                && setter.Attribute("Value")?.Value == "0"));
        Assert.Contains("CompactHistoryFolders", source, StringComparison.Ordinal);
        Assert.Contains("IsCompactLayout", source, StringComparison.Ordinal);
        Assert.Contains("CompactFolderCombo_SelectionChanged", source, StringComparison.Ordinal);
        Assert.Contains("CompactBatchCombo_SelectionChanged", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryViewLeftAlignsFolderRowsAndPulsesLeadingRecentDot()
    {
        var document = XDocument.Load(GetViewPath("HistoryView.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var recentDotStyle = document.Descendants().Single(element =>
            element.Name.LocalName == "Style"
            && element.Attribute(x + "Key")?.Value == "RecentFolderDot");

        Assert.Contains(recentDotStyle.Elements(), setter =>
            setter.Name.LocalName == "Setter"
            && setter.Attribute("Property")?.Value == "Width"
            && setter.Attribute("Value")?.Value == "8");
        Assert.Contains(recentDotStyle.Elements(), setter =>
            setter.Name.LocalName == "Setter"
            && setter.Attribute("Property")?.Value == "Height"
            && setter.Attribute("Value")?.Value == "8");

        var recentTrigger = recentDotStyle.Descendants().Single(element =>
            element.Name.LocalName == "DataTrigger"
            && element.Attribute("Binding")?.Value == "{Binding HasRecentCompletion}"
            && element.Attribute("Value")?.Value == "True");
        var pulseStoryboard = recentTrigger.Descendants().Single(element => element.Name.LocalName == "Storyboard");
        var pulseAnimation = pulseStoryboard.Elements().Single(element => element.Name.LocalName == "DoubleAnimation");
        Assert.Equal("Forever", pulseStoryboard.Attribute("RepeatBehavior")?.Value);
        Assert.Equal("True", pulseStoryboard.Attribute("AutoReverse")?.Value);
        Assert.Equal("Opacity", pulseAnimation.Attribute("Storyboard.TargetProperty")?.Value);
        Assert.Equal("0.58", pulseAnimation.Attribute("From")?.Value);
        Assert.Equal("1", pulseAnimation.Attribute("To")?.Value);
        Assert.Equal("0:0:1.4", pulseAnimation.Attribute("Duration")?.Value);

        var leadingDot = document.Descendants().Single(element =>
            element.Name.LocalName == "Ellipse"
            && element.Attribute("Style")?.Value == "{StaticResource RecentFolderDot}");
        var batchRow = leadingDot.Parent!;
        var batchColumns = batchRow.Elements()
            .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .Select(element => element.Attribute("Width")?.Value)
            .ToArray();
        Assert.Equal("0", leadingDot.Attribute("Grid.Column")?.Value);
        Assert.Equal(new[] { "16", "20", "8", "*", "Auto", "4" }, batchColumns);
        Assert.Contains(batchRow.Elements(), element =>
            element.Name.LocalName == "TextBlock"
            && element.Attribute("Grid.Column")?.Value == "1"
            && element.Attribute("Text")?.Value == "\uE8D5");
        Assert.Contains(batchRow.Elements(), element =>
            element.Name.LocalName == "TextBlock"
            && element.Attribute("Grid.Column")?.Value == "3"
            && element.Attribute("Text")?.Value == "{Binding Name}"
            && element.Attribute("HorizontalAlignment")?.Value == "Left"
            && element.Attribute("TextAlignment")?.Value == "Left");

        var historyFolders = document.Descendants().Single(element =>
            element.Name.LocalName == "ItemsControl"
            && element.Attribute("ItemsSource")?.Value == "{Binding HistoryFolders}");
        var organizerName = historyFolders.Descendants().Single(element =>
            element.Name.LocalName == "TextBlock"
            && element.Attribute("Text")?.Value == "{Binding Name}");
        Assert.Equal("Left", organizerName.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("Left", organizerName.Attribute("TextAlignment")?.Value);

        var cardIndicator = document.Descendants().Single(element =>
            element.Name.LocalName == "Ellipse"
            && (element.Attribute("Visibility")?.Value ?? "").Contains("IsRecentlyCompleted", StringComparison.Ordinal));
        Assert.Equal("8", cardIndicator.Attribute("Width")?.Value);
        Assert.Equal("8", cardIndicator.Attribute("Height")?.Value);
    }

    [Fact]
    public void HistoryViewUsesResponsiveMediaGridAndVirtualizedRows()
    {
        var document = XDocument.Load(GetViewPath("HistoryView.xaml"));
        var source = document.ToString(SaveOptions.DisableFormatting);
        var codeBehind = File.ReadAllText(GetViewPath("HistoryView.xaml.cs"));
        var card = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Grid"
            && element.Attribute("Width")?.Value == "252"
            && element.Attribute("Height")?.Value == "240");

        Assert.Contains("HistoryCardRows", source, StringComparison.Ordinal);
        Assert.Contains("HistoryList_SizeChanged", source, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding HistoryCardRows}\"", source, StringComparison.Ordinal);
        Assert.NotNull(card);
        Assert.Equal("0,0,16,16", card!.Attribute("Margin")?.Value);
        Assert.Contains("private const double HistoryCardSlotWidth = 268;", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryViewExposesSearchMediaFiltersAndSelectionToolbar()
    {
        var document = XDocument.Load(GetViewPath("HistoryView.xaml"));
        var source = document.ToString(SaveOptions.DisableFormatting);
        var searchColumn = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "ColumnDefinition"
            && element.Attribute("Width")?.Value == "280");

        Assert.NotNull(searchColumn);
        Assert.Contains("SearchKeyword", source, StringComparison.Ordinal);
        Assert.Contains("SetMediaFilterCommand", source, StringComparison.Ordinal);
        Assert.Contains("SelectionHistoryToolbar", source, StringComparison.Ordinal);
        Assert.Contains("ToggleSelectAllVisibleCommand", source, StringComparison.Ordinal);
        Assert.Contains("MoveSelectedToFolderCommand", source, StringComparison.Ordinal);
        Assert.Contains("RemoveSelectedFromFolderCommand", source, StringComparison.Ordinal);
        Assert.Contains("DeleteSelectedCommand", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryViewKeepsFolderOrganizationAndDragDropBehavior()
    {
        var source = File.ReadAllText(GetViewPath("HistoryView.xaml"));
        var codeBehind = File.ReadAllText(GetViewPath("HistoryView.xaml.cs"));

        Assert.Contains("HistoryFolders", source, StringComparison.Ordinal);
        Assert.Contains("BatchFolderCards", source, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding BulkTargetFolders}\"", source, StringComparison.Ordinal);
        Assert.Contains("DisplayMemberPath=\"DisplayName\"", source, StringComparison.Ordinal);
        Assert.Contains("BulkTargetFolderPlaceholderText", source, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding HasBulkTargetFolders}\"", source, StringComparison.Ordinal);
        Assert.Contains("CurrentLocationPathText", source, StringComparison.Ordinal);
        Assert.Contains("CurrentLocationFileCountText", source, StringComparison.Ordinal);
        Assert.Contains("CurrentLocationSizeText", source, StringComparison.Ordinal);
        Assert.Contains("CreateFolderCommand", source, StringComparison.Ordinal);
        Assert.Contains("DeleteBatchCommand", source, StringComparison.Ordinal);
        Assert.Contains("HistoryFolder_Drop", source, StringComparison.Ordinal);
        Assert.Contains("HistoryCard_PreviewMouseMove", source, StringComparison.Ordinal);
        Assert.Contains("HistoryCard_PreviewMouseLeftButtonUp", source, StringComparison.Ordinal);
        Assert.Contains("DragDrop.DoDragDrop", codeBehind, StringComparison.Ordinal);
        Assert.Contains("history.IsSelected = !history.IsSelected", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryBatchFolderDeletionIsAvailableFromTheRowContextMenuOnly()
    {
        var document = XDocument.Load(GetViewPath("HistoryView.xaml"));
        var batchFolders = document.Descendants().First(element =>
            element.Name.LocalName == "ItemsControl"
            && element.Attribute("ItemsSource")?.Value == "{Binding BatchFolderCards}");
        var contextMenu = Assert.Single(
            batchFolders.Descendants(),
            element => element.Name.LocalName == "ContextMenu");
        var deleteMenuItem = Assert.Single(
            contextMenu.Descendants(),
            element => element.Name.LocalName == "MenuItem");

        Assert.Equal(
            "{Binding PlacementTarget, RelativeSource={RelativeSource Self}}",
            contextMenu.Attribute("DataContext")?.Value);
        Assert.Equal("{Binding Tag.DeleteBatchCommand}", deleteMenuItem.Attribute("Command")?.Value);
        Assert.Equal("{Binding DataContext}", deleteMenuItem.Attribute("CommandParameter")?.Value);
        Assert.DoesNotContain(batchFolders.Descendants()
            .Where(element => element.Name.LocalName == "Button"),
            button => (button.Attribute("Command")?.Value ?? "")
                .Contains("DeleteBatchCommand", StringComparison.Ordinal));
    }

    [Fact]
    public void HistoryViewQuickActionsUseAvailableFilePath()
    {
        var document = XDocument.Load(GetViewPath("HistoryView.xaml"));
        var quickActions = document.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .Where(element => AutomationName(element) is "打开文件夹" or "预览文件")
            .ToList();

        Assert.Equal(2, quickActions.Count);
        Assert.All(quickActions, button =>
            Assert.Equal("{Binding AvailableFilePath}", button.Attribute("CommandParameter")?.Value));
    }

    [Fact]
    public void HistoryCardsShowAttachmentSummaryAndMissingFileRecovery()
    {
        var source = File.ReadAllText(GetViewPath("HistoryView.xaml"));

        Assert.Contains("AttachmentSummaryText", source, StringComparison.Ordinal);
        Assert.Contains("HasAttachmentSummary", source, StringComparison.Ordinal);
        Assert.Contains("FileExists", source, StringComparison.Ordinal);
        Assert.Contains("文件缺失", source, StringComparison.Ordinal);
        Assert.Contains("RedownloadHistoryItemCommand", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsViewUsesSevenCategoryInformationArchitecture()
    {
        var document = XDocument.Load(GetViewPath("SettingsView.xaml"));
        var categories = document.Descendants()
            .Where(element => element.Name.LocalName == "RadioButton")
            .Where(element => (element.Attribute("Command")?.Value ?? "")
                .Contains("SelectCategoryCommand", StringComparison.Ordinal))
            .Select(element => element.Attribute("CommandParameter")?.Value)
            .ToList();

        Assert.Equal(new[]
        {
            "常规", "下载", "网络", "账号与 Cookie", "集成", "更新与环境", "数据管理"
        }, categories);
    }

    [Fact]
    public void SettingsViewStretchesReadableContentAndKeepsInputsUsable()
    {
        var document = XDocument.Load(GetViewPath("SettingsView.xaml"));
        var content = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Grid"
            && element.Attribute("MaxWidth")?.Value == "1080");
        var telegramInputs = document.Descendants()
            .Where(element => element.Name.LocalName == "TextBox")
            .Where(element => (element.Attribute("Text")?.Value ?? "")
                .Contains("Tg", StringComparison.Ordinal))
            .ToList();
        var ffmpegButton = FindButtonByCommand(document, "CheckEnvironmentCommand");

        Assert.NotNull(content);
        Assert.Equal("Left", content!.Attribute("HorizontalAlignment")?.Value);
        Assert.NotEmpty(telegramInputs);
        Assert.All(telegramInputs, input => Assert.Equal("320", input.Attribute("MinWidth")?.Value));
        Assert.Equal("1", ffmpegButton?.Attribute("Grid.Column")?.Value);
    }

    [Theory]
    [InlineData("CheckEnvironmentCommand", "CanCheckEnvironment")]
    [InlineData("InstallMissingToolsCommand", "CanInstallMissingTools")]
    [InlineData("UpdateYtDlpCommand", "CanUpdateYtDlp")]
    public void SettingsEnvironmentButtonsBindExpectedEnabledState(string commandName, string enabledProperty)
    {
        var document = XDocument.Load(GetViewPath("SettingsView.xaml"));
        var button = FindButtonByCommand(document, commandName);

        Assert.NotNull(button);
        Assert.Contains(enabledProperty, button!.Attribute("IsEnabled")?.Value ?? "");
    }

    [Fact]
    public void SettingsViewExposesApplicationUpdateControls()
    {
        var source = File.ReadAllText(GetViewPath("SettingsView.xaml"));

        Assert.Contains("CheckAppUpdateCommand", source, StringComparison.Ordinal);
        Assert.Contains("DownloadAppUpdateCommand", source, StringComparison.Ordinal);
        Assert.Contains("InstallAppUpdateCommand", source, StringComparison.Ordinal);
        Assert.Contains("AppUpdateProgress", source, StringComparison.Ordinal);
        Assert.Contains("AppVersionText", source, StringComparison.Ordinal);
        Assert.Contains("AppRuntimeText", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsViewExposesPersistedClipboardAndAria2cToggles()
    {
        var source = File.ReadAllText(GetViewPath("SettingsView.xaml"));

        Assert.Contains("ClipboardMonitoringEnabled", source, StringComparison.Ordinal);
        Assert.Contains("UseAria2c", source, StringComparison.Ordinal);
        Assert.Contains("aria2c 外部下载器", source, StringComparison.Ordinal);
        Assert.Contains("GlobalDownloadRateLimitDisplayText", source, StringComparison.Ordinal);
        Assert.Contains("GlobalDownloadRateLimitSliderStep", source, StringComparison.Ordinal);
        Assert.Contains("GlobalDownloadRateLimitSliderMaximum", source, StringComparison.Ordinal);
        Assert.Contains("全局下载限速", source, StringComparison.Ordinal);
        Assert.Contains("AccentSlider", source, StringComparison.Ordinal);
        Assert.Contains("AppConfig.MinConcurrentFragments", source, StringComparison.Ordinal);
        Assert.Contains("AppConfig.MaxConcurrentFragments", source, StringComparison.Ordinal);
        Assert.Contains("AppConfig.MinConcurrentDownloadLimit", source, StringComparison.Ordinal);
        Assert.Contains("AppConfig.MaxConcurrentDownloadLimit", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsSliderBoundsUseBindingConversionForIntegerConfigConstants()
    {
        var source = File.ReadAllText(GetViewPath("SettingsView.xaml"));

        Assert.Contains(
            "Minimum=\"{Binding Source={x:Static models:AppConfig.MinConcurrentFragments}, Mode=OneTime}\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Maximum=\"{Binding Source={x:Static models:AppConfig.MaxConcurrentFragments}, Mode=OneTime}\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Minimum=\"{Binding Source={x:Static models:AppConfig.MinConcurrentDownloadLimit}, Mode=OneTime}\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Maximum=\"{Binding Source={x:Static models:AppConfig.MaxConcurrentDownloadLimit}, Mode=OneTime}\"",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Minimum=\"{x:Static models:AppConfig.",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Maximum=\"{x:Static models:AppConfig.",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsDataManagementUsesConfirmationCommands()
    {
        var source = File.ReadAllText(GetViewPath("SettingsView.xaml"));

        Assert.Contains("ConfirmClearAllManagedSessionsCommand", source, StringComparison.Ordinal);
        Assert.Contains("ConfirmClearCookieCommand", source, StringComparison.Ordinal);
        Assert.Contains("ConfirmTgLogOutCommand", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding ClearAllManagedSessionsCommand}\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmationDialogDefaultsToCancelAndUsesDestructiveBrush()
    {
        var document = XDocument.Load(GetViewPath("ConfirmationDialog.xaml"));
        var source = document.ToString(SaveOptions.DisableFormatting);
        var cancel = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Button"
            && element.Attribute("IsCancel")?.Value == "True");

        Assert.Equal("None", document.Root?.Attribute("WindowStyle")?.Value);
        Assert.Equal("True", document.Root?.Attribute("AllowsTransparency")?.Value);
        Assert.NotNull(cancel);
        Assert.Equal("True", cancel!.Attribute("IsDefault")?.Value);
        Assert.Contains("DestructiveBrush", source, StringComparison.Ordinal);
        Assert.Contains("ConfirmText", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DownloadView.xaml", "单个下载")]
    [InlineData("BatchDownloadView.xaml", "批量下载")]
    [InlineData("HistoryView.xaml", "下载历史")]
    [InlineData("SettingsView.xaml", "设置")]
    public void MainPageTitlesUseCompactDesignerTypography(string viewFileName, string titleText)
    {
        var document = XDocument.Load(GetViewPath(viewFileName));
        var title = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "TextBlock"
            && element.Attribute("Text")?.Value == titleText);

        Assert.NotNull(title);
        Assert.Contains("TextPageTitle", title!.Attribute("Style")?.Value ?? "");
    }

    [Fact]
    public void ViewsDoNotRenderPrototypePlaceholderStatusCopy()
    {
        var files = new[]
        {
            GetRootPath("MainWindow.xaml"),
            GetViewPath("DownloadView.xaml"),
            GetViewPath("BatchDownloadView.xaml"),
            GetViewPath("HistoryView.xaml")
        };
        var forbidden = new[]
        {
            "PRO ACCOUNT", "SERVER STATUS", "V1.0.8", "v1.2.4",
            "磁盘空间充足", "Batch Operations", "无限制", "系统默认"
        };

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (var text in forbidden)
                Assert.DoesNotContain(text, source, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("BatchDownloadView.xaml")]
    [InlineData("HistoryView.xaml")]
    [InlineData("SettingsView.xaml")]
    public void IconOnlyButtonsExposeTooltipAndAutomationName(string viewFileName)
    {
        var document = XDocument.Load(GetViewPath(viewFileName));
        var missingHints = document.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .Where(element => IsIconOnlyContent(element.Attribute("Content")?.Value))
            .Where(element => string.IsNullOrWhiteSpace(element.Attribute("ToolTip")?.Value)
                || string.IsNullOrWhiteSpace(AutomationName(element)))
            .Select(element => element.Attribute("Content")?.Value ?? "")
            .ToList();

        Assert.True(missingHints.Count == 0,
            "Icon-only buttons must expose ToolTip and AutomationProperties.Name: "
                + string.Join("; ", missingHints));
    }

    private static XElement? FindButtonByCommand(XDocument document, string commandName)
        => document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Button"
            && element.Attributes("Command").Any(attribute =>
                attribute.Value.Contains(commandName, StringComparison.Ordinal)));

    private static string AutomationName(XElement element)
        => element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == "AutomationProperties.Name")
            ?.Value ?? "";

    private static bool IsIconOnlyContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var value = content.Trim();
        return value.Length <= 3 && value.All(character => !char.IsLetterOrDigit(character));
    }

    private static string GetViewPath(string fileName)
        => TestRepositoryPaths.GetViewPath(fileName);

    private static string GetRootPath(string fileName)
        => TestRepositoryPaths.GetRootPath(fileName);
}
