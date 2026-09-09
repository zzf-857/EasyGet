using System;
using System.Collections.Generic;
using System.Globalization;

namespace EasyGet.Services;

/// <summary>
/// Parses message links without accessing an account. Message IDs in forum links belong
/// to the channel; the preceding topic ID only selects the thread in Telegram's UI.
/// </summary>
internal static class TelegramLinkParser
{
    public static (string chatTarget, int startId, int? endId)? Parse(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)
            || link.Contains('\\')
            || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri)
            || !TryParseQuery(uri, out var query)
            // Comments belong to a different discussion group. Treating the channel's
            // post ID as the download target would silently fetch the wrong message.
            || query.ContainsKey("comment"))
        {
            return null;
        }

        return ParseMessageLink(uri, query);
    }

    public static bool IsTelegramMessageLinkWithComment(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)
            || link.Contains('\\')
            || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri)
            || !TryParseQuery(uri, out var query)
            || !query.Remove("comment"))
        {
            return false;
        }

        // Apply the same host/path/message validation with only comment removed.
        // An unrelated website's comment parameter must not change its download route.
        return ParseMessageLink(uri, query) is not null;
    }

    private static (string chatTarget, int startId, int? endId)? ParseMessageLink(
        Uri uri,
        Dictionary<string, string> query)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || (query.TryGetValue("thread", out var thread) && !TryParsePositiveInt(thread, out _)))
        {
            return null;
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return ParseHttpLink(uri);
        }

        return uri.Scheme.Equals("tg", StringComparison.OrdinalIgnoreCase)
            ? ParseTgLink(uri, query)
            : null;
    }

    private static (string chatTarget, int startId, int? endId)? ParseHttpLink(Uri uri)
    {
        if (!IsTelegramHost(uri.Host))
            return null;

        var path = uri.AbsolutePath;
        // Remove one conventional trailing slash without accepting empty path segments.
        if (path.EndsWith('/'))
            path = path[..^1];
        var segments = path.Split('/');
        if (segments.Length < 3 || segments[0].Length != 0)
            return null;

        if (segments[1].Equals("c", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length is not (4 or 5)
                || !TryBuildPrivateChatTarget(segments[2], out var target)
                || (segments.Length == 5 && !TryParsePositiveInt(segments[3], out _))
                || !TryParseMessageRange(segments[^1], out var startId, out var endId))
            {
                return null;
            }

            return (target, startId, endId);
        }

        // Public web previews add /s/ before the username.
        var usernameIndex = segments[1].Equals("s", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        var messageIndex = usernameIndex + 1;
        var hasTopic = segments.Length == usernameIndex + 3;
        if ((segments.Length != usernameIndex + 2 && !hasTopic)
            || !IsTelegramUsername(segments[usernameIndex])
            || (hasTopic && !TryParsePositiveInt(segments[messageIndex], out _))
            || !TryParseMessageRange(segments[^1], out var publicStartId, out var publicEndId))
        {
            return null;
        }

        return (segments[usernameIndex], publicStartId, publicEndId);
    }

    private static (string chatTarget, int startId, int? endId)? ParseTgLink(
        Uri uri,
        Dictionary<string, string> query)
    {
        if (uri.AbsolutePath is not ("" or "/")
            || !query.TryGetValue("post", out var post)
            || !TryParseMessageRange(post, out var startId, out var endId))
        {
            return null;
        }

        if (uri.Host.Equals("resolve", StringComparison.OrdinalIgnoreCase)
            && query.TryGetValue("domain", out var username)
            && IsTelegramUsername(username))
        {
            return (username, startId, endId);
        }

        // privatepost is Telegram's official scheme; retain EasyGet's legacy private alias.
        if ((uri.Host.Equals("privatepost", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("private", StringComparison.OrdinalIgnoreCase))
            && query.TryGetValue("channel", out var channel)
            && TryBuildPrivateChatTarget(channel, out var target))
        {
            return (target, startId, endId);
        }

        return null;
    }

    private static bool IsTelegramHost(string host) =>
        host.Equals("t.me", StringComparison.OrdinalIgnoreCase)
        || host.Equals("www.t.me", StringComparison.OrdinalIgnoreCase)
        || host.Equals("telegram.me", StringComparison.OrdinalIgnoreCase)
        || host.Equals("www.telegram.me", StringComparison.OrdinalIgnoreCase);

    private static bool IsTelegramUsername(string value)
    {
        if (value.Length == 0 || ReservedPaths.Contains(value))
            return false;

        foreach (var character in value)
        {
            if (character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z'
                or >= '0' and <= '9' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    // These paths invoke Telegram actions, even when their argument happens to be numeric.
    private static readonly HashSet<string> ReservedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "c", "s", "joinchat", "addlist", "addstickers", "addemoji", "addtheme", "addstyle",
        "share", "msg", "proxy", "socks", "login", "auth", "confirmphone", "contact",
        "boost", "call", "giftcode", "invoice", "m", "nft", "setlanguage", "web",
        "a", "k", "z", "auction", "bg", "oauth", "newbot"
    };

    private static bool TryBuildPrivateChatTarget(string channel, out string target)
    {
        target = "";
        if (!ContainsOnlyAsciiDigits(channel)
            || !long.TryParse(channel, NumberStyles.None, CultureInfo.InvariantCulture, out var channelId)
            || channelId <= 0)
        {
            return false;
        }

        target = "-100" + channelId.ToString(CultureInfo.InvariantCulture);
        return long.TryParse(target, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
    }

    private static bool TryParseMessageRange(string value, out int startId, out int? endId)
    {
        startId = 0;
        endId = null;
        var separatorIndex = value.IndexOfAny(['-', '_']);
        var startText = separatorIndex >= 0 ? value[..separatorIndex] : value;
        if (!TryParsePositiveInt(startText, out startId))
            return false;
        if (separatorIndex < 0)
            return true;

        if (!TryParsePositiveInt(value[(separatorIndex + 1)..], out var parsedEndId)
            || parsedEndId < startId)
        {
            return false;
        }

        endId = parsedEndId;
        return true;
    }

    private static bool TryParsePositiveInt(string value, out int result)
    {
        result = 0;
        return ContainsOnlyAsciiDigits(value)
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            && result > 0;
    }

    private static bool ContainsOnlyAsciiDigits(string value)
    {
        if (value.Length == 0)
            return false;
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
                return false;
        }
        return true;
    }

    private static bool TryParseQuery(Uri uri, out Dictionary<string, string> query)
    {
        query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            var encodedName = separatorIndex >= 0 ? pair[..separatorIndex] : pair;
            var encodedValue = separatorIndex >= 0 ? pair[(separatorIndex + 1)..] : "";
            if (!TryDecodeQueryComponent(encodedName, out var name)
                || !TryDecodeQueryComponent(encodedValue, out var value)
                || !query.TryAdd(name, value))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryDecodeQueryComponent(string value, out string decoded)
    {
        decoded = "";
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
                continue;
            if (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2]))
                return false;
            index += 2;
        }

        decoded = Uri.UnescapeDataString(value.Replace('+', ' '));
        return true;
    }
}
