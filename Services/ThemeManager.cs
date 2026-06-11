using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyGet.Services;

public class ThemePalette
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string AccentColor { get; set; } = "";
    public string AccentContainerColor { get; set; } = "";
    public string GradientStartColor { get; set; } = "";
    public string GradientEndColor { get; set; } = "";
}

public static class ThemeManager
{
    public static readonly List<ThemePalette> Palettes = new()
    {
        new ThemePalette { Name = "Indigo", DisplayName = "星空靛蓝 (默认)", AccentColor = "#818CF8", AccentContainerColor = "#1E1F35", GradientStartColor = "#818CF8", GradientEndColor = "#C084FC" },
        new ThemePalette { Name = "Teal", DisplayName = "极光青", AccentColor = "#2DD4BF", AccentContainerColor = "#0D2D27", GradientStartColor = "#2DD4BF", GradientEndColor = "#34D399" },
        new ThemePalette { Name = "Rose", DisplayName = "玫瑰粉", AccentColor = "#FB7185", AccentContainerColor = "#3C161E", GradientStartColor = "#FB7185", GradientEndColor = "#F43F5E" },
        new ThemePalette { Name = "Amber", DisplayName = "琥珀金", AccentColor = "#FBBF24", AccentContainerColor = "#382A0F", GradientStartColor = "#FBBF24", GradientEndColor = "#F97316" },
        new ThemePalette { Name = "Blue", DisplayName = "经典浅蓝", AccentColor = "#60CDFF", AccentContainerColor = "#1A4250", GradientStartColor = "#89B4FA", GradientEndColor = "#74C7EC" }
    };

    public static void ApplyTheme(string? name)
    {
        var palette = Palettes.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Palettes[0];
        ApplyPalette(palette);
    }

    private static void ApplyPalette(ThemePalette palette)
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is null) return;

            var accent = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(palette.AccentColor);
            var container = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(palette.AccentContainerColor);
            var gradStart = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(palette.GradientStartColor);
            var gradEnd = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(palette.GradientEndColor);

            app.Resources["Accent"] = accent;
            app.Resources["AccentContainer"] = container;
            app.Resources["AccentGradientStart"] = gradStart;
            app.Resources["AccentGradientEnd"] = gradEnd;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ThemeManager] ApplyPalette failed: {ex.Message}");
        }
    }
}
