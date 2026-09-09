using System.IO;

namespace EasyGet.Services;

internal enum DownloadEngine
{
    YtDlp,
    M3u8,
    Telegram,
    Resource
}

internal static class DownloadRouteResolver
{
    internal static DownloadEngine Resolve(string? url, bool resourceHint = false)
    {
        if (M3u8DownloadService.IsM3u8Url(url ?? ""))
            return DownloadEngine.M3u8;

        if (TelegramDownloadService.IsTelegramUrl(url))
            return DownloadEngine.Telegram;

        if (TelegramLinkParser.IsTelegramMessageLinkWithComment(url))
            throw new NotSupportedException("暂不支持 Telegram 评论链接，请复制讨论组内具体消息链接后重试。");

        if (resourceHint || HttpResourceDownloadService.IsResourceUrl(url))
            return DownloadEngine.Resource;

        return DownloadEngine.YtDlp;
    }

    internal static bool TryCreateLocalVideoInfo(
        string url,
        out VideoInfo info,
        DateTime? now = null,
        bool resourceHint = false,
        string? resourceExtensionHint = null,
        string? resourceMimeType = null)
    {
        switch (Resolve(url, resourceHint))
        {
            case DownloadEngine.M3u8:
                info = CreateM3u8VideoInfo(url, now ?? DateTime.Now);
                return true;
            case DownloadEngine.Telegram:
                info = CreateTelegramVideoInfo(url);
                return true;
            case DownloadEngine.Resource:
                info = CreateResourceInfo(url, resourceExtensionHint, resourceMimeType);
                return true;
            default:
                info = null!;
                return false;
        }
    }

    private static VideoInfo CreateResourceInfo(
        string url,
        string? extensionHint = null,
        string? mimeType = null)
    {
        var title = "课程资源";
        var extension = HttpResourceDownloadService.ResolveExtension(url, extensionHint, mimeType);
        try
        {
            var uri = new Uri(url);
            var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                title = Path.GetFileNameWithoutExtension(fileName);
                if (string.IsNullOrWhiteSpace(title))
                    title = fileName;
            }
        }
        catch (UriFormatException)
        {
        }

        return new VideoInfo
        {
            Title = title,
            Platform = "HTTP资源",
            Url = url,
            IsResource = true,
            Extension = extension,
            MimeType = mimeType?.Trim() ?? ""
        };
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
