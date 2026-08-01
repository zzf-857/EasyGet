using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    private readonly IVideoInfoProvider _videoInfoProvider;
    private readonly DownloadPreflightService _preflightService;
    private readonly DownloadDuplicateDetector? _duplicateDetector;
    private readonly ExistingCollectionFolderStore _collectionFolderStore;
    private readonly Func<string, CancellationToken, Task<PlaylistInfo>> _getPlaylistInfoAsync;
    private readonly Func<Task<List<DownloadHistory>>>? _loadDownloadHistoryAsync;
    private readonly Action<ProcessStartInfo> _startProcess;
    private readonly Func<string?> _readClipboardText;
    private readonly Func<string, string?> _selectDirectory;
    private readonly HashSet<DownloadTask> _trackedQueueTasks = [];
    private readonly HashSet<BatchDownloadDraft> _trackedPendingItems = [];
    private readonly object _queueStateLock = new();
    private volatile bool _suppressQueueRefresh;
    private string _downloadRootDirectory = "";
    private string? _selectedCollectionDirectoryBeforeRefresh;
    private string _pendingCollectionTitle = "";
    private List<string> _pendingCollectionUrls = [];
    private CancellationTokenSource? _nameResolutionCts;
    private int _inputRevision;
    private bool _suppressDraftInvalidation;
    private bool _draftsAreExactCollectionImport;
    private string _draftCollectionTitle = "";
    private bool _applyingSharedDestination;
    private bool _isRefreshingDestinationOptions;
    private Task<bool> _destinationPersistenceTask = Task.FromResult(true);

    [ObservableProperty] private string _urlsText = "";
    [ObservableProperty] private string _selectedFormat = "mp4";
    [ObservableProperty] private string _selectedQuality = "最高画质";
    [ObservableProperty] private int _linkCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditBatchDestination))]
    [NotifyPropertyChangedFor(nameof(CanSelectExistingCollectionFolder))]
    [NotifyPropertyChangedFor(nameof(CanEditPendingItems))]
    private bool _isDownloading;
    [ObservableProperty] private bool _isImportingPlaylist;
    [ObservableProperty] private string _playlistUrl = "";
    [ObservableProperty] private string _selectedQueueFilter = "全部";
    [ObservableProperty] private string _downloadDirectory = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBatchInputStep))]
    [NotifyPropertyChangedFor(nameof(BatchConfirmationSummary))]
    [NotifyPropertyChangedFor(nameof(CanEditPendingItems))]
    private bool _isNameConfirmationStep;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchConfirmationSummary))]
    [NotifyPropertyChangedFor(nameof(CanEditBatchDestination))]
    [NotifyPropertyChangedFor(nameof(CanSelectExistingCollectionFolder))]
    [NotifyPropertyChangedFor(nameof(CanEditPendingItems))]
    private bool _isResolvingNames;
    [ObservableProperty]
    private ExistingCollectionFolder? _selectedCollectionFolder;

    public ObservableCollection<DownloadTask> QueueTasks => _downloadManager.Tasks;
    public ObservableCollection<DownloadTask> VisibleQueueTasks { get; } = [];
    public ObservableCollection<BatchDownloadDraft> PendingItems { get; } = [];
    public ReadOnlyObservableCollection<ExistingCollectionFolder> ExistingCollectionFolders
        => _collectionFolderStore.Folders;
    public int ActiveDownloadCount => QueueTasks.Count(task => task.Status == DownloadStatus.Downloading);
    public int WaitingTaskCount => QueueTasks.Count(task => task.Status == DownloadStatus.Waiting);
    public int TotalTaskCount => QueueTasks.Count;
    public int CompletedTaskCount => QueueTasks.Count(task => task.Status == DownloadStatus.Completed);
    public int FailedTaskCount => QueueTasks.Count(task => task.Status == DownloadStatus.Failed);
    public int CancelledTaskCount => QueueTasks.Count(task => task.Status == DownloadStatus.Cancelled);
    public int PausedTaskCount => QueueTasks.Count(task => task.Status == DownloadStatus.Paused);
    public int ScheduledTaskCount => QueueTasks.Count(task => task.Status == DownloadStatus.Scheduled);
    public int RunningTaskCount => QueueTasks.Count(task => task.Status is DownloadStatus.Resolving or DownloadStatus.Downloading or DownloadStatus.Merging);
    public int RemainingTaskCount => QueueTasks.Count(task => task.Status is not (DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled));
    public int FinishedTaskCount => TotalTaskCount - RemainingTaskCount;
    public bool HasQueueTasks => TotalTaskCount > 0;
    public bool HasVisibleQueueTasks => VisibleQueueTasks.Count > 0;
    public bool CanPauseAll => QueueTasks.Any(task => task.Status == DownloadStatus.Downloading);
    public bool CanResumeAll => QueueTasks.Any(task => task.Status == DownloadStatus.Paused);
    public bool CanStopAll => QueueTasks.Any(task => task.Status is DownloadStatus.Waiting or DownloadStatus.Resolving or DownloadStatus.Downloading or DownloadStatus.Merging or DownloadStatus.Paused or DownloadStatus.Scheduled);
    public bool CanClearFinished => QueueTasks.Any(task => task.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled);
    public bool CanRetryFailed => FailedTaskCount > 0;
    public bool IsLoadingCollectionFolders => _collectionFolderStore.IsLoading;
    public bool IsBatchInputStep => !IsNameConfirmationStep;
    public string BatchConfirmationSummary => IsResolvingNames
        ? $"正在解析 {PendingItems.Count} 个视频的名称..."
        : $"已解析 {PendingItems.Count} 个视频，请确认或修改名称";
    public bool CanEditBatchDestination
        => !IsDownloading && !IsResolvingNames && !IsLoadingCollectionFolders;
    public bool CanSelectExistingCollectionFolder
        => _collectionFolderStore.HasFolders && CanEditBatchDestination;
    public bool CanEditPendingItems
        => IsNameConfirmationStep && !IsResolvingNames && !IsDownloading;
    public string ExistingCollectionFolderPlaceholder
        => $"临时下载 · {_downloadRootDirectory}";
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
        : $"已完成 {CompletedTaskCount}/{TotalTaskCount} · 进行中 {RunningTaskCount} · 计划 {ScheduledTaskCount} · 剩余 {RemainingTaskCount} · 失败 {FailedTaskCount}";
    public string EmptyQueueFilterText => SelectedQueueFilter switch
    {
        "进行中" => "当前没有正在处理的任务",
        "等待" => "当前没有等待中的任务",
        "已暂停" => "当前没有暂停的任务",
        "计划" => "当前没有计划任务",
        "失败" => "当前没有失败任务",
        "已完成" => "当前没有已完成任务",
        "全部" => "暂无下载任务",
        _ => "当前筛选下没有任务"
    };

    public string[] FormatOptions { get; } = ["mp4", "mkv", "webm", "mp3 (仅音频)"];
    public string[] QualityOptions { get; } = ["最高画质", "1080p", "720p", "480p"];
    public string[] QueueFilterOptions { get; } = ["全部", "进行中", "等待", "计划", "已暂停", "失败", "已完成"];

    public event Action<string, bool>? RequestShowNotification;

    public void ImportText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var existingLines = string.IsNullOrEmpty(UrlsText)
            ? []
            : UrlsText.Split('\n', StringSplitOptions.None).ToList();
        var existingEntries = new Dictionary<string, (int LineIndex, bool HasProvidedTitle)>(
            StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < existingLines.Count; index++)
        {
            var existingItem = ParseBatchInputLine(existingLines[index]);
            if (existingItem is null)
                continue;

            if (!existingEntries.TryGetValue(existingItem.Url, out var current)
                || (!current.HasProvidedTitle && existingItem.HasProvidedTitle))
            {
                existingEntries[existingItem.Url] = (index, existingItem.HasProvidedTitle);
            }
        }

        var validEntries = new List<string>();
        var importedEntries = new Dictionary<string, (int EntryIndex, bool HasProvidedTitle)>(
            StringComparer.OrdinalIgnoreCase);
        int ignoredCount = 0;
        int duplicateCount = 0;
        int updatedTitleCount = 0;

        foreach (var line in lines)
        {
            var item = ParseBatchInputLine(line);
            if (item is null)
            {
                ignoredCount++;
                continue;
            }

            var formattedEntry = FormatBatchInput(item);
            if (existingEntries.TryGetValue(item.Url, out var existingEntry))
            {
                duplicateCount++;
                if (item.HasProvidedTitle && !existingEntry.HasProvidedTitle)
                {
                    existingLines[existingEntry.LineIndex] = formattedEntry;
                    existingEntries[item.Url] = (existingEntry.LineIndex, true);
                    updatedTitleCount++;
                }
                continue;
            }

            if (importedEntries.TryGetValue(item.Url, out var importedEntry))
            {
                duplicateCount++;
                if (item.HasProvidedTitle && !importedEntry.HasProvidedTitle)
                {
                    validEntries[importedEntry.EntryIndex] = formattedEntry;
                    importedEntries[item.Url] = (importedEntry.EntryIndex, true);
                    updatedTitleCount++;
                }
                continue;
            }

            importedEntries.Add(item.Url, (validEntries.Count, item.HasProvidedTitle));
            validEntries.Add(formattedEntry);
        }

        if (validEntries.Count > 0 || updatedTitleCount > 0)
        {
            existingLines.AddRange(validEntries);
            UrlsText = string.Join("\n", existingLines);
            ClearPendingCollectionImport();
        }

        var details = new List<string> { $"新增 {validEntries.Count} 个链接" };
        if (updatedTitleCount > 0)
            details.Add($"补充 {updatedTitleCount} 个标题");
        if (duplicateCount > 0)
            details.Add($"跳过 {duplicateCount} 个重复链接");
        if (ignoredCount > 0)
            details.Add($"忽略 {ignoredCount} 行无效文本");
        RequestShowNotification?.Invoke(
            string.Join("，", details),
            validEntries.Count > 0 || updatedTitleCount > 0);
    }

    private sealed record ParsedBatchInput(string Url, string Title, bool HasProvidedTitle);

    private sealed record ConfirmedBatchItem(
        string Url,
        string Title,
        VideoInfo? ResolvedInfo,
        int CollectionItemIndex,
        int CollectionItemCount);

    private static string FormatBatchInput(ParsedBatchInput item)
        => item.HasProvidedTitle ? $"{item.Title}---{item.Url}" : item.Url;

    private static List<ParsedBatchInput> ParseBatchInput(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseBatchInputLine)
            .Where(item => item is not null)
            .Cast<ParsedBatchInput>()
            .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.FirstOrDefault(item => item.HasProvidedTitle) ?? group.First())
            .ToList();

    private static ParsedBatchInput? ParseBatchInputLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return null;

        var extractedUrl = DownloadViewModel.ExtractUrl(trimmed);
        if (extractedUrl is null)
            return null;

        var urlIndex = trimmed.IndexOf(extractedUrl, StringComparison.OrdinalIgnoreCase);
        if (urlIndex > 0)
        {
            var prefix = trimmed[..urlIndex].TrimEnd();
            if (prefix.EndsWith("---", StringComparison.Ordinal))
            {
                var title = prefix[..^3].Trim();
                if (title.Length > 0)
                    return new ParsedBatchInput(extractedUrl, title, true);
            }
        }

        var pipeIndex = trimmed.IndexOfAny(['|', '｜']);
        if (pipeIndex >= 0)
        {
            var url = DownloadViewModel.ExtractUrl(trimmed[..pipeIndex]);
            if (url is not null)
            {
                var title = trimmed[(pipeIndex + 1)..].Trim();
                return new ParsedBatchInput(url, title, title.Length > 0);
            }
        }

        return new ParsedBatchInput(extractedUrl, "", false);
    }

    public BatchDownloadViewModel(
        DownloadManager downloadManager,
        ConfigService configService,
        YtDlpService ytDlpService,
        DownloadPreflightService? preflightService = null,
        HistoryService? historyService = null,
        DownloadDuplicateDetector? duplicateDetector = null,
        ExistingCollectionFolderStore? collectionFolderStore = null,
        IVideoInfoProvider? videoInfoProvider = null)
        : this(
            downloadManager,
            configService,
            ytDlpService,
            StartProcess,
            null,
            preflightService,
            historyService,
            duplicateDetector,
            null,
            collectionFolderStore,
            videoInfoProvider)
    {
    }

    internal BatchDownloadViewModel(
        DownloadManager downloadManager,
        ConfigService configService,
        YtDlpService ytDlpService,
        Action<ProcessStartInfo> startProcess,
        Func<string?>? readClipboardText = null,
        DownloadPreflightService? preflightService = null,
        HistoryService? historyService = null,
        DownloadDuplicateDetector? duplicateDetector = null,
        Func<string, string?>? selectDirectory = null,
        ExistingCollectionFolderStore? collectionFolderStore = null,
        IVideoInfoProvider? videoInfoProvider = null,
        Func<string, CancellationToken, Task<PlaylistInfo>>? getPlaylistInfoAsync = null,
        Func<Task<List<DownloadHistory>>>? loadDownloadHistoryAsync = null)
    {
        _downloadManager = downloadManager;
        _configService = configService;
        _ytDlpService = ytDlpService;
        _videoInfoProvider = videoInfoProvider ?? new YtDlpVideoInfoProvider(ytDlpService);
        _preflightService = preflightService ?? new DownloadPreflightService();
        _duplicateDetector = duplicateDetector;
        _collectionFolderStore = collectionFolderStore
            ?? new ExistingCollectionFolderStore(historyService, configService);
        _getPlaylistInfoAsync = getPlaylistInfoAsync ?? _ytDlpService.GetPlaylistInfoAsync;
        _loadDownloadHistoryAsync = loadDownloadHistoryAsync
            ?? (historyService is null ? null : () => historyService.GetAllAsync());
        _startProcess = startProcess;
        _readClipboardText = readClipboardText ?? ReadClipboardText;
        _selectDirectory = selectDirectory ?? SelectDirectory;
        _downloadRootDirectory = _configService.Config.DefaultDownloadPath;
        DownloadDirectory = _downloadRootDirectory;
        _configService.DefaultDownloadPathChanged += OnSharedDefaultDownloadPathChanged;
        _configService.SelectedCollectionDirectoryChanged += OnSharedSelectedCollectionDirectoryChanged;
        _collectionFolderStore.PropertyChanged += OnCollectionFolderStorePropertyChanged;
        _collectionFolderStore.FoldersRefreshing += OnCollectionFoldersRefreshing;
        _collectionFolderStore.FoldersRefreshed += OnCollectionFoldersRefreshed;
        QueueTasks.CollectionChanged += OnQueueTasksChanged;
        PendingItems.CollectionChanged += OnPendingItemsChanged;
        SynchronizeQueueSubscriptions();
        RefreshQueueState();
    }

    public Task InitializeAsync()
        => LoadExistingCollectionFoldersAsync(forceRefresh: false);

    public void RefreshRuntimeConfigDisplay()
    {
        OnSharedDefaultDownloadPathChanged(_configService.Config.DefaultDownloadPath);
        OnSharedSelectedCollectionDirectoryChanged(
            _configService.Config.SelectedCollectionDirectory);
    }

    [RelayCommand(CanExecute = nameof(CanEditDestination))]
    private async Task BrowseDirectory()
    {
        var selectedDirectory = _selectDirectory(DownloadDirectory);
        if (string.IsNullOrWhiteSpace(selectedDirectory))
            return;

        try
        {
            SelectedCollectionFolder = await _collectionFolderStore.RegisterCollectionAsync(
                selectedDirectory);
            if (!await _destinationPersistenceTask)
            {
                RequestShowNotification?.Invoke(
                    "合集已选择，但保存失败；应用退出时将再次尝试保存。",
                    false);
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException
                                   or InvalidOperationException)
        {
            RequestShowNotification?.Invoke($"无法添加合集目录：{ex.Message}", false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanClearSelectedCollectionFolder))]
    private void ClearSelectedCollectionFolder()
        => SelectedCollectionFolder = null;

    private bool CanClearSelectedCollectionFolder()
        => SelectedCollectionFolder is not null && CanEditDestination();

    private bool CanEditDestination()
        => CanEditBatchDestination;

    [RelayCommand(CanExecute = nameof(CanEditDestination))]
    private Task RefreshExistingCollectionFolders()
        => LoadExistingCollectionFoldersAsync(forceRefresh: true);

    private async Task LoadExistingCollectionFoldersAsync(bool forceRefresh)
    {
        try
        {
            if (forceRefresh)
                await _collectionFolderStore.RefreshAsync();
            else
                await _collectionFolderStore.EnsureLoadedAsync();

            OnSharedSelectedCollectionDirectoryChanged(
                _configService.Config.SelectedCollectionDirectory);
        }
        catch (Exception ex)
        {
            RequestShowNotification?.Invoke($"读取已有合集失败：{ex.Message}", false);
        }
    }

    private void OnCollectionFolderStorePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ExistingCollectionFolderStore.IsLoading):
                OnPropertyChanged(nameof(IsLoadingCollectionFolders));
                OnPropertyChanged(nameof(CanEditBatchDestination));
                OnPropertyChanged(nameof(CanSelectExistingCollectionFolder));
                ResolveBatchNamesCommand.NotifyCanExecuteChanged();
                StartBatchDownloadCommand.NotifyCanExecuteChanged();
                NotifyDestinationCommandsCanExecuteChanged();
                break;
            case nameof(ExistingCollectionFolderStore.HasFolders):
                OnPropertyChanged(nameof(CanSelectExistingCollectionFolder));
                break;
            case nameof(ExistingCollectionFolderStore.Placeholder):
                OnPropertyChanged(nameof(ExistingCollectionFolderPlaceholder));
                break;
        }
    }

    private void OnCollectionFoldersRefreshing(object? sender, EventArgs e)
    {
        _selectedCollectionDirectoryBeforeRefresh = SelectedCollectionFolder?.Directory
            ?? _configService.Config.SelectedCollectionDirectory;
        _isRefreshingDestinationOptions = true;
    }

    private void OnCollectionFoldersRefreshed(object? sender, EventArgs e)
    {
        var selectedPath = _selectedCollectionDirectoryBeforeRefresh
            ?? SelectedCollectionFolder?.Directory
            ?? _configService.Config.SelectedCollectionDirectory;
        _selectedCollectionDirectoryBeforeRefresh = null;
        try
        {
            OnSharedSelectedCollectionDirectoryChanged(selectedPath ?? "");
        }
        finally
        {
            _isRefreshingDestinationOptions = false;
        }
    }

    partial void OnSelectedCollectionFolderChanged(ExistingCollectionFolder? value)
    {
        DownloadDirectory = value?.Directory ?? _downloadRootDirectory;
        ClearSelectedCollectionFolderCommand.NotifyCanExecuteChanged();
        if (_applyingSharedDestination || _isRefreshingDestinationOptions)
            return;

        _configService.UpdateSelectedCollectionDirectory(value?.Directory);
        _destinationPersistenceTask = _configService.SaveAsync();
    }

    private void OnSharedDefaultDownloadPathChanged(string path)
    {
        void Apply()
        {
            _downloadRootDirectory = path;
            if (SelectedCollectionFolder is null)
                DownloadDirectory = path;
            OnPropertyChanged(nameof(ExistingCollectionFolderPlaceholder));
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Apply();
        else
            dispatcher.Invoke(Apply);
    }

    private void OnSharedSelectedCollectionDirectoryChanged(string directory)
    {
        void Apply()
        {
            var selected = string.IsNullOrWhiteSpace(directory)
                ? null
                : ExistingCollectionFolderStore.PathsEqual(
                    SelectedCollectionFolder?.Directory,
                    directory)
                    && Directory.Exists(directory)
                    ? SelectedCollectionFolder
                    : _collectionFolderStore.FindByDirectory(directory);
            _applyingSharedDestination = true;
            try
            {
                SelectedCollectionFolder = selected;
            }
            finally
            {
                _applyingSharedDestination = false;
            }

            DownloadDirectory = selected?.Directory ?? _downloadRootDirectory;
            if (!string.IsNullOrWhiteSpace(directory) && selected is null)
            {
                _configService.UpdateSelectedCollectionDirectory(null);
                _destinationPersistenceTask = _configService.SaveAsync();
            }
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Apply();
        else
            dispatcher.Invoke(Apply);
    }

    private static string? SelectDirectory(string currentDirectory)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择文件夹作为合集",
            InitialDirectory = currentDirectory
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
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

    private void OnPendingItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SynchronizePendingItemSubscriptions();
        OnPropertyChanged(nameof(BatchConfirmationSummary));
        StartBatchDownloadCommand.NotifyCanExecuteChanged();
    }

    private void OnPendingItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BatchDownloadDraft.Title))
            StartBatchDownloadCommand.NotifyCanExecuteChanged();
    }

    private void SynchronizePendingItemSubscriptions()
    {
        foreach (var tracked in _trackedPendingItems.Where(item => !PendingItems.Contains(item)).ToList())
        {
            tracked.PropertyChanged -= OnPendingItemPropertyChanged;
            _trackedPendingItems.Remove(tracked);
        }

        foreach (var item in PendingItems.Where(item => _trackedPendingItems.Add(item)))
            item.PropertyChanged += OnPendingItemPropertyChanged;
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
                         nameof(ActiveDownloadCount), nameof(WaitingTaskCount), nameof(TotalTaskCount), nameof(CompletedTaskCount),
                         nameof(FailedTaskCount), nameof(CancelledTaskCount), nameof(PausedTaskCount), nameof(ScheduledTaskCount),
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
            "进行中" => task.Status is DownloadStatus.Resolving or DownloadStatus.Downloading or DownloadStatus.Merging,
            "等待" => task.Status == DownloadStatus.Waiting,
            "计划" => task.Status == DownloadStatus.Scheduled,
            "已暂停" => task.Status == DownloadStatus.Paused,
            "失败" => task.Status == DownloadStatus.Failed,
            "已完成" => task.Status == DownloadStatus.Completed,
            "全部" => true,
            _ => true
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
        _inputRevision++;
        if (!_suppressDraftInvalidation && IsNameConfirmationStep)
            ResetNameConfirmation();

        LinkCount = string.IsNullOrWhiteSpace(value)
            ? 0
            : ParseBatchInput(value).Count;
        ResolveBatchNamesCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDownloadingChanged(bool value)
    {
        ResolveBatchNamesCommand.NotifyCanExecuteChanged();
        StartBatchDownloadCommand.NotifyCanExecuteChanged();
        EditBatchInputCommand.NotifyCanExecuteChanged();
        RemovePendingItemCommand.NotifyCanExecuteChanged();
        ImportPlaylistCommand.NotifyCanExecuteChanged();
        NotifyDestinationCommandsCanExecuteChanged();
    }

    partial void OnIsResolvingNamesChanged(bool value)
    {
        ResolveBatchNamesCommand.NotifyCanExecuteChanged();
        StartBatchDownloadCommand.NotifyCanExecuteChanged();
        EditBatchInputCommand.NotifyCanExecuteChanged();
        RemovePendingItemCommand.NotifyCanExecuteChanged();
        ImportPlaylistCommand.NotifyCanExecuteChanged();
        NotifyDestinationCommandsCanExecuteChanged();
    }

    partial void OnIsNameConfirmationStepChanged(bool value)
    {
        ResolveBatchNamesCommand.NotifyCanExecuteChanged();
        StartBatchDownloadCommand.NotifyCanExecuteChanged();
        EditBatchInputCommand.NotifyCanExecuteChanged();
        RemovePendingItemCommand.NotifyCanExecuteChanged();
        ImportPlaylistCommand.NotifyCanExecuteChanged();
    }

    private void NotifyDestinationCommandsCanExecuteChanged()
    {
        BrowseDirectoryCommand.NotifyCanExecuteChanged();
        RefreshExistingCollectionFoldersCommand.NotifyCanExecuteChanged();
        ClearSelectedCollectionFolderCommand.NotifyCanExecuteChanged();
    }

    partial void OnPlaylistUrlChanged(string value)
        => ImportPlaylistCommand.NotifyCanExecuteChanged();

    partial void OnIsImportingPlaylistChanged(bool value)
    {
        ImportPlaylistCommand.NotifyCanExecuteChanged();
        ResolveBatchNamesCommand.NotifyCanExecuteChanged();
        StartBatchDownloadCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void PasteUrls()
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
            ImportText(text.Trim());
    }

    private static string? ReadClipboardText()
        => Clipboard.ContainsText() ? Clipboard.GetText() : null;

    [RelayCommand(CanExecute = nameof(CanResolveBatchNames))]
    private async Task ResolveBatchNames()
    {
        if (!CanResolveBatchNames())
            return;

        var inputRevision = _inputRevision;
        var parsedItems = ParseBatchInput(UrlsText);
        var parsedUrls = parsedItems.Select(item => item.Url).ToList();
        var isExactCollectionImport = _pendingCollectionUrls.Count > 0
                                      && parsedUrls.Count == _pendingCollectionUrls.Count
                                      && parsedUrls.ToHashSet(StringComparer.OrdinalIgnoreCase)
                                          .SetEquals(_pendingCollectionUrls);
        var knownUrls = new HashSet<string>(
            _downloadManager.Tasks.Select(task => task.Url),
            StringComparer.OrdinalIgnoreCase);
        var validItems = parsedItems.Where(item => knownUrls.Add(item.Url)).ToList();
        if (validItems.Count == 0)
        {
            RequestShowNotification?.Invoke("没有新增任务：这些链接已经在下载队列中", false);
            return;
        }

        ResetNameConfirmation();
        _draftsAreExactCollectionImport = isExactCollectionImport;
        _draftCollectionTitle = isExactCollectionImport ? _pendingCollectionTitle : "";

        foreach (var item in validItems)
        {
            var collectionItemIndex = isExactCollectionImport
                ? _pendingCollectionUrls.FindIndex(url => string.Equals(
                    url,
                    item.Url,
                    StringComparison.OrdinalIgnoreCase)) + 1
                : 0;
            PendingItems.Add(new BatchDownloadDraft(
                item.Url,
                item.Title,
                item.HasProvidedTitle,
                collectionItemIndex,
                isExactCollectionImport ? _pendingCollectionUrls.Count : 0));
        }

        IsNameConfirmationStep = true;
        IsResolvingNames = true;
        var cts = new CancellationTokenSource();
        _nameResolutionCts = cts;
        try
        {
            await ResolvePendingNamesAsync(PendingItems.ToArray(), cts.Token);
            if (inputRevision != _inputRevision || !IsNameConfirmationStep)
                return;

            var unresolvedCount = PendingItems.Count(item => string.IsNullOrWhiteSpace(item.Title));
            if (unresolvedCount > 0)
            {
                RequestShowNotification?.Invoke(
                    $"有 {unresolvedCount} 个名称未能解析，请手动填写后继续",
                    false);
            }
            else if (validItems.Count < parsedItems.Count)
            {
                RequestShowNotification?.Invoke(
                    $"已解析 {validItems.Count} 个名称，并跳过 {parsedItems.Count - validItems.Count} 个队列内重复链接",
                    true);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RequestShowNotification?.Invoke($"批量名称解析失败：{ex.Message}", false);
        }
        finally
        {
            if (ReferenceEquals(_nameResolutionCts, cts))
            {
                _nameResolutionCts = null;
                IsResolvingNames = false;
            }
            cts.Dispose();
        }
    }

    private async Task ResolvePendingNamesAsync(
        IReadOnlyCollection<BatchDownloadDraft> drafts,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(4, 4);
        var resolutions = drafts.Select(async draft =>
        {
            if (draft.HasProvidedTitle)
            {
                draft.ResolvedInfo = DownloadRouteResolver.TryCreateLocalVideoInfo(
                    draft.Url,
                    out var localInfo)
                        ? localInfo
                        : new VideoInfo { Url = draft.Url, Title = draft.Title.Trim() };
                return;
            }

            await gate.WaitAsync(cancellationToken);
            draft.IsResolving = true;
            try
            {
                var info = await _videoInfoProvider.GetVideoInfoAsync(draft.Url, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                draft.ResolvedInfo = info;
                if (info is null)
                {
                    draft.ResolutionMessage = draft.HasProvidedTitle
                        ? "未读取到元数据，下载时将重试解析"
                        : "未能解析名称，请手动输入后继续";
                    return;
                }

                if (!draft.HasProvidedTitle)
                {
                    var resolvedTitle = info.Title?.Trim() ?? "";
                    draft.Title = _draftsAreExactCollectionImport
                        && !string.IsNullOrWhiteSpace(_draftCollectionTitle)
                            ? CollectionNamingService.BuildItemTitle(
                                resolvedTitle,
                                _draftCollectionTitle,
                                draft.CollectionItemIndex,
                                draft.CollectionItemCount)
                            : resolvedTitle;
                }

                if (string.IsNullOrWhiteSpace(draft.Title))
                    draft.ResolutionMessage = "未能解析名称，请手动输入后继续";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                draft.ResolutionMessage = draft.HasProvidedTitle
                    ? "元数据解析失败，下载时将重试解析"
                    : "名称解析失败，请手动输入后继续";
            }
            finally
            {
                draft.IsResolving = false;
                gate.Release();
            }
        });

        await Task.WhenAll(resolutions);
    }

    [RelayCommand(CanExecute = nameof(CanStartBatchDownload))]
    private async Task StartBatchDownload()
    {
        if (!CanStartBatchDownload())
            return;

        var knownUrls = new HashSet<string>(
            _downloadManager.Tasks.Select(task => task.Url),
            StringComparer.OrdinalIgnoreCase);
        var confirmedItems = PendingItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Title) && knownUrls.Add(item.Url))
            .Select(item => new ConfirmedBatchItem(
                item.Url,
                item.Title.Trim(),
                item.ResolvedInfo,
                item.CollectionItemIndex,
                item.CollectionItemCount))
            .ToList();
        var urls = confirmedItems.Select(item => item.Url).ToList();
        if (urls.Count == 0)
        {
            RequestShowNotification?.Invoke("没有新增任务：这些链接已经在下载队列中", false);
            return;
        }

        var draftsAreExactCollectionImport = _draftsAreExactCollectionImport;
        var draftCollectionTitle = _draftCollectionTitle;
        IsDownloading = true;
        var enqueuedCount = 0;
        var existingCollection = SelectedCollectionFolder;
        var requestedDownloadDirectory = DownloadDirectory;
        try
        {

            if (_loadDownloadHistoryAsync is not null && _duplicateDetector is not null)
            {
                var history = await _loadDownloadHistoryAsync();
                var duplicateUrls = urls
                    .Where(url => _duplicateDetector.Detect(url, history).IsDuplicate)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (duplicateUrls.Count > 0
                    && ConfirmFunc?.Invoke(
                        $"发现 {duplicateUrls.Count} 个链接已在历史或本地文件中。\n是否仍然全部重新下载？选择“否”将跳过重复项。",
                        "批量重复下载确认") != true)
                {
                    confirmedItems = confirmedItems.Where(x => !duplicateUrls.Contains(x.Url)).ToList();
                    urls = confirmedItems.Select(x => x.Url).ToList();
                    if (urls.Count == 0)
                    {
                        RequestShowNotification?.Invoke("已跳过全部重复链接，没有新增任务。", true);
                        return;
                    }
                }
            }

            if (existingCollection is not null && !Directory.Exists(existingCollection.Directory))
            {
                RequestShowNotification?.Invoke("所选合集文件夹已不存在，请刷新列表或重新选择。", false);
                await RefreshExistingCollectionFolders();
                return;
            }

            var isReusingExistingCollection = existingCollection is not null;
            var collectionTitle = draftsAreExactCollectionImport
                && !string.IsNullOrWhiteSpace(draftCollectionTitle)
                    ? draftCollectionTitle
                    : existingCollection?.Name ?? "";
            var batch = existingCollection is null
                ? null
                : BatchDownloadOrganizer.ReuseExisting(
                    existingCollection.Directory,
                    existingCollection.BatchId,
                    existingCollection.Name,
                    collectionTitle);
            var outputDirectory = batch?.Directory ?? requestedDownloadDirectory;

            var preflight = _preflightService.Check(outputDirectory);
            if (!preflight.CanProceed)
            {
                RequestShowNotification?.Invoke(preflight.BlockingMessage, false);
                return;
            }
            outputDirectory = preflight.OutputDirectory;
            foreach (var warning in preflight.Issues.Where(issue =>
                         issue.Severity == DownloadPreflightSeverity.Warning))
            {
                RequestShowNotification?.Invoke(warning.Message, false);
            }

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

            for (var index = 0; index < confirmedItems.Count; index++)
            {
                var item = confirmedItems[index];
                // History does not persist collection indexes, so manual appends stay unnumbered
                // instead of guessing a next index from the number of completed history rows.
                var collectionItemIndex = draftsAreExactCollectionImport
                    ? item.CollectionItemIndex
                    : existingCollection is null
                        ? index + 1
                        : 0;
                var collectionItemCount = batch is null
                    ? 0
                    : draftsAreExactCollectionImport
                        ? item.CollectionItemCount
                        : existingCollection is null
                            ? urls.Count
                            : 0;
                var task = new DownloadTask
                {
                    Url = item.Url,
                    Title = item.Title.Trim(),
                    Format = format,
                    Quality = quality,
                    OutputDirectory = outputDirectory,
                    BatchId = batch?.Id ?? "",
                    BatchName = batch?.Name ?? "",
                    BatchDirectory = batch?.Directory ?? "",
                    CollectionTitle = batch?.CollectionTitle ?? "",
                    CollectionItemIndex = batch is null ? 0 : collectionItemIndex,
                    CollectionItemCount = collectionItemCount
                };
                var resolvedInfo = item.ResolvedInfo ?? new VideoInfo
                {
                    Url = item.Url,
                    Title = item.Title.Trim()
                };
                await _downloadManager.EnqueueAsync(task, resolvedInfo);
                enqueuedCount++;
            }

            if (isReusingExistingCollection)
            {
                RequestShowNotification?.Invoke(
                    $"已将 {urls.Count} 个任务加入合集：{batch!.Name}",
                    true);
            }
            else
            {
                RequestShowNotification?.Invoke($"已加入 {urls.Count} 个下载任务", true);
            }

            _suppressDraftInvalidation = true;
            try
            {
                UrlsText = "";
            }
            finally
            {
                _suppressDraftInvalidation = false;
            }
            ResetNameConfirmation();
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

    private bool CanResolveBatchNames()
        => LinkCount > 0
           && IsBatchInputStep
           && !IsDownloading
           && !IsResolvingNames
           && !IsImportingPlaylist
           && !IsLoadingCollectionFolders;

    private bool CanStartBatchDownload()
        => IsNameConfirmationStep
           && !IsResolvingNames
           && !IsDownloading
           && !IsImportingPlaylist
           && PendingItems.Count > 0
           && PendingItems.All(item => !string.IsNullOrWhiteSpace(item.Title));

    [RelayCommand(CanExecute = nameof(CanEditBatchInput))]
    private void EditBatchInput()
        => ResetNameConfirmation();

    private bool CanEditBatchInput()
        => IsNameConfirmationStep && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanRemovePendingItem))]
    private void RemovePendingItem(BatchDownloadDraft? item)
    {
        if (item is null || !CanEditPendingItems)
            return;

        PendingItems.Remove(item);
        if (PendingItems.Count == 0)
            ResetNameConfirmation();
    }

    private bool CanRemovePendingItem(BatchDownloadDraft? item)
        => item is not null && CanEditPendingItems;

    private void ResetNameConfirmation()
    {
        _nameResolutionCts?.Cancel();
        _nameResolutionCts = null;
        foreach (var item in _trackedPendingItems)
            item.PropertyChanged -= OnPendingItemPropertyChanged;
        _trackedPendingItems.Clear();
        PendingItems.Clear();
        _draftsAreExactCollectionImport = false;
        _draftCollectionTitle = "";
        IsResolvingNames = false;
        IsNameConfirmationStep = false;
        StartBatchDownloadCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanImportPlaylist))]
    private async Task ImportPlaylist()
    {
        if (!CanImportPlaylist())
            return;

        IsImportingPlaylist = true;
        try
        {
            var sourceUrl = PlaylistUrl.Trim();
            var playlist = await _getPlaylistInfoAsync(sourceUrl, CancellationToken.None);
            if (ApplyPlaylistImport(playlist))
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
        => !string.IsNullOrWhiteSpace(PlaylistUrl)
           && !IsImportingPlaylist
           && IsBatchInputStep
           && !IsResolvingNames
           && !IsDownloading;

    internal bool ApplyPlaylistImport(PlaylistInfo playlist)
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
        if (!IsBatchInputStep || IsResolvingNames || IsDownloading)
            return false;

        UrlsText = string.Join("\n", urls);
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
                or DownloadStatus.Paused
                or DownloadStatus.Scheduled)
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
