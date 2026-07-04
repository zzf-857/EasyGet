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
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Unknown(originalUrl);
        }

        var host = uri.Host.ToLowerInvariant();
        if (IsShortLinkHost(host))
            return Build(DouyinUrlKind.ShortLink, originalUrl, GetPathSegment(uri, 0));

        if (!IsDouyinHost(host))
            return Unknown(originalUrl);

        if (host.Equals("live.douyin.com", StringComparison.OrdinalIgnoreCase))
            return BuildWithNumericId(DouyinUrlKind.Live, originalUrl, GetPathSegment(uri, 0));

        var modalId = GetQueryParameter(uri, "modal_id");
        if (!string.IsNullOrWhiteSpace(modalId) && modalId.All(char.IsDigit))
            return Build(DouyinUrlKind.Video, originalUrl, modalId);

        var firstSegment = GetPathSegment(uri, 0);
        if (string.IsNullOrWhiteSpace(firstSegment))
            return Unknown(originalUrl);

        var firstSegmentLower = firstSegment.ToLowerInvariant();
        if (firstSegmentLower == "follow"
            && string.Equals(GetPathSegment(uri, 1), "live", StringComparison.OrdinalIgnoreCase))
        {
            return BuildWithNumericId(DouyinUrlKind.Live, originalUrl, GetPathSegment(uri, 2));
        }

        var kindSegment = firstSegmentLower;
        var idSegmentIndex = 1;
        if (kindSegment == "share")
        {
            kindSegment = GetPathSegment(uri, 1)?.ToLowerInvariant() ?? "";
            idSegmentIndex = 2;
        }

        return kindSegment switch
        {
            "video" => BuildWithNumericId(DouyinUrlKind.Video, originalUrl, GetPathSegment(uri, idSegmentIndex)),
            "note" => BuildWithNumericId(DouyinUrlKind.Note, originalUrl, GetPathSegment(uri, idSegmentIndex)),
            "gallery" => BuildWithNumericId(DouyinUrlKind.Gallery, originalUrl, GetPathSegment(uri, idSegmentIndex)),
            "slides" => BuildWithNumericId(DouyinUrlKind.Slides, originalUrl, GetPathSegment(uri, idSegmentIndex)),
            "user" => BuildUser(originalUrl, uri, GetPathSegment(uri, idSegmentIndex)),
            "collection" when idSegmentIndex == 1 => BuildWithNumericId(DouyinUrlKind.Collection, originalUrl, GetPathSegment(uri, idSegmentIndex)),
            "mix" when idSegmentIndex == 1 => BuildWithNumericId(DouyinUrlKind.Mix, originalUrl, GetPathSegment(uri, idSegmentIndex)),
            "music" when idSegmentIndex == 1 => BuildWithNumericId(DouyinUrlKind.Music, originalUrl, GetPathSegment(uri, idSegmentIndex)),
            "live" when idSegmentIndex == 1 => BuildWithNumericId(DouyinUrlKind.Live, originalUrl, GetPathSegment(uri, idSegmentIndex)),
            _ => Unknown(originalUrl)
        };
    }

    public static bool TryParse(string? url, out DouyinUrlInfo info)
    {
        info = Parse(url);
        return info.IsRecognized;
    }

    private static DouyinUrlInfo Build(DouyinUrlKind kind, string originalUrl, string? id)
        => new(kind, originalUrl, string.IsNullOrWhiteSpace(id) ? null : id);

    private static DouyinUrlInfo BuildWithRequiredId(DouyinUrlKind kind, string originalUrl, string? id)
        => string.IsNullOrWhiteSpace(id)
            ? Unknown(originalUrl)
            : Build(kind, originalUrl, id);

    private static DouyinUrlInfo BuildUser(string originalUrl, Uri uri, string? id)
        => string.IsNullOrWhiteSpace(id)
            ? Unknown(originalUrl)
            : new DouyinUrlInfo(
                DouyinUrlKind.User,
                originalUrl,
                id,
                IsFavoriteCollectionTab(uri));

    private static DouyinUrlInfo BuildWithNumericId(DouyinUrlKind kind, string originalUrl, string? id)
        => !string.IsNullOrWhiteSpace(id) && id.All(char.IsDigit)
            ? Build(kind, originalUrl, id)
            : Unknown(originalUrl);

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

    private static string? GetPathSegment(Uri uri, int index)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
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
