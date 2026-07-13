namespace EasyGet.Services.Cookies;

internal static class CookieStorageKey
{
    public static void ValidatePlatformId(string platformId)
    {
        if (string.IsNullOrWhiteSpace(platformId)
            || platformId.Length > 64
            || !char.IsAsciiLetterOrDigit(platformId[0])
            || !char.IsAsciiLetterOrDigit(platformId[^1])
            || platformId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            throw new ArgumentException("Platform ID contains unsupported characters.", nameof(platformId));
        }
    }
}
