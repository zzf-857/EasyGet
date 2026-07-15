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
        foreach (var cookie in cookies)
        {
            if (string.IsNullOrEmpty(cookie.Value)
                || !IsAuthenticatedCookieMetadata(
                    platform,
                    cookie.Domain,
                    cookie.Name,
                    cookie.ExpiresUnix,
                    nowUnix))
            {
                continue;
            }
            return true;
        }

        return false;
    }

    public static bool IsAuthenticatedCookieMetadata(
        MediaPlatformDefinition platform,
        string domain,
        string name,
        long expiresUnix)
        => IsAuthenticatedCookieMetadata(
            platform,
            domain,
            name,
            expiresUnix,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public static IReadOnlyList<string> GetKnownAuthenticationCookieNames(
        MediaPlatformDefinition platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        return KnownAuthenticationCookies.TryGetValue(platform.Id, out var names)
            ? names.ToArray()
            : [];
    }

    private static bool IsAuthenticatedCookieMetadata(
        MediaPlatformDefinition platform,
        string domain,
        string name,
        long expiresUnix,
        long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(platform);
        if (string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(domain)
            || (expiresUnix > 0 && expiresUnix <= nowUnix)
            || !platform.CookieDomains.Any(allowedDomain =>
                MediaPlatformResolver.HostMatches(
                    domain.Trim().TrimStart('.'),
                    allowedDomain)))
        {
            return false;
        }

        if (KnownAuthenticationCookies.TryGetValue(platform.Id, out var knownNames))
            return knownNames.Contains(name);

        return LooksLikeAuthenticationCookie(name);
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
