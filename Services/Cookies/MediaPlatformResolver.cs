using System.Security.Cryptography;
using System.Text;

namespace EasyGet.Services.Cookies;

public sealed record MediaPlatformDefinition(
    string Id,
    string DisplayName,
    Uri LoginUri,
    IReadOnlyList<string> CookieDomains,
    bool AnonymousFirst = true)
{
    public string StorageKey
    {
        get
        {
            if (!string.Equals(Id, "generic", StringComparison.Ordinal))
                return Id;

            var scope = CookieDomains.FirstOrDefault()?.Trim().TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(scope))
                return "generic-invalid";

            var hash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(scope)))
                .ToLowerInvariant();
            return $"generic-{hash[..32]}";
        }
    }
}

public static class MediaPlatformResolver
{
    private static readonly MediaPlatformDefinition InvalidDefinition = CreateDefinition(
        "generic",
        "通用网站",
        "about:blank",
        Array.Empty<string>());

    private static readonly PlatformRegistration[] Platforms =
    {
        Register(
            CreateDefinition(
                "youtube",
                "YouTube",
                "https://accounts.google.com/ServiceLogin?service=youtube",
                new[] { "youtube.com", "google.com" }),
            "youtube.com",
            "youtu.be"),
        Register(
            CreateDefinition(
                "bilibili",
                "哔哩哔哩",
                "https://passport.bilibili.com/login",
                new[] { "bilibili.com" }),
            "bilibili.com",
            "b23.tv"),
        Register(
            CreateDefinition(
                "douyin",
                "抖音",
                "https://www.douyin.com/",
                new[] { "douyin.com", "iesdouyin.com" }),
            "douyin.com",
            "iesdouyin.com"),
        Register(
            CreateDefinition(
                "tiktok",
                "TikTok",
                "https://www.tiktok.com/login",
                new[] { "tiktok.com" }),
            "tiktok.com"),
        Register(
            CreateDefinition(
                "twitter",
                "X / Twitter",
                "https://x.com/i/flow/login",
                new[] { "x.com", "twitter.com" }),
            "x.com",
            "twitter.com"),
        Register(
            CreateDefinition(
                "instagram",
                "Instagram",
                "https://www.instagram.com/accounts/login/",
                new[] { "instagram.com" }),
            "instagram.com"),
        Register(
            CreateDefinition(
                "facebook",
                "Facebook",
                "https://www.facebook.com/login",
                new[] { "facebook.com" }),
            "facebook.com"),
        Register(
            CreateDefinition(
                "kuaishou",
                "快手",
                "https://www.kuaishou.com/",
                new[] { "kuaishou.com" }),
            "kuaishou.com"),
        Register(
            CreateDefinition(
                "xiaohongshu",
                "小红书",
                "https://www.xiaohongshu.com/",
                new[] { "xiaohongshu.com", "xhslink.com" }),
            "xiaohongshu.com",
            "xhslink.com"),
        Register(
            CreateDefinition(
                "weibo",
                "微博",
                "https://weibo.com/login.php",
                new[] { "weibo.com" }),
            "weibo.com"),
        Register(
            CreateDefinition(
                "twitch",
                "Twitch",
                "https://www.twitch.tv/login",
                new[] { "twitch.tv" }),
            "twitch.tv")
    };

    public static IReadOnlyList<MediaPlatformDefinition> KnownPlatforms { get; } =
        Array.AsReadOnly(Platforms.Select(platform => platform.Definition).ToArray());

    public static MediaPlatformDefinition Resolve(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return InvalidDefinition;
        }

        foreach (var platform in Platforms)
        {
            if (platform.Hosts.Any(domain => HostMatches(uri.Host, domain)))
                return platform.Definition;
        }

        return new MediaPlatformDefinition(
            "generic",
            uri.Host,
            new Uri($"{uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)}/"),
            Array.AsReadOnly(new[] { uri.Host }));
    }

    public static bool HostMatches(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
           || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);

    private static MediaPlatformDefinition CreateDefinition(
        string id,
        string displayName,
        string loginUri,
        string[] cookieDomains)
        => new(
            id,
            displayName,
            new Uri(loginUri),
            Array.AsReadOnly(cookieDomains));

    private static PlatformRegistration Register(
        MediaPlatformDefinition definition,
        params string[] hosts)
        => new(definition, hosts);

    private sealed record PlatformRegistration(
        MediaPlatformDefinition Definition,
        IReadOnlyList<string> Hosts);
}
