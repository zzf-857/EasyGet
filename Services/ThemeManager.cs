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
        // Keep the legacy "Indigo" key so existing user configuration migrates without reset.
        new ThemePalette { Name = "Indigo", DisplayName = "工作台蓝 (默认)", AccentColor = "#5B9CFF", AccentContainerColor = "#203653", GradientStartColor = "#5B9CFF", GradientEndColor = "#5B9CFF" },
        new ThemePalette { Name = "Teal", DisplayName = "清透青", AccentColor = "#38BFCE", AccentContainerColor = "#173A40", GradientStartColor = "#38BFCE", GradientEndColor = "#38BFCE" },
        new ThemePalette { Name = "Rose", DisplayName = "柔和粉", AccentColor = "#E37FB4", AccentContainerColor = "#42273A", GradientStartColor = "#E37FB4", GradientEndColor = "#E37FB4" },
        new ThemePalette { Name = "Amber", DisplayName = "琥珀金", AccentColor = "#DFA23F", AccentContainerColor = "#42331B", GradientStartColor = "#DFA23F", GradientEndColor = "#DFA23F" },
        new ThemePalette { Name = "Blue", DisplayName = "工作台蓝", AccentColor = "#5B9CFF", AccentContainerColor = "#203653", GradientStartColor = "#5B9CFF", GradientEndColor = "#5B9CFF" }
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

            // 1. Mutate existing brush instances to trigger instant UI refresh for already loaded elements
            if (app.Resources["AccentBrush"] is System.Windows.Media.SolidColorBrush accentBrush && !accentBrush.IsFrozen)
            {
                accentBrush.Color = accent;
            }
            if (app.Resources["AccentContainerBrush"] is System.Windows.Media.SolidColorBrush containerBrush && !containerBrush.IsFrozen)
            {
                containerBrush.Color = container;
            }

            // 2. Update resource color keys
            app.Resources["Accent"] = accent;
            app.Resources["AccentContainer"] = container;
            app.Resources["AccentGradientStart"] = gradStart;
            app.Resources["AccentGradientEnd"] = gradEnd;

            // 3. Register new brush objects in resources for any newly loaded controls
            app.Resources["AccentBrush"] = new System.Windows.Media.SolidColorBrush(accent);
            app.Resources["AccentContainerBrush"] = new System.Windows.Media.SolidColorBrush(container);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ThemeManager] ApplyPalette failed: {ex.Message}");
        }
    }
}
