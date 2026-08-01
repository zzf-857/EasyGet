using System.IO;

namespace EasyGet.Services;

internal enum DownloadEngine
{
    YtDlp,
    M3u8,
    Telegram
}

internal static class DownloadRouteResolver
{
    internal static DownloadEngine Resolve(string? url)
    {
        if (M3u8DownloadService.IsM3u8Url(url ?? ""))
            return DownloadEngine.M3u8;

        if (TelegramDownloadService.IsTelegramUrl(url))
            return DownloadEngine.Telegram;

        return DownloadEngine.YtDlp;
    }

    internal static bool TryCreateLocalVideoInfo(
        string url,
        out VideoInfo info,
        DateTime? now = null)
    {
        switch (Resolve(url))
        {
            case DownloadEngine.M3u8:
                info = CreateM3u8VideoInfo(url, now ?? DateTime.Now);
                return true;
            case DownloadEngine.Telegram:
                info = CreateTelegramVideoInfo(url);
                return true;
            default:
                info = null!;
                return false;
        }
    }

    private static VideoInfo CreateM3u8VideoInfo(string url, DateTime now)
    {
        var title = "M3U8_Video";
        try
        {
            var uri = new Uri(url);
            var filename = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            title = !string.IsNullOrWhiteSpace(filename)
                    && !filename.Equals("index", StringComparison.OrdinalIgnoreCase)
                    && !filename.Equals("playlist", StringComparison.OrdinalIgnoreCase)
                ? filename
                : $"M3U8_{now:yyyyMMdd_HHmmss}";
        }
        catch (Exception) when (!string.IsNullOrWhiteSpace(url))
        {
            title = $"M3U8_{now:yyyyMMdd_HHmmss}";
        }

        return new VideoInfo
        {
            Title = title,
            Platform = "M3U8",
            Url = url
        };
    }

    private static VideoInfo CreateTelegramVideoInfo(string url)
    {
        var title = "Telegram_Message";
        var parsed = TelegramDownloadService.ParseTelegramLink(url);
        if (parsed is { } link)
        {
            var (chatTarget, startId, endId) = link;
            title = endId is not null
                ? $"TG_{chatTarget}_{startId}-{endId}"
                : $"TG_{chatTarget}_{startId}";
        }

        return new VideoInfo
        {
            Title = title,
            Platform = "Telegram",
            Url = url
        };
    }
}
