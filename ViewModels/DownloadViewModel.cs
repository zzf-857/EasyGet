using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyGet.Models;
using EasyGet.Services;

namespace EasyGet.ViewModels;

public sealed record SourceFormatChoice(string DisplayName, string Selector, long ExpectedBytes = 0);

/// <summary>
/// 单视频下载页 ViewModel
/// </summary>
public partial class DownloadViewModel : ObservableObject
{
    private const int MaxLogLines = 200;

    private readonly DownloadManager _downloadManager;
    private readonly ConfigService _configService;
    private readonly IVideoInfoProvider _videoInfoProvider;
    private readonly DownloadPreflightService _preflightService;
    private readonly HistoryService? _historyService;
    private readonly DownloadDuplicateDetector? _duplicateDetector;
    private readonly Action<ProcessStartInfo> _startProcess;
    private readonly Func<string?> _readClipboardText;
    private CancellationTokenSource? _parseCts;
    private int _parseRequestId;

    // 输入
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _selectedFormat = "mp4";
    [ObservableProperty] private string _selectedQuality = "best";
    [ObservableProperty] private string _selectedSubtitle = "none";
    [ObservableProperty] private SourceFormatChoice? _selectedSourceFormat;
    [ObservableProperty] private string _downloadDirectory = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartDownloadButtonText))]
    private bool _isScheduledDownloadEnabled;
    [ObservableProperty] private string _scheduledStartText = CreateDefaultScheduledStartText();
    [ObservableProperty] private string _scheduleValidationMessage = "";
    [ObservableProperty] private string _proxyStatusText = "未启用";
    [ObservableProperty] private string _concurrentFragmentsText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsParsing))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsParseActionVisible))]
    [NotifyPropertyChangedFor(nameof(IsDownloadActive))]
    [NotifyPropertyChangedFor(nameof(IsScheduled))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsTaskFailed))]
    [NotifyPropertyChangedFor(nameof(IsProgressCardVisible))]
    private DownloadPageState _pageState = DownloadPageState.Idle;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewTitle))]
    [NotifyPropertyChangedFor(nameof(PreviewPlatform))]
    [NotifyPropertyChangedFor(nameof(PreviewThumbnailUrl))]
    [NotifyPropertyChangedFor(nameof(PreviewDurationText))]
    [NotifyPropertyChangedFor(nameof(PreviewFileSizeText))]
    private VideoInfo? _previewInfo;
    [ObservableProperty] private string _customFileName = "";
    [ObservableProperty] private string _parseErrorMessage = "";
    [ObservableProperty] private string? _urlError;
    [ObservableProperty] private bool _isLogExpanded; // Default is false (collapsed)

    [ObservableProperty] private string _clipboardPromptUrl = "";
    private string _lastClipboardPromptUrl = "";

    public event Action<string>? ClipboardLinkDetected;
    public Func<string, string, bool>? ConfirmFunc { get; set; } = ConfirmationDialogService.Show;

    // 当前任务状态
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressCardVisible))]
    [NotifyPropertyChangedFor(nameof(CurrentOutputLocationText))]
    [NotifyPropertyChangedFor(nameof(CurrentErrorMessage))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsTaskFailed))]
    private DownloadTask? _currentTask;
    [ObservableProperty] private bool _isDownloading;

    // 日志
    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<SourceFormatChoice> SourceFormatOptions { get; } = [];
    public string LogText => string.Join(Environment.NewLine, LogLines);

    // 选项列表
    public string[] FormatOptions { get; } = ["mp4", "mkv", "webm", "mp3 (仅音频)", "m4a (仅音频)"];
    public string[] QualityOptions { get; } = ["最高画质", "2160p (4K)", "1080p", "720p", "480p"];
    public string[] SubtitleOptions { get; } = ["不下载", "自动字幕", "全部字幕"];

    public bool IsIdle => PageState == DownloadPageState.Idle;
    public bool IsParsing => PageState == DownloadPageState.Parsing;
    public bool IsReady => PageState == DownloadPageState.Ready;
    public bool IsFailed => PageState == DownloadPageState.Failed;
    public bool IsParseActionVisible => PageState is DownloadPageState.Idle or DownloadPageState.Parsing or DownloadPageState.Failed;
    public bool IsDownloadActive => PageState == DownloadPageState.Downloading;
    public bool IsScheduled => PageState == DownloadPageState.Scheduled;
    public bool IsCompleted => CurrentTask is not null && PageState == DownloadPageState.Completed;
    public bool IsTaskFailed => CurrentTask is not null && PageState == DownloadPageState.Failed;
    public bool CanParse => !IsParsing && ExtractUrl(Url) is not null;
    public bool IsProgressCardVisible => CurrentTask is not null
        && PageState is DownloadPageState.Scheduled or DownloadPageState.Downloading
            or DownloadPageState.Completed or DownloadPageState.Failed;
    public string PreviewTitle => PreviewInfo?.Title ?? "";
    public string PreviewPlatform => PreviewInfo?.Platform ?? "";
    public string PreviewThumbnailUrl => PreviewInfo?.Thumbnail ?? "";
    public string PreviewDurationText => FormatDuration(PreviewInfo?.Duration ?? 0);
    public string PreviewFileSizeText => ByteSizeFormatter.FormatOrUnknown(PreviewInfo?.FileSize ?? 0);
    public string CurrentOutputLocationText => string.IsNullOrWhiteSpace(CurrentTask?.OutputFilePath)
        ? CurrentTask?.OutputDirectory ?? ""
        : CurrentTask.OutputFilePath;
    public string CurrentErrorMessage => CurrentTask?.ErrorMessage ?? "";
    public bool HasResolvedSourceFormats => SourceFormatOptions.Count > 1;
    public bool UsesAutomaticSourceFormat => string.IsNullOrWhiteSpace(SelectedSourceFormat?.Selector);
    public string StartDownloadButtonText => IsScheduledDownloadEnabled ? "加入计划" : "开始下载";

    public DownloadViewModel(
        DownloadManager downloadManager,
        ConfigService configService,
        IVideoInfoProvider videoInfoProvider,
        DownloadPreflightService? preflightService = null,
        HistoryService? historyService = null,
        DownloadDuplicateDetector? duplicateDetector = null)
        : this(
            downloadManager,
            configService,
            videoInfoProvider,
            StartProcess,
            null,
            preflightService,
            historyService,
            duplicateDetector)
    {
    }

    internal DownloadViewModel(
        DownloadManager downloadManager,
        ConfigService configService,
        IVideoInfoProvider videoInfoProvider,
        Action<ProcessStartInfo> startProcess,
        Func<string?>? readClipboardText = null,
        DownloadPreflightService? preflightService = null,
        HistoryService? historyService = null,
        DownloadDuplicateDetector? duplicateDetector = null)
    {
        _downloadManager = downloadManager;
        _configService = configService;
        _videoInfoProvider = videoInfoProvider;
        _preflightService = preflightService ?? new DownloadPreflightService();
        _historyService = historyService;
        _duplicateDetector = duplicateDetector;
        _startProcess = startProcess;
        _readClipboardText = readClipboardText ?? ReadClipboardText;
        LogLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LogText));
        RebuildSourceFormatOptions();

        // 转发下载管理器的日志
        _downloadManager.LogReceived += line =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                LogLines.Add(line);
                // 保持最新日志窗口，避免长时间下载时 UI 文本无限增长。
                while (LogLines.Count > MaxLogLines)
                    LogLines.RemoveAt(0);
            });
        };
    }

    partial void OnUrlChanged(string value)
    {
        CancelParse();
        PreviewInfo = null;
        CustomFileName = "";
        ParseErrorMessage = "";
        UrlError = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            if (!IsDownloading)
            {
                CurrentTask = null;
                PageState = DownloadPageState.Idle;
            }
        }
        else
        {
            if (!IsDownloading)
            {
                CurrentTask = null;
                PageState = DownloadPageState.Idle;
            }
        }
        ParseCommand.NotifyCanExecuteChanged();
    }

    partial void OnPageStateChanged(DownloadPageState value)
        => ParseCommand.NotifyCanExecuteChanged();

    partial void OnPreviewInfoChanged(VideoInfo? value)
        => RebuildSourceFormatOptions();

    partial void OnSelectedFormatChanged(string value)
        => RebuildSourceFormatOptions();

    partial void OnIsScheduledDownloadEnabledChanged(bool value)
        => ScheduleValidationMessage = "";

    partial void OnScheduledStartTextChanged(string value)
        => ScheduleValidationMessage = "";

    partial void OnSelectedSourceFormatChanged(SourceFormatChoice? value)
        => OnPropertyChanged(nameof(UsesAutomaticSourceFormat));

    /// <summary>
    /// 初始化默认值
    /// </summary>
    public void Initialize()
    {
        var config = _configService.Config;
        DownloadDirectory = config.DefaultDownloadPath;
        SelectedFormat = config.DefaultFormat;
        SelectedQuality = config.DefaultQuality switch
        {
            "best" => "最高画质",
            "2160" => "2160p (4K)",
            "1080" => "1080p",
            "720" => "720p",
            "480" => "480p",
            _ => "最高画质"
        };
        SelectedSubtitle = config.DefaultSubtitle switch
        {
            "none" => "不下载",
            "auto" => "自动字幕",
            "all" => "全部字幕",
            _ => "不下载"
        };
        RefreshRuntimeConfigDisplay();
    }

    public void RefreshRuntimeConfigDisplay()
    {
        var config = _configService.Config;
        DownloadDirectory = config.DefaultDownloadPath;
        ProxyStatusText = DescribeProxyStatus(config);
        ConcurrentFragmentsText = DescribeConcurrentFragments(config);
    }

    /// <summary>
    /// 从剪贴板粘贴 URL
    /// </summary>
    [RelayCommand]
    private void PasteUrl()
    {
        string? text;
        try
        {
            text = _readClipboardText();
        }
        catch (COMException)
        {
            // Another process can temporarily own the clipboard.
            return;
        }

        if (text is not null)
            Url = text.Trim();
    }

    private static string? ReadClipboardText()
        => Clipboard.ContainsText() ? Clipboard.GetText() : null;

    /// <summary>
    /// 浏览选择下载目录
    /// </summary>
    [RelayCommand]
    private void BrowseDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择下载目录",
            InitialDirectory = DownloadDirectory
        };

        if (dialog.ShowDialog() == true)
        {
            DownloadDirectory = dialog.FolderName;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanParse))]
    private async Task Parse()
    {
        if (IsParsing)
            return;

        var cleanUrl = ExtractUrl(Url);
        if (string.IsNullOrWhiteSpace(cleanUrl))
        {
            UrlError = "未能从输入中识别出有效链接";
            return;
        }

        CancelParse();
        var requestId = ++_parseRequestId;
        using var cts = new CancellationTokenSource();
        _parseCts = cts;
        PreviewInfo = null;
        CustomFileName = "";
        ParseErrorMessage = "";
        CurrentTask = null;
        PageState = DownloadPageState.Parsing;

        try
        {
            var info = await _videoInfoProvider.GetVideoInfoAsync(cleanUrl, cts.Token);
            if (cts.IsCancellationRequested || requestId != _parseRequestId)
                return;

            if (info is null)
            {
                ShowParseError("解析失败，请检查链接或稍后重试。");
                return;
            }

            if (string.IsNullOrWhiteSpace(info.Url))
                info.Url = cleanUrl;

            PreviewInfo = info;
            CustomFileName = PreviewInfo.Title;
            PageState = DownloadPageState.Ready;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (requestId == _parseRequestId)
                ShowParseError($"解析失败: {ex.Message}");
        }
        finally
        {
            if (_parseCts == cts)
                _parseCts = null;
        }
    }

    /// <summary>
    /// 开始下载
    /// </summary>
    [RelayCommand]
    private async Task StartDownload()
    {
        if (IsDownloading) 
        {
            UrlError = "当前任务正在进行中。如需同时建立多个下载任务，请使用左侧“批量下载”功能。";
            return;
        }

        if (string.IsNullOrWhiteSpace(Url))
        {
            UrlError = "请输入视频链接";
            return;
        }

        var cleanUrl = ExtractUrl(Url);
        if (string.IsNullOrWhiteSpace(cleanUrl))
        {
            UrlError = "未能从输入中识别出有效链接";
            return;
        }

        DateTimeOffset? scheduledStartUtc = null;
        if (IsScheduledDownloadEnabled)
        {
            if (!TryParseScheduledStart(
                    ScheduledStartText,
                    DateTimeOffset.Now,
                    out var scheduledStart,
                    out var scheduleError))
            {
                ScheduleValidationMessage = scheduleError;
                return;
            }

            scheduledStartUtc = scheduledStart.ToUniversalTime();
        }

        if (_historyService is not null && _duplicateDetector is not null)
        {
            var duplicate = await _duplicateDetector.DetectAsync(cleanUrl, _historyService);
            if (duplicate.IsDuplicate)
            {
                var locationHint = string.IsNullOrWhiteSpace(duplicate.ExistingPath)
                    ? "下载历史中已有相同链接。"
                    : $"本地已有文件：{duplicate.ExistingPath}";
                if (ConfirmFunc?.Invoke($"{locationHint}\n是否仍然重新下载？", "重复下载确认") != true)
                    return;
            }
        }

        var preflight = _preflightService.Check(
            DownloadDirectory,
            SelectedSourceFormat?.ExpectedBytes > 0
                ? SelectedSourceFormat.ExpectedBytes
                : PreviewInfo?.FileSize ?? 0);
        if (!preflight.CanProceed)
        {
            UrlError = preflight.BlockingMessage;
            return;
        }

        DownloadDirectory = preflight.OutputDirectory;
        foreach (var warning in preflight.Issues.Where(issue =>
                     issue.Severity == DownloadPreflightSeverity.Warning))
        {
            LogLines.Add($"[{DateTime.Now:HH:mm:ss}] 提示: {warning.Message}");
        }

        var task = new DownloadTask
        {
            Url = cleanUrl,
            Title = string.IsNullOrWhiteSpace(CustomFileName) ? PreviewInfo!.Title : CustomFileName,
            Format = ParseFormat(SelectedFormat),
            Quality = ParseQuality(SelectedQuality),
            SourceFormatSelector = SelectedSourceFormat?.Selector ?? "",
            Subtitle = ParseSubtitle(SelectedSubtitle),
            OutputDirectory = DownloadDirectory,
            ScheduledStartTimeUtc = scheduledStartUtc
        };

        IsDownloading = scheduledStartUtc is null;
        PageState = scheduledStartUtc is null
            ? DownloadPageState.Downloading
            : DownloadPageState.Scheduled;

        // 监听任务状态变化以准确更新 IsDownloading
        task.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(DownloadTask.OutputFilePath) or nameof(DownloadTask.ErrorMessage))
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (CurrentTask != task) return;
                    OnPropertyChanged(nameof(CurrentOutputLocationText));
                    OnPropertyChanged(nameof(CurrentErrorMessage));
                });
            }

            if (e.PropertyName == nameof(DownloadTask.Status))
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (CurrentTask != task) return;

                    var status = task.Status;
                    IsDownloading = status is DownloadStatus.Waiting
                        or DownloadStatus.Resolving
                        or DownloadStatus.Downloading
                        or DownloadStatus.Merging;
                    PageState = status switch
                    {
                        DownloadStatus.Scheduled => DownloadPageState.Scheduled,
                        DownloadStatus.Completed => DownloadPageState.Completed,
                        DownloadStatus.Failed => DownloadPageState.Failed,
                        DownloadStatus.Cancelled => DownloadPageState.Idle,
                        _ when IsDownloading => DownloadPageState.Downloading,
                        _ => PageState
                    };
                });
            }
        };

        CurrentTask = task;
        await _downloadManager.EnqueueAsync(task, PreviewInfo);
    }

    /// <summary>
    /// 取消当前下载
    /// </summary>
    [RelayCommand]
    private void CancelDownload()
    {
        if (CurrentTask != null)
        {
            _downloadManager.Cancel(CurrentTask.Id);
            IsDownloading = false;
            PageState = DownloadPageState.Idle;
        }
    }

    [RelayCommand]
    private async Task OpenCurrentFolder()
    {
        var task = CurrentTask;
        if (task is null)
            return;

        await Task.Run(() =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(task.OutputFilePath))
                {
                    if (File.Exists(task.OutputFilePath) || Directory.Exists(task.OutputFilePath))
                    {
                        _startProcess(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{task.OutputFilePath}\"",
                            UseShellExecute = true
                        });
                        return;
                    }
                }

                if (!string.IsNullOrWhiteSpace(task.OutputDirectory) && Directory.Exists(task.OutputDirectory))
                {
                    _startProcess(new ProcessStartInfo
                    {
                        FileName = task.OutputDirectory,
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
            }
        });
    }

    [RelayCommand]
    private async Task PlayCurrentFile()
    {
        var filePath = CurrentTask?.OutputFilePath;
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        await Task.Run(() =>
        {
            try
            {
                var targetPath = MediaPreviewFileResolver.Resolve(filePath);
                if (File.Exists(targetPath))
                {
                    _startProcess(new ProcessStartInfo
                    {
                        FileName = targetPath,
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(targetPath) ?? ""
                    });
                }
            }
            catch
            {
            }
        });
    }

    [RelayCommand]
    private async Task RetryCurrentDownload()
    {
        var task = CurrentTask;
        if (task is null)
            return;

        IsDownloading = true;
        PageState = DownloadPageState.Downloading;
        await _downloadManager.RetryAsync(task.Id);
    }

    /// <summary>
    /// 复制日志到剪贴板
    /// </summary>
    [RelayCommand]
    private void CopyLog()
    {
        if (LogLines.Count > 0)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(LogText, true);
            }
            catch
            {
                // 剪贴板被占用时静默忽略
            }
        }
    }

    /// <summary>
    /// 清空日志
    /// </summary>
    [RelayCommand]
    private void ClearLog()
    {
        LogLines.Clear();
    }

    private static void StartProcess(ProcessStartInfo startInfo)
    {
        Process.Start(startInfo);
    }

    [RelayCommand]
    private void CancelParse()
    {
        _parseRequestId++;
        _parseCts?.Cancel();
        _parseCts?.Dispose();
        _parseCts = null;
        if (PageState == DownloadPageState.Parsing)
        {
            PageState = DownloadPageState.Idle;
        }
        CustomFileName = "";
    }

    private void ShowParseError(string message)
    {
        PreviewInfo = null;
        ParseErrorMessage = message;
        PageState = DownloadPageState.Failed;
    }

    private static string ParseFormat(string display) => display switch
    {
        "mp3 (仅音频)" => "mp3",
        "m4a (仅音频)" => "m4a",
        _ => display
    };

    internal static bool TryParseScheduledStart(
        string text,
        DateTimeOffset now,
        out DateTimeOffset scheduledStart,
        out string error)
    {
        scheduledStart = default;
        error = "";
        var formats = new[]
        {
            "yyyy-MM-dd HH:mm",
            "yyyy/M/d H:mm",
            "yyyy-M-d H:mm"
        };
        if (!DateTime.TryParseExact(
                text?.Trim(),
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var localTime))
        {
            error = "请输入有效时间，例如 2026-07-29 18:30。";
            return false;
        }

        localTime = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(localTime))
        {
            error = "该本地时间不存在，请避开夏令时切换时段。";
            return false;
        }

        scheduledStart = new DateTimeOffset(localTime, TimeZoneInfo.Local.GetUtcOffset(localTime));
        if (scheduledStart <= now)
        {
            error = "计划时间必须晚于当前时间。";
            return false;
        }

        return true;
    }

    private static string CreateDefaultScheduledStartText()
        => DateTime.Now.AddMinutes(30).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    internal static IReadOnlyList<SourceFormatChoice> BuildSourceFormatChoices(
        VideoInfo? videoInfo,
        string outputFormat)
    {
        var audioOnly = outputFormat.StartsWith("mp3", StringComparison.OrdinalIgnoreCase)
                        || outputFormat.StartsWith("m4a", StringComparison.OrdinalIgnoreCase);
        var choices = new List<SourceFormatChoice>
        {
            new(
                audioOnly ? "自动选择最佳音频" : "自动选择（按画质上限）",
                "")
        };
        if (videoInfo?.AvailableFormats is not { Count: > 0 } formats)
            return choices;

        IEnumerable<VideoFormatInfo> candidates;
        if (audioOnly)
        {
            var audioOnlyFormats = formats.Where(format => format.HasAudio && !format.HasVideo).ToArray();
            candidates = audioOnlyFormats.Length > 0
                ? audioOnlyFormats
                : formats.Where(format => format.HasAudio);
        }
        else
        {
            candidates = formats.Where(format => format.HasVideo);
        }

        choices.AddRange(candidates
            .Select(format => new SourceFormatChoice(
                BuildSourceFormatDisplayName(format, audioOnly),
                audioOnly || format.IsCombined
                    ? format.FormatId
                    : $"{format.FormatId}+ba/b",
                format.FileSize))
            .DistinctBy(choice => choice.Selector, StringComparer.Ordinal));
        return choices;
    }

    private void RebuildSourceFormatOptions()
    {
        var previousSelector = SelectedSourceFormat?.Selector;
        var choices = BuildSourceFormatChoices(PreviewInfo, SelectedFormat);
        SourceFormatOptions.Clear();
        foreach (var choice in choices)
            SourceFormatOptions.Add(choice);

        SelectedSourceFormat = SourceFormatOptions.FirstOrDefault(choice =>
                                   string.Equals(choice.Selector, previousSelector, StringComparison.Ordinal))
                               ?? SourceFormatOptions.FirstOrDefault();
        OnPropertyChanged(nameof(HasResolvedSourceFormats));
    }

    private static string BuildSourceFormatDisplayName(VideoFormatInfo format, bool audioOnly)
    {
        var parts = new List<string>();
        if (audioOnly)
        {
            parts.Add(format.AudioBitrateKilobytesPerSecond > 0
                ? $"音频 {format.AudioBitrateKilobytesPerSecond:0.#} kbps"
                : "音频");
            AddIfPresent(parts, NormalizeContainer(format.Extension));
            AddIfPresent(parts, NormalizeCodec(format.AudioCodec));
        }
        else
        {
            var dimensions = format.Height > 0
                ? $"{format.Height}p"
                : format.Width > 0
                    ? $"{format.Width}px"
                    : "视频";
            if (format.FramesPerSecond > 0)
                dimensions += $" {format.FramesPerSecond:0.#}fps";
            parts.Add(dimensions);
            AddIfPresent(parts, NormalizeContainer(format.Extension));
            AddIfPresent(parts, NormalizeCodec(format.VideoCodec));
            parts.Add(format.HasAudio
                ? NormalizeCodec(format.AudioCodec)
                : "自动匹配音频");
        }

        if (!string.IsNullOrWhiteSpace(format.FormatNote)
            && !parts.Any(part => part.Contains(format.FormatNote, StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add(format.FormatNote);
        }
        if (format.FileSize > 0)
            parts.Add(ByteSizeFormatter.FormatClampZero(format.FileSize));
        parts.Add($"ID {format.FormatId}");
        return string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static void AddIfPresent(ICollection<string> parts, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            parts.Add(value);
    }

    private static string NormalizeContainer(string extension)
        => string.IsNullOrWhiteSpace(extension) ? "" : extension.ToUpperInvariant();

    private static string NormalizeCodec(string codec)
    {
        if (string.IsNullOrWhiteSpace(codec) || codec.Equals("none", StringComparison.OrdinalIgnoreCase))
            return "";

        var normalized = codec.ToLowerInvariant();
        if (normalized.StartsWith("avc1", StringComparison.Ordinal)
            || normalized.StartsWith("h264", StringComparison.Ordinal))
            return "H.264";
        if (normalized.StartsWith("hev1", StringComparison.Ordinal)
            || normalized.StartsWith("hvc1", StringComparison.Ordinal)
            || normalized.StartsWith("hevc", StringComparison.Ordinal))
            return "H.265";
        if (normalized.StartsWith("av01", StringComparison.Ordinal))
            return "AV1";
        if (normalized.StartsWith("vp9", StringComparison.Ordinal))
            return "VP9";
        if (normalized.StartsWith("vp8", StringComparison.Ordinal))
            return "VP8";
        if (normalized.StartsWith("mp4a", StringComparison.Ordinal)
            || normalized.StartsWith("aac", StringComparison.Ordinal))
            return "AAC";
        if (normalized.StartsWith("opus", StringComparison.Ordinal))
            return "Opus";
        if (normalized.StartsWith("vorbis", StringComparison.Ordinal))
            return "Vorbis";
        if (normalized.StartsWith("eac3", StringComparison.Ordinal))
            return "E-AC-3";
        if (normalized.StartsWith("ac3", StringComparison.Ordinal))
            return "AC-3";

        var separator = codec.IndexOf('.');
        var shortName = separator > 0 ? codec[..separator] : codec;
        return shortName.Length <= 16 ? shortName : shortName[..16];
    }

    internal static string DescribeProxyStatus(AppConfig config)
    {
        if (!config.UseProxy)
            return "未启用";

        return string.IsNullOrWhiteSpace(config.ProxyAddress)
            ? "已启用，地址未配置"
            : config.ProxyAddress.Trim();
    }

    internal static string DescribeConcurrentFragments(AppConfig config)
    {
        var effective = YtDlpService.ResolveConcurrentFragments(
            config.ConcurrentFragments,
            config.MaxConcurrentDownloads);
        return effective == config.ConcurrentFragments
            ? $"{effective} 分片"
            : $"{effective} 分片（智能限流）";
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0)
            return "时长未知";

        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? $"{ts:hh\\:mm\\:ss}" : $"{ts:mm\\:ss}";
    }

    /// <summary>
    /// 从粘贴文本中提取第一个 http/https URL（支持抖音分享文本等）
    /// </summary>
    internal static string? ExtractUrl(string input)
        => ShareUrlExtractor.Extract(input);

    private static string ParseQuality(string display) => display switch
    {
        "最高画质" => "best",
        "2160p (4K)" => "2160",
        "1080p" => "1080",
        "720p" => "720",
        "480p" => "480",
        _ => "best"
    };

    private static string ParseSubtitle(string display) => display switch
    {
        "不下载" => "none",
        "自动字幕" => "auto",
        "全部字幕" => "all",
        _ => "none"
    };

    [RelayCommand]
    private async Task RunPrimaryAction()
    {
        if (IsReady)
        {
            await StartDownloadCommand.ExecuteAsync(null);
            return;
        }

        if (ParseCommand.CanExecute(null))
            await ParseCommand.ExecuteAsync(null);
    }

    public static bool IsValidClipboardUrl(string text, string currentUrl, string lastPromptedUrl)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var extracted = ExtractUrl(text);
        if (extracted == null)
            return false;

        if (!Uri.TryCreate(extracted, UriKind.Absolute, out var uri) || 
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        if (extracted.Trim().Equals(currentUrl?.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (extracted.Trim().Equals(lastPromptedUrl?.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public void CheckClipboardAndPrompt(string clipboardText)
    {
        if (IsValidClipboardUrl(clipboardText, Url, _lastClipboardPromptUrl))
        {
            var extracted = ExtractUrl(clipboardText)!;
            ClipboardPromptUrl = extracted;
            _lastClipboardPromptUrl = extracted;
            ClipboardLinkDetected?.Invoke(extracted);
        }
    }
}
