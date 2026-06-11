using System.Xml.Linq;
using System.Text.RegularExpressions;
using Xunit;
using EasyGet.Services;

namespace EasyGet.Tests;

public class ThemeStyleTests
{
    [Fact]
    public void ToolPanelBorderStyleUsesStitchGlassPanelTreatment()
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
        Assert.InRange(radius, 8, 12);
    }

    [Fact]
    public void ThemeColorTokensFollowStitchEasyGetDarkPalette()
    {
        var document = XDocument.Load(GetThemePath("Generic.xaml"));

        AssertColor(document, "BgPrimary", "#131313");
        AssertColor(document, "BgSidebar", "#1B1B1C");
        AssertColor(document, "BgSurface", "#202020");
        AssertColor(document, "BgSurfaceHigh", "#2A2A2A");
        AssertColor(document, "BgSurfaceHighest", "#353535");
        AssertColor(document, "TextPrimary", "#E5E2E1");
        AssertColor(document, "TextSecondary", "#BDC8CF");
        AssertColor(document, "TextMuted", "#889299");
        AssertColor(document, "BorderPrimary", "#3E484E");
        AssertColor(document, "BorderSubtle", "#2A3338");
        AssertColor(document, "Accent", "#818CF8");
        AssertColor(document, "Success", "#6CCB77");
        AssertColor(document, "Warning", "#FFB955");
        AssertColor(document, "Error", "#FFB4AB");

        AssertColor(document, "AccentContainer", "#1E1F35");
        AssertColor(document, "SuccessContainer", "#2D4A2D");
        AssertColor(document, "ErrorContainer", "#4A1E1E");
        AssertColor(document, "Scrim", "#66000000");
        AssertColor(document, "ScrimLight", "#99FFFFFF");
        AssertColor(document, "ScrimHeavy", "#99000000");
        AssertColor(document, "ScrimOverlay", "#AA101416");
        AssertColor(document, "BgConsole", "#0E0E0E");
        AssertColor(document, "WindowCloseHover", "#4A1515");
        AssertColor(document, "WindowClosePressed", "#5A1A1A");
        AssertColor(document, "AccentGradientStart", "#818CF8");
        AssertColor(document, "AccentGradientEnd", "#C084FC");
        AssertColor(document, "ToggleTrack", "#313244");
        AssertColor(document, "ToggleThumb", "#585B70");
    }

    [Fact]
    public void ThemeDefinesTypographyTokens()
    {
        var document = XDocument.Load(GetThemePath("Generic.xaml"));

        AssertDoubleToken(document, "FontSizeCaption", 11);
        AssertDoubleToken(document, "FontSizeBody", 12);
        AssertDoubleToken(document, "FontSizeBodyStrong", 14);
        AssertDoubleToken(document, "FontSizeSection", 16);
        AssertDoubleToken(document, "FontSizeCardTitle", 20);
        AssertDoubleToken(document, "FontSizePageTitle", 28);

        AssertDoubleToken(document, "IconSizeSmall", 12);
        AssertDoubleToken(document, "IconSizeBody", 16);
        AssertDoubleToken(document, "IconSizeLarge", 18);
        AssertDoubleToken(document, "IconSizeEmptyState", 48);

        AssertFontFamily(document, "FontFamilyUI", "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
        AssertFontFamily(document, "FontFamilyMono", "Cascadia Code, Consolas, Microsoft YaHei UI");
        AssertFontFamily(document, "FontFamilyIcon", "Segoe Fluent Icons, Segoe MDL2 Assets");
    }

    [Theory]
    [InlineData("TextPageTitle")]
    [InlineData("TextCardTitle")]
    [InlineData("TextSection")]
    [InlineData("TextBodyStrong")]
    [InlineData("TextBody")]
    [InlineData("TextCaption")]
    [InlineData("TextMono")]
    [InlineData("IconGlyph")]
    public void ThemeDefinesNamedStyles(string styleKey)
    {
        var document = XDocument.Load(GetThemePath("Generic.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var style = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Style"
                && element.Attribute(x + "Key")?.Value == styleKey);

        Assert.NotNull(style);
    }

    [Theory]
    [InlineData("Views")]
    [InlineData("MainWindow.xaml")]
    public void ViewsAndMainWindowDoNotUseHexColorLiterals(string relativePath)
    {
        var path = GetRootPath(relativePath);
        var files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.xaml", SearchOption.AllDirectories)
            : [path];

        var offenders = files
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => new { file, line, lineNumber = index + 1 }))
            .Where(item => Regex.IsMatch(item.line, @"#[0-9A-Fa-f]{3,8}"))
            .Select(item => $"{Path.GetFileName(item.file)}:{item.lineNumber}:{item.line.Trim()}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Views and MainWindow must use theme color tokens instead of hex literals: "
                + string.Join("; ", offenders));
    }

    [Theory]
    [InlineData("Views")]
    [InlineData("MainWindow.xaml")]
    public void ViewsAndMainWindowDoNotUseNamedColors(string relativePath)
    {
        var path = GetRootPath(relativePath);
        var files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.xaml", SearchOption.AllDirectories)
            : [path];

        // Match Foreground/Background/BorderBrush/Fill/Stroke set to a named color (ignoring Transparent, Binding, DynamicResource, StaticResource)
        var colorPattern = @"\b(Foreground|Background|BorderBrush|Fill|Stroke)\s*=\s*""(?!Transparent|Binding |TemplateBinding |StaticResource |DynamicResource)(White|Black|Red|Gray|Green|Blue|Yellow|Pink|Purple|Orange|LightGray|DarkGray)""";

        var offenders = files
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => new { file, line, lineNumber = index + 1 }))
            .Where(item => Regex.IsMatch(item.line, colorPattern, RegexOptions.IgnoreCase))
            .Select(item => $"{Path.GetFileName(item.file)}:{item.lineNumber}:{item.line.Trim()}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Views and MainWindow must use theme color tokens instead of named colors: "
                + string.Join("; ", offenders));
    }

    [Fact]
    public void ThemeHexColorLiteralsAreOnlyColorTokenValues()
    {
        var document = XDocument.Load(GetThemePath("Generic.xaml"));
        var offenders = document
            .Descendants()
            .Attributes()
            .Where(attribute => Regex.IsMatch(attribute.Value, @"#[0-9A-Fa-f]{3,8}"))
            .Select(attribute => $"{attribute.Parent?.Name.LocalName}.{attribute.Name.LocalName}={attribute.Value}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Generic.xaml hex literals must be promoted to Color tokens: "
                + string.Join("; ", offenders));
    }

    [Fact]
    public void ThemeDefinesSharedMotionResources()
    {
        var document = XDocument.Load(GetThemePath("Generic.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "CubicEase"
            && element.Attribute(x + "Key")?.Value == "MotionEaseOut"
            && element.Attribute("EasingMode")?.Value == "EaseOut");
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "Duration"
            && element.Attribute(x + "Key")?.Value == "MotionDurationFast"
            && element.Value.Trim() == "0:0:0.15");
    }

    [Theory]
    [InlineData("AccentButton")]
    [InlineData("SurfaceButton")]
    [InlineData("NavRadioButton")]
    public void InteractiveStylesUseMotionStoryboards(string styleKey)
    {
        var document = XDocument.Load(GetThemePath("Generic.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var style = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Style"
                && element.Attribute(x + "Key")?.Value == styleKey);

        Assert.NotNull(style);
        Assert.Contains(style!.Descendants(), element => element.Name.LocalName == "Storyboard");
        Assert.Contains(style.Descendants().Attributes("EasingFunction"), attribute =>
            attribute.Value.Contains("MotionEaseOut", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ToolPanelBorder")]
    [InlineData("AccentButton")]
    [InlineData("SurfaceButton")]
    [InlineData("NavRadioButton")]
    [InlineData("HistoryFilterRadioButton")]
    public void CheckedStylesDoNotContainEffectSettersOrAttributes(string styleKey)
    {
        var document = XDocument.Load(GetThemePath("Generic.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var style = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Style"
                && element.Attribute(x + "Key")?.Value == styleKey);

        Assert.NotNull(style);

        // Ensure there is no Setter for Property="Effect"
        var effectSetters = style!
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "Setter"
                && element.Attribute("Property")?.Value == "Effect")
            .ToList();

        Assert.Empty(effectSetters);

        // Ensure there is no inline Effect attribute on any element inside the template
        var elementsWithEffectAttribute = style!
            .Descendants()
            .Where(element => element.Attribute("Effect") != null)
            .ToList();

        Assert.Empty(elementsWithEffectAttribute);
    }

    [Fact]
    public void ToggleSwitchAnimatesThumbWithTransform()
    {
        var document = XDocument.Load(GetThemePath("Generic.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var style = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Style"
                && element.Attribute(x + "Key")?.Value == "ToggleSwitch");

        Assert.NotNull(style);
        Assert.Contains(style!.Descendants(), element =>
            element.Name.LocalName == "TranslateTransform"
            && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "ThumbTranslate"));
        Assert.Contains(style.Descendants(), element =>
            element.Name.LocalName == "DoubleAnimation"
            && element.Attribute("Storyboard.TargetName")?.Value == "ThumbTranslate"
            && element.Attribute("Storyboard.TargetProperty")?.Value == "X");
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

    private static string GetRootPath(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from test output directory.");
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
        return targetType == expected || targetType == $"{expected}" || targetType == $"{{x:Type {expected}}}";
    }

    private static void AssertDoubleToken(XDocument document, string key, double expectedValue)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var token = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Double"
                && element.Attribute(x + "Key")?.Value == key);

        Assert.NotNull(token);
        Assert.Equal(expectedValue, double.Parse(token!.Value.Trim()));
    }

    private static void AssertFontFamily(XDocument document, string key, string expectedValue)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var token = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "FontFamily"
                && element.Attribute(x + "Key")?.Value == key);

        Assert.NotNull(token);
        Assert.Equal(expectedValue, token!.Value.Trim());
    }

    [Theory]
    [InlineData("Views")]
    [InlineData("MainWindow.xaml")]
    public void ViewsAndMainWindowDoNotUseHardcodedFontAttributes(string relativePath)
    {
        var path = GetRootPath(relativePath);
        var files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.xaml", SearchOption.AllDirectories)
            : [path];

        // Match: (FontSize|FontFamily|FontWeight)\s*=\s*"(?!{)
        var fontPattern = @"\b(FontSize|FontFamily|FontWeight)\s*=\s*""(?!\{)";

        var offenders = files
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => new { file, line, lineNumber = index + 1 }))
            .Where(item => Regex.IsMatch(item.line, fontPattern))
            .Select(item => $"{Path.GetFileName(item.file)}:{item.lineNumber}:{item.line.Trim()}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Views and MainWindow must use typography styles/tokens instead of hardcoded attributes: "
                + string.Join("; ", offenders));
    }

    [Fact]
    public void FontWeightBoldIsBannedGlobally()
    {
        var foldersToScan = new[] { "Views", "Themes", "MainWindow.xaml" };
        var offenders = new List<string>();

        foreach (var folderOrFile in foldersToScan)
        {
            var path = GetRootPath(folderOrFile);
            if (!Directory.Exists(path) && !File.Exists(path))
                continue;

            var files = Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*.xaml", SearchOption.AllDirectories)
                : [path];

            foreach (var file in files)
            {
                var lines = File.ReadLines(file).ToList();
                for (int i = 0; i < lines.Count; i++)
                {
                    if (Regex.IsMatch(lines[i], @"FontWeight\s*=\s*[""']Bold[""']", RegexOptions.IgnoreCase))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:L{i + 1}:{lines[i].Trim()}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "FontWeight=\"Bold\" is globally banned. Use FontWeightSemiBold or FontWeightNormal instead: "
                + string.Join("; ", offenders));
    }

    [Fact]
    public void ThemeManagerCanApplyThemes()
    {
        Assert.NotEmpty(ThemeManager.Palettes);
        Assert.Contains(ThemeManager.Palettes, p => p.Name == "Indigo");
        Assert.Contains(ThemeManager.Palettes, p => p.Name == "Teal");
        Assert.Contains(ThemeManager.Palettes, p => p.Name == "Rose");
        Assert.Contains(ThemeManager.Palettes, p => p.Name == "Amber");
        Assert.Contains(ThemeManager.Palettes, p => p.Name == "Blue");

        ThemeManager.ApplyTheme("Teal");
        ThemeManager.ApplyTheme("Rose");
        ThemeManager.ApplyTheme("InvalidThemeName");
    }
}
