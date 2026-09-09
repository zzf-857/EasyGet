namespace EasyGet.Services;

internal enum DouyinUrlKind
{
    Unknown,
    ShortLink,
    Video,
    Note,
    Gallery,
    Slides,
    User,
    Collection,
    Mix,
    Music,
    Live
}

internal readonly record struct DouyinUrlInfo(
    DouyinUrlKind Kind,
    string OriginalUrl,
    string? Id,
    bool IsFavoriteCollectionTab = false)
{
    public bool IsRecognized => Kind != DouyinUrlKind.Unknown;

    public bool RequiresExpansion => Kind == DouyinUrlKind.ShortLink;
}

internal static class DouyinUrlParser
{
    public static DouyinUrlInfo Parse(string? url)
    {
        var originalUrl = (url ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(originalUrl))
            return Unknown(originalUrl);

        if (!Uri.TryCreate(EnsureScheme(originalUrl), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
        {
            return Unknown(originalUrl);
        }

        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (IsShortLinkHost(host) && segments.Length == 1
            && segments[0].ToLowerInvariant() is not ("share" or "video" or "note" or "user" or "live"))
            return Build(DouyinUrlKind.ShortLink, originalUrl, GetPathSegment(segments, 0));

        if (!IsDouyinHost(host))
            return Unknown(originalUrl);

        if (host.Equals("live.douyin.com", StringComparison.OrdinalIgnoreCase))
            return BuildWithNumericId(DouyinUrlKind.Live, originalUrl, GetPathSegment(segments, 0));

        var modalId = GetQueryParameter(uri, "modal_id");
        if (IsNumericId(modalId))
            return Build(DouyinUrlKind.Video, originalUrl, modalId);

        var firstSegment = GetPathSegment(segments, 0);
        if (string.IsNullOrWhiteSpace(firstSegment))
            return Unknown(originalUrl);

        var firstSegmentLower = firstSegment.ToLowerInvariant();
        if (firstSegmentLower == "follow"
            && string.Equals(GetPathSegment(segments, 1), "live", StringComparison.OrdinalIgnoreCase))
        {
            return BuildWithNumericId(DouyinUrlKind.Live, originalUrl, GetPathSegment(segments, 2));
        }

        var kindSegment = firstSegmentLower;
        var idSegmentIndex = 1;
        if (kindSegment == "share")
        {
            kindSegment = GetPathSegment(segments, 1)?.ToLowerInvariant() ?? "";
            idSegmentIndex = 2;
        }

        return kindSegment switch
        {
            "video" => BuildWithNumericId(DouyinUrlKind.Video, originalUrl, GetPathSegment(segments, idSegmentIndex)),
            "note" => BuildWithNumericId(DouyinUrlKind.Note, originalUrl, GetPathSegment(segments, idSegmentIndex)),
            "gallery" => BuildWithNumericId(DouyinUrlKind.Gallery, originalUrl, GetPathSegment(segments, idSegmentIndex)),
            "slides" => BuildWithNumericId(DouyinUrlKind.Slides, originalUrl, GetPathSegment(segments, idSegmentIndex)),
            "user" => BuildUser(originalUrl, uri, GetPathSegment(segments, idSegmentIndex)),
            "collection" when idSegmentIndex == 1 => BuildWithNumericId(DouyinUrlKind.Collection, originalUrl, GetPathSegment(segments, idSegmentIndex)),
            "mix" when idSegmentIndex == 1 => BuildWithNumericId(DouyinUrlKind.Mix, originalUrl, GetPathSegment(segments, idSegmentIndex)),
            "music" when idSegmentIndex == 1 => BuildWithNumericId(DouyinUrlKind.Music, originalUrl, GetPathSegment(segments, idSegmentIndex)),
            "live" when idSegmentIndex == 1 => BuildWithNumericId(DouyinUrlKind.Live, originalUrl, GetPathSegment(segments, idSegmentIndex)),
            _ => Unknown(originalUrl)
        };
    }

    public static bool TryParse(string? url, out DouyinUrlInfo info)
    {
        info = Parse(url);
        return info.IsRecognized;
    }

    internal static bool TryGetCanonicalVideoUrl(string? url, out string canonicalUrl)
    {
        var info = Parse(url);
        canonicalUrl = info.Kind == DouyinUrlKind.Video
            ? $"https://www.douyin.com/video/{info.Id}"
            : "";
        return canonicalUrl.Length > 0;
    }

    private static DouyinUrlInfo Build(DouyinUrlKind kind, string originalUrl, string? id)
        => new(kind, originalUrl, string.IsNullOrWhiteSpace(id) ? null : id);

    private static DouyinUrlInfo BuildUser(string originalUrl, Uri uri, string? id)
        => string.IsNullOrWhiteSpace(id)
            ? Unknown(originalUrl)
            : new DouyinUrlInfo(
                DouyinUrlKind.User,
                originalUrl,
                id,
                IsFavoriteCollectionTab(uri));

    private static DouyinUrlInfo BuildWithNumericId(DouyinUrlKind kind, string originalUrl, string? id)
        => IsNumericId(id)
            ? Build(kind, originalUrl, id)
            : Unknown(originalUrl);

    private static bool IsNumericId(string? id)
        => !string.IsNullOrWhiteSpace(id) && id.All(character => character is >= '0' and <= '9');

    private static DouyinUrlInfo Unknown(string originalUrl)
        => new(DouyinUrlKind.Unknown, originalUrl, null);

    private static string EnsureScheme(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return url.StartsWith("//", StringComparison.Ordinal)
            ? $"https:{url}"
            : $"https://{url}";
    }

    private static bool IsShortLinkHost(string host)
        => host.Equals("v.douyin.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("v.iesdouyin.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("iesdouyin.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsDouyinHost(string host)
        => host.Equals("douyin.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("iesdouyin.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".iesdouyin.com", StringComparison.OrdinalIgnoreCase);

    private static string? GetPathSegment(string[] segments, int index)
    {
        if (index < 0 || index >= segments.Length)
            return null;

        return Uri.UnescapeDataString(segments[index]);
    }

    private static string? GetQueryParameter(Uri uri, string parameterName)
    {
        var query = uri.Query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(query))
            return null;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            var key = separatorIndex >= 0 ? pair[..separatorIndex] : pair;
            if (!string.Equals(Uri.UnescapeDataString(key), parameterName, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = separatorIndex >= 0 ? pair[(separatorIndex + 1)..] : "";
            return Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));
        }

        return null;
    }

    private static bool IsFavoriteCollectionTab(Uri uri)
        => string.Equals(
            GetQueryParameter(uri, "showTab"),
            "favorite_collection",
            StringComparison.OrdinalIgnoreCase);
}
