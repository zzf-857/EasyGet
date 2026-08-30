using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace EasyGet.Tests;

public class LayoutContractTests
{
    [Fact]
    public void FramedTextStylesReserveEnoughSpaceForCurrentTypography()
    {
        var document = XDocument.Load(TestRepositoryPaths.GetThemePath("Generic.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var textBoxStyle = FindStyle(document, x, "DarkTextBox");
        var comboBoxStyle = FindStyle(document, x, "DarkComboBox");
        var accentButtonStyle = FindStyle(document, x, "AccentButton");
        var surfaceButtonStyle = FindStyle(document, x, "SurfaceButton");

        AssertSetterAtLeast(textBoxStyle, "MinHeight", 32);
        AssertSetterAtLeast(comboBoxStyle, "MinHeight", 32);
        AssertSetterAtLeast(accentButtonStyle, "MinHeight", 32);
        AssertSetterAtLeast(surfaceButtonStyle, "MinHeight", 32);

        var contentHost = textBoxStyle.Descendants().First(element =>
            element.Name.LocalName == "ScrollViewer"
            && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "PART_ContentHost"));
        Assert.Equal("{TemplateBinding Padding}", contentHost.Attribute("Padding")?.Value);

        var comboSelection = comboBoxStyle.Descendants().First(element =>
            element.Name.LocalName == "ContentPresenter"
            && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "ContentSite"));
        var margin = ParseThickness(comboSelection.Attribute("Margin")?.Value);
        Assert.Equal(0, margin.Top);
        Assert.Equal(0, margin.Bottom);
    }

    [Fact]
    public void FramedControlsDoNotUseUnsafeFixedHeights()
    {
        var offenders = GetSurfacePaths()
            .SelectMany(path => XDocument.Load(path).Descendants()
                .Where(element => element.Name.LocalName is "TextBox" or "ComboBox" or "Button")
                .Select(element => new
                {
                    File = Path.GetFileName(path),
                    Element = element,
                    Height = ParseNumber(element.Attribute("Height")?.Value)
                }))
            .Where(item => item.Height is > 0 and < 32)
            .Select(item => $"{item.File}:{Describe(item.Element)} Height={item.Height}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "TextBox, ComboBox, and Button controls must be at least 32px high: "
                + string.Join("; ", offenders));
    }

    [Fact]
    public void NavigationStyleStretchesWithoutAClippingFixedWidth()
    {
        var document = XDocument.Load(TestRepositoryPaths.GetThemePath("Generic.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var style = FindStyle(document, x, "NavRadioButton");

        Assert.Equal("{StaticResource NavigationHeight}", GetSetterValue(style, "Height"));
        Assert.DoesNotContain(style.Elements(), element =>
            element.Name.LocalName == "Setter"
            && element.Attribute("Property")?.Value == "Width");
    }

    [Fact]
    public void MainSidebarNavigationUsesSpaciousConsistentRows()
    {
        var document = XDocument.Load(TestRepositoryPaths.GetRootPath("MainWindow.xaml"));
        var theme = XDocument.Load(TestRepositoryPaths.GetThemePath("Generic.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var style = FindStyle(document, x, "SidebarNavItem");
        var glyphStyle = FindStyle(document, x, "SidebarNavGlyph");
        var labelStyle = FindStyle(document, x, "SidebarNavLabel");
        var panelStyle = FindStyle(document, x, "SidebarNavigationPanel");

        Assert.Equal("{StaticResource NavigationHeight}", GetSetterValue(style, "Height"));
        Assert.Equal("Center", GetSetterValue(glyphStyle, "VerticalAlignment"));
        Assert.Equal("Center", GetSetterValue(labelStyle, "VerticalAlignment"));
        Assert.Contains(style.Elements(), element =>
            element.Name.LocalName == "Setter"
            && element.Attribute("Property")?.Value == "Margin"
            && element.Attribute("Value")?.Value == "0,0,0,4");

        var compactNavTrigger = style.Descendants().Single(element =>
            element.Name.LocalName == "DataTrigger"
            && element.Attribute("Binding")?.Value == "{Binding IsCompactLayout}"
            && element.Attribute("Value")?.Value == "True");
        Assert.Equal(
            "{StaticResource NavigationHeight}",
            compactNavTrigger.Elements().Single(element =>
                element.Name.LocalName == "Setter"
                && element.Attribute("Property")?.Value == "Width").Attribute("Value")?.Value);
        Assert.Equal(
            "Center",
            compactNavTrigger.Elements().Single(element =>
                element.Name.LocalName == "Setter"
                && element.Attribute("Property")?.Value == "HorizontalAlignment").Attribute("Value")?.Value);

        var compactPanelTrigger = panelStyle.Descendants().Single(element =>
            element.Name.LocalName == "DataTrigger"
            && element.Attribute("Binding")?.Value == "{Binding IsCompactLayout}"
            && element.Attribute("Value")?.Value == "True");
        var compactPanelMargin = ParseThickness(compactPanelTrigger.Elements().Single(element =>
            element.Name.LocalName == "Setter"
            && element.Attribute("Property")?.Value == "Margin").Attribute("Value")?.Value);
        var compactNavigationWidth = GetNumericResource(theme, x, "NavigationHeight");
        Assert.Equal(compactNavigationWidth, 74 - compactPanelMargin.Left - compactPanelMargin.Right);

        var navItems = document.Descendants()
            .Where(element => element.Name.LocalName == "RadioButton")
            .Where(element => element.Attribute("Style")?.Value == "{StaticResource SidebarNavItem}")
            .ToList();

        Assert.Equal(4, navItems.Count);
        Assert.Equal("Center", GetSetterValue(glyphStyle, "HorizontalAlignment"));
        Assert.All(navItems, item =>
        {
            var contentGrid = item.Elements().Single(element => element.Name.LocalName == "Grid");
            Assert.Equal("Center", contentGrid.Attribute("VerticalAlignment")?.Value);
            var centeredContent = contentGrid.Elements().Single(element =>
                element.Name.LocalName == "StackPanel");

            Assert.Equal("Horizontal", centeredContent.Attribute("Orientation")?.Value);
            Assert.Equal("Center", centeredContent.Attribute("HorizontalAlignment")?.Value);
            Assert.Equal("Center", centeredContent.Attribute("VerticalAlignment")?.Value);
            Assert.Contains(centeredContent.Elements(), element =>
                element.Name.LocalName == "TextBlock"
                && element.Attribute("Style")?.Value == "{StaticResource SidebarNavGlyph}");
            Assert.Contains(centeredContent.Elements(), element =>
                element.Name.LocalName == "TextBlock"
                && element.Attribute("Style")?.Value == "{StaticResource SidebarNavLabel}");
        });

        var batchContent = navItems.Single(item =>
            item.Attribute("CommandParameter")?.Value == "batch").Elements().Single(element =>
                element.Name.LocalName == "Grid");
        var batchBadge = batchContent.Elements().Single(element =>
            element.Name.LocalName == "Border"
            && element.Attribute("Style")?.Value == "{StaticResource SidebarBadge}");
        var badgeStyle = FindStyle(document, x, "SidebarBadge");
        Assert.Equal("Right", GetSetterValue(badgeStyle, "HorizontalAlignment"));
        Assert.Equal("Center", GetSetterValue(badgeStyle, "VerticalAlignment"));
        Assert.Null(batchBadge.Attribute("Grid.Column"));
    }

    [Fact]
    public void NavigationGroupsRemainScopedToTheirOwnSurfaces()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var theme = XDocument.Load(TestRepositoryPaths.GetThemePath("Generic.xaml"));
        var main = XDocument.Load(TestRepositoryPaths.GetRootPath("MainWindow.xaml"));
        var settings = XDocument.Load(TestRepositoryPaths.GetViewPath("SettingsView.xaml"));
        var history = XDocument.Load(TestRepositoryPaths.GetViewPath("HistoryView.xaml"));

        var sharedStyle = FindStyle(theme, x, "NavRadioButton");
        Assert.Null(GetSetterValue(sharedStyle, "GroupName"));
        Assert.Null(GetSetterValue(FindStyle(theme, x, "HistoryFilterRadioButton"), "GroupName"));
        Assert.Equal("MainNavigation", GetSetterValue(FindStyle(main, x, "SidebarNavItem"), "GroupName"));
        Assert.Equal("SettingsCategories", GetSetterValue(FindStyle(settings, x, "SettingsNavItem"), "GroupName"));

        var historyFolderFilters = history.Descendants()
            .Where(element => element.Name.LocalName == "RadioButton")
            .Where(element => element.Attribute("Style")?.Value == "{StaticResource NavRadioButton}")
            .ToList();
        Assert.Equal(2, historyFolderFilters.Count);
        Assert.All(historyFolderFilters, filter =>
            Assert.Equal("HistoryFolders", filter.Attribute("GroupName")?.Value));

        var historyMediaFilters = history.Descendants()
            .Where(element => element.Name.LocalName == "RadioButton")
            .Where(element => element.Attribute("Style")?.Value == "{StaticResource HistoryFilterRadioButton}")
            .ToList();
        Assert.Equal(3, historyMediaFilters.Count);
        Assert.All(historyMediaFilters, filter =>
            Assert.Equal("HistoryMediaFilters", filter.Attribute("GroupName")?.Value));

        var batch = XDocument.Load(TestRepositoryPaths.GetViewPath("BatchDownloadView.xaml"));
        var queueFilters = batch.Descendants()
            .Where(element => element.Name.LocalName == "RadioButton")
            .Where(element => element.Attribute("GroupName")?.Value == "BatchQueueFilters")
            .ToList();
        Assert.Equal(7, queueFilters.Count);
    }

    [Fact]
    public void MainTitleBarKeepsPageNameAccessibleWithoutRenderingDuplicateText()
    {
        var document = XDocument.Load(TestRepositoryPaths.GetRootPath("MainWindow.xaml"));
        var source = document.ToString(SaveOptions.DisableFormatting);

        Assert.DoesNotContain("Text=\"{Binding CurrentPageTitle}\"", source, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{Binding CurrentPageTitle}\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains("SettingsVM.ClipboardMonitoringEnabled", source, StringComparison.Ordinal);
        Assert.Contains("Value=\"剪贴板监视中\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsTextAndRightAlignedActionsUseSeparateColumns()
    {
        var document = XDocument.Load(TestRepositoryPaths.GetViewPath("SettingsView.xaml"));
        var interactiveTypes = new HashSet<string>
        {
            "Button", "ComboBox", "TextBox", "ToggleButton"
        };

        var offenders = document.Descendants()
            .Where(element => element.Name.LocalName == "Grid")
            .Where(grid => grid.Elements().Any(child =>
                child.Name.LocalName is "StackPanel" or "TextBlock"))
            .Where(grid => grid.Elements().Any(child =>
                interactiveTypes.Contains(child.Name.LocalName)
                && child.Attribute("HorizontalAlignment")?.Value == "Right"))
            .Where(grid => !grid.Elements().Any(child =>
                child.Name.LocalName == "Grid.ColumnDefinitions"))
            .Select(Describe)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Settings label and action rows must use explicit * + Auto columns: "
                + string.Join("; ", offenders));
    }

    [Fact]
    public void DeclaredTemplateBoundsStayInsideControlStyleBounds()
    {
        var document = XDocument.Load(TestRepositoryPaths.GetThemePath("Generic.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var offenders = new List<string>();

        foreach (var style in document.Descendants().Where(element => element.Name.LocalName == "Style"))
        {
            var width = GetNumericSetter(style, "Width");
            var height = GetNumericSetter(style, "Height");
            if (width is null && height is null)
                continue;

            var template = style.Elements()
                .FirstOrDefault(element =>
                    element.Name.LocalName == "Setter"
                    && element.Attribute("Property")?.Value == "Template");
            if (template is null)
                continue;

            var key = style.Attribute(x + "Key")?.Value ?? style.Attribute("TargetType")?.Value ?? "unnamed style";
            foreach (var element in template.Descendants())
            {
                var innerWidth = ParseNumber(element.Attribute("Width")?.Value);
                var innerHeight = ParseNumber(element.Attribute("Height")?.Value);
                if (width is not null && innerWidth is not null && innerWidth > width + 0.01)
                    offenders.Add($"{key}: {element.Name.LocalName} width {innerWidth} > {width}");
                if (height is not null && innerHeight is not null && innerHeight > height + 0.01)
                    offenders.Add($"{key}: {element.Name.LocalName} height {innerHeight} > {height}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Template content cannot be larger than its declared control bounds: "
                + string.Join("; ", offenders));
    }

    [Fact]
    public void BatchClipboardActionCannotOverlayLinkSummary()
    {
        var document = XDocument.Load(TestRepositoryPaths.GetViewPath("BatchDownloadView.xaml"));
        var pasteButton = document.Descendants().First(element =>
            element.Name.LocalName == "Button"
            && (element.Attribute("Command")?.Value ?? string.Empty)
                .Contains("PasteUrlsCommand", StringComparison.Ordinal));
        var grid = pasteButton.Parent;

        Assert.NotNull(grid);
        Assert.Contains(grid!.Elements(), element => element.Name.LocalName == "Grid.ColumnDefinitions");
        Assert.Equal("1", pasteButton.Attribute("Grid.Column")?.Value);
        Assert.Contains(grid.Elements(), element =>
            element.Name.LocalName == "TextBlock"
            && element.Attribute("Grid.Column")?.Value == "0"
            && element.Attribute("TextTrimming")?.Value == "CharacterEllipsis");
    }

    [Fact]
    public void BatchFiltersAndAggregateProgressUseSeparateRows()
    {
        var document = XDocument.Load(TestRepositoryPaths.GetViewPath("BatchDownloadView.xaml"));
        var filter = document.Descendants().First(element =>
            element.Name.LocalName == "RadioButton"
            && (element.Attribute("Command")?.Value ?? string.Empty)
                .Contains("SetQueueFilterCommand", StringComparison.Ordinal));
        var progress = document.Descendants().First(element =>
            element.Name.LocalName == "TextBlock"
            && (element.Attribute("Text")?.Value ?? string.Empty)
                .Contains("OverallProgressText", StringComparison.Ordinal));
        var filterRow = filter.Ancestors().First(element => element.Attribute("Grid.Row") is not null);
        var progressRow = progress.Ancestors().First(element => element.Attribute("Grid.Row") is not null);

        Assert.Same(filterRow.Parent, progressRow.Parent);
        Assert.Equal("0", filterRow.Attribute("Grid.Row")?.Value);
        Assert.Equal("1", progressRow.Attribute("Grid.Row")?.Value);
        Assert.Equal(2, filterRow.Parent!.Elements()
            .First(element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .Count(element => element.Name.LocalName == "RowDefinition"));
    }

    [Fact]
    public void DownloadLogHeaderUsesOneThemedDisclosureAndOneTextBaseline()
    {
        var document = XDocument.Load(TestRepositoryPaths.GetViewPath("DownloadView.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var expander = document.Descendants().First(element =>
            element.Name.LocalName == "Expander"
            && element.Attribute(x + "Name")?.Value == "LogExpander");

        Assert.Equal("{StaticResource ConsoleExpander}", expander.Attribute("Style")?.Value);
        Assert.DoesNotContain(expander.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && element.Attribute("Text")?.Value == "\uE76C");

        var summary = expander.Descendants().First(element =>
            element.Name.LocalName == "TextBlock"
            && element.Elements().Any(run =>
                run.Name.LocalName == "Run"
                && run.Attribute("Text")?.Value == "运行日志"));
        Assert.Contains(summary.Elements(), run =>
            run.Name.LocalName == "Run"
            && (run.Attribute("Text")?.Value ?? string.Empty).Contains("LogLines.Count", StringComparison.Ordinal));
        Assert.Equal("Center", summary.Attribute("VerticalAlignment")?.Value);

        var headerButtons = expander.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .Where(element => element.Attribute("Command")?.Value is "{Binding CopyLogCommand}" or "{Binding ClearLogCommand}")
            .ToList();
        Assert.Equal(2, headerButtons.Count);
        Assert.All(headerButtons, button => Assert.Equal("Center", button.Attribute("VerticalAlignment")?.Value));
    }

    [Fact]
    public void RecentDownloadRowsUseOneUiMetricAndCenterTheirText()
    {
        var document = XDocument.Load(TestRepositoryPaths.GetViewPath("DownloadView.xaml"));
        var title = document.Descendants().First(element =>
            element.Name.LocalName == "TextBlock"
            && element.Attribute("Text")?.Value == "{Binding Title}");
        var fileSize = document.Descendants().First(element =>
            element.Name.LocalName == "TextBlock"
            && element.Attribute("Text")?.Value == "{Binding FileSizeText}");

        Assert.Same(title.Parent, fileSize.Parent);
        Assert.Equal("{StaticResource TextBody}", title.Attribute("Style")?.Value);
        Assert.Equal("{StaticResource TextBody}", fileSize.Attribute("Style")?.Value);
        Assert.Equal("Center", title.Attribute("VerticalAlignment")?.Value);
        Assert.Equal("Center", fileSize.Attribute("VerticalAlignment")?.Value);
        Assert.Equal("Tabular", fileSize.Attribute("Typography.NumeralAlignment")?.Value);
    }

    [Fact]
    public void BatchQueueStatusAndPercentageShareCaptionMetrics()
    {
        var document = XDocument.Load(TestRepositoryPaths.GetViewPath("BatchDownloadView.xaml"));
        var statusRun = document.Descendants().First(element =>
            element.Name.LocalName == "Run"
            && element.Attribute("Text")?.Value == "{Binding StatusText, Mode=OneWay}");
        var progressRun = document.Descendants().First(element =>
            element.Name.LocalName == "Run"
            && (element.Attribute("Text")?.Value ?? string.Empty).Contains("StringFormat={}{0:F0}", StringComparison.Ordinal));
        var status = statusRun.Parent!;
        var progress = progressRun.Parent!;

        Assert.Same(status.Parent, progress.Parent);
        Assert.Equal("Center", status.Attribute("VerticalAlignment")?.Value);
        Assert.Equal("Center", progress.Attribute("VerticalAlignment")?.Value);
        Assert.Contains(status.Descendants(), element =>
            element.Name.LocalName == "Style"
            && element.Attribute("BasedOn")?.Value == "{StaticResource TextCaption}");
        Assert.Equal("{StaticResource TextCaption}", progress.Attribute("Style")?.Value);
        Assert.Equal("Tabular", progress.Attribute("Typography.NumeralAlignment")?.Value);
    }

    [Fact]
    public void HistoryCardsKeepTwoLineTitlesAndFourStableActionSlots()
    {
        var document = XDocument.Load(TestRepositoryPaths.GetViewPath("HistoryView.xaml"));
        var title = document.Descendants().First(element =>
            element.Name.LocalName == "TextBlock"
            && element.Attribute("Text")?.Value == "{Binding Title}");
        var actions = document.Descendants().First(element =>
            element.Name.LocalName == "Grid"
            && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "NormalActions"));
        var actionColumns = actions.Elements()
            .First(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .Select(element => element.Attribute("Width")?.Value)
            .ToList();

        Assert.Equal("Wrap", title.Attribute("TextWrapping")?.Value);
        Assert.Equal("CharacterEllipsis", title.Attribute("TextTrimming")?.Value);
        Assert.Equal(new[] { "*", "*", "*", "*" }, actionColumns);
    }

    [Theory]
    [InlineData(1080, 680)]
    [InlineData(1360, 840)]
    [InlineData(1920, 1080)]
    public void TargetViewportsKeepPrimaryWorkAreasUsable(double width, double height)
    {
        const double compactBreakpoint = 1180;
        var sidebar = width <= compactBreakpoint ? 74 : 232;
        var mainWorkspace = width - sidebar;
        var downloadWorkspace = mainWorkspace - 48;
        var batchRootWorkspace = mainWorkspace - 48;
        var batchQueueWorkspace = batchRootWorkspace - 390 - 6;
        var batchQueueContentWorkspace = batchQueueWorkspace - 48;
        var settingsContentWorkspace = mainWorkspace - 48 - 208 - 20;

        Assert.True(height >= 680);
        Assert.True(downloadWorkspace >= 800, $"Download workspace is only {downloadWorkspace}px wide.");
        Assert.True(batchQueueWorkspace >= 560, $"Batch queue workspace is only {batchQueueWorkspace}px wide.");
        Assert.True(batchQueueContentWorkspace >= 512, $"Batch queue content is only {batchQueueContentWorkspace}px wide.");
        Assert.True(settingsContentWorkspace >= 720, $"Settings content workspace is only {settingsContentWorkspace}px wide.");
    }

    private static IEnumerable<string> GetSurfacePaths()
    {
        yield return TestRepositoryPaths.GetRootPath("MainWindow.xaml");
        foreach (var path in Directory.EnumerateFiles(
                     TestRepositoryPaths.GetRootPath("Views"),
                     "*.xaml",
                     SearchOption.TopDirectoryOnly))
        {
            yield return path;
        }
    }

    private static XElement FindStyle(XDocument document, XNamespace x, string key)
        => document.Descendants().First(element =>
            element.Name.LocalName == "Style"
            && element.Attribute(x + "Key")?.Value == key);

    private static void AssertSetterAtLeast(XElement style, string property, double minimum)
    {
        var value = GetNumericSetter(style, property);
        Assert.True(value >= minimum, $"{property} must be at least {minimum}, but was {value?.ToString() ?? "missing"}.");
    }

    private static double? GetNumericSetter(XElement style, string property)
    {
        var value = style.Elements().FirstOrDefault(element =>
            element.Name.LocalName == "Setter"
            && element.Attribute("Property")?.Value == property)?.Attribute("Value")?.Value;
        var number = ParseNumber(value);
        if (number is not null || value is null || style.Document is null)
            return number;

        const string prefix = "{StaticResource ";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || !value.EndsWith('}'))
            return null;

        var key = value[prefix.Length..^1];
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return ParseNumber(style.Document.Descendants().FirstOrDefault(element =>
            element.Attribute(x + "Key")?.Value == key)?.Value);
    }

    private static string? GetSetterValue(XElement style, string property)
        => style.Elements().FirstOrDefault(element =>
            element.Name.LocalName == "Setter"
            && element.Attribute("Property")?.Value == property)?.Attribute("Value")?.Value;

    private static double? GetNumericResource(XDocument document, XNamespace x, string key)
        => ParseNumber(document.Descendants().FirstOrDefault(element =>
            element.Attribute(x + "Key")?.Value == key)?.Value);

    private static double? ParseNumber(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static (double Left, double Top, double Right, double Bottom) ParseThickness(string? value)
    {
        var values = (value ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => double.Parse(part, CultureInfo.InvariantCulture))
            .ToArray();
        return values.Length switch
        {
            1 => (values[0], values[0], values[0], values[0]),
            2 => (values[0], values[1], values[0], values[1]),
            4 => (values[0], values[1], values[2], values[3]),
            _ => throw new FormatException($"Unsupported thickness: {value}")
        };
    }

    private static string Describe(XElement element)
    {
        var content = element.Attribute("Content")?.Value;
        var text = element.DescendantsAndSelf()
            .Attributes("Text")
            .Select(attribute => attribute.Value)
            .FirstOrDefault(value => !value.StartsWith("{Binding", StringComparison.Ordinal));
        return $"{element.Name.LocalName}[{content ?? text ?? "unnamed"}]";
    }
}
