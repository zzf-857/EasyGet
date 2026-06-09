using System.Xml.Linq;
using Xunit;

namespace EasyGet.Tests;

public class XamlBindingTests
{
    [Fact]
    public void DownloadViewUsesModernToolPanelStyleForPrimaryPanels()
    {
        var document = XDocument.Load(GetViewPath("DownloadView.xaml"));

        var styledPanels = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Border")
            .Select(element => element.Attribute("Style")?.Value ?? "")
            .Where(value => value.Contains("ToolPanelBorder", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            styledPanels.Count >= 3,
            $"DownloadView should use ToolPanelBorder for option, progress, and log panels. Found {styledPanels.Count}.");
    }

    [Fact]
    public void SettingsViewUsesModernToolPanelStyleForPrimarySections()
    {
        var document = XDocument.Load(GetViewPath("SettingsView.xaml"));

        var styledPanels = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Border")
            .Select(element => element.Attribute("Style")?.Value ?? "")
            .Where(value => value.Contains("ToolPanelBorder", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            styledPanels.Count >= 5,
            $"SettingsView should use ToolPanelBorder for environment, download, proxy, cookie, and performance sections. Found {styledPanels.Count}.");
    }

    [Fact]
    public void BatchDownloadViewUsesModernToolPanelStyleForPrimarySections()
    {
        var document = XDocument.Load(GetViewPath("BatchDownloadView.xaml"));

        var styledPanels = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Border")
            .Select(element => element.Attribute("Style")?.Value ?? "")
            .Where(value => value.Contains("ToolPanelBorder", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            styledPanels.Count >= 2,
            $"BatchDownloadView should use ToolPanelBorder for URL input and queue panels. Found {styledPanels.Count}.");
    }

    [Fact]
    public void HistoryViewUsesModernToolPanelStyleForHistoryCards()
    {
        var document = XDocument.Load(GetViewPath("HistoryView.xaml"));

        var styledPanels = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Border")
            .Select(element => element.Attribute("Style")?.Value ?? "")
            .Where(value => value.Contains("ToolPanelBorder", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            styledPanels.Count >= 1,
            $"HistoryView should use ToolPanelBorder for history item cards. Found {styledPanels.Count}.");
    }

    [Fact]
    public void MainWindowSidebarUsesSubtleContentDivider()
    {
        var document = XDocument.Load(GetRootPath("MainWindow.xaml"));

        var sidebar = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Border"
                && element.Attribute("Background")?.Value.Contains("BgSidebarBrush", StringComparison.Ordinal) == true);

        Assert.NotNull(sidebar);
        Assert.Contains("BorderSubtleBrush", sidebar!.Attribute("BorderBrush")?.Value ?? "");
        Assert.Equal("0,0,1,0", sidebar.Attribute("BorderThickness")?.Value);
    }

    [Fact]
    public void MainWindowUsesStitchBrandSidebarAndTopAppBar()
    {
        var document = XDocument.Load(GetRootPath("MainWindow.xaml"));

        var columns = document
            .Descendants()
            .Where(element => element.Name.LocalName == "ColumnDefinition")
            .ToList();

        Assert.NotEmpty(columns);
        Assert.Equal("240", columns[0].Attribute("Width")?.Value);

        var rows = document
            .Descendants()
            .Where(element => element.Name.LocalName == "RowDefinition")
            .ToList();

        Assert.Contains(rows, row => row.Attribute("Height")?.Value == "48");
        Assert.Equal("None", document.Root?.Attribute("WindowStyle")?.Value);

        var logoMark = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Border"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name"
                    && attribute.Value == "SidebarLogoMark"));

        Assert.NotNull(logoMark);
        Assert.Equal("40", logoMark!.Attribute("Width")?.Value);
        Assert.Equal("40", logoMark.Attribute("Height")?.Value);

        Assert.Contains(
            document.Descendants().Attributes("Text").Select(attribute => attribute.Value),
            text => text == "EasyGet");
        Assert.Contains(
            document.Descendants().Attributes("Text").Select(attribute => attribute.Value),
            text => text.Equals("Pro Account", StringComparison.OrdinalIgnoreCase));

        var navItems = document
            .Descendants()
            .Where(element => element.Name.LocalName == "RadioButton")
            .Where(element => element.Attribute("CommandParameter") is not null)
            .ToList();

        Assert.Equal(4, navItems.Count);
        Assert.All(navItems, item =>
        {
            var textBlocks = item.Descendants().Where(element => element.Name.LocalName == "TextBlock").ToList();
            Assert.True(textBlocks.Count >= 2, "Stitch sidebar items should include an icon and a visible text label.");
            Assert.Contains(textBlocks, textBlock =>
                (textBlock.Attribute("FontFamily")?.Value ?? "").Contains("Segoe", StringComparison.Ordinal));
            Assert.Contains(textBlocks, textBlock =>
                (textBlock.Attribute("Text")?.Value ?? "") == item.Attribute("ToolTip")?.Value);
        });

        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "ContentControl"
            && (element.Attribute("Content")?.Value ?? "").Contains("CurrentPageTitle", StringComparison.Ordinal));
    }

    [Fact]
    public void MainWindowSidebarLogoUsesApplicationIconAsset()
    {
        var document = XDocument.Load(GetRootPath("MainWindow.xaml"));

        var logoMark = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Border"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name"
                    && attribute.Value == "SidebarLogoMark"));

        Assert.NotNull(logoMark);

        var image = logoMark!
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Image");

        Assert.NotNull(image);
        Assert.Equal("/Assets/app.png", image!.Attribute("Source")?.Value);
        Assert.Equal("Uniform", image.Attribute("Stretch")?.Value);

        Assert.DoesNotContain(logoMark.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && (element.Attribute("FontFamily")?.Value ?? "").Contains("Segoe", StringComparison.Ordinal));
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
    public void MainWindowDownloadNavigationUsesCleanerDownloadGlyph()
    {
        var document = XDocument.Load(GetRootPath("MainWindow.xaml"));

        var downloadNavItem = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "RadioButton"
                && element.Attribute("CommandParameter")?.Value == "download");

        Assert.NotNull(downloadNavItem);
        var icon = downloadNavItem!
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "TextBlock");

        Assert.NotNull(icon);
        Assert.Equal("\uE118", icon!.Attribute("Text")?.Value);
        Assert.Contains("Segoe", icon.Attribute("FontFamily")?.Value ?? "");
    }

    [Fact]
    public void DownloadViewLogViewerSupportsMouseTextSelection()
    {
        var document = XDocument.Load(GetViewPath("DownloadView.xaml"));

        var logTextBox = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "TextBox"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name"
                    && attribute.Value == "LogTextBox"));

        Assert.NotNull(logTextBox);
        Assert.Equal("True", logTextBox!.Attribute("IsReadOnly")?.Value);
        Assert.Equal("True", logTextBox.Attribute("AcceptsReturn")?.Value);
        Assert.Contains("LogText", logTextBox.Attribute("Text")?.Value ?? "");
        Assert.Equal("Auto", logTextBox.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.Equal("Auto", logTextBox.Attribute("HorizontalScrollBarVisibility")?.Value);

        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName == "ListBox"
            && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "LogList"));
    }

    [Fact]
    public void DownloadViewLogAreaAllocatesMoreVerticalSpace()
    {
        var document = XDocument.Load(GetViewPath("DownloadView.xaml"));

        var logRow = document
            .Descendants()
            .Where(element => element.Name.LocalName == "RowDefinition")
            .FirstOrDefault(element => element.Attribute("Height")?.Value is string height
                && int.TryParse(height, out var value)
                && value >= 300);

        Assert.NotNull(logRow);
    }

    [Fact]
    public void DownloadViewUsesStitchSingleDownloadWorkspaceCopy()
    {
        var document = XDocument.Load(GetViewPath("DownloadView.xaml"));
        var texts = document.Descendants().Attributes("Text").Select(attribute => attribute.Value).ToList();

        Assert.Contains("粘贴视频链接", texts);
        Assert.Contains(texts, text => text.Contains("YouTube", StringComparison.Ordinal)
            && text.Contains("Bilibili", StringComparison.Ordinal));
        Assert.Contains("开始下载", texts);
        Assert.Contains("下载日志", texts);
        Assert.Contains("网速限制", texts);
        Assert.Contains("保存目录", texts);
        Assert.Contains("代理状态", texts);
    }

    [Fact]
    public void BatchDownloadViewUsesStitchQueueConsoleCopy()
    {
        var document = XDocument.Load(GetViewPath("BatchDownloadView.xaml"));
        var texts = document.Descendants().Attributes("Text").Select(attribute => attribute.Value).ToList();

        Assert.Contains(texts, text => text.Contains("输入多个视频链接", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("已检测到", StringComparison.Ordinal));
        Assert.Contains("开始批量下载", texts);
        Assert.Contains("下载队列", texts);
        Assert.Contains("暂停全部", texts);
        Assert.Contains("取消全部", texts);

        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "Button"
            && element.Attributes("Command").Any(attribute =>
                attribute.Value.Contains("PauseAllCommand", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("BatchDownloadView.xaml", "PasteUrlsCommand")]
    [InlineData("BatchDownloadView.xaml", "StartBatchDownloadCommand")]
    [InlineData("BatchDownloadView.xaml", "CancelAllCommand")]
    public void BatchDownloadPrimaryActionButtonsUseFluentIconTextContent(string viewFileName, string commandName)
    {
        var document = XDocument.Load(GetViewPath(viewFileName));

        var button = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Button"
                && element.Attributes("Command").Any(attribute =>
                    attribute.Value.Contains(commandName, StringComparison.Ordinal)));

        Assert.NotNull(button);
        Assert.Null(button!.Attribute("Content"));

        var textBlocks = button.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .ToList();

        Assert.Contains(textBlocks, textBlock =>
            (textBlock.Attribute("FontFamily")?.Value ?? "").Contains("Segoe", StringComparison.Ordinal));
        Assert.Contains(textBlocks, textBlock =>
            !string.IsNullOrWhiteSpace(textBlock.Attribute("Text")?.Value)
            && !textBlock.Attributes().Any(attribute => attribute.Name.LocalName == "FontFamily"));
    }

    [Fact]
    public void HistoryViewUsesStitchMediaLibraryGrid()
    {
        var document = XDocument.Load(GetViewPath("HistoryView.xaml"));
        var texts = document.Descendants().Attributes("Text").Select(attribute => attribute.Value).ToList();

        Assert.Contains("下载历史", texts);
        Assert.Contains(texts, text => text.Contains("已完成任务", StringComparison.Ordinal));
        Assert.Contains("全部", texts);
        Assert.Contains("视频", texts);
        Assert.Contains("音频", texts);

        Assert.Contains(document.Descendants(), element => element.Name.LocalName == "WrapPanel");
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "Button"
            && element.Attributes("Command").Any(attribute =>
                attribute.Value.Contains("SetMediaFilterCommand", StringComparison.Ordinal)));
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "Image"
            && element.Attributes("Source").Any(attribute =>
                attribute.Value.Contains("ThumbnailUrl", StringComparison.Ordinal)));
    }

    [Fact]
    public void SettingsViewUsesStitchSettingsCenterInformationArchitecture()
    {
        var document = XDocument.Load(GetViewPath("SettingsView.xaml"));
        var texts = document.Descendants().Attributes("Text").Select(attribute => attribute.Value).ToList();

        Assert.Contains("系统设置", texts);
        Assert.Contains(texts, text => text.Contains("管理下载环境", StringComparison.Ordinal));
        Assert.Contains("环境检测", texts);
        Assert.Contains("下载设置", texts);
        Assert.Contains("代理设置", texts);
        Assert.Contains("Cookie 设置", texts);
        Assert.Contains("性能设置", texts);
        Assert.Contains("默认保存目录", texts);
        Assert.Contains("默认下载格式", texts);
        Assert.Contains("最大下载分辨率", texts);
        Assert.Contains("启用网络代理", texts);
        Assert.Contains("启用 aria2c 外部下载器", texts);
        Assert.Contains("保存配置", texts);
    }

    [Theory]
    [InlineData("download")]
    [InlineData("batch")]
    [InlineData("history")]
    [InlineData("settings")]
    public void MainWindowNavigationItemsExposeTooltipAndAutomationName(string commandParameter)
    {
        var document = XDocument.Load(GetRootPath("MainWindow.xaml"));

        var navItem = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "RadioButton"
                && element.Attribute("CommandParameter")?.Value == commandParameter);

        Assert.NotNull(navItem);
        Assert.False(string.IsNullOrWhiteSpace(navItem!.Attribute("ToolTip")?.Value));
        Assert.False(string.IsNullOrWhiteSpace(navItem
            .Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == "AutomationProperties.Name")
            ?.Value));
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

        var button = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Button"
                && element.Attributes("Command").Any(attribute =>
                    attribute.Value.Contains(commandName, StringComparison.Ordinal)));

        Assert.NotNull(button);
        Assert.False(string.IsNullOrWhiteSpace(button!.Attribute("ToolTip")?.Value));
        Assert.False(string.IsNullOrWhiteSpace(button
            .Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == "AutomationProperties.Name")
            ?.Value));
    }

    [Theory]
    [InlineData("PasteUrlCommand")]
    [InlineData("StartDownloadCommand")]
    [InlineData("BrowseDirectoryCommand")]
    [InlineData("CopyLogCommand")]
    public void DownloadViewPrimaryActionButtonsUseFluentIconTextContent(string commandName)
    {
        var document = XDocument.Load(GetViewPath("DownloadView.xaml"));

        var button = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Button"
                && element.Attributes("Command").Any(attribute =>
                    attribute.Value.Contains(commandName, StringComparison.Ordinal)));

        Assert.NotNull(button);
        Assert.Null(button!.Attribute("Content"));

        var textBlocks = button.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .ToList();

        Assert.Contains(textBlocks, textBlock =>
            (textBlock.Attribute("FontFamily")?.Value ?? "").Contains("Segoe", StringComparison.Ordinal));
        Assert.Contains(textBlocks, textBlock =>
            !string.IsNullOrWhiteSpace(textBlock.Attribute("Text")?.Value)
            && !textBlock.Attributes().Any(attribute => attribute.Name.LocalName == "FontFamily"));
    }

    [Theory]
    [InlineData("SearchCommand")]
    [InlineData("ClearAllCommand")]
    [InlineData("OpenFolderCommand")]
    [InlineData("DeleteItemCommand")]
    public void HistoryViewActionButtonsUseFluentIconContent(string commandName)
    {
        var document = XDocument.Load(GetViewPath("HistoryView.xaml"));

        var button = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Button"
                && element.Attributes("Command").Any(attribute =>
                    attribute.Value.Contains(commandName, StringComparison.Ordinal)));

        Assert.NotNull(button);
        Assert.Null(button!.Attribute("Content"));

        var icon = button.Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "TextBlock"
                && (element.Attribute("FontFamily")?.Value ?? "").Contains("Segoe", StringComparison.Ordinal));

        Assert.NotNull(icon);
    }

    [Theory]
    [InlineData("BatchDownloadView.xaml")]
    [InlineData("HistoryView.xaml")]
    public void PlatformLabelsUseStringVisibilityConverter(string viewFileName)
    {
        var document = XDocument.Load(GetViewPath(viewFileName));

        var platformVisibilityBindings = document
            .Descendants()
            .Attributes("Visibility")
            .Select(attribute => attribute.Value)
            .Where(value => value.Contains("Binding Platform", StringComparison.Ordinal)
                && value.Contains("Converter=", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(platformVisibilityBindings);
        Assert.All(platformVisibilityBindings, binding =>
            Assert.Contains("StringToVisibility", binding));
    }

    [Theory]
    [InlineData("CheckEnvironmentCommand", "CanCheckEnvironment")]
    [InlineData("InstallMissingToolsCommand", "CanInstallMissingTools")]
    [InlineData("UpdateYtDlpCommand", "CanUpdateYtDlp")]
    public void SettingsEnvironmentButtonsBindExpectedEnabledState(string commandName, string enabledProperty)
    {
        var document = XDocument.Load(GetViewPath("SettingsView.xaml"));

        var button = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Button"
                && element.Attributes("Command").Any(attribute =>
                    attribute.Value.Contains(commandName, StringComparison.Ordinal)));

        Assert.NotNull(button);
        var isEnabled = button!.Attribute("IsEnabled")?.Value ?? "";
        Assert.Contains(enabledProperty, isEnabled);
    }

    [Fact]
    public void SettingsUpdateStatusMessageVisibilityUsesMessageContent()
    {
        var document = XDocument.Load(GetViewPath("SettingsView.xaml"));

        var statusTextBlock = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "TextBlock"
                && element.Attributes("Text").Any(attribute =>
                    attribute.Value.Contains("UpdateStatusMessage", StringComparison.Ordinal)));

        Assert.NotNull(statusTextBlock);
        var visibility = statusTextBlock!.Attribute("Visibility")?.Value ?? "";
        Assert.Contains("UpdateStatusMessage", visibility);
        Assert.Contains("StringToVisibility", visibility);
    }

    [Fact]
    public void SettingsInstallStatusStageVisibilityUsesStageContent()
    {
        var document = XDocument.Load(GetViewPath("SettingsView.xaml"));

        var stageTextBlock = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "TextBlock"
                && element.Attributes("Text").Any(attribute =>
                    attribute.Value.Contains("InstallStatusStage", StringComparison.Ordinal)));

        Assert.NotNull(stageTextBlock);
        var visibility = stageTextBlock!.Attribute("Visibility")?.Value ?? "";
        Assert.Contains("InstallStatusStage", visibility);
        Assert.Contains("StringToVisibility", visibility);
    }

    [Fact]
    public void SettingsViewExposesAria2cToggle()
    {
        var document = XDocument.Load(GetViewPath("SettingsView.xaml"));

        var aria2cToggle = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "ToggleButton"
                && element.Attributes("IsChecked").Any(attribute =>
                    attribute.Value.Contains("UseAria2c", StringComparison.Ordinal)));

        Assert.NotNull(aria2cToggle);
        Assert.Contains(
            document.Descendants().Attributes("Text").Select(attribute => attribute.Value),
            text => text.Contains("aria2c", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("BatchDownloadView.xaml")]
    [InlineData("HistoryView.xaml")]
    [InlineData("SettingsView.xaml")]
    public void IconOnlyButtonsExposeTooltipAndAutomationName(string viewFileName)
    {
        var document = XDocument.Load(GetViewPath(viewFileName));

        var missingHints = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .Where(element => IsIconOnlyContent(element.Attribute("Content")?.Value))
            .Select(element => new
            {
                Content = element.Attribute("Content")?.Value ?? "",
                ToolTip = element.Attribute("ToolTip")?.Value ?? "",
                AutomationName = element
                    .Attributes()
                    .FirstOrDefault(attribute =>
                        attribute.Name.LocalName == "AutomationProperties.Name")
                    ?.Value ?? ""
            })
            .Where(button =>
                string.IsNullOrWhiteSpace(button.ToolTip)
                || string.IsNullOrWhiteSpace(button.AutomationName))
            .Select(button =>
                $"{viewFileName} Content=\"{button.Content}\" ToolTip=\"{button.ToolTip}\" AutomationProperties.Name=\"{button.AutomationName}\"")
            .ToList();

        Assert.True(
            missingHints.Count == 0,
            "Icon-only buttons must expose ToolTip and AutomationProperties.Name. Missing: "
                + string.Join("; ", missingHints));
    }

    private static string GetViewPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Views", fileName);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find Views/{fileName} from test output directory.");
    }

    private static string GetRootPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {fileName} from test output directory.");
    }

    private static bool IsIconOnlyContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var value = content.Trim();
        return value.Length <= 3 && value.All(character => !char.IsLetterOrDigit(character));
    }
}
