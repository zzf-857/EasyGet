using System.Diagnostics;
using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
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

public interface IDouyinSidecarHealthService
{
    Task<DouyinSidecarHealthResult> CheckHealthAsync(CancellationToken ct = default);
}

public sealed record DouyinSidecarHealthResult(
    bool IsAvailable,
    string StatusText,
    IReadOnlyList<string> CheckedModules,
    IReadOnlyList<string> FailedModules,
    string ErrorMessage = "");

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
    private bool _canAddToQueue;
    private string _queueStateText = "未入队";

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

    public string QueueStateText
    {
        get => _queueStateText;
        set
        {
            if (string.Equals(_queueStateText, value, StringComparison.Ordinal))
                return;

            _queueStateText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueStateText)));
        }
    }

    public bool CanAddToQueue
    {
        get => _canAddToQueue;
        set
        {
            if (_canAddToQueue == value)
                return;

            _canAddToQueue = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAddToQueue)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class DouyinSpecialDownloadService : IDouyinSpecialDownloadService, IDouyinSidecarHealthService
{
    internal const string DouyinCookieEnvironmentVariableName = "EASYGET_DOUYIN_COOKIE";
    private const int MaxTaskEventLogLines = 6;
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

    public async Task<DouyinSidecarHealthResult> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            await foreach (var line in _runner.RunSelfTestAsync(ct))
            {
                if (!DouyinSidecarMessageParser.TryParse(line, out var message))
                    continue;

                if (message.Kind is DouyinSidecarEventKind.Success or DouyinSidecarEventKind.Failed)
                    return BuildHealthResult(message);
            }

            return new DouyinSidecarHealthResult(
                false,
                "抖音 sidecar 异常 · 未返回自检结果",
                [],
                [],
                "Douyin sidecar did not return a health summary.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = FormatUserFacingError(RedactSensitiveText(ex.Message, ""));
            return new DouyinSidecarHealthResult(
                false,
                $"抖音 sidecar 异常 · {message}",
                [],
                [],
                message);
        }
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
                if (!DouyinSidecarMessageParser.TryParse(line, out var message))
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
                if (!DouyinSidecarMessageParser.TryParse(line, out var message))
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
                            AppendTaskEvent(
                                task,
                                DouyinOutputHintFormatter.FormatLiveHlsPlaylistWarning(
                                    DouyinOutputHintFormatter.CountLiveHlsPlaylistFiles(
                                        task.Url,
                                        task.OutputFilePaths)));
                            AppendTaskEvent(
                                task,
                                RedactSensitiveText(
                                    FormatLiveRoomSummary(message),
                                    request.Cookie));
                            AppendTaskEvent(task, FormatTranscriptSummary(message));
                            AppendTaskEvent(task, FormatDatabaseSummary(message));
                            AppendTaskEvent(task, FormatAuthorSummary(message));
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

    private static DouyinSidecarHealthResult BuildHealthResult(DouyinSidecarMessage message)
    {
        var checkedModules = message.SelfTestCheckedModules;
        var failedModules = message.SelfTestFailedModules;
        var isAvailable = message.Kind == DouyinSidecarEventKind.Success
                          && message.SelfTestImportsOk != false
                          && failedModules.Count == 0;
        if (isAvailable)
        {
            var status = checkedModules.Count > 0
                ? $"抖音 sidecar 可用 · 已检查 {checkedModules.Count} 个模块"
                : "抖音 sidecar 可用";
            return new DouyinSidecarHealthResult(true, status, checkedModules, failedModules);
        }

        var error = SelectFirstNonEmpty(message.Error, message.Message, "Douyin sidecar self-test failed.");
        var statusText = failedModules.Count > 0
            ? $"抖音 sidecar 异常 · 失败模块 {string.Join("、", failedModules)}"
            : $"抖音 sidecar 异常 · {error}";
        return new DouyinSidecarHealthResult(false, statusText, checkedModules, failedModules, error);
    }

    internal static void ApplySuccessSummary(DownloadTask task, DouyinSidecarMessage message)
    {
        ApplySummaryMetadata(task, message, useDefaultDouyinPlatform: true);
        ApplyOutcomeCounts(task, message);
        var outputResolver = new DouyinOutputFileResolver(task.OutputDirectory);

        if (!string.IsNullOrWhiteSpace(message.OutputFilePath))
        {
            var outputFilePath = message.OutputFilePath.Trim();
            if (!outputResolver.IsSafeFilePath(outputFilePath))
            {
                task.Status = DownloadStatus.Failed;
                task.ErrorMessage = "Douyin sidecar returned output file outside the task output directory.";
                return;
            }

            task.OutputFilePath = outputFilePath;
        }

        task.OutputFilePaths = outputResolver.Collect(
            task.OutputFilePath, message.OutputFilePaths, message.ManifestPath);

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

    private static string FormatLiveRoomSummary(DouyinSidecarMessage message)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(message.LiveAuthorName))
            parts.Add(message.LiveAuthorName.Trim());
        if (!string.IsNullOrWhiteSpace(message.LiveRoomStatusText))
            parts.Add(message.LiveRoomStatusText.Trim());
        else if (message.LiveRoomStatus.HasValue)
            parts.Add($"状态 {message.LiveRoomStatus.Value}");
        if (!string.IsNullOrWhiteSpace(message.LiveRoomTitle))
            parts.Add(message.LiveRoomTitle.Trim());

        return parts.Count == 0 ? "" : $"直播间: {string.Join(" · ", parts)}";
    }

    private static string FormatTranscriptSummary(DouyinSidecarMessage message)
    {
        if (message.TranscriptFileCount is not { } count || count <= 0)
            return "";

        return $"转写文件: {count} 个";
    }

    private static string FormatDatabaseSummary(DouyinSidecarMessage message)
    {
        if (message.DatabaseEnabled is not true)
            return "";

        return message.DatabaseExists is false
            ? "数据库: 已启用（文件未生成）"
            : "数据库: 已启用";
    }

    private static string FormatAuthorSummary(DouyinSidecarMessage message)
    {
        if (message.AuthorSummaries.Count == 0)
            return "";

        var parts = message.AuthorSummaries
            .Take(3)
            .Select(author => $"{author.AuthorName} {FormatWorkCount(author.WorkCount)}")
            .ToList();
        if (message.AuthorSummaries.Count > parts.Count)
            parts.Add($"等 {message.AuthorSummaries.Count} 位作者");

        return $"作者: {string.Join("、", parts)}";
    }

    private static string FormatWorkCount(int count)
        => count == 1 ? "1 个作品" : $"{count} 个作品";

    private static double NormalizeFiniteValue(double? value)
    {
        var number = value ?? 0;
        return double.IsFinite(number) ? number : 0;
    }

    private static long NormalizeNonNegativeInt64(long? value)
        => Math.Max(0, value ?? 0);

    internal static bool IsDouyinManifestPath(string path)
        => DouyinOutputFileResolver.IsDouyinManifestPath(path);

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
        if (IsCommandLineArgumentError(normalized))
        {
            var detail = message
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(line => line.Contains(": error:", StringComparison.OrdinalIgnoreCase))
                ?? message;
            return $"抖音专项组件启动参数异常，请更新或重新安装 EasyGet 后重试。原始信息：{detail}";
        }

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
           || message.StartsWith("抖音专项引擎", StringComparison.Ordinal)
           || message.StartsWith("抖音专项组件启动参数", StringComparison.Ordinal);

    private static bool IsCommandLineArgumentError(string normalizedMessage)
        => ContainsAny(
            normalizedMessage,
            "the following arguments are required:",
            "unrecognized arguments:",
            "invalid choice:",
            "expected one argument");

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
    public int? TranscriptFileCount { get; set; }
    public IReadOnlyList<DouyinManifestAuthorSummary> AuthorSummaries { get; set; } = [];
    public bool? DatabaseEnabled { get; set; }
    public string DatabasePath { get; set; } = "";
    public bool? DatabaseExists { get; set; }
    public string LiveRoomTitle { get; set; } = "";
    public string LiveAuthorName { get; set; } = "";
    public int? LiveRoomStatus { get; set; }
    public string LiveRoomStatusText { get; set; } = "";
    public bool IsDiscovery { get; set; }
    public string DiscoveryType { get; set; } = "";
    public string DiscoveryKeyword { get; set; } = "";
    public int? DiscoveryLimit { get; set; }
    public int? DiscoverySearchMax { get; set; }
    public int? DiscoveryItemCount { get; set; }
    public IReadOnlyList<DouyinDiscoveryItem> DiscoveryItems { get; set; } = [];
    public bool? SelfTestImportsOk { get; set; }
    public IReadOnlyList<string> SelfTestCheckedModules { get; set; } = [];
    public IReadOnlyList<string> SelfTestFailedModules { get; set; } = [];
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
    bool EnableBrowserFallback = false,
    int LiveMaxDurationSeconds = 0,
    int LiveChunkSize = AppConfig.DefaultDouyinLiveChunkSize,
    int LiveIdleTimeoutSeconds = AppConfig.DefaultDouyinLiveIdleTimeoutSeconds,
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
        var threadCount = DownloadConcurrencyPolicy.ResolvePerTaskConnections(
            config?.ConcurrentFragments ?? 3,
            config?.MaxConcurrentDownloads ?? 3);
        var commentPageSize = Math.Clamp(
            config?.DouyinCommentPageSize ?? AppConfig.MaxDouyinCommentPageSize,
            1,
            AppConfig.MaxDouyinCommentPageSize);
        var liveChunkSize = config?.DouyinLiveChunkSize ?? AppConfig.DefaultDouyinLiveChunkSize;
        if (liveChunkSize <= 0)
            liveChunkSize = AppConfig.DefaultDouyinLiveChunkSize;
        var liveIdleTimeoutSeconds = config?.DouyinLiveIdleTimeoutSeconds ?? AppConfig.DefaultDouyinLiveIdleTimeoutSeconds;
        if (liveIdleTimeoutSeconds <= 0)
            liveIdleTimeoutSeconds = AppConfig.DefaultDouyinLiveIdleTimeoutSeconds;

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
            EnableBrowserFallback: config?.DouyinEnableBrowserFallback ?? false,
            LiveMaxDurationSeconds: Math.Max(0, config?.DouyinLiveMaxDurationSeconds ?? 0),
            LiveChunkSize: liveChunkSize,
            LiveIdleTimeoutSeconds: liveIdleTimeoutSeconds,
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

    IAsyncEnumerable<string> RunSelfTestAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("Douyin sidecar runner does not support self-test requests.");
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

    public async IAsyncEnumerable<string> RunSelfTestAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in RunProcessAsync(CreateSelfTestProcessStartInfo(), cookie: "", cancellationToken))
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
        AddSwitch(psi, "--browser-fallback", request.EnableBrowserFallback);
        AddArgument(psi, "--live-max-duration-seconds", request.LiveMaxDurationSeconds.ToString(CultureInfo.InvariantCulture));
        AddArgument(psi, "--live-chunk-size", request.LiveChunkSize.ToString(CultureInfo.InvariantCulture));
        AddArgument(psi, "--live-idle-timeout-seconds", request.LiveIdleTimeoutSeconds.ToString(CultureInfo.InvariantCulture));

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

    private ProcessStartInfo CreateSelfTestProcessStartInfo()
    {
        var psi = CreateBaseProcessStartInfo();
        AddArgument(psi, "--output-dir", Path.GetTempPath());
        psi.ArgumentList.Add("--self-test-imports");
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
