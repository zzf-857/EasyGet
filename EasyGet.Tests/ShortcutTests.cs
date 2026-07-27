using System;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Xml.Linq;
using Xunit;

namespace EasyGet.Tests;

public class ShortcutTests
{
    [Fact]
    public void SettingsViewDoesNotRenderInstructionalShortcutCopy()
    {
        var viewPath = GetViewPath("SettingsView.xaml");
        var document = XDocument.Load(viewPath);
        var textBlocks = document
            .Descendants()
            .Where(e => e.Name.LocalName == "TextBlock")
            .Select(e => e.Attribute("Text")?.Value ?? "")
            .ToList();

        Assert.DoesNotContain(textBlocks, text => text.Contains("键盘快捷键", StringComparison.Ordinal));
        Assert.DoesNotContain(textBlocks, text => text.Contains("Ctrl+1~4", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Key.D1, "download")]
    [InlineData(Key.D2, "batch")]
    [InlineData(Key.D3, "history")]
    [InlineData(Key.D4, "settings")]
    [InlineData(Key.D5, null)]
    public void NavigationShortcutsMatchVisiblePages(Key key, string? expectedPage)
    {
        Assert.Equal(expectedPage, MainWindow.ResolveNavigationShortcut(key));
    }

    [Fact]
    public void DownloadViewUnsubscribesFromLogCollectionWhenUnloaded()
    {
        var code = File.ReadAllText(GetViewPath("DownloadView.xaml.cs"));

        Assert.Contains("Unloaded += DownloadView_Unloaded", code, StringComparison.Ordinal);
        Assert.Contains("CollectionChanged -= LogLines_CollectionChanged", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CollectionChanged += (_, _)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowCodeBehindContainsPreviewKeyDownHandler()
    {
        var codePath = GetRootPath("MainWindow.xaml.cs");
        var codeContent = File.ReadAllText(codePath);
        Assert.Contains("PreviewKeyDown += MainWindow_PreviewKeyDown", codeContent);
        Assert.Contains("private void MainWindow_PreviewKeyDown", codeContent);
    }

    [Fact]
    public void DesignerKeyboardWorkflowsAreConnectedToRealViewsAndCommands()
    {
        var mainWindow = File.ReadAllText(GetRootPath("MainWindow.xaml.cs"));
        var downloadView = File.ReadAllText(GetViewPath("DownloadView.xaml"));
        var batchView = File.ReadAllText(GetViewPath("BatchDownloadView.xaml"));
        var batchCode = File.ReadAllText(GetViewPath("BatchDownloadView.xaml.cs"));
        var historyView = File.ReadAllText(GetViewPath("HistoryView.xaml"));
        var historyCode = File.ReadAllText(GetViewPath("HistoryView.xaml.cs"));

        Assert.Contains("Key.F", mainWindow, StringComparison.Ordinal);
        Assert.Contains("FocusSearch", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Key.Space or Key.Delete", mainWindow, StringComparison.Ordinal);
        Assert.Contains("DeleteSelectedCommand", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RunPrimaryActionCommand", downloadView, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"QueueList\"", batchView, StringComparison.Ordinal);
        Assert.Contains("TryHandleQueueShortcut", batchCode, StringComparison.Ordinal);
        Assert.Contains("PauseTaskCommand", batchCode, StringComparison.Ordinal);
        Assert.Contains("ResumeTaskCommand", batchCode, StringComparison.Ordinal);
        Assert.Contains("CancelTaskCommand", batchCode, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HistorySearchBox\"", historyView, StringComparison.Ordinal);
        Assert.Contains("HistorySearchBox.SelectAll", historyCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ClipboardMonitoringIsGlobalAndDoesNotForceNavigation()
    {
        var mainWindow = File.ReadAllText(GetRootPath("MainWindow.xaml.cs"));
        var activatedStart = mainWindow.IndexOf(
            "private void MainWindow_Activated",
            StringComparison.Ordinal);
        var activatedEnd = mainWindow.IndexOf(
            "private static T? FindVisualChild",
            activatedStart,
            StringComparison.Ordinal);
        var activatedBody = mainWindow[activatedStart..activatedEnd];

        Assert.Contains("ClipboardMonitoringEnabled", activatedBody, StringComparison.Ordinal);
        Assert.Contains("CheckClipboardAndPrompt", activatedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedNavIndex", activatedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("NavigateCommand", activatedBody, StringComparison.Ordinal);
    }

    private static string GetViewPath(string fileName)
        => TestRepositoryPaths.GetViewPath(fileName);

    private static string GetRootPath(string fileName)
        => TestRepositoryPaths.GetRootPath(fileName);
}
