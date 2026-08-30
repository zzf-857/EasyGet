using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EasyGet.Models;

internal static class PlatformIconCatalog
{
    private static readonly ResourceManager Resources = new(
        "EasyGet.Resources.PlatformIcons",
        typeof(PlatformIconCatalog).Assembly);

    private static readonly ConcurrentDictionary<string, ImageSource?> Cache =
        new(StringComparer.Ordinal);

    public static ImageSource? Get(string platformId)
    {
        var resourceName = platformId.ToLowerInvariant() switch
        {
            "youtube" => "youtube",
            "bilibili" => "bilibili",
            "douyin" => "douyin",
            "tiktok" => "tiktok",
            "twitter" => "x_twitter",
            "instagram" => "instagram",
            "facebook" => "facebook",
            "kuaishou" => "kuaishou",
            "xiaohongshu" => "xiaohongshu",
            "weibo" => "weibo",
            "twitch" => "twitch",
            _ => ""
        };

        return resourceName.Length == 0
            ? null
            : Cache.GetOrAdd(resourceName, Load);
    }

    private static ImageSource? Load(string resourceName)
    {
        if (Resources.GetObject(resourceName, CultureInfo.InvariantCulture) is not byte[] bytes)
            return null;

        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
