using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyGet.Models;
using EasyGet.Services;

namespace EasyGet.ViewModels;

/// <summary>
/// 批量下载页 ViewModel
/// </summary>
public partial class BatchDownloadViewModel : ObservableObject
{
    private readonly DownloadManager _downloadManager;
    private readonly ConfigService _configService;
    private readonly YtDlpService _ytDlpService;

    [ObservableProperty] private string _urlsText = "";
    [ObservableProperty] private string _selectedFormat = "mp4";
    [ObservableProperty] private string _selectedQuality = "最高画质";
    [ObservableProperty] private int _linkCount;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isImportingPlaylist;
    [ObservableProperty] private string _playlistUrl = "";

    public ObservableCollection<DownloadTask> QueueTasks => _downloadManager.Tasks;
    public int ActiveDownloadCount => QueueTasks.Count(task => task.Status == DownloadStatus.Downloading);

    public string[] FormatOptions { get; } = ["mp4", "mkv", "webm", "mp3 (仅音频)"];
    public string[] QualityOptions { get; } = ["最高画质", "1080p", "720p", "480p"];

    public event Action<string, bool>? RequestShowNotification;

    public void ImportText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var validUrls = new List<string>();
        int ignoredCount = 0;

        foreach (var line in lines)
        {
            var url = DownloadViewModel.ExtractUrl(line);
            if (url != null)
            {
                validUrls.Add(url);
            }
            else
            {
                ignoredCount++;
            }
        }

        if (validUrls.Count > 0)
        {
            var newText = string.Join("\n", validUrls);
            UrlsText = string.IsNullOrEmpty(UrlsText) ? newText : UrlsText + "\n" + newText;
        }

        if (ignoredCount > 0)
        {
            bool isSuccess = validUrls.Count > 0;
            RequestShowNotification?.Invoke($"已导入 {validUrls.Count} 个链接，忽略了 {ignoredCount} 行无效文本", isSuccess);
        }
    }

    public BatchDownloadViewModel(DownloadManager downloadManager, ConfigService configService, YtDlpService ytDlpService)
    {
        _downloadManager = downloadManager;
        _configService = configService;
        _ytDlpService = ytDlpService;
        QueueTasks.CollectionChanged += OnQueueTasksChanged;
    }

    private void OnQueueTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (DownloadTask task in e.OldItems)
                task.PropertyChanged -= OnQueueTaskPropertyChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (DownloadTask task in e.NewItems)
                task.PropertyChanged += OnQueueTaskPropertyChanged;
        }

        OnPropertyChanged(nameof(ActiveDownloadCount));
    }

    private void OnQueueTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadTask.Status))
            OnPropertyChanged(nameof(ActiveDownloadCount));
    }

    partial void OnUrlsTextChanged(string value)
    {
        LinkCount = string.IsNullOrWhiteSpace(value) 
            ? 0 
            : value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Count(line => DownloadViewModel.ExtractUrl(line) != null);
    }

    [RelayCommand]
    private void PasteUrls()
    {
        if (System.Windows.Clipboard.ContainsText())
        {
            var clipText = System.Windows.Clipboard.GetText().Trim();
            UrlsText = string.IsNullOrEmpty(UrlsText) ? clipText : UrlsText + "\n" + clipText;
        }
    }

    [RelayCommand]
    private async Task StartBatchDownload()
    {
        if (LinkCount == 0)
            return;

        IsDownloading = true;

        var urls = UrlsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                           .Select(line => DownloadViewModel.ExtractUrl(line))
                           .Where(u => u != null)
                           .Cast<string>()
                           .ToList();

        var format = SelectedFormat switch
        {
            "mp3 (仅音频)" => "mp3",
            _ => SelectedFormat
        };

        var quality = SelectedQuality switch
        {
            "最高画质" => "best",
            "1080p" => "1080",
            "720p" => "720",
            "480p" => "480",
            _ => "best"
        };

        foreach (var url in urls)
        {
            var task = new DownloadTask
            {
                Url = url,
                Format = format,
                Quality = quality,
                OutputDirectory = _configService.Config.DefaultDownloadPath
            };
            await _downloadManager.EnqueueAsync(task);
        }

        IsDownloading = false;
    }

    [RelayCommand]
    private async Task ImportPlaylist()
    {
        if (string.IsNullOrWhiteSpace(PlaylistUrl))
            return;

        IsImportingPlaylist = true;
        try
        {
            var urls = await _ytDlpService.GetPlaylistUrlsAsync(PlaylistUrl);
            if (urls.Count > 0)
            {
                var newText = string.Join("\n", urls);
                UrlsText = string.IsNullOrEmpty(UrlsText) ? newText : UrlsText + "\n" + newText;
                PlaylistUrl = "";
            }
        }
        finally
        {
            IsImportingPlaylist = false;
        }
    }

    [RelayCommand]
    private void PauseTask(string taskId)
    {
        _downloadManager.Pause(taskId);
    }

    [RelayCommand]
    private async Task ResumeTask(string taskId)
    {
        await _downloadManager.ResumeAsync(taskId);
    }

    [RelayCommand]
    private void CancelTask(string taskId)
    {
        var task = _downloadManager.Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task != null)
        {
            if (task.Status is DownloadStatus.Downloading or DownloadStatus.Waiting or DownloadStatus.Resolving or DownloadStatus.Paused)
            {
                _downloadManager.Cancel(taskId);
            }
            else
            {
                // 如果任务已经结束（完成、失败或取消），点击 X 时将其从列表中移除
                _downloadManager.Tasks.Remove(task);
            }
        }
    }

    [RelayCommand]
    private async Task RetryTask(string taskId)
    {
        await _downloadManager.RetryAsync(taskId);
    }

    public Func<string, string, bool>? ConfirmFunc { get; set; } = (msg, title) =>
    {
        if (System.Windows.Application.Current == null)
            return true; // 单测默认返回 true
        var result = System.Windows.MessageBox.Show(
            msg,
            title,
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        return result == System.Windows.MessageBoxResult.Yes;
    };

    [RelayCommand]
    private void CancelAll()
    {
        if (ConfirmFunc != null && !ConfirmFunc("确定要取消全部下载任务并清理队列吗？", "确认取消全部"))
        {
            return;
        }

        // 先发送取消信号给所有正在运行的任务
        _downloadManager.CancelAll();
        IsDownloading = false;

        // 然后清理掉所有已经结束的任务（完成、失败、已取消），只留下等待或暂停的
        var tasksToRemove = _downloadManager.Tasks
            .Where(t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled)
            .ToList();
            
        foreach(var t in tasksToRemove)
        {
            _downloadManager.Tasks.Remove(t);
        }
    }

    [RelayCommand]
    private void PauseAll()
    {
        foreach (var task in _downloadManager.Tasks.ToList())
        {
            if (task.Status == DownloadStatus.Downloading)
                _downloadManager.Pause(task.Id);
        }
    }

}
