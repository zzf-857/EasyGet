using System.Xml.Linq;
using Xunit;

namespace EasyGet.Tests;

public class XamlBindingTests
{
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
}
