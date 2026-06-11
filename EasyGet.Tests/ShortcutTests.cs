using System;
using System.IO;
using System.Linq;
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

    [Fact]
    public void MainWindowCodeBehindContainsPreviewKeyDownHandler()
    {
        var codePath = GetRootPath("MainWindow.xaml.cs");
        var codeContent = File.ReadAllText(codePath);
        Assert.Contains("PreviewKeyDown += MainWindow_PreviewKeyDown", codeContent);
        Assert.Contains("private void MainWindow_PreviewKeyDown", codeContent);
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
}
