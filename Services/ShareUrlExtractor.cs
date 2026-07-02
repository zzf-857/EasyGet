using System.Text.RegularExpressions;

namespace EasyGet.Services;

internal static partial class ShareUrlExtractor
{
    public static string? Extract(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var trimmed = input.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var match = UrlAtStartRegex().Match(trimmed);
            if (match.Success)
                return TrimTrailingSharePunctuation(match.Value);
        }

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

    [GeneratedRegex(@"^https?://[^\s\u4e00-\u9fff]+")]
    private static partial Regex UrlAtStartRegex();

    [GeneratedRegex(@"https?://[^\s\u4e00-\u9fff]+")]
    private static partial Regex UrlRegex();
}
