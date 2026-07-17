using System.Text.RegularExpressions;

namespace EasyGet.Services;

internal static partial class CollectionNamingService
{
    internal static string BuildItemTitle(
        string resolvedTitle,
        string collectionTitle,
        int oneBasedIndex,
        int itemCount)
    {
        var fullTitle = (resolvedTitle ?? "").Trim();
        var candidate = fullTitle;

        if (!string.IsNullOrWhiteSpace(collectionTitle)
            && candidate.StartsWith(collectionTitle.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[collectionTitle.Trim().Length..].Trim();
        }
        else if (TrySplitBilibiliTitle(fullTitle, out _, out var splitItemTitle))
        {
            candidate = splitItemTitle;
        }

        candidate = LeadingPartMarkerRegex().Replace(candidate, "")
            .Trim()
            .TrimStart('-', '–', '—', '_', '|', ':', '：')
            .TrimStart();
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = fullTitle;

        if (LeadingSequenceRegex().IsMatch(candidate) || oneBasedIndex <= 0)
            return candidate;

        var width = Math.Max(2, Math.Max(1, itemCount).ToString().Length);
        return $"{oneBasedIndex.ToString($"D{width}")}. {candidate}";
    }

    internal static bool TryExtractCollectionTitle(string resolvedTitle, out string collectionTitle)
    {
        if (TrySplitBilibiliTitle(resolvedTitle, out collectionTitle, out _))
            return true;

        collectionTitle = "";
        return false;
    }

    private static bool TrySplitBilibiliTitle(
        string resolvedTitle,
        out string collectionTitle,
        out string itemTitle)
    {
        collectionTitle = "";
        itemTitle = "";
        if (string.IsNullOrWhiteSpace(resolvedTitle))
            return false;

        var match = BilibiliTitleRegex().Match(resolvedTitle.Trim());
        if (!match.Success)
            return false;

        collectionTitle = match.Groups["collection"].Value.Trim();
        itemTitle = match.Groups["item"].Value.Trim();
        return collectionTitle.Length > 0 && itemTitle.Length > 0;
    }

    [GeneratedRegex(
        @"^(?<collection>.+?)\s+[pP]\d{1,4}\s+(?<item>.+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex BilibiliTitleRegex();

    [GeneratedRegex(
        @"^[pP]\d{1,4}(?:\s*[-_:：.]\s*|\s+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex LeadingPartMarkerRegex();

    [GeneratedRegex(
        @"^\d{1,4}(?:[.、_\-：:]|\s)",
        RegexOptions.CultureInvariant)]
    private static partial Regex LeadingSequenceRegex();
}
