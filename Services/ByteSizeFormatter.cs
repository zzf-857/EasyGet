using System.Globalization;

namespace EasyGet.Services;

internal static class ByteSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string FormatClampZero(long bytes, string suffix = "")
        => Format(Math.Max(0, bytes), suffix);

    public static string FormatOrUnknown(long bytes, string unknownText = "大小未知")
        => bytes <= 0 ? unknownText : Format(bytes);

    private static string Format(long bytes, string suffix = "")
    {
        var value = (double)bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < Units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{value:0.#} {Units[unitIndex]}{suffix}");
    }
}
