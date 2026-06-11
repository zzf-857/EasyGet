using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyGet.Models;
using EasyGet.Services;

namespace EasyGet.ViewModels;

/// <summary>
/// 单视频下载页 ViewModel
/// </summary>
public partial class DownloadViewModel : ObservableObject
{
    private readonly DownloadManager _downloadManager;
    private readonly ConfigService _configService;
    private readonly IVideoInfoProvider _videoInfoProvider;
    private CancellationTokenSource? _parseCts;
    private int _parseRequestId;

    // 输入
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _selectedFormat = "mp4";
    [ObservableProperty] private string _selectedQuality = "best";
    [ObservableProperty] private string _selectedSubtitle = "none";
    [ObservableProperty] private string _downloadDirectory = "";
    [ObservableProperty] private string _proxyStatusText = "未启用";
    [ObservableProperty] private string _concurrentFragmentsText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsParsing))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsParseActionVisible))]
    [NotifyPropertyChangedFor(nameof(IsDownloadActive))]
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
    [ObservableProperty] private string _parseErrorMessage = "";
    [ObservableProperty] private string? _urlError;
    [ObservableProperty] private bool _isLogExpanded; // Default is false (collapsed)

    [ObservableProperty] private bool _showClipboardPrompt;
    [ObservableProperty] private string _clipboardPromptUrl = "";
    private string _lastClipboardPromptUrl = "";
    private System.Timers.Timer? _clipboardPromptTimer;

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
    public bool IsCompleted => CurrentTask is not null && PageState == DownloadPageState.Completed;
    public bool IsTaskFailed => CurrentTask is not null && PageState == DownloadPageState.Failed;
    public bool IsProgressCardVisible => CurrentTask is not null
        && PageState is DownloadPageState.Downloading or DownloadPageState.Completed or DownloadPageState.Failed;
    public string PreviewTitle => PreviewInfo?.Title ?? "";
    public string PreviewPlatform => PreviewInfo?.Platform ?? "";
    public string PreviewThumbnailUrl => PreviewInfo?.Thumbnail ?? "";
    public string PreviewDurationText => FormatDuration(PreviewInfo?.Duration ?? 0);
    public string PreviewFileSizeText => FormatBytes(PreviewInfo?.FileSize ?? 0);
    public string CurrentOutputLocationText => string.IsNullOrWhiteSpace(CurrentTask?.OutputFilePath)
        ? CurrentTask?.OutputDirectory ?? ""
        : CurrentTask.OutputFilePath;
    public string CurrentErrorMessage => CurrentTask?.ErrorMessage ?? "";

    public DownloadViewModel(DownloadManager downloadManager, ConfigService configService, IVideoInfoProvider videoInfoProvider)
    {
        _downloadManager = downloadManager;
        _configService = configService;
        _videoInfoProvider = videoInfoProvider;
        LogLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LogText));

        // 转发下载管理器的日志
        _downloadManager.LogReceived += line =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                LogLines.Add(line);
                // 保持最新 200 行
                while (LogLines.Count > 200)
                    LogLines.RemoveAt(0);
            });
        };
    }

    partial void OnUrlChanged(string value)
    {
        CancelParse();
        PreviewInfo = null;
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
    }

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
        if (System.Windows.Clipboard.ContainsText())
        {
            Url = System.Windows.Clipboard.GetText().Trim();
        }
    }

    /// <summary>
    /// 浏览选择下载目录
    /// </summary>
    [RelayCommand]
    private void BrowseDirectory()
    {
        // 使用 FolderBrowserDialog (WinForms interop)
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择下载目录",
            InitialDirectory = DownloadDirectory,
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            DownloadDirectory = dialog.SelectedPath;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
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

        IsDownloading = true;
        PageState = DownloadPageState.Downloading;

        var task = new DownloadTask
        {
            Url = cleanUrl,
            Format = ParseFormat(SelectedFormat),
            Quality = ParseQuality(SelectedQuality),
            Subtitle = ParseSubtitle(SelectedSubtitle),
            OutputDirectory = DownloadDirectory
        };

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
        await _downloadManager.EnqueueAsync(task);
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
                if (!string.IsNullOrWhiteSpace(task.OutputFilePath) && File.Exists(task.OutputFilePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{task.OutputFilePath}\"",
                        UseShellExecute = true
                    });
                    return;
                }

                if (!string.IsNullOrWhiteSpace(task.OutputDirectory) && Directory.Exists(task.OutputDirectory))
                {
                    Process.Start(new ProcessStartInfo
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
                if (File.Exists(filePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(filePath) ?? ""
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

    internal static string DescribeProxyStatus(AppConfig config)
    {
        if (!config.UseProxy)
            return "未启用";

        return string.IsNullOrWhiteSpace(config.ProxyAddress)
            ? "已启用，地址未配置"
            : config.ProxyAddress.Trim();
    }

    internal static string DescribeConcurrentFragments(AppConfig config)
        => $"{config.ConcurrentFragments} 分片";

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0)
            return "时长未知";

        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? $"{ts:hh\\:mm\\:ss}" : $"{ts:mm\\:ss}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "大小未知";

        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.#} {sizes[order]}";
    }

    /// <summary>
    /// 从粘贴文本中提取第一个 http/https URL（支持抖音分享文本等）
    /// </summary>
    internal static string? ExtractUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var trimmed = input.Trim();
        // 如果本身就是一个干净的 URL，直接返回
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // 取到第一个空白或中文字符为止
            var match = Regex.Match(trimmed, @"^https?://[^\s\u4e00-\u9fff]+");
            if (match.Success) return TrimTrailingSharePunctuation(match.Value);
        }
        // 从混合文本中提取第一个 URL
        var urlMatch = Regex.Match(input, @"https?://[^\s\u4e00-\u9fff]+");
        return urlMatch.Success ? TrimTrailingSharePunctuation(urlMatch.Value) : null;
    }

    private static string TrimTrailingSharePunctuation(string url)
    {
        return url.TrimEnd(
            ',',
            '.',
            ';',
            ':',
            ')',
            ']',
            '}',
            '>',
            '!',
            '?',
            '"',
            '\'',
            '，',
            '。',
            '、',
            '；',
            '：',
            '）',
            '】',
            '》',
            '！',
            '？',
            '”',
            '’');
    }

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
    private async Task UseClipboardPrompt()
    {
        ShowClipboardPrompt = false;
        Url = ClipboardPromptUrl;
        await ParseCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void DismissClipboardPrompt()
    {
        ShowClipboardPrompt = false;
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
            ShowClipboardPrompt = true;

            _clipboardPromptTimer?.Stop();
            _clipboardPromptTimer?.Dispose();

            _clipboardPromptTimer = new System.Timers.Timer(8000) { AutoReset = false };
            _clipboardPromptTimer.Elapsed += (s, e) =>
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                var action = new Action(() => ShowClipboardPrompt = false);
                if (dispatcher is null || dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    dispatcher.Invoke(action);
                }
            };
            _clipboardPromptTimer.Start();
        }
    }
}
