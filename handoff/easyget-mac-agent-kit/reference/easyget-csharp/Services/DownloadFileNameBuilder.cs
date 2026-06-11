using System.IO;
using System.Text;

namespace EasyGet.Services;

internal static class DownloadFileNameBuilder
{
    private static readonly char[] TrailingTrimChars =
    [
        ' ', '.', '。', '，', ',', '!', '！', '?', '？', ';', '；', ':', '：'
    ];

    internal static string BuildOutputTemplate(string outputDirectory, string? resolvedTitle)
    {
        var fileName = string.IsNullOrWhiteSpace(resolvedTitle)
            ? "%(title).150s.%(ext)s"
            : $"{SanitizeResolvedTitle(resolvedTitle)}.%(ext)s";

        return Path.Combine(outputDirectory, fileName);
    }

    internal static string SanitizeResolvedTitle(string? resolvedTitle)
    {
        return SanitizeResolvedTitle(resolvedTitle, CreateStrictEncoding(Encoding.Default));
    }

    internal static string SanitizeResolvedTitle(string? resolvedTitle, Encoding externalToolEncoding)
    {
        var title = (resolvedTitle ?? string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", " ")
            .Trim()
            .TrimEnd(TrailingTrimChars);

        if (string.IsNullOrWhiteSpace(title))
            return "video";

        var builder = new StringBuilder(title.Length);
        foreach (var rune in title.EnumerateRunes())
        {
            var text = rune.ToString();
            var replacement = text switch
            {
                "<" => "＜",
                ">" => "＞",
                ":" => "：",
                "\"" => "'",
                "/" => "／",
                "\\" => "＼",
                "|" => "｜",
                "?" => "？",
                "*" => "＊",
                "%" => "%%",
                _ when Rune.IsControl(rune) => " ",
                _ => text
            };

            if (replacement.Length == 0)
                continue;

            if (!CanEncode(externalToolEncoding, replacement))
                continue;

            builder.Append(replacement);
        }

        var sanitized = builder
            .ToString()
            .Trim()
            .TrimEnd(TrailingTrimChars);

        return string.IsNullOrWhiteSpace(sanitized) ? "video" : sanitized;
    }

    private static Encoding CreateStrictEncoding(Encoding encoding)
    {
        return Encoding.GetEncoding(
            encoding.CodePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    private static bool CanEncode(Encoding encoding, string text)
    {
        try
        {
            encoding.GetBytes(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
