using System.Windows.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyGet.Models;
using EasyGet.Services;
using System.ComponentModel;
using System.Reflection;

namespace EasyGet.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly EnvironmentService _envService;
    private readonly DownloadManager _downloadManager;
    private readonly ConfigService? _configService;
    private readonly FirstRunReadinessService? _readinessService;
    private readonly LongRunningSessionService? _longRunningSession;
    private readonly FailureRecoveryAdvisor _failureRecoveryAdvisor;
    private readonly TrayIconService? _trayIconService;
    private readonly BackgroundUpdateCoordinator? _backgroundUpdateCoordinator;

    [ObservableProperty] private ObservableObject? _currentPage;
    [ObservableProperty] private int _selectedNavIndex;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarWidth))]
    private bool _isCompactLayout;

    [ObservableProperty] private TaskbarItemProgressState _taskbarState = TaskbarItemProgressState.None;
    [ObservableProperty] private double _taskbarValue;

    public System.Collections.ObjectModel.ObservableCollection<NotificationItem> Notifications { get; } = [];

    public DownloadViewModel DownloadVM { get; }
    public BatchDownloadViewModel BatchDownloadVM { get; }
    public HistoryViewModel HistoryVM { get; }
    public SettingsViewModel SettingsVM { get; }

    public string AppVersion { get; } = $"v{GetAssemblyVersion()}";

    public double SidebarWidth => IsCompactLayout ? 56 : 216;
    public int RunningTaskCount => _downloadManager.Tasks.Count(task =>
        task.Status is DownloadStatus.Resolving or DownloadStatus.Downloading or DownloadStatus.Merging);
    public int WaitingTaskCount => _downloadManager.Tasks.Count(task => task.Status == DownloadStatus.Waiting);
    public int ScheduledTaskCount => _downloadManager.Tasks.Count(task => task.Status == DownloadStatus.Scheduled);
    public int FailedTaskCount => _downloadManager.Tasks.Count(task => task.Status == DownloadStatus.Failed);
    public int QueueTaskCount => _downloadManager.Tasks.Count;
    public bool HasQueueBadge => QueueTaskCount > 0;
    public string TaskStatusText => $"{RunningTaskCount} 进行中 · {WaitingTaskCount} 等待 · {ScheduledTaskCount} 计划 · {FailedTaskCount} 失败";
    public string AggregateSpeedText => $"↓ {ByteSizeFormatter.FormatClampZero((long)_downloadManager.Tasks
        .Where(task => task.Status == DownloadStatus.Downloading)
        .Sum(task => double.IsFinite(task.Speed) ? Math.Max(0, task.Speed) : 0))}/s";
    public string DiskStatusText => HistoryVM.StorageStatusText;
    public bool IsEngineReady => SettingsVM.YtDlpFound && SettingsVM.FfmpegFound;
    public string EngineVersionText
    {
        get
        {
            var ytDlp = SettingsVM.YtDlpFound && !string.IsNullOrWhiteSpace(SettingsVM.YtDlpVersion)
                ? $"yt-dlp {SettingsVM.YtDlpVersion}"
                : "yt-dlp 未就绪";
            var ffmpeg = SettingsVM.FfmpegFound
                ? $"ffmpeg {NormalizeToolVersion(SettingsVM.FfmpegVersion)}"
                : "ffmpeg 未就绪";
            return $"{ytDlp} · {ffmpeg}";
        }
    }

    public string CurrentPageTitle => SelectedNavIndex switch
    {
        0 => "单个视频下载",
        1 => "批量下载",
        2 => "下载历史",
        3 => "设置中心",
        _ => "EasyGet"
    };

    public string ToolStatusText => IsEngineReady
        ? "下载工具已就绪"
        : "下载工具未就绪";

    public MainViewModel(
        EnvironmentService envService,
        DownloadManager downloadManager,
        DownloadViewModel downloadVm,
        BatchDownloadViewModel batchDownloadVm,
        HistoryViewModel historyVm,
        SettingsViewModel settingsVm,
        ConfigService? configService = null,
        FirstRunReadinessService? readinessService = null,
        LongRunningSessionService? longRunningSession = null,
        FailureRecoveryAdvisor? failureRecoveryAdvisor = null,
        TrayIconService? trayIconService = null,
        BackgroundUpdateCoordinator? backgroundUpdateCoordinator = null)
    {
        _envService = envService;
        _downloadManager = downloadManager;
        _configService = configService;
        _readinessService = readinessService;
        _longRunningSession = longRunningSession;
        _failureRecoveryAdvisor = failureRecoveryAdvisor ?? new FailureRecoveryAdvisor();
        _trayIconService = trayIconService;
        _backgroundUpdateCoordinator = backgroundUpdateCoordinator;

        DownloadVM = downloadVm;
        BatchDownloadVM = batchDownloadVm;
        HistoryVM = historyVm;
        SettingsVM = settingsVm;

        CurrentPage = DownloadVM;
        SelectedNavIndex = 0;

        _downloadManager.TaskFinished += OnTaskFinished;
        SettingsVM.PropertyChanged += OnSettingsViewModelPropertyChanged;
        SettingsVM.SettingsSaved += OnSettingsSaved;
        HistoryVM.PropertyChanged += OnHistoryViewModelPropertyChanged;

        _downloadManager.Tasks.CollectionChanged += OnTasksCollectionChanged;
        foreach (var task in _downloadManager.Tasks)
        {
            task.PropertyChanged += OnTaskPropertyChanged;
        }

        BatchDownloadVM.RequestShowNotification += (msg, isSuccess) =>
            ShowToast(msg, isSuccess, isSuccess ? null : "查看队列", isSuccess ? null : () => Navigate("batch"));
        DownloadVM.RequestShowNotification += (msg, isSuccess) =>
            ShowToast(msg, isSuccess);
        HistoryVM.RequestShowNotification += (msg, isSuccess) =>
            ShowToast(msg, isSuccess, isSuccess ? null : "查看历史", isSuccess ? null : () => Navigate("history"));
        DownloadVM.ClipboardLinkDetected += OnClipboardLinkDetected;
    }

    public void ShowToast(string message, bool isSuccess, string? actionLabel = null, Action? recoveryAction = null)
        => ShowToast(
            message,
            isSuccess ? NotificationKind.Success : NotificationKind.Failure,
            actionLabel,
            recoveryAction);

    public void ShowInfoToast(string message, string? actionLabel = null, Action? action = null)
        => ShowToast(message, NotificationKind.Info, actionLabel, action);

    private void ShowToast(
        string message,
        NotificationKind kind,
        string? actionLabel = null,
        Action? recoveryAction = null)
    {
        var action = new Action(() =>
        {
            if (Notifications.Count >= 3)
            {
                var oldest = Notifications.FirstOrDefault();
                if (oldest != null)
                {
                    oldest.Close();
                }
            }

            var item = new NotificationItem(message, kind, actionLabel, recoveryAction);
            item.Expired += OnNotificationExpired;
            item.Closed += OnNotificationClosed;
            Notifications.Add(item);
        });

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    private void OnNotificationExpired(NotificationItem item)
    {
        var action = new Action(() => RemoveNotification(item));
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    private void OnNotificationClosed(NotificationItem item)
    {
        var action = new Action(() => RemoveNotification(item));
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    private void RemoveNotification(NotificationItem item)
    {
        item.Expired -= OnNotificationExpired;
        item.Closed -= OnNotificationClosed;
        Notifications.Remove(item);
    }

    private void OnSettingsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.FfmpegFound)
            && SettingsVM.FfmpegFound)
        {
            HistoryVM.RetryLocalThumbnails();
        }

        if (e.PropertyName is nameof(SettingsViewModel.YtDlpFound)
            or nameof(SettingsViewModel.FfmpegFound)
            or nameof(SettingsViewModel.YtDlpVersion)
            or nameof(SettingsViewModel.FfmpegVersion))
        {
            OnPropertyChanged(nameof(ToolStatusText));
            OnPropertyChanged(nameof(IsEngineReady));
            OnPropertyChanged(nameof(EngineVersionText));
        }
    }

    private void OnClipboardLinkDetected(string url)
    {
        ShowInfoToast(
            "检测到新的媒体链接，可立即解析。",
            "立即解析",
            () => ParseClipboardLink(url));
    }

    private void ParseClipboardLink(string url)
    {
        DownloadVM.Url = url;
        Navigate("download");
        if (DownloadVM.ParseCommand.CanExecute(null))
            DownloadVM.ParseCommand.Execute(null);
    }

    private void OnHistoryViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HistoryViewModel.StorageStatusText))
            OnPropertyChanged(nameof(DiskStatusText));
    }

    private void OnSettingsSaved()
    {
        DownloadVM.RefreshRuntimeConfigDisplay();
        BatchDownloadVM.RefreshRuntimeConfigDisplay();
        HistoryVM.RefreshStorageStatus();
        UpdateLongRunningSession();
    }

    private void OnTaskFinished(DownloadTask task)
    {
        var title = string.IsNullOrEmpty(task.Title) ? task.Url : task.Title;
        switch (task.Status)
        {
            case DownloadStatus.Completed:
                ShowToast($"下载完成: {title}", true, "查看历史", () => Navigate("history"));
                ShowSystemNotification("下载完成", title, false);
                break;
            case DownloadStatus.Failed:
                var advice = _failureRecoveryAdvisor.Advise(task.ErrorMessage);
                var (actionLabel, action) = CreateFailureRecoveryAction(task, advice);
                ShowToast(
                    $"下载失败: {title}\n{advice.UserMessage}",
                    false,
                    actionLabel,
                    action);
                ShowSystemNotification("下载失败", advice.UserMessage, true);
                break;
            case DownloadStatus.Cancelled:
                ShowInfoToast(
                    $"已取消: {title}",
                    "查看队列",
                    () => Navigate("batch"));
                break;
        }
    }

    private void ShowSystemNotification(string title, string message, bool isError)
    {
        if (_trayIconService is null || _configService?.Config.SystemNotificationsEnabled == false)
            return;

        try
        {
            _trayIconService.ShowNotification(title, message, isError);
        }
        catch
        {
        }
    }

    private (string Label, Action Action) CreateFailureRecoveryAction(
        DownloadTask task,
        FailureRecoveryAdvice advice)
        => advice.SuggestedActionKey switch
        {
            FailureRecoveryActionKeys.OpenAccountSettings => ("重新登录", () => Navigate("settings")),
            FailureRecoveryActionKeys.OpenProxySettings => ("检查代理", () => Navigate("settings")),
            FailureRecoveryActionKeys.RepairTools => ("修复组件", () => Navigate("settings")),
            FailureRecoveryActionKeys.ChooseOutputFolder => ("更换目录", () => Navigate("download")),
            FailureRecoveryActionKeys.Retry or FailureRecoveryActionKeys.RetryLater =>
                ("重新尝试", () => _ = _downloadManager.RetryAsync(task.Id)),
            _ => ("查看队列", () => Navigate("batch"))
        };

    internal static string BuildTaskFailureMessage(DownloadTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var title = string.IsNullOrEmpty(task.Title) ? task.Url : task.Title;
        return string.IsNullOrWhiteSpace(task.ErrorMessage)
            ? $"下载失败: {title}"
            : $"下载失败: {title}\n{task.ErrorMessage.Trim()}";
    }

    [RelayCommand]
    private void DismissNotification()
    {
        var action = new Action(() =>
        {
            var list = Notifications.ToList();
            foreach (var item in list)
            {
                item.Close();
            }
            Notifications.Clear();
        });

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        (CurrentPage, SelectedNavIndex) = page switch
        {
            "download" => ((ObservableObject)DownloadVM, 0),
            "batch" => (BatchDownloadVM, 1),
            "history" => (HistoryVM, 2),
            "settings" => (SettingsVM, 3),
            _ => (DownloadVM, 0)
        };
    }

    partial void OnSelectedNavIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentPageTitle));
    }

    public async Task InitializeAsync()
    {
        SettingsVM.Initialize();
        DownloadVM.Initialize();
        HistoryVM.RefreshStorageStatus();
        await HistoryVM.EnsureHistoryLoadedAsync();
        await DownloadVM.InitializeExistingCollectionFoldersAsync();
        if (!ReferenceEquals(
                DownloadVM.ExistingCollectionFolders,
                BatchDownloadVM.ExistingCollectionFolders))
        {
            await BatchDownloadVM.InitializeAsync();
        }

        StatusMessage = "正在检查运行环境...";
        var report = _readinessService is null || _configService is null
            ? null
            : await _readinessService.CheckAsync(_configService.Config.DefaultDownloadPath);
        var status = report?.Environment ?? await _envService.CheckEnvironmentAsync();

        if (report is not null && !report.IsReady)
        {
            StatusMessage = report.Summary;
            if (report.MissingTools.Count > 0)
            {
                ShowInfoToast(
                    $"首次使用需要安装：{string.Join("、", report.MissingTools)}",
                    "安装并继续",
                    () => _ = InstallRequiredComponentsAsync());
            }
            else
                ShowToast(report.Summary, false, "前往设置", () => Navigate("settings"));
        }
        else if (!status.IsReady)
        {
            StatusMessage = "环境未就绪，请在设置页安装缺失组件。";
            ShowToast(StatusMessage, false, "前往设置", () => Navigate("settings"));
        }
        else if (_configService is not null && !_configService.Config.FirstRunCompleted)
        {
            _configService.Config.FirstRunCompleted = true;
            _ = await _configService.SaveAsync();
        }

        SettingsVM.RefreshEnvironmentStatus();

        StatusMessage = status.IsReady
            ? "Ready"
            : "环境未就绪，请检查设置。";
        _ = CheckForBackgroundUpdateAsync();
    }

    private async Task CheckForBackgroundUpdateAsync()
    {
        if (_backgroundUpdateCoordinator is null)
            return;

        try
        {
            var update = await _backgroundUpdateCoordinator.CheckIfDueAsync();
            if (update?.IsUpdateAvailable == true)
            {
                ShowInfoToast(
                    $"发现 EasyGet v{update.LatestVersion}",
                    "查看更新",
                    () => Navigate("settings"));
            }
        }
        catch
        {
            // Background checks stay silent; manual checks expose detailed errors.
        }
    }

    private async Task InstallRequiredComponentsAsync()
    {
        StatusMessage = "正在安装运行组件...";
        try
        {
            var status = await _envService.InstallMissingToolsAsync(
                new Progress<string>(message => StatusMessage = message));
            SettingsVM.RefreshEnvironmentStatus();
            if (!status.IsReady)
            {
                ShowToast("运行组件安装未完成，请检查网络后重试。", false, "前往设置", () => Navigate("settings"));
                return;
            }

            if (_configService is not null)
            {
                _configService.Config.FirstRunCompleted = true;
                _ = await _configService.SaveAsync();
            }
            StatusMessage = "Ready";
            ShowToast("运行组件安装完成，可以开始下载。", true);
        }
        catch (Exception ex)
        {
            StatusMessage = "环境安装失败，请在设置页重试或手动安装。";
            ShowToast($"环境安装失败: {ex.Message}", false, "前往设置", () => Navigate("settings"));
        }
    }

    private void OnTasksCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (DownloadTask task in e.NewItems)
            {
                task.PropertyChanged += OnTaskPropertyChanged;
            }
        }
        if (e.OldItems != null)
        {
            foreach (DownloadTask task in e.OldItems)
            {
                task.PropertyChanged -= OnTaskPropertyChanged;
            }
        }
        UpdateTaskbarProgress();
        NotifyTaskStatusChanged();
    }

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadTask.Progress) or nameof(DownloadTask.Status) or nameof(DownloadTask.Speed))
        {
            var app = System.Windows.Application.Current;
            if (app is not null)
            {
                app.Dispatcher.Invoke(() =>
                {
                    UpdateTaskbarProgress();
                    NotifyTaskStatusChanged();
                });
            }
            else
            {
                UpdateTaskbarProgress();
                NotifyTaskStatusChanged();
            }
        }
    }

    [RelayCommand]
    private void RedownloadHistoryItem(DownloadHistory? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Url))
            return;

        DownloadVM.Url = item.Url;
        CurrentPage = DownloadVM;
        SelectedNavIndex = 0;
        if (DownloadVM.ParseCommand.CanExecute(null))
            DownloadVM.ParseCommand.Execute(null);
    }

    private void NotifyTaskStatusChanged()
    {
        OnPropertyChanged(nameof(RunningTaskCount));
        OnPropertyChanged(nameof(WaitingTaskCount));
        OnPropertyChanged(nameof(ScheduledTaskCount));
        OnPropertyChanged(nameof(FailedTaskCount));
        OnPropertyChanged(nameof(QueueTaskCount));
        OnPropertyChanged(nameof(HasQueueBadge));
        OnPropertyChanged(nameof(TaskStatusText));
        OnPropertyChanged(nameof(AggregateSpeedText));
        UpdateLongRunningSession();
    }

    private void UpdateLongRunningSession()
    {
        if (_longRunningSession is not null)
        {
            try
            {
                var shouldPreventSleep = _configService?.Config.PreventSleepDuringDownloads != false
                    && RunningTaskCount > 0;
                _longRunningSession.SetActive(shouldPreventSleep);
            }
            catch
            {
                // Power-session integration must never interrupt downloads.
            }
        }
    }

    private void UpdateTaskbarProgress()
    {
        var activeTasks = _downloadManager.Tasks
            .Where(t => t.Status is DownloadStatus.Waiting or DownloadStatus.Resolving or DownloadStatus.Downloading or DownloadStatus.Merging)
            .ToList();

        var failedTasks = _downloadManager.Tasks
            .Where(t => t.Status == DownloadStatus.Failed)
            .ToList();

        if (activeTasks.Count > 0)
        {
            if (failedTasks.Count > 0)
            {
                TaskbarState = TaskbarItemProgressState.Error;
            }
            else
            {
                TaskbarState = TaskbarItemProgressState.Normal;
            }

            double totalProgress = activeTasks.Sum(t => t.Progress);
            TaskbarValue = totalProgress / (activeTasks.Count * 100.0);
        }
        else
        {
            TaskbarState = TaskbarItemProgressState.None;
            TaskbarValue = 0.0;
        }
    }

    private static string GetAssemblyVersion()
    {
        var version = typeof(MainViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? typeof(MainViewModel).Assembly.GetName().Version?.ToString()
            ?? "1.0.0";

        var metadataIndex = version.IndexOf('+');
        if (metadataIndex >= 0)
            version = version[..metadataIndex];

        return version;
    }

    private static string NormalizeToolVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "就绪";

        var firstLine = version.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? "就绪";
        return firstLine.Length <= 24 ? firstLine : firstLine[..24];
    }
}
