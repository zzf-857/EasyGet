using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    private readonly Action<ProcessStartInfo> _startProcess;
    private readonly HashSet<DownloadTask> _trackedQueueTasks = [];
    private readonly object _queueStateLock = new();
    private volatile bool _suppressQueueRefresh;
    private string _pendingCollectionSourceUrl = "";
    private string _pendingCollectionTitle = "";
    private List<string> _pendingCollectionUrls = [];

    [ObservableProperty] private string _urlsText = "";
    [ObservableProperty] private string _selectedFormat = "mp4";
    [ObservableProperty] private string _selectedQuality = "最高画质";
    [ObservableProperty] private int _linkCount;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isImportingPlaylist;
    [ObservableProperty] private string _playlistUrl = "";
    [ObservableProperty] private string _selectedQueueFilter = "进行中";

    public ObservableCollection<DownloadTask> QueueTasks => _downloadManager.Tasks;
    public ObservableCollection<DownloadTask> VisibleQueueTasks { get; } = [];
    public int ActiveDownloadCount => QueueTasks.Count(task => task.Status == DownloadStatus.Downloading);
    public int TotalTaskCount => QueueTasks.Count;
    public int CompletedTaskCount => QueueTasks.Count(task => task.Status == DownloadStatus.Completed);
    public int FailedTaskCount => QueueTasks.Count(task => task.Status == DownloadStatus.Failed);
    public int CancelledTaskCount => QueueTasks.Count(task => task.Status == DownloadStatus.Cancelled);
    public int PausedTaskCount => QueueTasks.Count(task => task.Status == DownloadStatus.Paused);
    public int RunningTaskCount => QueueTasks.Count(task => task.Status is DownloadStatus.Resolving or DownloadStatus.Downloading or DownloadStatus.Merging);
    public int RemainingTaskCount => QueueTasks.Count(task => task.Status is not (DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled));
    public int FinishedTaskCount => TotalTaskCount - RemainingTaskCount;
    public bool HasQueueTasks => TotalTaskCount > 0;
    public bool HasVisibleQueueTasks => VisibleQueueTasks.Count > 0;
    public bool CanPauseAll => QueueTasks.Any(task => task.Status == DownloadStatus.Downloading);
    public bool CanResumeAll => QueueTasks.Any(task => task.Status == DownloadStatus.Paused);
    public bool CanStopAll => QueueTasks.Any(task => task.Status is DownloadStatus.Waiting or DownloadStatus.Resolving or DownloadStatus.Downloading or DownloadStatus.Merging or DownloadStatus.Paused);
    public bool CanClearFinished => QueueTasks.Any(task => task.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled);
    public bool CanRetryFailed => FailedTaskCount > 0;
    public double OverallProgress => TotalTaskCount == 0
        ? 0
        : QueueTasks.Sum(task => task.Status == DownloadStatus.Completed ? 100 : Math.Clamp(task.Progress, 0, 100)) / TotalTaskCount;
    public double AggregateSpeed => QueueTasks
        .Where(task => task.Status == DownloadStatus.Downloading)
        .Sum(task => double.IsFinite(task.Speed) ? Math.Max(0, task.Speed) : 0);
    public string AggregateSpeedText => $"{ByteSizeFormatter.FormatClampZero((long)AggregateSpeed)}/s";
    public string OverallProgressText => $"{OverallProgress:F0}%";
    public string QueueSummaryText => TotalTaskCount == 0
        ? "暂无任务"
        : $"已完成 {CompletedTaskCount}/{TotalTaskCount} · 进行中 {RunningTaskCount} · 剩余 {RemainingTaskCount} · 失败 {FailedTaskCount}";
    public string EmptyQueueFilterText => SelectedQueueFilter switch
    {
        "失败" => "当前没有失败任务",
        "已结束" => "当前没有已结束任务",
        "全部" => "暂无下载任务",
        _ => TotalTaskCount == 0 ? "暂无并行任务" : "当前批次已处理完毕"
    };

    public string[] FormatOptions { get; } = ["mp4", "mkv", "webm", "mp3 (仅音频)"];
    public string[] QualityOptions { get; } = ["最高画质", "1080p", "720p", "480p"];
    public string[] QueueFilterOptions { get; } = ["进行中", "失败", "已结束", "全部"];

    public event Action<string, bool>? RequestShowNotification;

    public void ImportText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var existingUrls = UrlsText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(DownloadViewModel.ExtractUrl)
            .Where(url => url is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var validUrls = new List<string>();
        int ignoredCount = 0;
        int duplicateCount = 0;

        foreach (var line in lines)
        {
            var url = DownloadViewModel.ExtractUrl(line);
            if (url != null && existingUrls.Add(url))
            {
                validUrls.Add(url);
            }
            else if (url != null)
            {
                duplicateCount++;
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
            ClearPendingCollectionImport();
        }

        var details = new List<string> { $"新增 {validUrls.Count} 个链接" };
        if (duplicateCount > 0)
            details.Add($"跳过 {duplicateCount} 个重复链接");
        if (ignoredCount > 0)
            details.Add($"忽略 {ignoredCount} 行无效文本");
        RequestShowNotification?.Invoke(string.Join("，", details), validUrls.Count > 0);
    }

    public BatchDownloadViewModel(DownloadManager downloadManager, ConfigService configService, YtDlpService ytDlpService)
        : this(downloadManager, configService, ytDlpService, StartProcess)
    {
    }

    internal BatchDownloadViewModel(
        DownloadManager downloadManager,
        ConfigService configService,
        YtDlpService ytDlpService,
        Action<ProcessStartInfo> startProcess)
    {
        _downloadManager = downloadManager;
        _configService = configService;
        _ytDlpService = ytDlpService;
        _startProcess = startProcess;
        QueueTasks.CollectionChanged += OnQueueTasksChanged;
        SynchronizeQueueSubscriptions();
        RefreshQueueState();
    }

    private void OnQueueTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressQueueRefresh)
            return;

        try
        {
            SynchronizeQueueSubscriptions();
            RefreshQueueState();
        }
        catch
        {
            // 队列汇总属于 UI 辅助状态，绝不能中断实际下载工作线程。
        }
    }

    private void OnQueueTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Action? refresh = e.PropertyName switch
        {
            nameof(DownloadTask.Status) => RefreshQueueState,
            nameof(DownloadTask.Progress) or nameof(DownloadTask.Speed) => RefreshQueueMetrics,
            _ => null
        };
        if (refresh is null)
            return;
        if (_suppressQueueRefresh)
            return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        try
        {
            if (dispatcher is not null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(refresh);
            else
                refresh();
        }
        catch
        {
            // 属性订阅异常不能向上传播到 DownloadManager 的状态机。
        }
    }

    private void SynchronizeQueueSubscriptions()
    {
        foreach (var tracked in _trackedQueueTasks.Where(task => !QueueTasks.Contains(task)).ToList())
        {
            tracked.PropertyChanged -= OnQueueTaskPropertyChanged;
            _trackedQueueTasks.Remove(tracked);
        }

        foreach (var task in QueueTasks.Where(task => _trackedQueueTasks.Add(task)))
            task.PropertyChanged += OnQueueTaskPropertyChanged;
    }

    private void RefreshQueueState()
    {
        lock (_queueStateLock)
        {
            RebuildVisibleQueue();
            RefreshQueueMetrics();
            foreach (var propertyName in new[]
                     {
                         nameof(ActiveDownloadCount), nameof(TotalTaskCount), nameof(CompletedTaskCount),
                         nameof(FailedTaskCount), nameof(CancelledTaskCount), nameof(PausedTaskCount),
                         nameof(RunningTaskCount), nameof(RemainingTaskCount), nameof(FinishedTaskCount),
                         nameof(HasQueueTasks), nameof(HasVisibleQueueTasks),
                         nameof(QueueSummaryText), nameof(EmptyQueueFilterText)
                     })
            {
                OnPropertyChanged(propertyName);
            }

            PauseAllCommand.NotifyCanExecuteChanged();
            ResumeAllCommand.NotifyCanExecuteChanged();
            CancelAllCommand.NotifyCanExecuteChanged();
            ClearFinishedCommand.NotifyCanExecuteChanged();
            RetryFailedCommand.NotifyCanExecuteChanged();
        }
    }

    private void RefreshQueueMetrics()
    {
        lock (_queueStateLock)
        {
            OnPropertyChanged(nameof(OverallProgress));
            OnPropertyChanged(nameof(OverallProgressText));
            OnPropertyChanged(nameof(AggregateSpeed));
            OnPropertyChanged(nameof(AggregateSpeedText));
        }
    }

    private void RebuildVisibleQueue()
    {
        var visible = QueueTasks.Where(task => SelectedQueueFilter switch
        {
            "失败" => task.Status == DownloadStatus.Failed,
            "已结束" => task.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled,
            "全部" => true,
            _ => task.Status is not (DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled)
        }).ToList();

        VisibleQueueTasks.Clear();
        foreach (var task in visible)
            VisibleQueueTasks.Add(task);
    }

    private void CompleteBulkQueueUpdate()
    {
        _suppressQueueRefresh = false;
        SynchronizeQueueSubscriptions();
        RefreshQueueState();
    }

    partial void OnSelectedQueueFilterChanged(string value)
        => RefreshQueueState();

    partial void OnUrlsTextChanged(string value)
    {
        LinkCount = string.IsNullOrWhiteSpace(value) 
            ? 0 
            : value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Select(DownloadViewModel.ExtractUrl)
                   .Where(url => url is not null)
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .Count();
        StartBatchDownloadCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDownloadingChanged(bool value)
        => StartBatchDownloadCommand.NotifyCanExecuteChanged();

    partial void OnPlaylistUrlChanged(string value)
        => ImportPlaylistCommand.NotifyCanExecuteChanged();

    partial void OnIsImportingPlaylistChanged(bool value)
        => ImportPlaylistCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void PasteUrls()
    {
        if (System.Windows.Clipboard.ContainsText())
        {
            var clipText = System.Windows.Clipboard.GetText().Trim();
            ImportText(clipText);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartBatchDownload))]
    private async Task StartBatchDownload()
    {
        if (LinkCount == 0)
            return;

        IsDownloading = true;
        var enqueuedCount = 0;
        try
        {
            var parsedUrls = UrlsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                               .Select(line => DownloadViewModel.ExtractUrl(line))
                               .Where(url => url is not null)
                               .Cast<string>()
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .ToList();
            var isExactCollectionImport = _pendingCollectionUrls.Count > 0
                                          && parsedUrls.Count == _pendingCollectionUrls.Count
                                          && parsedUrls.ToHashSet(StringComparer.OrdinalIgnoreCase)
                                              .SetEquals(_pendingCollectionUrls);
            var knownUrls = new HashSet<string>(
                _downloadManager.Tasks.Select(task => task.Url),
                StringComparer.OrdinalIgnoreCase);
            var urls = parsedUrls
                               .Where(knownUrls.Add)
                               .ToList();

            if (urls.Count == 0)
            {
                RequestShowNotification?.Invoke("没有新增任务：这些链接已经在下载队列中", false);
                return;
            }

            var batch = BatchDownloadOrganizer.Create(
                _configService.Config.DefaultDownloadPath,
                urls,
                isExactCollectionImport ? _pendingCollectionSourceUrl : "",
                collectionTitle: isExactCollectionImport ? _pendingCollectionTitle : "");
            var outputDirectory = batch?.Directory
                ?? _configService.Config.DefaultDownloadPath;

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

            for (var index = 0; index < urls.Count; index++)
            {
                var collectionItemIndex = isExactCollectionImport
                    ? _pendingCollectionUrls.FindIndex(url => string.Equals(
                        url,
                        urls[index],
                        StringComparison.OrdinalIgnoreCase)) + 1
                    : index + 1;
                var task = new DownloadTask
                {
                    Url = urls[index],
                    Format = format,
                    Quality = quality,
                    OutputDirectory = outputDirectory,
                    BatchId = batch?.Id ?? "",
                    BatchName = batch?.Name ?? "",
                    BatchDirectory = batch?.Directory ?? "",
                    CollectionTitle = batch?.CollectionTitle ?? "",
                    CollectionItemIndex = batch is null ? 0 : collectionItemIndex,
                    CollectionItemCount = batch is null
                        ? 0
                        : isExactCollectionImport
                            ? _pendingCollectionUrls.Count
                            : urls.Count
                };
                await _downloadManager.EnqueueAsync(task);
                enqueuedCount++;
            }

            if (batch is not null)
            {
                RequestShowNotification?.Invoke(
                    $"已加入 {urls.Count} 个任务，并创建目录：{Path.GetFileName(batch.Directory)}",
                    true);
            }
            else
            {
                RequestShowNotification?.Invoke($"已加入 {urls.Count} 个下载任务", true);
            }

            UrlsText = "";
            ClearPendingCollectionImport();
            SelectedQueueFilter = "进行中";
        }
        catch (Exception ex)
        {
            var prefix = enqueuedCount > 0
                ? $"已加入 {enqueuedCount} 个任务，但后续任务创建失败"
                : "批量任务创建失败";
            RequestShowNotification?.Invoke($"{prefix}：{ex.Message}", false);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private bool CanStartBatchDownload()
        => LinkCount > 0 && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanImportPlaylist))]
    private async Task ImportPlaylist()
    {
        if (string.IsNullOrWhiteSpace(PlaylistUrl))
            return;

        IsImportingPlaylist = true;
        try
        {
            var sourceUrl = PlaylistUrl.Trim();
            var playlist = await _ytDlpService.GetPlaylistInfoAsync(sourceUrl);
            if (ApplyPlaylistImport(playlist, sourceUrl))
                PlaylistUrl = "";
            else
                RequestShowNotification?.Invoke("未能从该链接读取播放列表，请检查链接或登录状态", false);
        }
        catch (Exception ex)
        {
            RequestShowNotification?.Invoke($"播放列表导入失败：{ex.Message}", false);
        }
        finally
        {
            IsImportingPlaylist = false;
        }
    }

    private bool CanImportPlaylist()
        => !string.IsNullOrWhiteSpace(PlaylistUrl) && !IsImportingPlaylist;

    internal bool ApplyPlaylistImport(PlaylistInfo playlist, string? fallbackSourceUrl = null)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        if (playlist.Urls.Count == 0)
            return false;

        var urls = playlist.Urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (urls.Count == 0)
            return false;
        UrlsText = string.Join("\n", urls);
        _pendingCollectionSourceUrl = string.IsNullOrWhiteSpace(playlist.SourceUrl)
            ? fallbackSourceUrl?.Trim() ?? ""
            : playlist.SourceUrl.Trim();
        _pendingCollectionTitle = playlist.Title.Trim();
        _pendingCollectionUrls = urls;
        RequestShowNotification?.Invoke(
            string.IsNullOrWhiteSpace(playlist.Title)
                ? $"已导入 {urls.Count} 个播放列表条目"
                : $"已导入“{playlist.Title}”的 {urls.Count} 个条目",
            true);
        return true;
    }

    private void ClearPendingCollectionImport()
    {
        _pendingCollectionSourceUrl = "";
        _pendingCollectionTitle = "";
        _pendingCollectionUrls = [];
    }

    [RelayCommand]
    private void SetQueueFilter(string? filter)
    {
        if (!string.IsNullOrWhiteSpace(filter))
            SelectedQueueFilter = filter;
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
            if (task.Status is DownloadStatus.Downloading
                or DownloadStatus.Waiting
                or DownloadStatus.Resolving
                or DownloadStatus.Merging
                or DownloadStatus.Paused)
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

    [RelayCommand]
    private async Task OpenTaskFolder(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return;

        var task = _downloadManager.Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is null)
            return;

        await Task.Run(() =>
        {
            try
            {
                var startInfo = CreateOpenTaskFolderStartInfo(task);
                if (startInfo is not null)
                    _startProcess(startInfo);
            }
            catch
            {
            }
        });
    }

    internal static ProcessStartInfo? CreateOpenTaskFolderStartInfo(DownloadTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.OutputFilePath))
        {
            if (File.Exists(task.OutputFilePath))
            {
                return new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{task.OutputFilePath}\"",
                    UseShellExecute = true
                };
            }

            if (Directory.Exists(task.OutputFilePath))
            {
                return new ProcessStartInfo
                {
                    FileName = task.OutputFilePath,
                    UseShellExecute = true
                };
            }
        }

        if (!string.IsNullOrWhiteSpace(task.OutputDirectory) && Directory.Exists(task.OutputDirectory))
        {
            return new ProcessStartInfo
            {
                FileName = task.OutputDirectory,
                UseShellExecute = true
            };
        }

        return null;
    }

    public Func<string, string, bool>? ConfirmFunc { get; set; } = ConfirmationDialogService.Show;

    [RelayCommand(CanExecute = nameof(CanStopAll))]
    private void CancelAll()
    {
        var unfinishedCount = RemainingTaskCount;
        if (unfinishedCount == 0)
            return;

        if (ConfirmFunc != null
            && !ConfirmFunc($"确定停止 {unfinishedCount} 个未完成任务吗？已完成记录会保留在队列中。", "确认停止未完成任务"))
        {
            return;
        }

        _suppressQueueRefresh = true;
        try
        {
            _downloadManager.CancelAll();
        }
        finally
        {
            CompleteBulkQueueUpdate();
        }
        IsDownloading = false;
        RequestShowNotification?.Invoke("已发送停止请求，已完成任务仍保留在队列中", true);
    }

    [RelayCommand(CanExecute = nameof(CanPauseAll))]
    private void PauseAll()
    {
        _suppressQueueRefresh = true;
        try
        {
            foreach (var task in _downloadManager.Tasks.ToList())
            {
                if (task.Status == DownloadStatus.Downloading)
                    _downloadManager.Pause(task.Id);
            }
        }
        finally
        {
            CompleteBulkQueueUpdate();
        }
    }

    [RelayCommand(CanExecute = nameof(CanResumeAll))]
    private async Task ResumeAll()
    {
        _suppressQueueRefresh = true;
        try
        {
            foreach (var task in _downloadManager.Tasks
                         .Where(task => task.Status == DownloadStatus.Paused)
                         .ToList())
            {
                await _downloadManager.ResumeAsync(task.Id);
            }
        }
        finally
        {
            CompleteBulkQueueUpdate();
        }
    }

    [RelayCommand(CanExecute = nameof(CanClearFinished))]
    private void ClearFinished()
    {
        var finished = _downloadManager.Tasks
            .Where(task => task.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled)
            .ToList();
        _suppressQueueRefresh = true;
        try
        {
            foreach (var task in finished)
                _downloadManager.Tasks.Remove(task);
        }
        finally
        {
            CompleteBulkQueueUpdate();
        }

        RequestShowNotification?.Invoke($"已从队列清理 {finished.Count} 个已结束任务，下载历史和本地文件不受影响", true);
    }

    [RelayCommand(CanExecute = nameof(CanRetryFailed))]
    private async Task RetryFailed()
    {
        var failed = _downloadManager.Tasks
            .Where(task => task.Status == DownloadStatus.Failed)
            .ToList();
        _suppressQueueRefresh = true;
        try
        {
            foreach (var task in failed)
                await _downloadManager.RetryAsync(task.Id);
        }
        finally
        {
            CompleteBulkQueueUpdate();
        }

        SelectedQueueFilter = "进行中";
        RequestShowNotification?.Invoke($"已重新加入 {failed.Count} 个失败任务", true);
    }

    private static void StartProcess(ProcessStartInfo startInfo)
    {
        Process.Start(startInfo);
    }
}
