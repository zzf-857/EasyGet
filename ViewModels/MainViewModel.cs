using System.Windows.Shell;
﻿using CommunityToolkit.Mvvm.ComponentModel;
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

    [ObservableProperty] private ObservableObject? _currentPage;
    [ObservableProperty] private int _selectedNavIndex;
    [ObservableProperty] private string _statusMessage = "Ready";

    [ObservableProperty] private TaskbarItemProgressState _taskbarState = TaskbarItemProgressState.None;
    [ObservableProperty] private double _taskbarValue;

    public System.Collections.ObjectModel.ObservableCollection<NotificationItem> Notifications { get; } = [];

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

        _downloadManager.Tasks.CollectionChanged += OnTasksCollectionChanged;
        foreach (var task in _downloadManager.Tasks)
        {
            task.PropertyChanged += OnTaskPropertyChanged;
        }

        BatchDownloadVM.RequestShowNotification += (msg, isSuccess) =>
        {
            ShowToast(msg, isSuccess);
        };
    }

    public void ShowToast(string message, bool isSuccess)
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

            var item = new NotificationItem(message, isSuccess);
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
        var title = string.IsNullOrEmpty(task.Title) ? task.Url : task.Title;
        string msg = "";
        bool isSuccess = false;

        switch (task.Status)
        {
            case DownloadStatus.Completed:
                msg = $"下载完成: {title}";
                isSuccess = true;
                break;
            case DownloadStatus.Failed:
                msg = $"下载失败: {title}";
                isSuccess = false;
                break;
            case DownloadStatus.Cancelled:
                msg = $"已取消: {title}";
                isSuccess = false;
                break;
        }

        if (!string.IsNullOrEmpty(msg))
        {
            ShowToast(msg, isSuccess);
        }
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
        await _configService.LoadAsync();
        ThemeManager.ApplyTheme(_configService.Config.ThemeColor);

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
                ShowToast($"环境安装失败: {ex.Message}", false);
            }
        }

        SettingsVM.RefreshEnvironmentStatus();

        StatusMessage = status.IsReady
            ? "Ready"
            : "环境未就绪，请检查设置。";
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
    }

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadTask.Progress) or nameof(DownloadTask.Status))
        {
            var app = System.Windows.Application.Current;
            if (app is not null)
            {
                app.Dispatcher.Invoke(() => UpdateTaskbarProgress());
            }
            else
            {
                UpdateTaskbarProgress();
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
}
