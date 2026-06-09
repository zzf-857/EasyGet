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

    private static bool IsIconOnlyContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var value = content.Trim();
        return value.Length <= 3 && value.All(character => !char.IsLetterOrDigit(character));
    }
}
