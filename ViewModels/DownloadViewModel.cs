using System.Collections.ObjectModel;
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

    // 输入
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _selectedFormat = "mp4";
    [ObservableProperty] private string _selectedQuality = "best";
    [ObservableProperty] private string _selectedSubtitle = "none";
    [ObservableProperty] private string _downloadDirectory = "";

    // 当前任务状态
    [ObservableProperty] private DownloadTask? _currentTask;
    [ObservableProperty] private bool _isDownloading;

    // 日志
    public ObservableCollection<string> LogLines { get; } = [];
    public string LogText => string.Join(Environment.NewLine, LogLines);

    // 选项列表
    public string[] FormatOptions { get; } = ["mp4", "mkv", "webm", "mp3 (仅音频)", "m4a (仅音频)"];
    public string[] QualityOptions { get; } = ["最高画质", "2160p (4K)", "1080p", "720p", "480p"];
    public string[] SubtitleOptions { get; } = ["不下载", "自动字幕", "全部字幕"];

    public DownloadViewModel(DownloadManager downloadManager, ConfigService configService)
    {
        _downloadManager = downloadManager;
        _configService = configService;
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

    /// <summary>
    /// 开始下载
    /// </summary>
    [RelayCommand]
    private async Task StartDownload()
    {
        if (IsDownloading) 
        {
            LogLines.Add("[提示] 当前任务正在进行中。如需同时建立多个下载任务，请使用左侧“批量下载”功能。");
            return;
        }

        if (string.IsNullOrWhiteSpace(Url))
        {
            LogLines.Add("[错误] 请输入视频链接");
            return;
        }

        IsDownloading = true;

        var cleanUrl = ExtractUrl(Url);
        if (string.IsNullOrWhiteSpace(cleanUrl))
        {
            LogLines.Add("[错误] 未能从输入中识别出有效链接");
            IsDownloading = false;
            return;
        }

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
        }
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

    private static string ParseFormat(string display) => display switch
    {
        "mp3 (仅音频)" => "mp3",
        "m4a (仅音频)" => "m4a",
        _ => display
    };

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
}
