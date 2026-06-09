using System.Xml.Linq;
using Xunit;

namespace EasyGet.Tests;

public class ThemeStyleTests
{
    [Fact]
    public void ToolPanelBorderStyleUsesVibeTrackerGlassPanelTreatment()
    {
        var document = XDocument.Load(GetThemePath("Generic.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var style = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Style"
                && element.Attribute(x + "Key")?.Value == "ToolPanelBorder"
                && element.Attribute("TargetType")?.Value == "Border");

        Assert.NotNull(style);

        AssertStyleSetter(style!, "Background", "{StaticResource BgSurfaceBrush}");
        AssertStyleSetter(style, "BorderBrush", "{StaticResource BorderPrimaryBrush}");
        AssertStyleSetter(style, "BorderThickness", "1");
        AssertStyleSetter(style, "SnapsToDevicePixels", "True");

        var cornerRadius = style
            .Elements()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Setter"
                && element.Attribute("Property")?.Value == "CornerRadius")
            ?.Attribute("Value")?.Value;

        Assert.True(double.TryParse(cornerRadius, out var radius), "ToolPanelBorder must set a numeric CornerRadius.");
        Assert.InRange(radius, 24, 32);
    }

    [Fact]
    public void ThemeColorTokensFollowVibeTrackerCalmAppleDarkPalette()
    {
        var document = XDocument.Load(GetThemePath("Generic.xaml"));

        AssertColor(document, "BgPrimary", "#080A0D");
        AssertColor(document, "BgSurface", "#12FFFFFF");
        AssertColor(document, "BgHover", "#1BFFFFFF");
        AssertColor(document, "TextPrimary", "#F7F8FB");
        AssertColor(document, "TextSecondary", "#A8B0BD");
        AssertColor(document, "TextMuted", "#707A8A");
        AssertColor(document, "BorderPrimary", "#1FFFFFFF");
        AssertColor(document, "BorderSubtle", "#14FFFFFF");
        AssertColor(document, "Accent", "#74A9FF");
        AssertColor(document, "Success", "#63D693");
        AssertColor(document, "Warning", "#F3BB6C");
        AssertColor(document, "Error", "#FF6B6B");
    }

    [Theory]
    [InlineData("AccentButton")]
    [InlineData("SurfaceButton")]
    public void ButtonStylesDefineDisabledVisualState(string styleKey)
    {
        var document = XDocument.Load(GetThemePath("Generic.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var style = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Style"
                && element.Attribute(x + "Key")?.Value == styleKey);

        Assert.NotNull(style);

        var disabledTrigger = style!
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Trigger"
                && element.Attribute("Property")?.Value == "IsEnabled"
                && element.Attribute("Value")?.Value == "False");

        Assert.NotNull(disabledTrigger);
        Assert.Contains(disabledTrigger!.Descendants(), element =>
            element.Name.LocalName == "Setter"
            && element.Attribute("Property")?.Value == "Opacity");
        Assert.Contains(disabledTrigger.Descendants(), element =>
            element.Name.LocalName == "Setter"
            && element.Attribute("Property")?.Value == "Cursor"
            && element.Attribute("Value")?.Value == "Arrow");
    }

    [Fact]
    public void ComboBoxStyleDefinesDisabledVisualState()
    {
        var document = XDocument.Load(GetThemePath("Generic.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var style = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Style"
                && element.Attribute(x + "Key")?.Value == "DarkComboBox");

        Assert.NotNull(style);

        var disabledTrigger = style!
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Trigger"
                && element.Attribute("Property")?.Value == "IsEnabled"
                && element.Attribute("Value")?.Value == "False");

        Assert.NotNull(disabledTrigger);
        Assert.Contains(disabledTrigger!.Descendants(), element =>
            element.Name.LocalName == "Setter"
            && element.Attribute("Property")?.Value == "Opacity");
        Assert.Contains(disabledTrigger.Descendants(), element =>
            element.Name.LocalName == "Setter"
            && element.Attribute("Property")?.Value == "Cursor"
            && element.Attribute("Value")?.Value == "Arrow");
    }

    [Fact]
    public void ToggleSwitchStyleDefinesDisabledVisualState()
    {
        var document = XDocument.Load(GetThemePath("Generic.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var style = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Style"
                && element.Attribute(x + "Key")?.Value == "ToggleSwitch");

        Assert.NotNull(style);

        var disabledTrigger = style!
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Trigger"
                && element.Attribute("Property")?.Value == "IsEnabled"
                && element.Attribute("Value")?.Value == "False");

        Assert.NotNull(disabledTrigger);
        Assert.Contains(disabledTrigger!.Descendants(), element =>
            element.Name.LocalName == "Setter"
            && element.Attribute("Property")?.Value == "Opacity");
        Assert.Contains(disabledTrigger.Descendants(), element =>
            element.Name.LocalName == "Setter"
            && element.Attribute("Property")?.Value == "Cursor"
            && element.Attribute("Value")?.Value == "Arrow");
    }

    [Fact]
    public void TextBoxStyleDefinesDisabledVisualState()
    {
        var document = XDocument.Load(GetThemePath("Generic.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var style = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Style"
                && element.Attribute(x + "Key")?.Value == "DarkTextBox");

        Assert.NotNull(style);

        var disabledTrigger = style!
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Trigger"
                && element.Attribute("Property")?.Value == "IsEnabled"
                && element.Attribute("Value")?.Value == "False");

        Assert.NotNull(disabledTrigger);
        Assert.Contains(disabledTrigger!.Descendants(), element =>
            element.Name.LocalName == "Setter"
            && element.Attribute("Property")?.Value == "Opacity");
        Assert.Contains(disabledTrigger.Descendants(), element =>
            element.Name.LocalName == "Setter"
            && element.Attribute("Property")?.Value == "Cursor"
            && element.Attribute("Value")?.Value == "Arrow");
    }

    [Fact]
    public void ScrollBarStyleUsesDarkTemplateInsteadOfNativeChrome()
    {
        var document = XDocument.Load(GetThemePath("Generic.xaml"));

        var style = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Style"
                && IsTargetType(element, "ScrollBar"));

        Assert.NotNull(style);
        AssertStyleSetter(style!, "Background", "Transparent");
        AssertStyleSetter(style, "Width", "6");

        var templateSetter = style
            .Elements()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Setter"
                && element.Attribute("Property")?.Value == "Template");

        Assert.NotNull(templateSetter);
        Assert.Contains(templateSetter!.Descendants(), element =>
            element.Name.LocalName == "Track"
            && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "PART_Track"));
        Assert.Contains(templateSetter.Descendants(), element =>
            element.Name.LocalName == "Thumb"
            && (element.Attribute("Style")?.Value ?? "").Contains("ScrollBarThumb", StringComparison.Ordinal));
        Assert.Contains(templateSetter.Descendants(), element =>
            element.Name.LocalName == "Trigger"
            && element.Attribute("Property")?.Value == "Orientation"
            && element.Attribute("Value")?.Value == "Horizontal");
    }

    [Fact]
    public void ScrollBarThumbStyleDefinesHoverAndDisabledStates()
    {
        var document = XDocument.Load(GetThemePath("Generic.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var thumbStyle = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Style"
                && element.Attribute(x + "Key")?.Value == "ScrollBarThumb"
                && IsTargetType(element, "Thumb"));

        Assert.NotNull(thumbStyle);

        var templateSetter = thumbStyle!
            .Elements()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Setter"
                && element.Attribute("Property")?.Value == "Template");

        Assert.NotNull(templateSetter);
        Assert.Contains(templateSetter!.Descendants(), element =>
            element.Name.LocalName == "Border"
            && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "ThumbBorder"));

        Assert.Contains(thumbStyle.Descendants(), element =>
            element.Name.LocalName == "Trigger"
            && element.Attribute("Property")?.Value == "IsMouseOver"
            && element.Attribute("Value")?.Value == "True");
        Assert.Contains(thumbStyle.Descendants(), element =>
            element.Name.LocalName == "Trigger"
            && element.Attribute("Property")?.Value == "IsEnabled"
            && element.Attribute("Value")?.Value == "False");
    }

    private static string GetThemePath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Themes", fileName);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find Themes/{fileName} from test output directory.");
    }

    private static void AssertStyleSetter(XElement style, string property, string expectedValue)
    {
        Assert.Contains(style.Elements(), element =>
            element.Name.LocalName == "Setter"
            && element.Attribute("Property")?.Value == property
            && element.Attribute("Value")?.Value == expectedValue);
    }

    private static void AssertColor(XDocument document, string key, string expectedValue)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var color = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Color"
                && element.Attribute(x + "Key")?.Value == key);

        Assert.NotNull(color);
        Assert.Equal(expectedValue, color!.Value.Trim());
    }

    private static bool IsTargetType(XElement element, string expected)
    {
        var targetType = element.Attribute("TargetType")?.Value ?? "";
        return targetType == expected || targetType == $"{{x:Type {expected}}}";
    }
}
