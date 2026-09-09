using System.IO;
using System.Text.Json;
using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>Converts yt-dlp metadata output to application models without running processes or acquiring cookies.</summary>
internal static class YtDlpMetadataParser
{
    internal static VideoInfo? ParseVideoInfoJson(string json, string url)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var title = NormalizeMetadataTitle(GetOptionalString(root, "title"));
        var platform = GetOptionalString(root, "extractor_key");
        if (string.IsNullOrWhiteSpace(platform))
            platform = GetOptionalString(root, "extractor");

        return new VideoInfo
        {
            Title = title,
            Platform = platform,
            Duration = GetOptionalDouble(root, "duration"),
            Thumbnail = GetThumbnail(root),
            FileSize = GetFileSize(root),
            Url = url,
            AvailableFormats = GetAvailableFormats(root)
        };
    }

    private static string NormalizeMetadataTitle(string title)
        => title.Replace("\r", "").Replace("\n", " ").Trim();

    private static string GetThumbnail(JsonElement root)
    {
        var thumbnail = GetOptionalString(root, "thumbnail");
        if (!string.IsNullOrWhiteSpace(thumbnail)
            || !root.TryGetProperty("thumbnails", out var thumbnails)
            || thumbnails.ValueKind != JsonValueKind.Array)
        {
            return thumbnail;
        }

        foreach (var item in thumbnails.EnumerateArray())
        {
            var candidate = GetOptionalString(item, "url");
            if (!string.IsNullOrWhiteSpace(candidate))
                thumbnail = candidate;
        }

        return thumbnail;
    }

    private static long GetFileSize(JsonElement root)
    {
        var fileSize = GetOptionalInt64(root, "filesize_approx");
        return fileSize > 0 ? fileSize : GetOptionalInt64(root, "filesize");
    }

    private static IReadOnlyList<VideoFormatInfo> GetAvailableFormats(JsonElement root)
    {
        if (!root.TryGetProperty("formats", out var formatsElement)
            || formatsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var formats = new List<VideoFormatInfo>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in formatsElement.EnumerateArray())
        {
            var formatId = GetOptionalString(item, "format_id").Trim();
            if (!IsSafeFormatId(formatId) || !seenIds.Add(formatId))
                continue;

            var format = new VideoFormatInfo(
                formatId,
                GetOptionalString(item, "ext").Trim().ToLowerInvariant(),
                GetOptionalString(item, "vcodec").Trim(),
                GetOptionalString(item, "acodec").Trim(),
                GetOptionalInt32(item, "width"),
                GetOptionalInt32(item, "height"),
                GetOptionalDouble(item, "fps"),
                GetOptionalDouble(item, "tbr"),
                GetOptionalDouble(item, "abr"),
                GetFileSize(item),
                GetOptionalString(item, "format_note").Trim());
            if (format.HasVideo || format.HasAudio)
                formats.Add(format);
        }

        var videoFormats = formats
            .Where(format => format.HasVideo)
            .OrderByDescending(format => format.Height)
            .ThenByDescending(format => format.FramesPerSecond)
            .ThenByDescending(format => format.TotalBitrateKilobytesPerSecond)
            .ThenByDescending(format => format.IsCombined)
            .Take(36);
        var audioFormats = formats
            .Where(format => format.HasAudio && !format.HasVideo)
            .OrderByDescending(format => format.AudioBitrateKilobytesPerSecond)
            .ThenByDescending(format => format.TotalBitrateKilobytesPerSecond)
            .Take(16);

        return videoFormats
            .Concat(audioFormats)
            .ToArray();
    }

    private static bool IsSafeFormatId(string formatId)
        => formatId.Length is > 0 and <= 80
           && formatId.All(character => char.IsAsciiLetterOrDigit(character)
                                        || character is '.' or '_' or '-');

    private static string GetOptionalString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        return value.GetString() ?? "";
    }

    private static long GetOptionalInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var number))
        {
            return 0;
        }

        return Math.Max(0, number);
    }

    private static int GetOptionalInt32(JsonElement element, string propertyName)
    {
        var number = GetOptionalInt64(element, propertyName);
        return number > int.MaxValue ? int.MaxValue : (int)number;
    }

    private static double GetOptionalDouble(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number))
        {
            return 0;
        }

        return double.IsFinite(number) ? Math.Max(0, number) : 0;
    }

    internal static PlaylistFetchResult ParsePlaylistFetchOutput(
        string standardOutput,
        string standardError,
        int exitCode,
        string sourceUrl)
    {
        var empty = new PlaylistInfo { SourceUrl = sourceUrl };
        PlaylistInfo? parsedInfo = null;

        foreach (var line in EnumerateProcessLines(standardOutput))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("entries", out var entries)
                    || entries.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                parsedInfo = ParsePlaylistInfo(document.RootElement, sourceUrl);
                break;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // yt-dlp 或包装脚本可能在 JSON 前后输出普通日志行。
            }
        }

        if (parsedInfo is not null && exitCode == 0)
            return PlaylistFetchResult.Success(parsedInfo, exitCode);

        string? errorMessage = null;
        foreach (var line in EnumerateProcessLines(standardError))
        {
            errorMessage ??= line;
            if (!line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                continue;

            errorMessage = line;
            break;
        }
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            errorMessage = parsedInfo is null
                ? "yt-dlp 未返回有效的合集信息。"
                : $"yt-dlp 获取合集失败（退出码 {exitCode}）。";
        }

        return PlaylistFetchResult.Failure(
            parsedInfo ?? empty,
            errorMessage,
            exitCode);
    }

    internal static PlaylistInfo ParsePlaylistInfoJson(string json, string sourceUrl)
    {
        using var doc = JsonDocument.Parse(json);
        return ParsePlaylistInfo(doc.RootElement, sourceUrl);
    }

    private static PlaylistInfo ParsePlaylistInfo(JsonElement root, string sourceUrl)
    {
        var urls = new List<string>();
        var playlistEntries = new List<PlaylistEntryInfo>();
        var knownEntries = new HashSet<string>(StringComparer.Ordinal);

        if (root.TryGetProperty("entries", out var entries)
            && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var videoUrl = ExtractPlaylistUrl(entry);
                if (!string.IsNullOrWhiteSpace(videoUrl))
                {
                    var fallbackIndex = playlistEntries.Count + 1;
                    var originalIndex = GetOptionalInt32(entry, "playlist_index");
                    var playlistEntry = new PlaylistEntryInfo
                    {
                        Id = GetOptionalString(entry, "id").Trim(),
                        IeKey = GetOptionalString(entry, "ie_key").Trim(),
                        ExtractorKey = GetOptionalString(entry, "extractor_key").Trim(),
                        Url = videoUrl,
                        OriginalTitle = NormalizeMetadataTitle(GetOptionalString(entry, "title")),
                        OriginalIndex = originalIndex > 0 ? originalIndex : fallbackIndex,
                        SectionTitle = NormalizeMetadataTitle(
                            GetOptionalString(entry, "section_title")),
                        ParentTitle = NormalizeMetadataTitle(
                            GetOptionalString(entry, "chapter")),
                        Level = GetOptionalInt32(entry, "level"),
                        Kind = ResolvePlaylistEntryKind(entry),
                        Resources = ParsePlaylistResources(entry)
                    };
                    if (!knownEntries.Add(playlistEntry.StableKey))
                        continue;

                    playlistEntries.Add(playlistEntry);
                    urls.Add(videoUrl);
                }
            }
        }

        return new PlaylistInfo
        {
            Id = GetOptionalString(root, "id").Trim(),
            ExtractorKey = GetOptionalString(root, "extractor_key").Trim(),
            Title = NormalizeMetadataTitle(GetOptionalString(root, "title")),
            SourceUrl = sourceUrl,
            Entries = playlistEntries,
            Urls = urls
        };
    }

    private static PlaylistEntryKind ResolvePlaylistEntryKind(JsonElement entry)
    {
        var videoCodec = GetOptionalString(entry, "vcodec");
        var audioCodec = GetOptionalString(entry, "acodec");
        if (!string.IsNullOrWhiteSpace(videoCodec)
            && !string.Equals(videoCodec, "none", StringComparison.OrdinalIgnoreCase))
        {
            return PlaylistEntryKind.Video;
        }

        if (!string.IsNullOrWhiteSpace(audioCodec)
            && !string.Equals(audioCodec, "none", StringComparison.OrdinalIgnoreCase))
        {
            return PlaylistEntryKind.Audio;
        }

        var extension = GetOptionalString(entry, "ext");
        return string.IsNullOrWhiteSpace(extension)
            ? PlaylistEntryKind.Unknown
            : PlaylistEntryKind.Resource;
    }

    private static IReadOnlyList<MediaResourceInfo> ParsePlaylistResources(JsonElement entry)
    {
        var resources = new List<MediaResourceInfo>();
        var knownUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var propertyName in new[] { "attachments", "resources", "files" })
        {
            if (!entry.TryGetProperty(propertyName, out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in items.EnumerateArray())
            {
                var resourceUrl = GetOptionalString(item, "url");
                if (string.IsNullOrWhiteSpace(resourceUrl)
                    || !IsAbsoluteHttpUrl(resourceUrl)
                    || !knownUrls.Add(resourceUrl))
                {
                    continue;
                }

                resources.Add(new MediaResourceInfo
                {
                    Url = resourceUrl,
                    Title = NormalizeMetadataTitle(
                        GetOptionalString(item, "title"))
                        is { Length: > 0 } title
                        ? title
                        : NormalizeMetadataTitle(GetOptionalString(item, "filename")),
                    Extension = GetOptionalString(item, "ext").Trim().ToLowerInvariant(),
                    MimeType = GetOptionalString(item, "mime_type").Trim(),
                    Kind = PlaylistEntryKind.Resource,
                    OriginalIndex = resources.Count + 1
                });
            }
        }

        return resources;
    }

    internal static string ExtractPlaylistUrlFromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ExtractPlaylistUrl(doc.RootElement);
    }

    private static string ExtractPlaylistUrl(JsonElement root)
    {
        var videoUrl = GetOptionalString(root, "url");
        if (string.IsNullOrWhiteSpace(videoUrl))
            return GetOptionalString(root, "webpage_url");

        if (IsAbsoluteHttpUrl(videoUrl))
            return videoUrl;

        var extractorKey = GetOptionalString(root, "ie_key");
        if (string.IsNullOrWhiteSpace(extractorKey))
            extractorKey = GetOptionalString(root, "extractor_key");

        return string.Equals(extractorKey, "Youtube", StringComparison.OrdinalIgnoreCase)
            ? $"https://www.youtube.com/watch?v={videoUrl}"
            : videoUrl;
    }

    private static bool IsAbsoluteHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    internal static IEnumerable<string> EnumerateProcessLines(string output)
    {
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                yield return trimmed;
        }
    }

}
