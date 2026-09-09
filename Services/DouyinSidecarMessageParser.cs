using System.Globalization;
using System.Text.Json;
using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>Decodes the sidecar JSON-lines protocol independently of task state and process execution.</summary>
internal static class DouyinSidecarMessageParser
{
    internal static bool TryParse(string line, out DouyinSidecarMessage message)
    {
        var rawLine = line.Trim();
        message = new DouyinSidecarMessage
        {
            RawLine = rawLine
        };

        if (!rawLine.StartsWith('{'))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var eventName = SelectFirstNonEmpty(
                GetOptionalString(root, "event"),
                GetOptionalString(root, "type"),
                GetOptionalString(root, "status"));
            var kind = ParseEventKind(eventName);

            var summary = GetOptionalObject(root, "summary") ?? root;
            var progress = GetOptionalObject(root, "progress") ?? root;
            var details = GetOptionalObject(root, "details") ?? GetOptionalObject(summary, "details");
            var counts = details.HasValue
                ? GetOptionalObject(details.Value, "counts")
                : null;

            if (kind == DouyinSidecarEventKind.Unknown && HasProgressFields(progress))
                kind = DouyinSidecarEventKind.Progress;

            message.Kind = kind;
            message.Message = GetString(summary, root, "message", "detail");
            message.Error = GetString(summary, root, "error", "reason");
            message.Title = GetString(summary, root, "title");
            message.Platform = GetString(summary, root, "platform", "extractor", "extractor_key");
            message.DurationSeconds = GetDouble(summary, root, "duration_seconds", "duration");
            message.ThumbnailUrl = GetString(summary, root, "thumbnail_url", "thumbnail");
            message.FileSizeBytes = GetInt64(summary, root, "file_size_bytes", "file_size", "filesize");
            message.OutputFilePath = GetString(summary, root, "output_file_path", "output_path", "file_path");
            message.OutputFilePaths = GetStringList(details, summary, root, "output_files");
            message.ManifestPath = SelectFirstNonEmpty(
                details.HasValue ? GetOptionalString(details.Value, "manifest_path") : "",
                GetString(summary, root, "manifest_path"));
            message.Percent = GetDouble(progress, root, "percent", "percentage");
            message.SpeedBytesPerSecond = GetDouble(progress, root, "speed_bytes_per_sec", "speed_bytes_per_second", "speed");
            message.EtaSeconds = GetDouble(progress, root, "eta_seconds", "eta");
            message.DownloadedBytes = GetInt64(progress, root, "downloaded_bytes", "downloaded");
            message.TotalBytes = GetInt64(progress, root, "total_bytes", "total");
            message.SuccessCount = GetInt32(counts, summary, root, "success_count", "success", "succeeded");
            message.FailedCount = GetInt32(counts, summary, root, "failed_count", "failed", "failure_count");
            message.SkippedCount = GetInt32(counts, summary, root, "skipped_count", "skipped", "skip_count");
            if (details.HasValue)
            {
                message.IsDiscovery = string.Equals(
                    GetOptionalString(details.Value, "kind"),
                    "discovery",
                    StringComparison.OrdinalIgnoreCase);
                message.DiscoveryType = GetOptionalString(details.Value, "discovery_type");
                message.DiscoveryKeyword = GetOptionalString(details.Value, "keyword");
                message.DiscoveryLimit = GetOptionalInt32(details.Value, "limit");
                message.DiscoverySearchMax = GetOptionalInt32(details.Value, "search_max");
                message.DiscoveryItemCount = GetOptionalInt32(details.Value, "item_count");
                message.DiscoveryItems = GetDiscoveryItems(details.Value);
                message.SelfTestImportsOk = GetOptionalBool(details.Value, "imports_ok");
                message.SelfTestCheckedModules = GetOptionalStringList(details.Value, "checked_modules");
                message.SelfTestFailedModules = GetOptionalStringList(details.Value, "failed_modules");
                message.AuthorSummaries = GetAuthorSummaries(details.Value);
                message.TranscriptFileCount = GetOptionalInt32(details.Value, "transcript_file_count");
                if (GetOptionalObject(details.Value, "database") is { } database)
                {
                    message.DatabaseEnabled = GetOptionalBool(database, "enabled");
                    message.DatabasePath = GetOptionalString(database, "path");
                    message.DatabaseExists = GetOptionalBool(database, "exists");
                }

                if (GetOptionalObject(details.Value, "live_room") is { } liveRoom)
                {
                    message.LiveRoomTitle = GetOptionalString(liveRoom, "title");
                    message.LiveAuthorName = GetOptionalString(liveRoom, "author_name");
                    message.LiveRoomStatus = GetOptionalInt32(liveRoom, "status");
                    message.LiveRoomStatusText = GetOptionalString(liveRoom, "status_text");
                }
            }

            return message.Kind != DouyinSidecarEventKind.Unknown;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DouyinSidecarEventKind ParseEventKind(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "progress" or "download_progress" or "downloading" => DouyinSidecarEventKind.Progress,
            "success" or "completed" or "complete" or "done" or "ok" => DouyinSidecarEventKind.Success,
            "failed" or "failure" or "error" => DouyinSidecarEventKind.Failed,
            "cancelled" or "canceled" or "cancel" => DouyinSidecarEventKind.Cancelled,
            "log" or "message" => DouyinSidecarEventKind.Log,
            _ => DouyinSidecarEventKind.Unknown
        };
    }

    private static bool HasProgressFields(JsonElement element)
        => HasProperty(element, "percent")
           || HasProperty(element, "percentage")
           || HasProperty(element, "downloaded_bytes")
           || HasProperty(element, "total_bytes");

    private static bool HasProperty(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out _);

    private static JsonElement? GetOptionalObject(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        return null;
    }

    private static List<string> GetStringList(
        JsonElement? primary,
        JsonElement secondary,
        JsonElement fallback,
        string propertyName)
    {
        var primaryValues = GetOptionalStringList(primary, propertyName);
        if (primaryValues.Count > 0)
            return primaryValues;

        var secondaryValues = GetOptionalStringList(secondary, propertyName);
        if (secondaryValues.Count > 0)
            return secondaryValues;

        return GetOptionalStringList(fallback, propertyName);
    }

    private static List<string> GetOptionalStringList(JsonElement? element, string propertyName)
    {
        if (!element.HasValue
            || element.Value.ValueKind != JsonValueKind.Object
            || !element.Value.TryGetProperty(propertyName, out var value))
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var singleValue = value.GetString();
            return string.IsNullOrWhiteSpace(singleValue) ? [] : [singleValue];
        }

        if (value.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var itemValue = item.GetString();
            if (!string.IsNullOrWhiteSpace(itemValue))
                results.Add(itemValue);
        }

        return results;
    }

    private static IReadOnlyList<DouyinDiscoveryItem> GetDiscoveryItems(JsonElement details)
    {
        if (details.ValueKind != JsonValueKind.Object
            || !details.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<DouyinDiscoveryItem>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            results.Add(new DouyinDiscoveryItem(
                Word: GetOptionalString(item, "word"),
                HotValue: GetOptionalInt64(item, "hot_value"),
                Position: GetOptionalInt32(item, "position"),
                AwemeId: GetOptionalString(item, "aweme_id"),
                Description: GetOptionalString(item, "desc"),
                AuthorNickname: GetOptionalString(item, "author_nickname"),
                SecUid: GetOptionalString(item, "sec_uid"),
                Url: GetOptionalString(item, "url")));
        }

        return results;
    }

    private static IReadOnlyList<DouyinManifestAuthorSummary> GetAuthorSummaries(JsonElement details)
    {
        if (details.ValueKind != JsonValueKind.Object
            || !details.TryGetProperty("author_summaries", out var authors)
            || authors.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<DouyinManifestAuthorSummary>();
        foreach (var author in authors.EnumerateArray())
        {
            if (author.ValueKind != JsonValueKind.Object)
                continue;

            var authorName = GetOptionalString(author, "author_name").Trim();
            var workCount = GetOptionalInt32(author, "work_count") ?? 0;
            if (string.IsNullOrWhiteSpace(authorName) || workCount <= 0)
                continue;

            results.Add(new DouyinManifestAuthorSummary(authorName, workCount));
        }

        return results;
    }

    private static string GetString(JsonElement primary, JsonElement fallback, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = GetOptionalString(primary, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        foreach (var propertyName in propertyNames)
        {
            var value = GetOptionalString(fallback, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private static double? GetDouble(JsonElement primary, JsonElement fallback, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = GetOptionalDouble(primary, propertyName);
            if (value.HasValue)
                return value;
        }

        foreach (var propertyName in propertyNames)
        {
            var value = GetOptionalDouble(fallback, propertyName);
            if (value.HasValue)
                return value;
        }

        return null;
    }

    private static long? GetInt64(JsonElement primary, JsonElement fallback, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = GetOptionalInt64(primary, propertyName);
            if (value.HasValue)
                return value;
        }

        foreach (var propertyName in propertyNames)
        {
            var value = GetOptionalInt64(fallback, propertyName);
            if (value.HasValue)
                return value;
        }

        return null;
    }

    private static int? GetInt32(JsonElement? primary, JsonElement secondary, JsonElement fallback, params string[] propertyNames)
    {
        var value = primary.HasValue
            ? GetInt64(primary.Value, secondary, propertyNames)
            : null;
        value ??= GetInt64(secondary, fallback, propertyNames);
        if (!value.HasValue)
            return null;

        return (int)Math.Clamp(value.Value, int.MinValue, int.MaxValue);
    }

    private static int? GetOptionalInt32(JsonElement element, string propertyName)
    {
        var value = GetOptionalInt64(element, propertyName);
        if (!value.HasValue)
            return null;

        return (int)Math.Clamp(value.Value, int.MinValue, int.MaxValue);
    }

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

    private static bool? GetOptionalBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.True)
            return true;
        if (value.ValueKind == JsonValueKind.False)
            return false;
        if (value.ValueKind == JsonValueKind.String
            && bool.TryParse(value.GetString(), out var boolValue))
        {
            return boolValue;
        }

        return null;
    }

    private static double? GetOptionalDouble(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var stringNumber))
        {
            return stringNumber;
        }

        return null;
    }

    private static long? GetOptionalInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var number))
                return number;

            if (value.TryGetDouble(out var doubleNumber)
                && double.IsFinite(doubleNumber)
                && doubleNumber >= long.MinValue
                && doubleNumber <= long.MaxValue)
            {
                return (long)doubleNumber;
            }
        }

        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var stringNumber))
        {
            return stringNumber;
        }

        return null;
    }

    private static string SelectFirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}
