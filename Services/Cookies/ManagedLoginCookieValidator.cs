namespace EasyGet.Services.Cookies;

internal static class ManagedLoginCookieValidator
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>> KnownAuthenticationCookies =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["youtube"] = Names(
                "SID",
                "HSID",
                "SSID",
                "APISID",
                "SAPISID",
                "LOGIN_INFO",
                "__Secure-1PSID",
                "__Secure-3PSID",
                "__Secure-1PAPISID",
                "__Secure-3PAPISID",
                "__Secure-1PSIDCC",
                "__Secure-3PSIDCC"),
            ["bilibili"] = Names("SESSDATA", "DedeUserID"),
            ["douyin"] = Names("sessionid", "sessionid_ss", "sid_guard"),
            ["tiktok"] = Names("sessionid", "sessionid_ss", "sid_guard", "sid_tt"),
            ["twitter"] = Names("auth_token"),
            ["instagram"] = Names("sessionid"),
            ["facebook"] = Names("c_user"),
            ["kuaishou"] = Names("userId", "kuaishou.server.web_st"),
            ["xiaohongshu"] = Names("web_session"),
            ["weibo"] = Names("SUB", "SUBP"),
            ["twitch"] = Names("auth-token", "login")
        };

    public static bool HasAuthenticatedSession(
        MediaPlatformDefinition platform,
        IReadOnlyList<BrowserCookie> cookies)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(cookies);
        if (cookies.Count == 0)
            return false;

        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        KnownAuthenticationCookies.TryGetValue(platform.Id, out var knownNames);
        foreach (var cookie in cookies)
        {
            if (string.IsNullOrWhiteSpace(cookie.Name)
                || string.IsNullOrEmpty(cookie.Value)
                || (cookie.ExpiresUnix > 0 && cookie.ExpiresUnix <= nowUnix)
                || !platform.CookieDomains.Any(domain =>
                    MediaPlatformResolver.HostMatches(cookie.Domain.Trim().TrimStart('.'), domain)))
            {
                continue;
            }

            if (knownNames is not null)
            {
                if (knownNames.Contains(cookie.Name))
                    return true;
                continue;
            }

            if (LooksLikeAuthenticationCookie(cookie.Name))
                return true;
        }

        return false;
    }

    private static HashSet<string> Names(params string[] names)
        => new(names, StringComparer.OrdinalIgnoreCase);

    private static bool LooksLikeAuthenticationCookie(string name)
    {
        var normalized = name.Trim().ToLowerInvariant();
        return normalized.Contains("session", StringComparison.Ordinal)
               || normalized.Contains("auth", StringComparison.Ordinal)
               || normalized.EndsWith("token", StringComparison.Ordinal)
               || normalized is "sid" or "login"
               || normalized.EndsWith("_sid", StringComparison.Ordinal);
    }
}
