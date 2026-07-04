using System.Diagnostics;
using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EasyGet.Models;

namespace EasyGet.Services;

public interface IDouyinSpecialDownloadService
{
    Task DownloadAsync(
        DownloadTask task,
        AppConfig config,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null,
        CancellationToken cancellationToken = default);

    Task<DouyinDiscoveryResult> DiscoverAsync(
        DouyinDiscoveryRequest request,
        AppConfig config,
        Action<string>? logCallback = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Douyin discovery is not supported by this service implementation.");
}

public enum DouyinDiscoveryType
{
    HotBoard,
    Search
}

public sealed record DouyinDiscoveryRequest(
    DouyinDiscoveryType Type,
    string OutputDirectory,
    string Keyword = "",
    int Limit = 30,
    int SearchMax = 50);

public sealed record DouyinDiscoveryResult(
    string DiscoveryType,
    string OutputFilePath,
    int ItemCount,
    IReadOnlyList<DouyinDiscoveryItem> Items,
    string Keyword = "",
    int? Limit = null,
    int? SearchMax = null);

public sealed record DouyinDiscoveryItem(
    string Word = "",
    long? HotValue = null,
    int? Position = null,
    string AwemeId = "",
    string Description = "",
    string AuthorNickname = "",
    string SecUid = "",
    string Url = "") : INotifyPropertyChanged
{
    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class DouyinSpecialDownloadService : IDouyinSpecialDownloadService
{
    internal const string DouyinCookieEnvironmentVariableName = "EASYGET_DOUYIN_COOKIE";
    private const int MaxTaskEventLogLines = 5;
    private const string DouyinManifestFileName = "download_manifest.jsonl";
    private const string DouyinManifestSnapshotPrefix = "download_manifest.easyget-";
    private const string DouyinManifestExtension = ".jsonl";
    private const int DouyinManifestSnapshotTimestampLength = 16;
    private const int DouyinManifestSnapshotUuidLength = 8;
    private const string SensitiveValueRedaction = "[redacted]";

    private readonly IDouyinSidecarProcessRunner _runner;

    public DouyinSpecialDownloadService()
        : this(new DouyinSidecarProcessRunner())
    {
    }

    internal DouyinSpecialDownloadService(IDouyinSidecarProcessRunner runner)
    {
        _runner = runner;
    }

    public async Task DownloadAsync(
        DownloadTask task,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null,
        CancellationToken cancellationToken = default)
        => await DownloadCoreAsync(task, progress, logCallback, cancellationToken, config: null);

    public async Task DownloadAsync(
        DownloadTask task,
        AppConfig config,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null,
        CancellationToken cancellationToken = default)
        => await DownloadCoreAsync(task, progress, logCallback, cancellationToken, config);

    public async Task<DouyinDiscoveryResult> DiscoverAsync(
        DouyinDiscoveryRequest request,
        AppConfig config,
        Action<string>? logCallback = null,
        CancellationToken cancellationToken = default)
    {
        var sidecarRequest = DouyinSidecarDiscoveryRequest.FromRequest(request, config);

        try
        {
            await foreach (var line in _runner.RunDiscoveryAsync(sidecarRequest, cancellationToken))
            {
                if (!TryParseStdoutLine(line, out var message))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        logCallback?.Invoke(RedactSensitiveText(line, sidecarRequest.Cookie));

                    continue;
                }

                switch (message.Kind)
                {
                    case DouyinSidecarEventKind.Success when message.IsDiscovery:
                        return BuildDiscoveryResult(message);
                    case DouyinSidecarEventKind.Success:
                        throw new InvalidOperationException("Douyin sidecar returned a non-discovery success summary.");
                    case DouyinSidecarEventKind.Failed:
                        throw new InvalidOperationException(
                            FormatUserFacingError(
                                RedactSensitiveText(
                                    SelectFirstNonEmpty(message.Error, message.Message, "Douyin discovery failed."),
                                    sidecarRequest.Cookie)));
                    case DouyinSidecarEventKind.Cancelled:
                        throw new OperationCanceledException(
                            SelectFirstNonEmpty(message.Message, "Douyin discovery was cancelled."),
                            cancellationToken);
                    case DouyinSidecarEventKind.Log:
                    case DouyinSidecarEventKind.Progress:
                        var logMessage = RedactSensitiveText(
                            SelectFirstNonEmpty(message.Message, message.RawLine),
                            sidecarRequest.Cookie);
                        if (!string.IsNullOrWhiteSpace(logMessage))
                            logCallback?.Invoke(logMessage);
                        break;
                }
            }

            throw new InvalidOperationException("Douyin sidecar did not return a discovery summary.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                FormatUserFacingError(RedactSensitiveText(ex.Message, sidecarRequest.Cookie)),
                ex);
        }
    }

    private async Task DownloadCoreAsync(
        DownloadTask task,
        IProgress<DownloadProgress>? progress,
        Action<string>? logCallback,
        CancellationToken cancellationToken,
        AppConfig? config)
    {
        task.Status = DownloadStatus.Downloading;

        var request = DouyinSidecarRequest.FromTask(task, config);

        try
        {
            var sawTerminalSummary = false;

            await foreach (var line in _runner.RunAsync(request, cancellationToken))
            {
                if (!TryParseStdoutLine(line, out var message))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        logCallback?.Invoke(RedactSensitiveText(line, request.Cookie));

                    continue;
                }

                switch (message.Kind)
                {
                    case DouyinSidecarEventKind.Progress:
                        ApplyOutcomeCounts(task, message);
                        if (!sawTerminalSummary && TryMapProgress(message, out var mappedProgress))
                        {
                            progress?.Report(mappedProgress);
                            AppendTaskEvent(task, $"进度 {mappedProgress.Percent:F0}%");
                        }
                        break;
                    case DouyinSidecarEventKind.Success:
                        if (!sawTerminalSummary)
                        {
                            ApplySuccessSummary(task, message);
                            AppendTaskEvent(
                                task,
                                FormatTerminalEvent(
                                    "已完成",
                                    RedactSensitiveText(message.Message, request.Cookie)));
                            sawTerminalSummary = true;
                        }
                        break;
                    case DouyinSidecarEventKind.Failed:
                        if (!sawTerminalSummary)
                        {
                            ApplyFailureSummary(task, message);
                            task.ErrorMessage = FormatUserFacingError(
                                RedactSensitiveText(task.ErrorMessage, request.Cookie));
                            AppendTaskEvent(task, FormatTerminalEvent("失败", SelectFirstNonEmpty(task.ErrorMessage, message.Message)));
                            sawTerminalSummary = true;
                        }
                        break;
                    case DouyinSidecarEventKind.Cancelled:
                        if (!sawTerminalSummary)
                        {
                            ApplyCancelledSummary(task, message);
                            AppendTaskEvent(
                                task,
                                FormatTerminalEvent(
                                    "已取消",
                                    RedactSensitiveText(message.Message, request.Cookie)));
                            sawTerminalSummary = true;
                        }
                        break;
                    case DouyinSidecarEventKind.Log:
                        var logMessage = RedactSensitiveText(
                            SelectFirstNonEmpty(message.Message, message.RawLine),
                            request.Cookie);
                        logCallback?.Invoke(logMessage);
                        AppendTaskEvent(task, logMessage);
                        break;
                }
            }

            if (task.Status == DownloadStatus.Downloading)
            {
                task.Status = DownloadStatus.Failed;
                task.ErrorMessage = "Douyin sidecar did not return a terminal summary.";
                AppendTaskEvent(task, task.ErrorMessage);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (task.Status == DownloadStatus.Paused)
            {
                task.ErrorMessage = "";
                return;
            }

            ApplyCancelledSummary(task, new DouyinSidecarMessage
            {
                Kind = DouyinSidecarEventKind.Cancelled
            });
        }
        catch (Exception ex)
        {
            if (task.Status is DownloadStatus.Failed or DownloadStatus.Cancelled)
                return;

            var message = FormatUserFacingError(RedactSensitiveText(ex.Message, request.Cookie));
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = message;
            AppendTaskEvent(task, message);
            logCallback?.Invoke($"[douyin-sidecar] failed: {message}");
        }
    }

    internal static bool TryParseStdoutLine(string line, out DouyinSidecarMessage message)
    {
        var rawLine = line.Trim();
        message = new DouyinSidecarMessage
        {
            RawLine = rawLine
        };

        if (string.IsNullOrWhiteSpace(rawLine))
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
            }

            return message.Kind != DouyinSidecarEventKind.Unknown;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryMapProgress(DouyinSidecarMessage message, out DownloadProgress progress)
    {
        progress = new DownloadProgress();
        if (message.Kind != DouyinSidecarEventKind.Progress)
            return false;

        var downloaded = NormalizeNonNegativeInt64(message.DownloadedBytes);
        var total = NormalizeNonNegativeInt64(message.TotalBytes);
        var percent = NormalizeFiniteValue(message.Percent);
        if (percent <= 0 && total > 0 && downloaded > 0)
            percent = downloaded * 100d / total;

        progress = new DownloadProgress
        {
            Percent = Math.Clamp(percent, 0, 100),
            Speed = Math.Max(0, NormalizeFiniteValue(message.SpeedBytesPerSecond)),
            Eta = Math.Max(0, NormalizeFiniteValue(message.EtaSeconds)),
            Downloaded = downloaded,
            Total = total,
            RawLine = message.RawLine
        };
        return true;
    }

    private static DouyinDiscoveryResult BuildDiscoveryResult(DouyinSidecarMessage message)
    {
        var discoveryType = SelectFirstNonEmpty(message.DiscoveryType, "unknown");
        var itemCount = message.DiscoveryItemCount ?? message.DiscoveryItems.Count;
        return new DouyinDiscoveryResult(
            DiscoveryType: discoveryType,
            OutputFilePath: message.OutputFilePath,
            ItemCount: Math.Max(0, itemCount),
            Items: message.DiscoveryItems,
            Keyword: message.DiscoveryKeyword,
            Limit: message.DiscoveryLimit,
            SearchMax: message.DiscoverySearchMax);
    }

    internal static void ApplySuccessSummary(DownloadTask task, DouyinSidecarMessage message)
    {
        ApplySummaryMetadata(task, message, useDefaultDouyinPlatform: true);
        ApplyOutcomeCounts(task, message);

        if (!string.IsNullOrWhiteSpace(message.OutputFilePath))
        {
            var outputFilePath = message.OutputFilePath.Trim();
            if (!IsSafeOutputFilePath(task.OutputDirectory, outputFilePath))
            {
                task.Status = DownloadStatus.Failed;
                task.ErrorMessage = "Douyin sidecar returned output file outside the task output directory.";
                return;
            }

            task.OutputFilePath = outputFilePath;
        }

        var safeOutputFilePaths = GetSafeOutputFilePaths(task.OutputDirectory, message.OutputFilePaths);
        if (!string.IsNullOrWhiteSpace(task.OutputFilePath)
            && !ContainsEquivalentPath(safeOutputFilePaths, task.OutputFilePath))
        {
            safeOutputFilePaths.Insert(0, task.OutputFilePath);
        }
        if (TryGetSafeManifestPath(task.OutputDirectory, message.ManifestPath, out var manifestPath)
            && !ContainsEquivalentPath(safeOutputFilePaths, manifestPath))
        {
            safeOutputFilePaths.Add(manifestPath);
        }

        task.OutputFilePaths = safeOutputFilePaths;

        var fileSize = NormalizeNonNegativeInt64(message.FileSizeBytes);
        if (fileSize > 0)
        {
            task.FileSize = fileSize;
            task.DownloadedSize = fileSize;
        }
        else if (task.FileSize > 0)
        {
            task.DownloadedSize = task.FileSize;
        }

        task.Progress = 100;
        task.ErrorMessage = "";
        task.Status = DownloadStatus.Completed;
    }

    internal static void ApplyFailureSummary(DownloadTask task, DouyinSidecarMessage message)
    {
        ApplySummaryMetadata(task, message, useDefaultDouyinPlatform: true);
        ApplyOutcomeCounts(task, message);

        task.Status = DownloadStatus.Failed;
        task.ErrorMessage = SelectFirstNonEmpty(
            message.Error,
            message.Message,
            "Douyin sidecar failed.");
    }

    internal static void ApplyCancelledSummary(DownloadTask task, DouyinSidecarMessage message)
    {
        ApplySummaryMetadata(task, message, useDefaultDouyinPlatform: false);
        ApplyOutcomeCounts(task, message);

        if (task.Status == DownloadStatus.Paused)
        {
            task.ErrorMessage = "";
            return;
        }

        task.Status = DownloadStatus.Cancelled;
        task.ErrorMessage = "";
    }

    private static void ApplySummaryMetadata(
        DownloadTask task,
        DouyinSidecarMessage message,
        bool useDefaultDouyinPlatform)
    {
        if (!string.IsNullOrWhiteSpace(message.Title))
            task.Title = message.Title.Trim();

        if (!string.IsNullOrWhiteSpace(message.Platform))
            task.Platform = message.Platform.Trim();
        else if (useDefaultDouyinPlatform && string.IsNullOrWhiteSpace(task.Platform))
            task.Platform = "Douyin";

        if (message.DurationSeconds is { } durationSeconds)
            task.Duration = Math.Max(0, NormalizeFiniteValue(durationSeconds));

        if (!string.IsNullOrWhiteSpace(message.ThumbnailUrl))
            task.ThumbnailUrl = message.ThumbnailUrl.Trim();
    }

    private static void ApplyOutcomeCounts(DownloadTask task, DouyinSidecarMessage message)
    {
        if (message.SuccessCount.HasValue)
            task.DouyinSuccessCount = Math.Max(0, message.SuccessCount.Value);
        if (message.FailedCount.HasValue)
            task.DouyinFailedCount = Math.Max(0, message.FailedCount.Value);
        if (message.SkippedCount.HasValue)
            task.DouyinSkippedCount = Math.Max(0, message.SkippedCount.Value);
    }

    private static void AppendTaskEvent(DownloadTask task, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var existingLines = task.DouyinTaskEventLog
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        existingLines.Add(message.Trim());
        if (existingLines.Count > MaxTaskEventLogLines)
            existingLines = existingLines[^MaxTaskEventLogLines..];

        task.DouyinTaskEventLog = string.Join(Environment.NewLine, existingLines);
    }

    private static string FormatTerminalEvent(string statusText, string message)
    {
        var detail = message.Trim();
        return string.IsNullOrWhiteSpace(detail)
            ? statusText
            : $"{statusText}: {detail}";
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

    private static double NormalizeFiniteValue(double? value)
    {
        var number = value ?? 0;
        return double.IsFinite(number) ? number : 0;
    }

    private static long NormalizeNonNegativeInt64(long? value)
        => Math.Max(0, value ?? 0);

    private static bool IsSafeOutputFilePath(string? outputDirectory, string outputFilePath)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || string.IsNullOrWhiteSpace(outputFilePath))
            return false;

        try
        {
            var fullOutputDirectory = Path.GetFullPath(outputDirectory);
            var fullOutputFilePath = Path.GetFullPath(outputFilePath);
            var directoryWithSeparator = fullOutputDirectory.EndsWith(Path.DirectorySeparatorChar)
                || fullOutputDirectory.EndsWith(Path.AltDirectorySeparatorChar)
                    ? fullOutputDirectory
                    : fullOutputDirectory + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return fullOutputFilePath.StartsWith(directoryWithSeparator, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static List<string> GetSafeOutputFilePaths(
        string? outputDirectory,
        IEnumerable<string> outputFilePaths)
    {
        var safePaths = new List<string>();
        foreach (var rawPath in outputFilePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            var outputFilePath = rawPath.Trim();
            if (!IsSafeOutputFilePath(outputDirectory, outputFilePath)
                || ContainsEquivalentPath(safePaths, outputFilePath))
            {
                continue;
            }

            safePaths.Add(outputFilePath);
        }

        return safePaths;
    }

    private static bool TryGetSafeManifestPath(string? outputDirectory, string? manifestPath, out string safeManifestPath)
    {
        safeManifestPath = "";
        if (string.IsNullOrWhiteSpace(manifestPath))
            return false;

        try
        {
            var fullManifestPath = Path.GetFullPath(manifestPath.Trim());
            if (!IsDouyinManifestPath(fullManifestPath)
                || !IsSafeOutputFilePath(outputDirectory, fullManifestPath)
                || !File.Exists(fullManifestPath))
            {
                return false;
            }

            safeManifestPath = fullManifestPath;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool IsDouyinManifestPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            return IsDouyinManifestFileName(Path.GetFileName(path.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsDouyinManifestFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(fileName, DouyinManifestFileName, comparison))
            return true;

        if (!fileName.StartsWith(DouyinManifestSnapshotPrefix, comparison)
            || !fileName.EndsWith(DouyinManifestExtension, comparison))
        {
            return false;
        }

        var tokenLength = fileName.Length
            - DouyinManifestSnapshotPrefix.Length
            - DouyinManifestExtension.Length;
        var expectedTokenLength = DouyinManifestSnapshotTimestampLength
            + 1
            + DouyinManifestSnapshotUuidLength;
        if (tokenLength != expectedTokenLength)
            return false;

        var tokenStart = DouyinManifestSnapshotPrefix.Length;
        var timestamp = fileName.Substring(tokenStart, DouyinManifestSnapshotTimestampLength);
        var separatorIndex = tokenStart + DouyinManifestSnapshotTimestampLength;
        var uuidStart = separatorIndex + 1;
        var uuid = fileName.Substring(uuidStart, DouyinManifestSnapshotUuidLength);

        return fileName[separatorIndex] == '-'
               && IsDouyinManifestSnapshotTimestamp(timestamp)
               && uuid.All(IsHexDigit);
    }

    private static bool IsDouyinManifestSnapshotTimestamp(string value)
    {
        if (value.Length != DouyinManifestSnapshotTimestampLength
            || value[8] != 'T'
            || value[15] != 'Z')
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (index is 8 or 15)
                continue;

            if (!char.IsDigit(value[index]))
                return false;
        }

        return true;
    }

    private static bool IsHexDigit(char value)
        => value is >= '0' and <= '9'
           or >= 'a' and <= 'f'
           or >= 'A' and <= 'F';

    private static bool ContainsEquivalentPath(IEnumerable<string> paths, string candidate)
        => paths.Any(path => AreEquivalentPaths(path, candidate));

    private static bool AreEquivalentPaths(string left, string right)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }
    }

    private static string SelectFirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    internal static string RedactSensitiveText(string? text, params string?[] sensitiveValues)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var redacted = text;
        foreach (var value in sensitiveValues)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            redacted = redacted.Replace(value, SensitiveValueRedaction, StringComparison.Ordinal);
        }

        return redacted;
    }

    internal static string FormatUserFacingError(string? rawMessage)
    {
        var message = SelectFirstNonEmpty(rawMessage, "Douyin sidecar failed.");
        if (IsAlreadyActionableDouyinError(message))
            return message;

        var normalized = message.ToLowerInvariant();
        var prefix = "";
        if (ContainsAny(normalized, "sidecar was not found", "failed to start douyin sidecar", "no such file"))
        {
            prefix = "抖音专项引擎不可用，请确认 sidecar 已随 EasyGet 发布或重新运行发布构建。";
        }
        else if (ContainsAny(normalized, "cookie", "cookies", "login", "loginrequired", "请先登录", "登录", "permission", "权限", "401", "403"))
        {
            prefix = "抖音 Cookie 或登录态可能失效，请在设置中更新 Cookie 后重试。";
        }
        else if (ContainsAny(normalized, "429", "rate limit", "ratelimit", "too many requests", "限流", "请求频繁", "anti-bot", "captcha", "验证码"))
        {
            prefix = "抖音请求被限流或触发风控，请降低并发、稍后重试，必要时更新 Cookie。";
        }
        else if (ContainsAny(normalized, "proxy", "timed out", "timeout", "connection", "connect", "network", "dns", "代理", "网络", "连接"))
        {
            prefix = "抖音网络或代理连接失败，请检查网络、代理设置和本机防火墙后重试。";
        }

        return string.IsNullOrWhiteSpace(prefix)
            ? message
            : $"{prefix} 原始信息：{message}";
    }

    private static bool IsAlreadyActionableDouyinError(string message)
        => message.StartsWith("抖音 Cookie", StringComparison.Ordinal)
           || message.StartsWith("抖音请求", StringComparison.Ordinal)
           || message.StartsWith("抖音网络", StringComparison.Ordinal)
           || message.StartsWith("抖音专项引擎", StringComparison.Ordinal);

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.Ordinal));
}

internal enum DouyinSidecarEventKind
{
    Unknown,
    Progress,
    Success,
    Failed,
    Cancelled,
    Log
}

internal sealed class DouyinSidecarMessage
{
    public DouyinSidecarEventKind Kind { get; set; }
    public string RawLine { get; set; } = "";
    public string Message { get; set; } = "";
    public string Error { get; set; } = "";
    public string Title { get; set; } = "";
    public string Platform { get; set; } = "";
    public double? DurationSeconds { get; set; }
    public string ThumbnailUrl { get; set; } = "";
    public long? FileSizeBytes { get; set; }
    public string OutputFilePath { get; set; } = "";
    public List<string> OutputFilePaths { get; set; } = [];
    public string ManifestPath { get; set; } = "";
    public double? Percent { get; set; }
    public double? SpeedBytesPerSecond { get; set; }
    public double? EtaSeconds { get; set; }
    public long? DownloadedBytes { get; set; }
    public long? TotalBytes { get; set; }
    public int? SuccessCount { get; set; }
    public int? FailedCount { get; set; }
    public int? SkippedCount { get; set; }
    public bool IsDiscovery { get; set; }
    public string DiscoveryType { get; set; } = "";
    public string DiscoveryKeyword { get; set; } = "";
    public int? DiscoveryLimit { get; set; }
    public int? DiscoverySearchMax { get; set; }
    public int? DiscoveryItemCount { get; set; }
    public IReadOnlyList<DouyinDiscoveryItem> DiscoveryItems { get; set; } = [];
}

internal sealed record DouyinSidecarRequest(
    string Url,
    string OutputDirectory,
    string Format,
    string Quality,
    string Title,
    string Cookie = "",
    string Proxy = "",
    string Mode = "post",
    int Limit = 1,
    string StartTime = "",
    string EndTime = "",
    bool DownloadPinned = false,
    bool IncludeCover = false,
    bool IncludeAvatar = false,
    bool IncludeMusic = false,
    bool IncludeComments = false,
    bool CommentIncludeReplies = false,
    int MaxComments = 0,
    int CommentPageSize = AppConfig.MaxDouyinCommentPageSize,
    bool IncludeJson = false,
    bool IncludeDatabase = false,
    bool IncrementalDownload = false,
    string FilenameTemplate = AppConfig.DefaultDouyinTemplate,
    string FolderTemplate = AppConfig.DefaultDouyinTemplate,
    string AuthorDirectoryMode = "nickname",
    bool GroupByMode = true,
    int ThreadCount = 3)
{
    public static DouyinSidecarRequest FromTask(DownloadTask task, AppConfig? config)
    {
        var proxy = config is { UseProxy: true }
            ? NormalizeText(config.ProxyAddress)
            : "";
        var threadCount = Math.Clamp(
            config?.ConcurrentFragments ?? 3,
            AppConfig.MinConcurrentFragments,
            AppConfig.MaxConcurrentFragments);
        var commentPageSize = Math.Clamp(
            config?.DouyinCommentPageSize ?? AppConfig.MaxDouyinCommentPageSize,
            1,
            AppConfig.MaxDouyinCommentPageSize);

        return new DouyinSidecarRequest(
            Url: NormalizeText(task.Url),
            OutputDirectory: NormalizeText(task.OutputDirectory),
            Format: NormalizeText(task.Format),
            Quality: NormalizeText(task.Quality),
            Title: NormalizeText(task.Title),
            Cookie: NormalizeText(config?.CookieContent),
            Proxy: proxy,
            Mode: ConfigService.NormalizeDouyinMode(config?.DouyinMode),
            Limit: Math.Max(0, config?.DouyinLimit ?? 1),
            StartTime: NormalizeText(config?.DouyinStartTime),
            EndTime: NormalizeText(config?.DouyinEndTime),
            DownloadPinned: config?.DouyinDownloadPinned ?? false,
            IncludeCover: config?.DouyinDownloadCover ?? false,
            IncludeAvatar: config?.DouyinDownloadAvatar ?? false,
            IncludeMusic: config?.DouyinDownloadMusic ?? false,
            IncludeComments: config?.DouyinDownloadComments ?? false,
            CommentIncludeReplies: config?.DouyinCommentIncludeReplies ?? false,
            MaxComments: Math.Max(0, config?.DouyinMaxComments ?? 0),
            CommentPageSize: commentPageSize,
            IncludeJson: config?.DouyinDownloadJson ?? false,
            IncludeDatabase: config?.DouyinEnableDatabase ?? false,
            IncrementalDownload: config?.DouyinIncrementalDownload ?? false,
            FilenameTemplate: ConfigService.NormalizeDouyinTemplate(config?.DouyinFilenameTemplate),
            FolderTemplate: ConfigService.NormalizeDouyinTemplate(config?.DouyinFolderTemplate),
            AuthorDirectoryMode: ConfigService.NormalizeDouyinAuthorDirectoryMode(config?.DouyinAuthorDirectoryMode),
            GroupByMode: config?.DouyinGroupByMode ?? true,
            ThreadCount: threadCount);
    }

    private static string NormalizeText(string? value, string fallback = "")
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }
}

internal sealed record DouyinSidecarDiscoveryRequest(
    DouyinDiscoveryType Type,
    string OutputDirectory,
    string Keyword = "",
    int Limit = 30,
    int SearchMax = 50,
    string Cookie = "",
    string Proxy = "")
{
    public static DouyinSidecarDiscoveryRequest FromRequest(DouyinDiscoveryRequest request, AppConfig? config)
    {
        var outputDirectory = NormalizeText(request.OutputDirectory);
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("Douyin discovery output directory is required.", nameof(request));

        var keyword = NormalizeText(request.Keyword);
        if (request.Type == DouyinDiscoveryType.Search && string.IsNullOrWhiteSpace(keyword))
            throw new ArgumentException("Douyin discovery search keyword is required.", nameof(request));

        var proxy = config is { UseProxy: true }
            ? NormalizeText(config.ProxyAddress)
            : "";

        return new DouyinSidecarDiscoveryRequest(
            Type: request.Type,
            OutputDirectory: outputDirectory,
            Keyword: keyword,
            Limit: Math.Max(0, request.Limit),
            SearchMax: Math.Max(1, request.SearchMax),
            Cookie: NormalizeText(config?.CookieContent),
            Proxy: proxy);
    }

    private static string NormalizeText(string? value, string fallback = "")
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }
}

internal interface IDouyinSidecarProcessRunner
{
    IAsyncEnumerable<string> RunAsync(
        DouyinSidecarRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<string> RunDiscoveryAsync(
        DouyinSidecarDiscoveryRequest request,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("Douyin sidecar runner does not support discovery requests.");
}

internal sealed class DouyinSidecarProcessRunner : IDouyinSidecarProcessRunner
{
    private readonly string _pythonExecutablePath;
    private readonly string _scriptPath;

    public DouyinSidecarProcessRunner(
        string pythonExecutablePath = "python",
        string? scriptPath = null)
    {
        _pythonExecutablePath = pythonExecutablePath;
        _scriptPath = scriptPath ?? ResolveDefaultSidecarPath(AppContext.BaseDirectory);
    }

    public async IAsyncEnumerable<string> RunAsync(
        DouyinSidecarRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in RunProcessAsync(CreateProcessStartInfo(request), request.Cookie, cancellationToken))
            yield return line;
    }

    public async IAsyncEnumerable<string> RunDiscoveryAsync(
        DouyinSidecarDiscoveryRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in RunProcessAsync(CreateDiscoveryProcessStartInfo(request), request.Cookie, cancellationToken))
            yield return line;
    }

    private async IAsyncEnumerable<string> RunProcessAsync(
        ProcessStartInfo processStartInfo,
        string cookie,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!File.Exists(_scriptPath))
            throw new InvalidOperationException($"Douyin sidecar was not found: {_scriptPath}");

        using var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException($"Failed to start Douyin sidecar: {_pythonExecutablePath}");
        using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));

        var stderr = new StringBuilder();
        var stderrTask = ReadLinesAsync(process.StandardError, line => stderr.AppendLine(line), cancellationToken);

        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            yield return line;

        await process.WaitForExitAsync(cancellationToken);
        await stderrTask;

        if (process.ExitCode != 0)
        {
            var stderrText = DouyinSpecialDownloadService.RedactSensitiveText(
                stderr.ToString().Trim(),
                cookie);
            throw new InvalidOperationException(
                !string.IsNullOrWhiteSpace(stderrText)
                    ? stderrText
                    : $"Douyin sidecar exited with code {process.ExitCode}.");
        }
    }

    private ProcessStartInfo CreateProcessStartInfo(DouyinSidecarRequest request)
    {
        var psi = CreateBaseProcessStartInfo();
        AddArgument(psi, "--url", request.Url);
        AddArgument(psi, "--output-dir", request.OutputDirectory);
        AddArgument(psi, "--format", request.Format);
        AddArgument(psi, "--quality", request.Quality);
        AddArgument(psi, "--title", request.Title);
        AddCookieEnvironmentArgument(psi, request.Cookie);
        AddArgument(psi, "--proxy", request.Proxy);
        AddArgument(psi, "--mode", request.Mode);
        AddArgument(psi, "--limit", request.Limit.ToString(CultureInfo.InvariantCulture));
        AddArgument(psi, "--start-time", request.StartTime);
        AddArgument(psi, "--end-time", request.EndTime);
        AddArgument(psi, "--filename-template", request.FilenameTemplate);
        AddArgument(psi, "--folder-template", request.FolderTemplate);
        AddArgument(psi, "--author-dir", request.AuthorDirectoryMode);
        AddArgument(psi, "--thread", request.ThreadCount.ToString(CultureInfo.InvariantCulture));
        AddSwitch(psi, "--download-pinned", request.DownloadPinned);
        AddSwitch(psi, "--no-group-by-mode", !request.GroupByMode);
        AddSwitch(psi, "--include-cover", request.IncludeCover);
        AddSwitch(psi, "--include-avatar", request.IncludeAvatar);
        AddSwitch(psi, "--include-music", request.IncludeMusic);
        AddSwitch(psi, "--include-comments", request.IncludeComments);
        AddSwitch(psi, "--comment-include-replies", request.CommentIncludeReplies);
        AddArgument(psi, "--max-comments", request.MaxComments.ToString(CultureInfo.InvariantCulture));
        AddArgument(psi, "--comment-page-size", request.CommentPageSize.ToString(CultureInfo.InvariantCulture));
        AddSwitch(psi, "--include-json", request.IncludeJson);
        AddSwitch(psi, "--enable-database", request.IncludeDatabase);
        AddSwitch(psi, "--incremental", request.IncrementalDownload);

        return psi;
    }

    private ProcessStartInfo CreateDiscoveryProcessStartInfo(DouyinSidecarDiscoveryRequest request)
    {
        var psi = CreateBaseProcessStartInfo();
        AddArgument(psi, "--output-dir", request.OutputDirectory);
        AddCookieEnvironmentArgument(psi, request.Cookie);
        AddArgument(psi, "--proxy", request.Proxy);

        if (request.Type == DouyinDiscoveryType.HotBoard)
        {
            AddArgument(psi, "--hot-board", Math.Max(0, request.Limit).ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            AddArgument(psi, "--search", request.Keyword);
            AddArgument(psi, "--search-max", Math.Max(1, request.SearchMax).ToString(CultureInfo.InvariantCulture));
        }

        return psi;
    }

    private ProcessStartInfo CreateBaseProcessStartInfo()
    {
        var runsAsExecutable = IsExecutableSidecar(_scriptPath);
        var psi = new ProcessStartInfo
        {
            FileName = runsAsExecutable ? _scriptPath : _pythonExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment.Remove(DouyinSpecialDownloadService.DouyinCookieEnvironmentVariableName);

        if (!runsAsExecutable)
            psi.ArgumentList.Add(_scriptPath);

        return psi;
    }

    internal static string ResolveDefaultSidecarPath(string baseDirectory)
    {
        var start = new DirectoryInfo(baseDirectory);
        foreach (var directory in EnumerateSelfAndParents(start))
        {
            var toolingScript = Path.Combine(directory.FullName, "tools", "douyin-sidecar", "sidecar.py");
            if (File.Exists(toolingScript))
                return toolingScript;
        }

        foreach (var directory in EnumerateSelfAndParents(start))
        {
            var publishedExecutable = Path.Combine(directory.FullName, "sidecars", "douyin", "EasyGet.DouyinSidecar.exe");
            if (File.Exists(publishedExecutable))
                return publishedExecutable;

            var publishedScript = Path.Combine(directory.FullName, "sidecars", "douyin_sidecar.py");
            if (File.Exists(publishedScript))
                return publishedScript;
        }

        return Path.Combine(baseDirectory, "sidecars", "douyin_sidecar.py");
    }

    private static IEnumerable<DirectoryInfo> EnumerateSelfAndParents(DirectoryInfo? start)
    {
        for (var directory = start; directory is not null; directory = directory.Parent)
            yield return directory;
    }

    private static bool IsExecutableSidecar(string sidecarPath)
        => string.Equals(Path.GetExtension(sidecarPath), ".exe", StringComparison.OrdinalIgnoreCase);

    private static void AddArgument(ProcessStartInfo psi, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        psi.ArgumentList.Add(name);
        psi.ArgumentList.Add(value);
    }

    private static void AddCookieEnvironmentArgument(ProcessStartInfo psi, string cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
            return;

        psi.Environment[DouyinSpecialDownloadService.DouyinCookieEnvironmentVariableName] = cookie;
        psi.ArgumentList.Add("--cookie-env");
        psi.ArgumentList.Add(DouyinSpecialDownloadService.DouyinCookieEnvironmentVariableName);
    }

    private static void AddSwitch(ProcessStartInfo psi, string name, bool enabled)
    {
        if (enabled)
            psi.ArgumentList.Add(name);
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        Action<string> lineReceived,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
                lineReceived(line);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Debug.WriteLine($"[DouyinSidecar] stderr read failed: {ex.Message}");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort process cleanup.
        }
    }
}
