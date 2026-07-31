using System.IO;

namespace EasyGet.Services;

internal static class PlatformDirectoryPolicy
{
    private const string YouTubeFolder = "YouTube";
    private const string BilibiliFolder = "哔哩哔哩";
    private const string DouyinFolder = "抖音";
    private const string TikTokFolder = "TikTok";
    private const string TwitterFolder = "Twitter(X)";
    private const string InstagramFolder = "Instagram";
    private const string FacebookFolder = "Facebook";
    private const string WeiboFolder = "微博";
    private const string XiaohongshuFolder = "小红书";
    private const string KuaishouFolder = "快手";
    private const string IqiyiFolder = "爱奇艺";
    private const string YoukuFolder = "优酷";
    private const string TencentVideoFolder = "腾讯视频";
    private const string TwitchFolder = "Twitch";
    private const string NicoNicoFolder = "NicoNico";
    private const string VimeoFolder = "Vimeo";
    private const string YangshipinFolder = "央视频";
    private const string TelegramFolder = "Telegram";
    private const string M3u8Folder = "M3U8";

    private static readonly PlatformRegistration[] Platforms =
    {
        new(
            YouTubeFolder,
            new[] { "youtube" },
            Array.Empty<string>(),
            new[] { "youtube.com", "youtube-nocookie.com", "youtu.be" }),
        new(
            BilibiliFolder,
            new[] { "bilibili" },
            Array.Empty<string>(),
            new[] { "bilibili.com", "b23.tv" }),
        new(
            DouyinFolder,
            new[] { "douyin" },
            Array.Empty<string>(),
            new[] { "douyin.com", "iesdouyin.com" }),
        new(
            TikTokFolder,
            new[] { "tiktok" },
            Array.Empty<string>(),
            new[] { "tiktok.com", "tiktokv.com" }),
        new(
            TwitterFolder,
            new[] { "twitter" },
            new[] { "x", "space" },
            new[] { "twitter.com", "x.com", "t.co" }),
        new(
            InstagramFolder,
            new[] { "instagram" },
            Array.Empty<string>(),
            new[] { "instagram.com", "instagr.am" }),
        new(
            FacebookFolder,
            new[] { "facebook" },
            Array.Empty<string>(),
            new[] { "facebook.com", "fb.watch" }),
        new(
            WeiboFolder,
            new[] { "weibo" },
            Array.Empty<string>(),
            new[] { "weibo.com", "weibo.cn" }),
        new(
            XiaohongshuFolder,
            new[] { "xiaohongshu" },
            Array.Empty<string>(),
            new[] { "xiaohongshu.com", "xhslink.com" }),
        new(
            KuaishouFolder,
            new[] { "kuaishou" },
            Array.Empty<string>(),
            new[] { "kuaishou.com", "gifshow.com" }),
        new(
            IqiyiFolder,
            new[] { "iqiyi" },
            Array.Empty<string>(),
            new[] { "iqiyi.com" }),
        new(
            YoukuFolder,
            new[] { "youku" },
            Array.Empty<string>(),
            new[] { "youku.com" }),
        new(
            TencentVideoFolder,
            new[] { "tencent" },
            new[] { "qq" },
            new[] { "v.qq.com", "video.qq.com", "wetv.vip" }),
        new(
            TwitchFolder,
            new[] { "twitch" },
            Array.Empty<string>(),
            new[] { "twitch.tv" }),
        new(
            NicoNicoFolder,
            new[] { "niconico" },
            Array.Empty<string>(),
            new[] { "nicovideo.jp", "nico.ms" }),
        new(
            VimeoFolder,
            new[] { "vimeo" },
            Array.Empty<string>(),
            new[] { "vimeo.com" }),
        new(
            YangshipinFolder,
            new[] { "yangshipin" },
            Array.Empty<string>(),
            new[] { "yangshipin.cn" }),
        new(
            TelegramFolder,
            new[] { "telegram" },
            Array.Empty<string>(),
            new[] { "t.me", "telegram.me", "telegram.org" }),
        new(
            M3u8Folder,
            new[] { "m3u8" },
            Array.Empty<string>(),
            Array.Empty<string>())
    };

    private static readonly IReadOnlyDictionary<string, string> DirectoryNames =
        BuildDirectoryNameMap();

    internal static bool TryResolveCanonicalFolder(
        string? platform,
        string? url,
        out string folder)
    {
        if (TryResolveFromUrl(url, out folder))
            return true;

        var extractorName = NormalizeExtractorName(platform);
        if (extractorName.Length == 0
            || string.Equals(extractorName, "generic", StringComparison.Ordinal))
        {
            folder = "";
            return false;
        }

        foreach (var registration in Platforms)
        {
            if (registration.ExactExtractorNames.Contains(
                    extractorName,
                    StringComparer.Ordinal)
                || registration.ExtractorPrefixes.Any(
                    prefix => extractorName.StartsWith(prefix, StringComparison.Ordinal)))
            {
                folder = registration.Folder;
                return true;
            }
        }

        folder = "";
        return false;
    }

    internal static bool TryCanonicalizeDirectoryName(string? name, out string folder)
    {
        if (!string.IsNullOrWhiteSpace(name)
            && DirectoryNames.TryGetValue(name, out var resolvedFolder))
        {
            folder = resolvedFolder;
            return true;
        }

        folder = "";
        return false;
    }

    internal static string ResolveCategorizedDirectory(
        string basePath,
        string canonicalFolder)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            throw new ArgumentException("下载根目录不能为空。", nameof(basePath));

        if (!TryCanonicalizeDirectoryName(canonicalFolder, out var resolvedFolder))
        {
            throw new ArgumentException(
                "目录名称不是受支持的平台规范目录。",
                nameof(canonicalFolder));
        }

        var trimmedBasePath = Path.TrimEndingDirectorySeparator(basePath.Trim());
        if (trimmedBasePath.Length == 0)
            throw new ArgumentException("下载根目录不能为空。", nameof(basePath));

        var leafName = Path.GetFileName(trimmedBasePath);
        if (!TryCanonicalizeDirectoryName(leafName, out _))
            return Path.Combine(trimmedBasePath, resolvedFolder);

        var parentPath = Path.GetDirectoryName(trimmedBasePath);
        return string.IsNullOrEmpty(parentPath)
            ? resolvedFolder
            : Path.Combine(parentPath, resolvedFolder);
    }

    private static bool TryResolveFromUrl(string? url, out string folder)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(uri.Host))
        {
            foreach (var registration in Platforms)
            {
                if (registration.Hosts.Any(host => HostMatches(uri.Host, host)))
                {
                    folder = registration.Folder;
                    return true;
                }
            }
        }

        folder = "";
        return false;
    }

    private static bool HostMatches(string host, string trustedDomain)
        => host.Equals(trustedDomain, StringComparison.OrdinalIgnoreCase)
           || host.EndsWith($".{trustedDomain}", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeExtractorName(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
            return "";

        return new string(platform
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static IReadOnlyDictionary<string, string> BuildDirectoryNameMap()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in Platforms)
            names[registration.Folder] = registration.Folder;

        names["抖音视频下载"] = DouyinFolder;
        names["Youtube视频下载"] = YouTubeFolder;
        names["X"] = TwitterFolder;
        return names;
    }

    private sealed record PlatformRegistration(
        string Folder,
        IReadOnlyList<string> ExtractorPrefixes,
        IReadOnlyList<string> ExactExtractorNames,
        IReadOnlyList<string> Hosts);
}
