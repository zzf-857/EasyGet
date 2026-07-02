using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using EasyGet.Models;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace EasyGet.Converters;

/// <summary>
/// 布尔值取反转换器
/// </summary>
public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}

/// <summary>
/// 布尔值 → Visibility 转换器
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>
/// 下载状态 → 颜色转换器
/// </summary>
public class StatusToColorConverter : IValueConverter
{
    private static readonly Brush CompletedBrush = ConverterBrushFactory.Create(0xA6, 0xE3, 0xA1);
    private static readonly Brush DownloadingBrush = ConverterBrushFactory.Create(0x89, 0xB4, 0xFA);
    private static readonly Brush FailedBrush = ConverterBrushFactory.Create(0xF3, 0x8B, 0xA8);
    private static readonly Brush MutedBrush = ConverterBrushFactory.Create(0x6C, 0x70, 0x86);
    private static readonly Brush DefaultBrush = ConverterBrushFactory.Create(0xBA, 0xC2, 0xDE);
    private static readonly Brush FallbackBrush = ConverterBrushFactory.Create(0xFF, 0xFF, 0xFF);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DownloadStatus status)
        {
            return status switch
            {
                DownloadStatus.Completed => CompletedBrush,
                DownloadStatus.Downloading => DownloadingBrush,
                DownloadStatus.Failed => FailedBrush,
                DownloadStatus.Waiting => MutedBrush,
                DownloadStatus.Cancelled => MutedBrush,
                _ => DefaultBrush
            };
        }

        return FallbackBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 下载状态 → 状态文本转换器
/// </summary>
public class StatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is DownloadStatus status ? status switch
        {
            DownloadStatus.Waiting => "等待中",
            DownloadStatus.Resolving => "解析中",
            DownloadStatus.Downloading => "下载中",
            DownloadStatus.Merging => "合并中",
            DownloadStatus.Completed => "已完成",
            DownloadStatus.Failed => "失败",
            DownloadStatus.Cancelled => "已取消",
            DownloadStatus.Paused => "已暂停",
            _ => ""
        } : "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 平台名 → 平台颜色转换器
/// </summary>
public class PlatformToColorConverter : IValueConverter
{
    private static readonly Brush YoutubeBrush = ConverterBrushFactory.Create(0xFF, 0x00, 0x00);
    private static readonly Brush BilibiliBrush = ConverterBrushFactory.Create(0xE9, 0x1E, 0x63);
    private static readonly Brush TikTokBrush = ConverterBrushFactory.Create(0x00, 0x00, 0x00);
    private static readonly Brush TwitterBrush = ConverterBrushFactory.Create(0x1D, 0xA1, 0xF2);
    private static readonly Brush InstagramBrush = ConverterBrushFactory.Create(0xE4, 0x40, 0x5F);
    private static readonly Brush FacebookBrush = ConverterBrushFactory.Create(0x18, 0x77, 0xF2);
    private static readonly Brush KuaishouBrush = ConverterBrushFactory.Create(0xFF, 0x63, 0x00);
    private static readonly Brush DefaultPlatformBrush = ConverterBrushFactory.Create(0x89, 0xB4, 0xFA);

    private static readonly IReadOnlyDictionary<string, Brush> PlatformBrushes =
        new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase)
        {
            ["youtube"] = YoutubeBrush,
            ["bilibili"] = BilibiliBrush,
            ["tiktok"] = TikTokBrush,
            ["douyin"] = TikTokBrush,
            ["twitter"] = TwitterBrush,
            ["x"] = TwitterBrush,
            ["instagram"] = InstagramBrush,
            ["facebook"] = FacebookBrush,
            ["kuaishou"] = KuaishouBrush
        };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string platform && PlatformBrushes.TryGetValue(platform, out var brush)
            ? brush
            : DefaultPlatformBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Cookie 内容 → 状态文本转换器
/// </summary>
public class CookieStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var content = value as string ?? "";
        if (string.IsNullOrWhiteSpace(content)) return "未配置 Cookie";
        var count = content.Split(';', StringSplitOptions.RemoveEmptyEntries)
                           .Count(p => p.Contains('='));
        return $"已配置 {count} 个 Cookie 项";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 字符串非空 → Visible, 空 → Collapsed
/// </summary>
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// HTTP(S) URL -> true; other values -> false.
/// </summary>
public class HttpUrlToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string url
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 整数 → 布尔转换器（比较 ConverterParameter）
/// </summary>
public class IntToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int intValue && parameter is string paramStr && int.TryParse(paramStr, out int target))
            return intValue == target;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string paramStr && int.TryParse(paramStr, out int target))
            return target;
        return System.Windows.Data.Binding.DoNothing;
    }
}

/// <summary>
/// 导航索引 → 选中状态颜色
/// </summary>
public class NavIndexToBackgroundConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[0] is int selected && parameter is string indexStr && int.TryParse(indexStr, out int index))
        {
            if (selected == index)
                return new SolidColorBrush(Color.FromArgb(0x20, 0x89, 0xB4, 0xFA)); // bg-hover with accent tint
        }
        return Brushes.Transparent;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 文件存在 → 透明度转换器（不存在时变灰）
/// </summary>
public class FileExistsToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.4;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 对象值与参数相等 → 布尔值转换器（用于 RadioButton 选中状态绑定）
/// </summary>
public class EqualsToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return value.ToString() == parameter.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter != null)
            return parameter.ToString()!;
        return System.Windows.Data.Binding.DoNothing;
    }
}

file static class ConverterBrushFactory
{
    public static Brush Create(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
