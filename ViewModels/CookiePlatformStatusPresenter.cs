using EasyGet.Models;
using EasyGet.Services.Cookies;

namespace EasyGet.ViewModels;

/// <summary>Projects one browser/health snapshot onto the settings platform rows.</summary>
internal sealed class CookiePlatformStatusPresenter
{
    private readonly Dictionary<string, SuccessfulCookieSources> _successfulSources =
        new(StringComparer.Ordinal);
    private readonly BrowserCookieLoginDetection _detection;
    private readonly int _profileCount;

    public CookiePlatformStatusPresenter(
        IEnumerable<CookieHealthRecord> health,
        BrowserCookieLoginDetection detection,
        int profileCount)
    {
        _detection = detection;
        _profileCount = profileCount;

        // Index once instead of filtering and sorting the whole history twice per platform.
        foreach (var record in health)
        {
            if (string.IsNullOrEmpty(record.PlatformId)
                || !record.LastSuccessUtc.HasValue
                || record.ConsecutiveFailures != 0
                || record.LastFailureUtc > record.LastSuccessUtc)
            {
                continue;
            }

            if (!_successfulSources.TryGetValue(record.PlatformId, out var sources))
                _successfulSources.Add(record.PlatformId, sources = new SuccessfulCookieSources());

            if (sources.Latest is null || record.LastSuccessUtc > sources.Latest.LastSuccessUtc)
                sources.Latest = record;
            if (record.Source is CookieSourceKind.LegacyScoped
                    or CookieSourceKind.Browser
                    or CookieSourceKind.ManagedSession
                && (sources.Authenticated is null
                    || record.LastSuccessUtc > sources.Authenticated.LastSuccessUtc))
            {
                sources.Authenticated = record;
            }
        }
    }

    /// <returns>Whether recent successful downloads verified this platform.</returns>
    public bool Apply(CookiePlatformStatusItem item)
    {
        _successfulSources.TryGetValue(item.StorageKey, out var sources);
        var successful = sources?.Latest;
        if (item.IsOperating)
            return successful is not null;

        var authenticated = sources?.Authenticated;
        var detected = _detection.TryGetProfile(item.StorageKey, out var profile);
        item.IsDetected = detected;
        item.HasAuthenticatedSession = detected || authenticated is not null;
        item.IsAvailable = successful is not null;

        if (successful is not null)
        {
            item.NeedsLogin = false;
            item.StatusText = authenticated is not null
                ? $"最近验证可用 · {DescribeCookieSource(authenticated.Source)}"
                : detected
                    ? $"已检测到 {profile.BrowserName} 登录状态 · 下载时自动读取 Cookie"
                    : $"最近验证可用 · {DescribeCookieSource(successful.Source)}";
        }
        else if (detected)
        {
            item.NeedsLogin = false;
            item.StatusText = $"已检测到 {profile.BrowserName} 登录状态 · 下载时自动读取 Cookie";
        }
        else if (_profileCount > 0)
        {
            item.NeedsLogin = _detection.ReadableProfileCount > 0;
            item.StatusText = item.NeedsLogin
                ? "未检测到该平台登录 Cookie · 可点击浏览器登录"
                : "浏览器配置已发现，但登录状态暂时无法读取 · 下载时仍会自动尝试";
        }
        else
        {
            item.NeedsLogin = true;
            item.StatusText = "未发现可复用浏览器配置，首次使用时需要登录";
        }

        return successful is not null;
    }

    private static string DescribeCookieSource(CookieSourceKind source)
        => source switch
        {
            CookieSourceKind.Anonymous => "公开访问",
            CookieSourceKind.LegacyScoped => "平台手动 Cookie",
            CookieSourceKind.Browser => "本机浏览器",
            CookieSourceKind.ManagedSession => "EasyGet 托管登录",
            _ => "本地登录状态"
        };

    private sealed class SuccessfulCookieSources
    {
        public CookieHealthRecord? Latest { get; set; }
        public CookieHealthRecord? Authenticated { get; set; }
    }
}
