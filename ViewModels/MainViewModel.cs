using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyGet.Models;
using EasyGet.Services;
using System.ComponentModel;
using System.Reflection;

namespace EasyGet.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly EnvironmentService _envService;
    private readonly DownloadManager _downloadManager;
    private System.Timers.Timer? _notificationTimer;

    [ObservableProperty] private ObservableObject? _currentPage;
    [ObservableProperty] private int _selectedNavIndex;
    [ObservableProperty] private string _statusMessage = "Ready";

    [ObservableProperty] private bool _showNotification;
    [ObservableProperty] private string _notificationMessage = "";
    [ObservableProperty] private bool _isNotificationSuccess;

    public DownloadViewModel DownloadVM { get; }
    public BatchDownloadViewModel BatchDownloadVM { get; }
    public HistoryViewModel HistoryVM { get; }
    public SettingsViewModel SettingsVM { get; }

    public string AppVersion { get; } = $"v{GetAssemblyVersion()}";

    public string CurrentPageTitle => SelectedNavIndex switch
    {
        0 => "单个视频下载",
        1 => "批量下载",
        2 => "下载历史",
        3 => "设置中心",
        _ => "EasyGet"
    };

    public string ToolStatusText => SettingsVM.YtDlpFound && SettingsVM.FfmpegFound
        ? "下载工具已就绪"
        : "下载工具未就绪";

    public MainViewModel(
        ConfigService configService,
        EnvironmentService envService,
        DownloadManager downloadManager,
        DownloadViewModel downloadVm,
        BatchDownloadViewModel batchDownloadVm,
        HistoryViewModel historyVm,
        SettingsViewModel settingsVm)
    {
        _configService = configService;
        _envService = envService;
        _downloadManager = downloadManager;

        DownloadVM = downloadVm;
        BatchDownloadVM = batchDownloadVm;
        HistoryVM = historyVm;
        SettingsVM = settingsVm;

        CurrentPage = DownloadVM;
        SelectedNavIndex = 0;

        _downloadManager.TaskFinished += OnTaskFinished;
        SettingsVM.PropertyChanged += OnSettingsViewModelPropertyChanged;
        SettingsVM.SettingsSaved += OnSettingsSaved;
    }

    private void OnSettingsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.YtDlpFound) or nameof(SettingsViewModel.FfmpegFound))
            OnPropertyChanged(nameof(ToolStatusText));
    }

    private void OnSettingsSaved()
    {
        DownloadVM.RefreshRuntimeConfigDisplay();
        HistoryVM.RefreshStorageStatus();
    }

    private void OnTaskFinished(DownloadTask task)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var title = string.IsNullOrEmpty(task.Title) ? task.Url : task.Title;
            (NotificationMessage, IsNotificationSuccess) = task.Status switch
            {
                DownloadStatus.Completed => ($"下载完成: {title}", true),
                DownloadStatus.Failed => ($"下载失败: {title}", false),
                DownloadStatus.Cancelled => ($"已取消: {title}", false),
                _ => ("", false)
            };

            if (string.IsNullOrEmpty(NotificationMessage))
                return;

            ShowNotification = true;
            _notificationTimer?.Stop();
            _notificationTimer = new System.Timers.Timer(4000) { AutoReset = false };
            _notificationTimer.Elapsed += (_, _) =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() => ShowNotification = false);
            };
            _notificationTimer.Start();
        });
    }

    [RelayCommand]
    private void DismissNotification()
    {
        ShowNotification = false;
        _notificationTimer?.Stop();
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
        await _configService.LoadAsync();

        SettingsVM.Initialize();
        DownloadVM.Initialize();
        HistoryVM.RefreshStorageStatus();

        StatusMessage = "正在检查运行环境...";
        var status = await _envService.CheckEnvironmentAsync();

        if (!status.IsReady)
        {
            var missingTools = string.Join("、", EnvironmentService.GetMissingToolNames(status));
            StatusMessage = $"正在安装缺失组件: {missingTools}";
            try
            {
                status = await _envService.InstallMissingToolsAsync(new Progress<string>(s => StatusMessage = s));
            }
            catch (Exception ex)
            {
                StatusMessage = "环境安装失败，请在设置页重试或手动安装。";
                NotificationMessage = $"环境安装失败: {ex.Message}";
                IsNotificationSuccess = false;
                ShowNotification = true;
            }
        }

        SettingsVM.RefreshEnvironmentStatus();

        StatusMessage = status.IsReady
            ? "Ready"
            : "环境未就绪，请检查设置。";
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
}
