namespace EasyGet.Services;

internal sealed record YangshipinUrlInfo(
    string VideoId,
    string PageUrl);

internal static class YangshipinUrlParser
{
    public static bool IsYangshipinVideoUrl(string? url)
        => TryParse(url, out _);

    public static bool TryParse(string? url, out YangshipinUrlInfo info)
    {
        info = null!;
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !HostMatches(uri.Host, "yangshipin.cn")
            || !uri.AbsolutePath.TrimEnd('/').Equals(
                "/video/home",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string videoId;
        try
        {
            videoId = GetQueryParameter(uri.Query, "vid");
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (!IsValidVideoId(videoId))
            return false;

        info = new YangshipinUrlInfo(
            videoId,
            $"https://www.yangshipin.cn/video/home?vid={Uri.EscapeDataString(videoId)}");
        return true;
    }

    private static bool HostMatches(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
           || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);

    private static string GetQueryParameter(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split(
                     '&',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            var key = separator >= 0 ? pair[..separator] : pair;
            if (!Uri.UnescapeDataString(key).Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = separator >= 0 ? pair[(separator + 1)..] : "";
            return Uri.UnescapeDataString(value.Replace('+', ' ')).Trim();
        }

        return "";
    }

    private static bool IsValidVideoId(string videoId)
        => videoId.Length is >= 6 and <= 64
           && videoId.All(character =>
               char.IsAsciiLetterOrDigit(character)
               || character is '-' or '_');
}
