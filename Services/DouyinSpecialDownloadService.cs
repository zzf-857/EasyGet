using System.Diagnostics;
using System.Globalization;
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
}

public sealed class DouyinSpecialDownloadService : IDouyinSpecialDownloadService
{
    internal const string DouyinCookieEnvironmentVariableName = "EASYGET_DOUYIN_COOKIE";
    private const string DouyinManifestFileName = "download_manifest.jsonl";
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
                        if (!sawTerminalSummary && TryMapProgress(message, out var mappedProgress))
                            progress?.Report(mappedProgress);
                        break;
                    case DouyinSidecarEventKind.Success:
                        if (!sawTerminalSummary)
                        {
                            ApplySuccessSummary(task, message);
                            sawTerminalSummary = true;
                        }
                        break;
                    case DouyinSidecarEventKind.Failed:
                        if (!sawTerminalSummary)
                        {
                            ApplyFailureSummary(task, message);
                            task.ErrorMessage = RedactSensitiveText(task.ErrorMessage, request.Cookie);
                            sawTerminalSummary = true;
                        }
                        break;
                    case DouyinSidecarEventKind.Cancelled:
                        if (!sawTerminalSummary)
                        {
                            ApplyCancelledSummary(task, message);
                            sawTerminalSummary = true;
                        }
                        break;
                    case DouyinSidecarEventKind.Log:
                        logCallback?.Invoke(RedactSensitiveText(
                            SelectFirstNonEmpty(message.Message, message.RawLine),
                            request.Cookie));
                        break;
                }
            }

            if (task.Status == DownloadStatus.Downloading)
            {
                task.Status = DownloadStatus.Failed;
                task.ErrorMessage = "Douyin sidecar did not return a terminal summary.";
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

            var message = RedactSensitiveText(ex.Message, request.Cookie);
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = message;
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

    internal static void ApplySuccessSummary(DownloadTask task, DouyinSidecarMessage message)
    {
        ApplySummaryMetadata(task, message, useDefaultDouyinPlatform: true);

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

        task.Status = DownloadStatus.Failed;
        task.ErrorMessage = SelectFirstNonEmpty(
            message.Error,
            message.Message,
            "Douyin sidecar failed.");
    }

    internal static void ApplyCancelledSummary(DownloadTask task, DouyinSidecarMessage message)
    {
        ApplySummaryMetadata(task, message, useDefaultDouyinPlatform: false);

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
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(Path.GetFileName(fullManifestPath), DouyinManifestFileName, comparison)
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
    bool IncludeJson = false,
    bool IncludeDatabase = false,
    bool IncrementalDownload = false,
    string FilenameTemplate = AppConfig.DefaultDouyinTemplate,
    string FolderTemplate = AppConfig.DefaultDouyinTemplate)
{
    public static DouyinSidecarRequest FromTask(DownloadTask task, AppConfig? config)
    {
        var proxy = config is { UseProxy: true }
            ? NormalizeText(config.ProxyAddress)
            : "";

        return new DouyinSidecarRequest(
            Url: NormalizeText(task.Url),
            OutputDirectory: NormalizeText(task.OutputDirectory),
            Format: NormalizeText(task.Format),
            Quality: NormalizeText(task.Quality),
            Title: NormalizeText(task.Title),
            Cookie: NormalizeText(config?.CookieContent),
            Proxy: proxy,
            Mode: NormalizeText(config?.DouyinMode, "post"),
            Limit: Math.Max(0, config?.DouyinLimit ?? 1),
            StartTime: NormalizeText(config?.DouyinStartTime),
            EndTime: NormalizeText(config?.DouyinEndTime),
            DownloadPinned: config?.DouyinDownloadPinned ?? false,
            IncludeCover: config?.DouyinDownloadCover ?? false,
            IncludeAvatar: config?.DouyinDownloadAvatar ?? false,
            IncludeMusic: config?.DouyinDownloadMusic ?? false,
            IncludeComments: config?.DouyinDownloadComments ?? false,
            IncludeJson: config?.DouyinDownloadJson ?? false,
            IncludeDatabase: config?.DouyinEnableDatabase ?? false,
            IncrementalDownload: config?.DouyinIncrementalDownload ?? false,
            FilenameTemplate: ConfigService.NormalizeDouyinTemplate(config?.DouyinFilenameTemplate),
            FolderTemplate: ConfigService.NormalizeDouyinTemplate(config?.DouyinFolderTemplate));
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
        if (!File.Exists(_scriptPath))
            throw new InvalidOperationException($"Douyin sidecar was not found: {_scriptPath}");

        using var process = Process.Start(CreateProcessStartInfo(request))
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
                request.Cookie);
            throw new InvalidOperationException(
                !string.IsNullOrWhiteSpace(stderrText)
                    ? stderrText
                    : $"Douyin sidecar exited with code {process.ExitCode}.");
        }
    }

    private ProcessStartInfo CreateProcessStartInfo(DouyinSidecarRequest request)
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
        AddSwitch(psi, "--download-pinned", request.DownloadPinned);
        AddSwitch(psi, "--include-cover", request.IncludeCover);
        AddSwitch(psi, "--include-avatar", request.IncludeAvatar);
        AddSwitch(psi, "--include-music", request.IncludeMusic);
        AddSwitch(psi, "--include-comments", request.IncludeComments);
        AddSwitch(psi, "--include-json", request.IncludeJson);
        AddSwitch(psi, "--enable-database", request.IncludeDatabase);
        AddSwitch(psi, "--incremental", request.IncrementalDownload);

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
