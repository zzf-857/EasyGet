namespace EasyGet.Services.Cookies;

public enum CookieFailureCategory
{
    None,
    AuthenticationRequired,
    CookieStoreLocked,
    CookieDecryptFailed,
    CookieExpired,
    BotChallenge,
    RateLimited,
    NetworkFailure,
    UnrelatedFailure
}

public sealed record CookieFailure(
    CookieFailureCategory Category,
    bool ShouldTryNextCookieSource,
    string? LastErrorLine);

public static class CookieFailureClassifier
{
    public static CookieFailure Classify(string platformId, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(platformId);
        ArgumentNullException.ThrowIfNull(lines);

        var normalizedPlatformId = platformId.Trim().ToLowerInvariant();
        var category = CookieFailureCategory.None;
        string? lastErrorLine = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                lastErrorLine = line;

            category = ChooseHigherPriority(category, Match(normalizedPlatformId, line));
        }

        var shouldRetry = category is CookieFailureCategory.AuthenticationRequired
            or CookieFailureCategory.CookieStoreLocked
            or CookieFailureCategory.CookieDecryptFailed
            or CookieFailureCategory.CookieExpired
            or CookieFailureCategory.BotChallenge;

        return new CookieFailure(category, shouldRetry, lastErrorLine);
    }

    private static CookieFailureCategory Match(string platformId, string line)
    {
        if (ContainsAny(line, "HTTP Error 429", "Too Many Requests", "rate limit", "ratelimit"))
            return CookieFailureCategory.RateLimited;

        if (ContainsAny(
                line,
                "Connection timed out",
                "timed out",
                "Temporary failure in name resolution",
                "Name or service not known",
                "No such host",
                "DNS",
                "Connection refused",
                "Network is unreachable",
                "Unable to connect",
                "Proxy Error",
                "Proxy Authentication Required",
                "HTTP Error 407",
                "could not be resolved",
                "certificate verify failed"))
        {
            return CookieFailureCategory.NetworkFailure;
        }

        if (ContainsAny(
                line,
                "Failed to decrypt with DPAPI",
                "Unable to decrypt cookie",
                "Could not decrypt cookie",
                "cookie decryption failed",
                "Key not valid for use in specified state"))
        {
            return CookieFailureCategory.CookieDecryptFailed;
        }

        if (ContainsAny(
                line,
                "Could not copy Chrome cookie database",
                "Could not copy cookie database",
                "cookie database is locked",
                "database is locked while reading cookies",
                "failed to access cookie database"))
        {
            return CookieFailureCategory.CookieStoreLocked;
        }

        if (ContainsAny(
                line,
                "Fresh cookies",
                "cookies have expired",
                "cookies are no longer valid",
                "cookie has expired",
                "cookie is expired"))
        {
            return CookieFailureCategory.CookieExpired;
        }

        if (platformId == "youtube")
        {
            if (ContainsAny(
                    line,
                    "Sign in to confirm you’re not a bot",
                    "Sign in to confirm you're not a bot",
                    "HTTP Error 403"))
            {
                return CookieFailureCategory.BotChallenge;
            }

            if (ContainsAny(
                    line,
                    "Sign in to confirm your age",
                    "This video may be inappropriate for some users",
                    "age-restricted"))
            {
                return CookieFailureCategory.AuthenticationRequired;
            }
        }

        if (platformId == "bilibili"
            && ContainsAny(line, "HTTP Error 412", "Precondition Failed"))
        {
            return CookieFailureCategory.BotChallenge;
        }

        if (ContainsAny(
                line,
                "login required",
                "authentication required",
                "without authentication",
                "sign in to view",
                "please log in",
                "please login",
                "login to continue",
                "cookies are required"))
        {
            return CookieFailureCategory.AuthenticationRequired;
        }

        if (ContainsAny(line, "captcha", "verify you are human", "verify that you are human"))
        {
            return CookieFailureCategory.BotChallenge;
        }

        return CookieFailureCategory.UnrelatedFailure;
    }

    private static CookieFailureCategory ChooseHigherPriority(
        CookieFailureCategory current,
        CookieFailureCategory candidate)
        => GetPriority(candidate) > GetPriority(current) ? candidate : current;

    private static int GetPriority(CookieFailureCategory category)
        => category switch
        {
            CookieFailureCategory.None => 0,
            CookieFailureCategory.UnrelatedFailure => 10,
            CookieFailureCategory.CookieStoreLocked => 30,
            CookieFailureCategory.CookieDecryptFailed => 35,
            CookieFailureCategory.NetworkFailure => 40,
            CookieFailureCategory.AuthenticationRequired => 70,
            CookieFailureCategory.CookieExpired => 80,
            CookieFailureCategory.BotChallenge => 90,
            CookieFailureCategory.RateLimited => 100,
            _ => 0
        };

    private static bool ContainsAny(string line, params string[] values)
        => values.Any(value => line.Contains(value, StringComparison.OrdinalIgnoreCase));
}
