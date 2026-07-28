using System.Text;
using System.IO;
using EasyGet.Models;

namespace EasyGet.Services;

public enum DownloadDuplicateKind
{
    None,
    HistoryMatch,
    FileMatch
}

public enum DownloadDuplicateSuggestion
{
    ProceedWithDownload,
    ReviewHistory,
    OpenExistingPath
}

public sealed record DownloadDuplicateResult(
    DownloadDuplicateKind Kind,
    DownloadDuplicateSuggestion Suggestion,
    string NormalizedUrl,
    DownloadHistory? MatchedHistory = null,
    string? ExistingPath = null)
{
    public bool IsDuplicate => Kind != DownloadDuplicateKind.None;
}

/// <summary>
/// Detects semantic URL duplicates without depending on a fixed download path.
/// The path probe is injectable so callers can use a real filesystem, a sandbox,
/// or a virtual storage provider.
/// </summary>
public sealed class DownloadDuplicateDetector
{
    private static readonly HashSet<string> TrackingParameters = new(
        [
            "fbclid",
            "gclid",
            "dclid",
            "msclkid",
            "mc_cid",
            "mc_eid",
            "igshid",
            "spm",
            "spm_id_from",
            "share_source",
            "share_medium",
            "share_plat",
            "share_session_id",
            "ref_src"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly Func<string, bool> _pathExists;

    public DownloadDuplicateDetector(Func<string, bool>? pathExists = null)
    {
        _pathExists = pathExists ?? new Func<string, bool>(
            path => File.Exists(path) || Directory.Exists(path));
    }

    public async Task<DownloadDuplicateResult> DetectAsync(
        string url,
        HistoryService historyService,
        IEnumerable<string>? candidateOutputPaths = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(historyService);
        cancellationToken.ThrowIfCancellationRequested();
        var history = await historyService.GetAllAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return Detect(url, history, candidateOutputPaths);
    }

    public DownloadDuplicateResult Detect(
        string url,
        IEnumerable<DownloadHistory> history,
        IEnumerable<string>? candidateOutputPaths = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        var normalizedUrl = NormalizeUrl(url);
        var historyMatches = history
            .Where(item => item is not null && UrlsMatch(normalizedUrl, item.Url))
            .ToArray();

        var explicitExistingPath = FindExistingPath(candidateOutputPaths);
        if (explicitExistingPath is not null)
        {
            return new DownloadDuplicateResult(
                DownloadDuplicateKind.FileMatch,
                DownloadDuplicateSuggestion.OpenExistingPath,
                normalizedUrl,
                historyMatches.FirstOrDefault(),
                explicitExistingPath);
        }

        foreach (var match in historyMatches)
        {
            var existingPath = FindExistingPath(EnumerateHistoryPaths(match));
            if (existingPath is not null)
            {
                return new DownloadDuplicateResult(
                    DownloadDuplicateKind.FileMatch,
                    DownloadDuplicateSuggestion.OpenExistingPath,
                    normalizedUrl,
                    match,
                    existingPath);
            }
        }

        var historyMatch = historyMatches.FirstOrDefault();
        return historyMatch is null
            ? new DownloadDuplicateResult(
                DownloadDuplicateKind.None,
                DownloadDuplicateSuggestion.ProceedWithDownload,
                normalizedUrl)
            : new DownloadDuplicateResult(
                DownloadDuplicateKind.HistoryMatch,
                DownloadDuplicateSuggestion.ReviewHistory,
                normalizedUrl,
                historyMatch);
    }

    public static string NormalizeUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            throw new ArgumentException(
                "A valid absolute HTTP or HTTPS URL without embedded credentials is required.",
                nameof(url));
        }

        if (TryNormalizeYouTubeShortUrl(parsed, out var youtubeUrl))
            parsed = youtubeUrl;

        var builder = new UriBuilder(parsed)
        {
            Scheme = parsed.Scheme.ToLowerInvariant(),
            Host = parsed.IdnHost.ToLowerInvariant(),
            Fragment = "",
            Query = BuildNormalizedQuery(parsed.Query, parsed.IdnHost)
        };
        if ((builder.Scheme == Uri.UriSchemeHttp && builder.Port == 80)
            || (builder.Scheme == Uri.UriSchemeHttps && builder.Port == 443))
        {
            builder.Port = -1;
        }

        var path = parsed.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        builder.Path = NormalizePath(path);
        return builder.Uri.AbsoluteUri;
    }

    private static bool UrlsMatch(string normalizedUrl, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        try
        {
            return string.Equals(
                normalizedUrl,
                NormalizeUrl(candidate),
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private string? FindExistingPath(IEnumerable<string>? paths)
    {
        if (paths is null)
            return null;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (_pathExists(fullPath))
                    return fullPath;
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or NotSupportedException
                                       or PathTooLongException)
            {
                // Invalid historical paths must not block a new download.
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateHistoryPaths(DownloadHistory history)
    {
        if (!string.IsNullOrWhiteSpace(history.FilePath))
            yield return history.FilePath;
        foreach (var attachment in history.AttachmentFilePaths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(attachment))
                yield return attachment;
        }
        if (!string.IsNullOrWhiteSpace(history.BatchDirectory))
            yield return history.BatchDirectory;
    }

    private static bool TryNormalizeYouTubeShortUrl(Uri uri, out Uri normalized)
    {
        normalized = uri;
        if (!string.Equals(uri.IdnHost, "youtu.be", StringComparison.OrdinalIgnoreCase))
            return false;

        var videoId = uri.AbsolutePath.Trim('/');
        if (videoId.Length == 0 || videoId.Contains('/'))
            return false;

        var query = ParseQuery(uri.Query).ToList();
        query.Add(new QueryPart("v", videoId));
        var builder = new UriBuilder(Uri.UriSchemeHttps, "www.youtube.com")
        {
            Path = "/watch",
            Query = BuildNormalizedQuery(query, "www.youtube.com"),
            Fragment = ""
        };
        normalized = builder.Uri;
        return true;
    }

    private static string NormalizePath(string escapedPath)
    {
        var path = string.IsNullOrEmpty(escapedPath) ? "/" : "/" + escapedPath.TrimStart('/');
        if (path.Length > 1)
            path = path.TrimEnd('/');
        return path;
    }

    private static string BuildNormalizedQuery(string query, string host)
        => BuildNormalizedQuery(ParseQuery(query), host);

    private static string BuildNormalizedQuery(IEnumerable<QueryPart> queryParts, string host)
    {
        var filtered = queryParts
            .Where(part => !IsTrackingParameter(host, part.Name))
            .OrderBy(part => part.Name, StringComparer.Ordinal)
            .ThenBy(part => part.Value, StringComparer.Ordinal)
            .ToArray();
        if (filtered.Length == 0)
            return "";

        var result = new StringBuilder();
        for (var index = 0; index < filtered.Length; index++)
        {
            if (index > 0)
                result.Append('&');
            result.Append(Uri.EscapeDataString(filtered[index].Name));
            if (filtered[index].HasEquals || filtered[index].Value.Length > 0)
                result.Append('=').Append(Uri.EscapeDataString(filtered[index].Value));
        }

        return result.ToString();
    }

    private static IReadOnlyList<QueryPart> ParseQuery(string query)
    {
        var result = new List<QueryPart>();
        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            var rawName = separator < 0 ? segment : segment[..separator];
            var rawValue = separator < 0 ? "" : segment[(separator + 1)..];
            result.Add(new QueryPart(
                DecodeQueryComponent(rawName),
                DecodeQueryComponent(rawValue),
                separator >= 0));
        }

        return result;
    }

    private static string DecodeQueryComponent(string value)
        => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static bool IsTrackingParameter(string host, string name)
    {
        if (name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
            || TrackingParameters.Contains(name))
        {
            return true;
        }

        if (HostMatches(host, "youtube.com")
            || HostMatches(host, "youtu.be"))
        {
            return name.Equals("si", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("feature", StringComparison.OrdinalIgnoreCase);
        }

        return HostMatches(host, "bilibili.com")
               && name.Equals("vd_source", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HostMatches(string host, string domain)
        => string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
           || host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase);

    private sealed record QueryPart(string Name, string Value, bool HasEquals = true);
}
