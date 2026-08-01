using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly ExistingCollectionFolderStore _collectionFolderStore;
    private readonly Action<ProcessStartInfo> _startProcess;
    private readonly Func<string?> _readClipboardText;
    private readonly Func<string, string?> _selectDirectory;
    private CancellationTokenSource? _parseCts;
    private CancellationTokenSource? _downloadPreparationCts;
    private int _parseRequestId;
    private int _inputRevision;
    private string _downloadRootDirectory = "";
    private string? _selectedCollectionDirectoryBeforeRefresh;
    private bool _applyingSharedDestination;
    private bool _isRefreshingDestinationOptions;
    private Task<bool> _destinationPersistenceTask = Task.FromResult(true);

    // 输入
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _selectedFormat = "mp4";
    [ObservableProperty] private string _selectedQuality = "best";
    [ObservableProperty] private string _selectedSubtitle = "none";
    [ObservableProperty] private SourceFormatChoice? _selectedSourceFormat;
    [ObservableProperty] private string _downloadDirectory = "";
    [ObservableProperty]
    private ExistingCollectionFolder? _selectedCollectionFolder;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditDownloadDestination))]
    [NotifyPropertyChangedFor(nameof(CanSelectExistingCollectionFolder))]
    private bool _isPreparingDownload;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditDownloadDestination))]
    [NotifyPropertyChangedFor(nameof(CanSelectExistingCollectionFolder))]
    private bool _isDownloading;

    // 日志
    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<SourceFormatChoice> SourceFormatOptions { get; } = [];
    public ReadOnlyObservableCollection<ExistingCollectionFolder> ExistingCollectionFolders
        => _collectionFolderStore.Folders;
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
    public bool IsLoadingCollectionFolders => _collectionFolderStore.IsLoading;
    public bool CanEditDownloadDestination
        => !IsDownloading && !IsPreparingDownload && !IsLoadingCollectionFolders;
    public bool CanSelectExistingCollectionFolder
        => _collectionFolderStore.HasFolders && CanEditDownloadDestination;
    public string ExistingCollectionFolderPlaceholder
        => $"临时下载 · {_downloadRootDirectory}";

    public event Action<string, bool>? RequestShowNotification;

    public DownloadViewModel(
        DownloadManager downloadManager,
        ConfigService configService,
        IVideoInfoProvider videoInfoProvider,
        DownloadPreflightService? preflightService = null,
        HistoryService? historyService = null,
        DownloadDuplicateDetector? duplicateDetector = null,
        ExistingCollectionFolderStore? collectionFolderStore = null)
        : this(
            downloadManager,
            configService,
            videoInfoProvider,
            StartProcess,
            null,
            preflightService,
            historyService,
            duplicateDetector,
            collectionFolderStore)
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
        DownloadDuplicateDetector? duplicateDetector = null,
        ExistingCollectionFolderStore? collectionFolderStore = null,
        Func<string, string?>? selectDirectory = null)
    {
        _downloadManager = downloadManager;
        _configService = configService;
        _videoInfoProvider = videoInfoProvider;
        _preflightService = preflightService ?? new DownloadPreflightService();
        _historyService = historyService;
        _duplicateDetector = duplicateDetector;
        _collectionFolderStore = collectionFolderStore
            ?? new ExistingCollectionFolderStore(historyService, configService);
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
        CancelDownloadPreparation();
        _inputRevision++;
        PreviewInfo = null;
        CustomFileName = "";
        ParseErrorMessage = "";
        UrlError = null;
        DetachCurrentTask();
        PageState = DownloadPageState.Idle;
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

    partial void OnIsDownloadingChanged(bool value)
        => NotifyDestinationCommandsCanExecuteChanged();

    partial void OnIsPreparingDownloadChanged(bool value)
        => NotifyDestinationCommandsCanExecuteChanged();

    partial void OnCurrentTaskChanged(DownloadTask? value)
    {
        if (value is null)
            IsDownloading = false;
    }

    /// <summary>
    /// 初始化默认值
    /// </summary>
    public void Initialize()
    {
        var config = _configService.Config;
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
        OnSharedDefaultDownloadPathChanged(config.DefaultDownloadPath);
        OnSharedSelectedCollectionDirectoryChanged(config.SelectedCollectionDirectory);
        ProxyStatusText = DescribeProxyStatus(config);
        ConcurrentFragmentsText = DescribeConcurrentFragments(config);
    }

    public Task InitializeExistingCollectionFoldersAsync()
        => LoadExistingCollectionFoldersAsync(forceRefresh: false);

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
        => CanEditDownloadDestination;

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
                OnPropertyChanged(nameof(CanEditDownloadDestination));
                OnPropertyChanged(nameof(CanSelectExistingCollectionFolder));
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

    private void NotifyDestinationCommandsCanExecuteChanged()
    {
        BrowseDirectoryCommand.NotifyCanExecuteChanged();
        RefreshExistingCollectionFoldersCommand.NotifyCanExecuteChanged();
        ClearSelectedCollectionFolderCommand.NotifyCanExecuteChanged();
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
        CancelDownloadPreparation();
        _inputRevision++;
        var requestId = ++_parseRequestId;
        using var cts = new CancellationTokenSource();
        _parseCts = cts;
        PreviewInfo = null;
        CustomFileName = "";
        ParseErrorMessage = "";
        DetachCurrentTask();
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
        if (IsLoadingCollectionFolders)
        {
            UrlError = "正在读取已有合集，请稍后再开始下载。";
            return;
        }

        if (IsDownloading || IsPreparingDownload)
        {
            UrlError = "当前任务正在准备或进行中。如需同时建立多个下载任务，请使用左侧“批量下载”功能。";
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

        var previewInfo = PreviewInfo;
        if (previewInfo is null)
        {
            UrlError = "请先解析链接，再开始下载。";
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

        var preparation = new DownloadPreparationSnapshot(
            _inputRevision,
            cleanUrl,
            previewInfo,
            CustomFileName,
            SelectedFormat,
            SelectedQuality,
            SelectedSourceFormat?.Selector ?? "",
            SelectedSourceFormat?.ExpectedBytes ?? 0,
            SelectedSubtitle,
            DownloadDirectory,
            SelectedCollectionFolder);
        CancelDownloadPreparation();
        var preparationCts = new CancellationTokenSource();
        _downloadPreparationCts = preparationCts;
        IsPreparingDownload = true;
        try
        {
            await PrepareAndEnqueueDownloadAsync(
                preparation,
                scheduledStartUtc,
                preparationCts.Token);
        }
        catch (OperationCanceledException) when (preparationCts.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_downloadPreparationCts, preparationCts))
            {
                _downloadPreparationCts = null;
                IsPreparingDownload = false;
            }

            preparationCts.Dispose();
        }
    }

    private async Task PrepareAndEnqueueDownloadAsync(
        DownloadPreparationSnapshot preparation,
        DateTimeOffset? scheduledStartUtc,
        CancellationToken cancellationToken)
    {
        var existingCollection = preparation.ExistingCollection;
        var requestedDownloadDirectory = preparation.DownloadDirectory;
        DownloadBatchContext? batch = null;
        if (_historyService is not null && _duplicateDetector is not null)
        {
            var duplicate = await _duplicateDetector.DetectAsync(
                preparation.Url,
                _historyService,
                cancellationToken: cancellationToken);
            if (!IsCurrentPreparation(preparation, cancellationToken))
                return;

            if (duplicate.IsDuplicate)
            {
                var locationHint = string.IsNullOrWhiteSpace(duplicate.ExistingPath)
                    ? "下载历史中已有相同链接。"
                    : $"本地已有文件：{duplicate.ExistingPath}";
                if (ConfirmFunc?.Invoke($"{locationHint}\n是否仍然重新下载？", "重复下载确认") != true)
                    return;
            }
        }

        if (existingCollection is not null)
        {
            if (!Directory.Exists(existingCollection.Directory))
            {
                RequestShowNotification?.Invoke(
                    "所选合集文件夹已不存在，请刷新列表或重新选择。",
                    false);
                await LoadExistingCollectionFoldersAsync(forceRefresh: true);
                return;
            }

            try
            {
                batch = BatchDownloadOrganizer.ReuseExisting(
                    existingCollection.Directory,
                    existingCollection.BatchId,
                    existingCollection.Name,
                    existingCollection.Name);
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or NotSupportedException
                                       or PathTooLongException
                                       or DirectoryNotFoundException)
            {
                RequestShowNotification?.Invoke($"无法使用所选合集：{ex.Message}", false);
                await LoadExistingCollectionFoldersAsync(forceRefresh: true);
                return;
            }
        }

        if (!IsCurrentPreparation(preparation, cancellationToken))
            return;

        var preflight = _preflightService.Check(
            batch?.Directory ?? requestedDownloadDirectory,
            preparation.ExpectedBytes > 0
                ? preparation.ExpectedBytes
                : preparation.PreviewInfo.FileSize);
        if (!preflight.CanProceed)
        {
            UrlError = preflight.BlockingMessage;
            return;
        }

        var outputDirectory = preflight.OutputDirectory;
        foreach (var warning in preflight.Issues.Where(issue =>
                     issue.Severity == DownloadPreflightSeverity.Warning))
        {
            LogLines.Add($"[{DateTime.Now:HH:mm:ss}] 提示: {warning.Message}");
        }

        var task = new DownloadTask
        {
            Url = preparation.Url,
            Title = string.IsNullOrWhiteSpace(preparation.CustomFileName)
                ? preparation.PreviewInfo.Title
                : preparation.CustomFileName,
            Format = ParseFormat(preparation.Format),
            Quality = ParseQuality(preparation.Quality),
            SourceFormatSelector = preparation.SourceFormatSelector,
            Subtitle = ParseSubtitle(preparation.Subtitle),
            OutputDirectory = outputDirectory,
            BatchId = batch?.Id ?? "",
            BatchName = batch?.Name ?? "",
            BatchDirectory = batch?.Directory ?? "",
            CollectionTitle = batch?.CollectionTitle ?? "",
            CollectionItemIndex = 0,
            CollectionItemCount = 0,
            ScheduledStartTimeUtc = scheduledStartUtc
        };

        if (!IsCurrentPreparation(preparation, cancellationToken))
            return;

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
        try
        {
            await _downloadManager.EnqueueAsync(task, preparation.PreviewInfo);
        }
        catch (Exception)
        {
            if (CurrentTask == task && IsCurrentPreparation(preparation, cancellationToken))
            {
                DetachCurrentTask();
                PageState = DownloadPageState.Ready;
                UrlError = "启动下载任务失败，请重试。";
            }
        }
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

    private bool IsCurrentPreparation(
        DownloadPreparationSnapshot preparation,
        CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested
           && preparation.InputRevision == _inputRevision;

    private void DetachCurrentTask()
    {
        CurrentTask = null;
        IsDownloading = false;
    }

    private void CancelDownloadPreparation()
    {
        var cts = _downloadPreparationCts;
        _downloadPreparationCts = null;
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        IsPreparingDownload = false;
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

    private sealed record DownloadPreparationSnapshot(
        int InputRevision,
        string Url,
        VideoInfo PreviewInfo,
        string CustomFileName,
        string Format,
        string Quality,
        string SourceFormatSelector,
        long ExpectedBytes,
        string Subtitle,
        string DownloadDirectory,
        ExistingCollectionFolder? ExistingCollection);

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
