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

    [Theory]
    [InlineData("DownloadView.xaml")]
    [InlineData("BatchDownloadView.xaml")]
    [InlineData("HistoryView.xaml")]
    [InlineData("SettingsView.xaml")]
    public void PageRootUsesMotionPageEnterBehavior(string viewFileName)
    {
        var document = XDocument.Load(GetViewPath(viewFileName));

        Assert.Equal("UserControl", document.Root?.Name.LocalName);
        Assert.Contains(document.Root!.Attributes(), attribute =>
            attribute.Name.LocalName == "Motion.PageEnter"
            && attribute.Value == "True");
    }

    [Fact]
    public void MainWindowToastUsesAnimatedVisibilityStates()
    {
        var document = XDocument.Load(GetRootPath("MainWindow.xaml"));

        var itemsControl = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "ItemsControl"
                && element.Attribute("ItemsSource")?.Value == "{Binding Notifications}");

        Assert.NotNull(itemsControl);

        var dataTemplates = itemsControl.Descendants()
            .Where(element => element.Name.LocalName == "DataTemplate")
            .ToList();

        Assert.NotEmpty(dataTemplates);

        var dataTriggers = itemsControl.Descendants()
            .Where(element => element.Name.LocalName == "DataTrigger")
            .ToList();

        Assert.Contains(dataTriggers, trigger =>
            trigger.Attribute("Binding")?.Value == "{Binding IsSuccess}");
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
            text => text.Contains("AppVersion", StringComparison.Ordinal));

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
                (textBlock.Attribute("FontFamily")?.Value ?? "").Contains("Segoe", StringComparison.Ordinal)
                || (textBlock.Attribute("Style")?.Value ?? "").Contains("IconGlyph", StringComparison.Ordinal));
            Assert.Contains(textBlocks, textBlock =>
                (textBlock.Attribute("Text")?.Value ?? "") == item.Attribute("ToolTip")?.Value);
        });

        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "ContentControl"
            && (element.Attribute("Content")?.Value ?? "").Contains("CurrentPageTitle", StringComparison.Ordinal));
    }

    [Fact]
    public void MainWindowSidebarDoesNotRenderAccountStatusFooter()
    {
        var document = XDocument.Load(GetRootPath("MainWindow.xaml"));

        var sidebarDockPanel = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "DockPanel"
                && element.Attribute("LastChildFill") is not null
                && element.Attribute("Margin")?.Value == "14,16");

        Assert.NotNull(sidebarDockPanel);
        Assert.Equal("False", sidebarDockPanel!.Attribute("LastChildFill")?.Value);

        var textAttributes = document.Descendants()
            .Attributes("Text")
            .Select(attribute => attribute.Value)
            .ToList();

        Assert.DoesNotContain("Power User", textAttributes);
        Assert.DoesNotContain(textAttributes, text => text.Contains("StatusMessage", StringComparison.Ordinal));
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
        Assert.True((icon.Attribute("FontFamily")?.Value ?? "").Contains("Segoe", StringComparison.Ordinal)
            || (icon.Attribute("Style")?.Value ?? "").Contains("IconGlyph", StringComparison.Ordinal));
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

        var logBorder = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Border"
                && element.Attribute("Background")?.Value == "{StaticResource BgConsoleBrush}"
                && element.Attribute("Height")?.Value is string height
                && int.TryParse(height, out var value)
                && value >= 300);

        Assert.NotNull(logBorder);
    }

    [Fact]
    public void DownloadProgressCardStaysVisibleForCompletedAndFailedStates()
    {
        var document = XDocument.Load(GetViewPath("DownloadView.xaml"));
        var source = document.ToString(SaveOptions.DisableFormatting);

        Assert.Contains("IsProgressCardVisible", source);
        Assert.DoesNotContain("Visibility=\"{Binding IsDownloading", source);
        Assert.Contains("IsCompleted", source);
        Assert.Contains("IsTaskFailed", source);
        Assert.Contains("OpenCurrentFolderCommand", source);
        Assert.Contains("PlayCurrentFileCommand", source);
        Assert.Contains("RetryCurrentDownloadCommand", source);
        Assert.Contains("SuccessBrush", source);
        Assert.Contains("ErrorContainerBrush", source);
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
        Assert.Contains("详细日志", texts);
        Assert.Contains("并发分片", texts);
        Assert.Contains("保存目录", texts);
        Assert.Contains("代理状态", texts);
        Assert.DoesNotContain("无限制", texts);
        Assert.DoesNotContain("系统默认", texts);
    }

    [Fact]
    public void DownloadViewExposesParsePreviewWorkflow()
    {
        var document = XDocument.Load(GetViewPath("DownloadView.xaml"));
        var source = document.ToString(SaveOptions.DisableFormatting);
        var texts = document.Descendants().Attributes("Text").Select(attribute => attribute.Value).ToList();

        Assert.Contains("解析视频", texts);
        Assert.Contains("开始下载", texts);
        Assert.Contains("视频预览", texts);
        Assert.Contains("解析失败", texts);
        Assert.Contains("ParseCommand", source);
        Assert.Contains("StartDownloadCommand", source);
        Assert.Contains("PreviewInfo", source);
        Assert.Contains("IsParsing", source);
        Assert.Contains("IsReady", source);
        Assert.Contains("IsFailed", source);
        Assert.Contains("PreviewDurationText, Mode=OneWay", source);
        Assert.Contains("PreviewFileSizeText, Mode=OneWay", source);
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
        Assert.DoesNotContain(texts, text => text.Contains("SERVER STATUS", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, text => text.Contains("ACTIVE THREADS", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, text => text.Contains("V1.0.8", StringComparison.Ordinal));

        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "Button"
            && element.Attributes("Command").Any(attribute =>
                attribute.Value.Contains("PauseAllCommand", StringComparison.Ordinal)));
    }

    [Fact]
    public void ViewsDoNotRenderStitchPlaceholderStatusCopy()
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
            "PRO ACCOUNT",
            "SERVER STATUS",
            "V1.0.8",
            "v1.2.4",
            "磁盘空间充足",
            "Batch Operations",
            "无限制",
            "系统默认"
        };

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (var text in forbidden)
                Assert.DoesNotContain(text, source, StringComparison.Ordinal);
        }
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
            (textBlock.Attribute("FontFamily")?.Value ?? "").Contains("Segoe", StringComparison.Ordinal)
            || (textBlock.Attribute("Style")?.Value ?? "").Contains("IconGlyph", StringComparison.Ordinal));
        Assert.Contains(textBlocks, textBlock =>
            !string.IsNullOrWhiteSpace(textBlock.Attribute("Text")?.Value)
            && (!textBlock.Attributes().Any(attribute => attribute.Name.LocalName == "FontFamily")
                || (textBlock.Attribute("Style")?.Value ?? "").Contains("IconGlyph", StringComparison.Ordinal)));
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
            element.Name.LocalName == "RadioButton"
            && element.Attributes("Command").Any(attribute =>
                attribute.Value.Contains("SetMediaFilterCommand", StringComparison.Ordinal)));
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "Image"
            && element.Attributes("Source").Any(attribute =>
                attribute.Value.Contains("ThumbnailUrl", StringComparison.Ordinal)));
    }

    [Fact]
    public void HistoryViewSearchBoxKeepsStableWideSearchField()
    {
        var document = XDocument.Load(GetViewPath("HistoryView.xaml"));

        var searchBox = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Border"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name"
                    && attribute.Value == "HistorySearchBox"));

        Assert.NotNull(searchBox);
        Assert.True(
            int.TryParse(searchBox!.Attribute("MinWidth")?.Value, out var minWidth)
            && minWidth >= 520,
            "History search field should keep a wide, stable pill shape even when the query is empty.");
    }

    [Fact]
    public void HistoryViewCardsRevealFullQuickActionsOnHover()
    {
        var document = XDocument.Load(GetViewPath("HistoryView.xaml"));

        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "Trigger"
            && element.Attribute("SourceName")?.Value == "HistoryCard"
            && element.Attribute("Property")?.Value == "IsMouseOver"
            && element.Attribute("Value")?.Value == "True");
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "BlurEffect"
            && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "ThumbnailBlur"));
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "TranslateTransform"
            && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "HoverOverlayTranslate")
            && element.Attribute("Y")?.Value == "6");
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "Setter"
            && element.Attribute("TargetName")?.Value == "HoverOverlay"
            && element.Attribute("Property")?.Value == "Opacity"
            && element.Attribute("Value")?.Value == "1");
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "DoubleAnimation"
            && element.Attribute("Storyboard.TargetName")?.Value == "HoverOverlay"
            && element.Attribute("Storyboard.TargetProperty")?.Value == "Opacity"
            && element.Attribute("To")?.Value == "1"
            && element.Attribute("Duration")?.Value?.Contains("MotionDurationFast", StringComparison.Ordinal) == true);
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "DoubleAnimation"
            && element.Attribute("Storyboard.TargetName")?.Value == "ThumbnailBlur"
            && element.Attribute("Storyboard.TargetProperty")?.Value == "Radius"
            && element.Attribute("To")?.Value == "7");
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "DoubleAnimation"
            && element.Attribute("Storyboard.TargetName")?.Value == "HoverOverlayTranslate"
            && element.Attribute("Storyboard.TargetProperty")?.Value == "Y"
            && element.Attribute("To")?.Value == "0");

        var commands = document.Descendants()
            .Attributes("Command")
            .Select(attribute => attribute.Value)
            .ToList();

        Assert.Contains(commands, command => command.Contains("OpenFolderCommand", StringComparison.Ordinal));
        Assert.Contains(commands, command => command.Contains("PreviewFileCommand", StringComparison.Ordinal));
        Assert.Contains(commands, command => command.Contains("OpenSourceUrlCommand", StringComparison.Ordinal));
        Assert.Contains(commands, command => command.Contains("DeleteItemCommand", StringComparison.Ordinal));

        var previewButton = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Button"
                && element.Attributes("Command").Any(attribute =>
                    attribute.Value.Contains("PreviewFileCommand", StringComparison.Ordinal)));

        Assert.NotNull(previewButton);
        Assert.Equal("预览文件", previewButton!.Attribute("ToolTip")?.Value);
        Assert.Contains("FileExists", previewButton.Attribute("IsEnabled")?.Value ?? "");

        var sourceButton = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Button"
                && element.Attributes("Command").Any(attribute =>
                    attribute.Value.Contains("OpenSourceUrlCommand", StringComparison.Ordinal)));

        Assert.NotNull(sourceButton);
        Assert.Equal("打开原网页", sourceButton!.Attribute("ToolTip")?.Value);
        Assert.Contains("HttpUrlToBool", sourceButton.Attribute("IsEnabled")?.Value ?? "");
    }

    [Fact]
    public void HistoryViewShowsAttachmentCountOnHistoryCards()
    {
        var source = File.ReadAllText(GetViewPath("HistoryView.xaml"));

        Assert.Contains("AttachmentCountText", source, StringComparison.Ordinal);
        Assert.Contains("HasAttachmentFiles", source, StringComparison.Ordinal);
        Assert.Contains("附属", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DownloadView.xaml", "粘贴视频链接", "支持 YouTube")]
    [InlineData("BatchDownloadView.xaml", "批量下载", "输入多个视频链接")]
    [InlineData("HistoryView.xaml", "下载历史", "共计")]
    [InlineData("SettingsView.xaml", "系统设置", "管理下载环境")]
    public void PageMainTitlesUseUnifiedTypography(string viewFileName, string titleText, string subtitleText)
    {
        var document = XDocument.Load(GetViewPath(viewFileName));

        var title = FindTextBlockByText(document, titleText);

        Assert.NotNull(title);
        var fontSize = title!.Attribute("FontSize")?.Value;
        var fontWeight = title.Attribute("FontWeight")?.Value;
        var style = title.Attribute("Style")?.Value ?? "";
        Assert.True(fontSize == "28" || style.Contains("TextPageTitle", StringComparison.Ordinal));
        Assert.True(fontWeight == "SemiBold" || style.Contains("TextPageTitle", StringComparison.Ordinal));

        var subtitle = FindTextBlockByText(document, subtitleText);

        Assert.NotNull(subtitle);
        var subFontSize = subtitle!.Attribute("FontSize")?.Value;
        var subStyle = subtitle.Attribute("Style")?.Value ?? "";
        Assert.True(subFontSize == "14" || subStyle.Contains("TextBodyStrong", StringComparison.Ordinal) || subStyle.Contains("TextBody", StringComparison.Ordinal));
        var subForeground = subtitle.Attribute("Foreground")?.Value ?? "";
        Assert.True(subForeground.Contains("TextSecondaryBrush", StringComparison.Ordinal) 
            || subStyle.Contains("TextBodyStrong", StringComparison.Ordinal)
            || subStyle.Contains("TextBody", StringComparison.Ordinal));
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
            (textBlock.Attribute("FontFamily")?.Value ?? "").Contains("Segoe", StringComparison.Ordinal)
            || (textBlock.Attribute("Style")?.Value ?? "").Contains("IconGlyph", StringComparison.Ordinal));
        Assert.Contains(textBlocks, textBlock =>
            !string.IsNullOrWhiteSpace(textBlock.Attribute("Text")?.Value)
            && (!textBlock.Attributes().Any(attribute => attribute.Name.LocalName == "FontFamily")
                || (textBlock.Attribute("Style")?.Value ?? "").Contains("IconGlyph", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("ClearAllCommand")]
    [InlineData("OpenFolderCommand")]
    [InlineData("PreviewFileCommand")]
    [InlineData("OpenSourceUrlCommand")]
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
                && ((element.Attribute("FontFamily")?.Value ?? "").Contains("Segoe", StringComparison.Ordinal)
                    || (element.Attribute("Style")?.Value ?? "").Contains("IconGlyph", StringComparison.Ordinal)));

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

    [Fact]
    public void SettingsViewExposesApplicationUpdateControls()
    {
        var document = XDocument.Load(GetViewPath("SettingsView.xaml"));
        var source = document.ToString(SaveOptions.DisableFormatting);
        var texts = document.Descendants().Attributes("Text").Select(attribute => attribute.Value).ToList();

        Assert.Contains("版本与更新", texts);
        Assert.Contains("当前软件版本", texts);
        Assert.Contains("检查新版本", texts);
        Assert.Contains("下载更新包", texts);
        Assert.Contains("安装更新", texts);
        Assert.Contains("AppVersionText", source);
        Assert.Contains("CheckAppUpdateCommand", source);
        Assert.Contains("DownloadAppUpdateCommand", source);
        Assert.Contains("InstallAppUpdateCommand", source);
        Assert.Contains("AppUpdateProgress", source);
        Assert.Contains("AccentProgressBar", source);
    }

    [Fact]
    public void SettingsViewExposesDouyinSpecialDownloadSettings()
    {
        var document = XDocument.Load(GetViewPath("SettingsView.xaml"));
        var source = document.ToString(SaveOptions.DisableFormatting);
        var texts = document.Descendants().Attributes("Text").Select(attribute => attribute.Value).ToList();

        Assert.Contains("抖音专项下载", texts);
        Assert.Contains("启用专项引擎", texts);
        Assert.Contains("用户作品模式", texts);
        Assert.Contains("下载数量上限", texts);
        Assert.Contains("下载封面", texts);
        Assert.Contains("下载音乐", texts);
        Assert.Contains("下载评论", texts);
        Assert.Contains("下载作者头像", texts);
        Assert.Contains("保存原始 JSON", texts);
        Assert.Contains("启用本地去重数据库", texts);
        Assert.Contains("增量下载", texts);
        Assert.Contains(texts, text => text.Contains("在下载目录下维护本地记录", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("依赖本地去重数据库", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("复用上方全局 Cookie", StringComparison.Ordinal));

        Assert.Contains("EnableDouyinSpecialEngine", source);
        Assert.Contains("DouyinModeOptions", source);
        Assert.Contains("DouyinMode", source);
        Assert.Contains("DouyinLimit", source);
        Assert.Contains("DouyinDownloadCover", source);
        Assert.Contains("DouyinDownloadMusic", source);
        Assert.Contains("DouyinDownloadComments", source);
        Assert.Contains("DouyinDownloadAvatar", source);
        Assert.Contains("DouyinDownloadJson", source);
        Assert.Contains("DouyinEnableDatabase", source);
        Assert.Contains("DouyinIncrementalDownload", source);

        var databaseToggle = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "ToggleButton"
                && (element.Attribute("IsChecked")?.Value ?? "").Contains("DouyinEnableDatabase", StringComparison.Ordinal));
        Assert.NotNull(databaseToggle);
        Assert.Contains("EnableDouyinSpecialEngine", databaseToggle!.ToString(SaveOptions.DisableFormatting), StringComparison.Ordinal);

        var incrementalToggle = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "ToggleButton"
                && (element.Attribute("IsChecked")?.Value ?? "").Contains("DouyinIncrementalDownload", StringComparison.Ordinal));
        Assert.NotNull(incrementalToggle);
        var incrementalToggleSource = incrementalToggle!.ToString(SaveOptions.DisableFormatting);
        Assert.Contains("EnableDouyinSpecialEngine", incrementalToggleSource, StringComparison.Ordinal);
        Assert.Contains("DouyinEnableDatabase", incrementalToggleSource, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchDownloadViewUsesStatefulQueueCards()
    {
        var document = XDocument.Load(GetViewPath("BatchDownloadView.xaml"));
        var source = document.ToString(SaveOptions.DisableFormatting);

        // Verify left status line presence
        Assert.Contains("4px left status line", source);
        // Verify stateful button commands
        Assert.Contains("PauseTaskCommand", source);
        Assert.Contains("ResumeTaskCommand", source);
        Assert.Contains("RetryTaskCommand", source);
        Assert.Contains("CancelTaskCommand", source);
        // Verify playback scrim has been removed from ListBox.ItemTemplate
        var itemTemplate = document.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ListBox.ItemTemplate");
        Assert.NotNull(itemTemplate);
        var itemTemplateSource = itemTemplate!.ToString(SaveOptions.DisableFormatting);
        Assert.DoesNotContain("ScrimBrush", itemTemplateSource);
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

    [Fact]
    public void DeadControlsAreCompletelyRemovedFromXaml()
    {
        var mainDocument = XDocument.Load(GetRootPath("MainWindow.xaml"));
        var toast = mainDocument
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Border"
                && element.Attribute("Name")?.Value == "NotificationToast");
        Assert.Null(toast);

        var historyDocument = XDocument.Load(GetViewPath("HistoryView.xaml"));
        var searchButton = historyDocument
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Button"
                && element.Attribute("Command")?.Value == "{Binding SearchCommand}");
        Assert.Null(searchButton);

        var collapsedFilterStack = historyDocument
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "StackPanel"
                && element.Attribute("Visibility")?.Value == "Collapsed"
                && element.Descendants().Any(child =>
                    child.Name.LocalName == "Button"
                    && child.Attribute("Command")?.Value == "{Binding SetMediaFilterCommand}"));
        Assert.Null(collapsedFilterStack);
    }

    private static string GetViewPath(string fileName)
        => TestRepositoryPaths.GetViewPath(fileName);

    private static string GetRootPath(string fileName)
        => TestRepositoryPaths.GetRootPath(fileName);

    private static bool IsIconOnlyContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var value = content.Trim();
        return value.Length <= 3 && value.All(character => !char.IsLetterOrDigit(character));
    }

    private static XElement? FindTextBlockByText(XDocument document, string text)
    {
        return document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "TextBlock"
                && TextBlockText(element).Contains(text, StringComparison.Ordinal));
    }

    private static string TextBlockText(XElement textBlock)
    {
        var directText = textBlock.Attribute("Text")?.Value ?? "";
        var runText = string.Concat(textBlock
            .Descendants()
            .Where(element => element.Name.LocalName == "Run")
            .Select(element => element.Attribute("Text")?.Value ?? ""));

        return directText + runText;
    }
}
