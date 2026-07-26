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
    public void SettingsViewContainsKeyboardShortcutsHelpText()
    {
        var viewPath = GetViewPath("SettingsView.xaml");
        var document = XDocument.Load(viewPath);
        var textBlocks = document
            .Descendants()
            .Where(e => e.Name.LocalName == "TextBlock")
            .Select(e => e.Attribute("Text")?.Value ?? "")
            .ToList();

        var containsShortcutsText = textBlocks.Any(t => t.Contains("键盘快捷键") && t.Contains("Ctrl+1~4"));
        Assert.True(containsShortcutsText, "SettingsView should contain keyboard shortcuts help text.");
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

    private static string GetViewPath(string fileName)
        => TestRepositoryPaths.GetViewPath(fileName);

    private static string GetRootPath(string fileName)
        => TestRepositoryPaths.GetRootPath(fileName);
}
