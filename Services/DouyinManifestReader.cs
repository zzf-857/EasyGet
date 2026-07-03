using System.IO;
using System.Text.Json;
using EasyGet.Models;

namespace EasyGet.Services;

public static class DouyinManifestReader
{
    private const int DefaultMaxLines = 1000;
    private const int DefaultMaxItems = 20;

    public static DouyinManifestSummary? ReadSummary(
        string manifestPath,
        int maxLines = DefaultMaxLines,
        int maxItems = DefaultMaxItems)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || maxLines <= 0)
            return null;

        try
        {
            if (!File.Exists(manifestPath))
                return null;

            var itemCount = 0;
            var videoCount = 0;
            var galleryCount = 0;
            var musicCount = 0;
            var unknownCount = 0;
            var fileCount = 0;
            var workIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<DouyinManifestItem>();

            using var reader = new StreamReader(manifestPath);
            for (var lineIndex = 0; lineIndex < maxLines; lineIndex++)
            {
                var line = reader.ReadLine();
                if (line is null)
                    break;

                var item = TryParseItem(line);
                if (item is null)
                    continue;

                itemCount++;
                if (!string.IsNullOrWhiteSpace(item.AwemeId))
                    workIds.Add(item.AwemeId);

                switch (item.MediaType)
                {
                    case "video":
                        videoCount++;
                        break;
                    case "gallery":
                        galleryCount++;
                        break;
                    case "music":
                        musicCount++;
                        break;
                    default:
                        unknownCount++;
                        break;
                }

                fileCount += item.FileCount;
                if (items.Count < maxItems)
                    items.Add(item);
            }

            var isTruncated = reader.ReadLine() is not null;
            return itemCount > 0
                ? new DouyinManifestSummary(
                    itemCount,
                    workIds.Count,
                    videoCount,
                    galleryCount,
                    musicCount,
                    unknownCount,
                    fileCount,
                    isTruncated,
                    items)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static DouyinManifestItem? TryParseItem(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var mediaType = NormalizeMediaType(GetString(root, "media_type"));
            return new DouyinManifestItem(
                AwemeId: GetString(root, "aweme_id"),
                MediaType: mediaType,
                MediaTypeText: ToMediaTypeText(mediaType),
                Description: FirstNonEmpty(GetString(root, "desc"), GetString(root, "title")),
                AuthorName: GetString(root, "author_name"),
                DateText: GetString(root, "date"),
                RecordedAtText: GetString(root, "recorded_at"),
                Tags: GetStringArray(root, "tags"),
                FileNames: ResolveFileNames(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeMediaType(string value)
    {
        var mediaType = value.Trim().ToLowerInvariant();
        return mediaType is "video" or "gallery" or "music"
            ? mediaType
            : "unknown";
    }

    private static string ToMediaTypeText(string mediaType)
        => mediaType switch
        {
            "video" => "视频",
            "gallery" => "图文",
            "music" => "音乐",
            _ => "未知"
        };

    private static IReadOnlyList<string> ResolveFileNames(JsonElement root)
    {
        var fileNames = GetStringArray(root, "file_names");
        if (fileNames.Count > 0)
            return fileNames;

        return GetStringArray(root, "file_paths");
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return [];

        if (value.ValueKind == JsonValueKind.String)
        {
            var single = value.GetString();
            return string.IsNullOrWhiteSpace(single) ? [] : [single.Trim()];
        }

        if (value.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                results.Add(text.Trim());
        }

        return results;
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        return value.GetString()?.Trim() ?? "";
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}
