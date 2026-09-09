using System.Text.RegularExpressions;

namespace EasyGet.Services;

internal static partial class ShareUrlExtractor
{
    public static string? Extract(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var urlMatch = UrlRegex().Match(input);
        return urlMatch.Success ? TrimTrailingSharePunctuation(urlMatch.Value) : null;
    }

    private static string TrimTrailingSharePunctuation(string url)
    {
        return url.TrimEnd(
            ',',
            '.',
            ';',
            ':',
            ')',
            ']',
            '}',
            '>',
            '!',
            '?',
            '"',
            '\'',
            '，',
            '。',
            '、',
            '；',
            '：',
            '）',
            '】',
            '》',
            '！',
            '？',
            '”',
            '’');
    }

    [GeneratedRegex(
        @"(?:https?://|tg://(?:resolve|privatepost|private)(?=[/?#]))[^\s\u4e00-\u9fff]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();
}
