using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyGet.Models;
using EasyGet.Services;

namespace EasyGet.ViewModels;

/// <summary>
/// 主窗口 ViewModel — 管理导航和全局状态
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly EnvironmentService _envService;
    private readonly DownloadManager _downloadManager;
    private System.Timers.Timer? _notificationTimer;

    [ObservableProperty] private ObservableObject? _currentPage;
    [ObservableProperty] private int _selectedNavIndex;
    [ObservableProperty] private string _statusMessage = "就绪";

    // 通知
    [ObservableProperty] private bool _showNotification;
    [ObservableProperty] private string _notificationMessage = "";
    [ObservableProperty] private bool _isNotificationSuccess;

    public DownloadViewModel DownloadVM { get; }
    public BatchDownloadViewModel BatchDownloadVM { get; }
    public HistoryViewModel HistoryVM { get; }
    public SettingsViewModel SettingsVM { get; }

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

        // 默认显示下载页
        CurrentPage = DownloadVM;
        SelectedNavIndex = 0;

        // 订阅下载完成通知
        _downloadManager.TaskFinished += OnTaskFinished;
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

            if (!string.IsNullOrEmpty(NotificationMessage))
            {
                ShowNotification = true;
                _notificationTimer?.Stop();
                _notificationTimer = new System.Timers.Timer(4000) { AutoReset = false };
                _notificationTimer.Elapsed += (_, _) =>
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() => ShowNotification = false);
                };
                _notificationTimer.Start();
            }
        });
    }

    [RelayCommand]
    private void DismissNotification()
    {
        ShowNotification = false;
        _notificationTimer?.Stop();
    }

    /// <summary>
    /// 切换页面
    /// </summary>
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

    /// <summary>
    /// 应用启动时的初始化
    /// </summary>
    public async Task InitializeAsync()
    {
        // 加载配置
        await _configService.LoadAsync();

        // 检测环境
        StatusMessage = "正在检测环境...";
        var status = await _envService.CheckEnvironmentAsync();

        if (!status.IsReady)
        {
            StatusMessage = "正在安装必要工具...";
            if (!status.YtDlpFound)
                await _envService.AutoInstallYtDlpAsync(new Progress<string>(s => StatusMessage = s));
            if (!status.FfmpegFound)
                await _envService.AutoInstallFfmpegAsync(new Progress<string>(s => StatusMessage = s));
        }

        // 更新设置页的环境状态
        SettingsVM.RefreshEnvironmentStatus();

        StatusMessage = status.IsReady ? "就绪" : "环境检测完成（部分工具缺失）";
    }
}
