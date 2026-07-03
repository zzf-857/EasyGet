using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyGet.Models;
using EasyGet.Services;

namespace EasyGet.ViewModels;

public partial class DouyinViewModel : ObservableObject
{
    private readonly DownloadManager _downloadManager;
    private readonly HashSet<DownloadTask> _subscribedTasks = [];

    public DownloadViewModel Download { get; }
    public BatchDownloadViewModel Batch { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }

    public string[] DouyinTaskFilterOptions { get; } = ["全部", "进行中", "已完成", "失败", "已暂停", "已取消"];

    [ObservableProperty]
    private string _selectedDouyinTaskFilter = "全部";

    [ObservableProperty]
    private string _douyinTaskSearchKeyword = "";

    public IEnumerable<DownloadTask> DouyinTasks => DouyinTaskItems;
    public ObservableCollection<DownloadTask> DouyinTaskItems { get; } = [];
    public ObservableCollection<DownloadHistory> DouyinHistoryItems { get; } = [];
    public ObservableCollection<DownloadHistory> DouyinManifestSummaryItems { get; } = [];

    public int DouyinTaskCount => CountDouyinTasks();

    public int FilteredDouyinTaskCount => DouyinTaskItems.Count;

    public int ActiveDouyinTaskCount => CountDouyinTasks(task =>
        task.Status is DownloadStatus.Waiting
            or DownloadStatus.Resolving
            or DownloadStatus.Downloading
            or DownloadStatus.Merging);

    public int CompletedDouyinTaskCount => CountDouyinTasks(task =>
        task.Status == DownloadStatus.Completed);

    public int FailedDouyinTaskCount => CountDouyinTasks(task =>
        task.Status == DownloadStatus.Failed);

    public int DouyinManifestSummaryCount => DouyinManifestSummaryItems.Count;

    public DouyinViewModel(
        ConfigService configService,
        DownloadManager downloadManager,
        DownloadViewModel download,
        BatchDownloadViewModel batch,
        HistoryViewModel history,
        SettingsViewModel settings)
    {
        ArgumentNullException.ThrowIfNull(configService);

        _downloadManager = downloadManager;
        Download = download;
        Batch = batch;
        History = history;
        Settings = settings;

        _downloadManager.Tasks.CollectionChanged += OnTasksCollectionChanged;
        foreach (var task in _downloadManager.Tasks)
        {
            SubscribeTask(task);
        }
        SyncDouyinTaskItems();

        History.HistoryItems.CollectionChanged += OnHistoryItemsCollectionChanged;
        SyncDouyinHistoryItems();
    }

    private void OnTasksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ClearTaskSubscriptions();
            foreach (var task in _downloadManager.Tasks)
            {
                SubscribeTask(task);
            }

            SyncDouyinTaskItems();
            NotifyDouyinTaskStateChanged();
            return;
        }

        if (e.NewItems is not null)
        {
            foreach (DownloadTask task in e.NewItems)
            {
                SubscribeTask(task);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (DownloadTask task in e.OldItems)
            {
                UnsubscribeTask(task);
            }
        }

        SyncDouyinTaskItems();
        NotifyDouyinTaskStateChanged();
    }

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadTask.Platform))
        {
            SyncDouyinTaskItems();
            NotifyDouyinTaskStateChanged();
        }
        else if (e.PropertyName == nameof(DownloadTask.Status))
        {
            SyncDouyinTaskItems();
            NotifyDouyinTaskStateChanged();
        }
        else if (e.PropertyName is nameof(DownloadTask.Title) or nameof(DownloadTask.ErrorMessage))
        {
            SyncDouyinTaskItems();
        }
    }

    private void SubscribeTask(DownloadTask task)
    {
        if (_subscribedTasks.Add(task))
        {
            task.PropertyChanged += OnTaskPropertyChanged;
        }
    }

    private void UnsubscribeTask(DownloadTask task)
    {
        if (_subscribedTasks.Remove(task))
        {
            task.PropertyChanged -= OnTaskPropertyChanged;
        }
    }

    private void ClearTaskSubscriptions()
    {
        foreach (var task in _subscribedTasks.ToList())
        {
            task.PropertyChanged -= OnTaskPropertyChanged;
        }

        _subscribedTasks.Clear();
    }

    private void NotifyDouyinTaskStateChanged()
    {
        OnPropertyChanged(nameof(DouyinTasks));
        OnPropertyChanged(nameof(DouyinTaskItems));
        OnPropertyChanged(nameof(DouyinTaskCount));
        OnPropertyChanged(nameof(FilteredDouyinTaskCount));
        OnPropertyChanged(nameof(ActiveDouyinTaskCount));
        OnPropertyChanged(nameof(CompletedDouyinTaskCount));
        OnPropertyChanged(nameof(FailedDouyinTaskCount));
    }

    private void SyncDouyinTaskItems()
    {
        DouyinTaskItems.Clear();
        foreach (var task in _downloadManager.Tasks
                     .Where(IsDouyinTask)
                     .Where(MatchesTaskCenterFilter))
        {
            DouyinTaskItems.Add(task);
        }

        OnPropertyChanged(nameof(DouyinTasks));
        OnPropertyChanged(nameof(DouyinTaskItems));
        OnPropertyChanged(nameof(FilteredDouyinTaskCount));
    }

    private void OnHistoryItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncDouyinHistoryItems();
    }

    private void SyncDouyinHistoryItems()
    {
        DouyinHistoryItems.Clear();
        DouyinManifestSummaryItems.Clear();
        foreach (var item in History.HistoryItems.Where(IsDouyinHistoryItem))
        {
            DouyinHistoryItems.Add(item);
            if (!string.IsNullOrWhiteSpace(item.DouyinManifestSummaryText))
            {
                DouyinManifestSummaryItems.Add(item);
            }
        }

        OnPropertyChanged(nameof(DouyinHistoryItems));
        OnPropertyChanged(nameof(DouyinManifestSummaryItems));
        OnPropertyChanged(nameof(DouyinManifestSummaryCount));
    }

    private int CountDouyinTasks(Func<DownloadTask, bool>? predicate = null)
    {
        return _downloadManager.Tasks.Count(task =>
            IsDouyinTask(task) && (predicate is null || predicate(task)));
    }

    partial void OnSelectedDouyinTaskFilterChanged(string value)
    {
        if (!DouyinTaskFilterOptions.Contains(value, StringComparer.Ordinal))
        {
            SelectedDouyinTaskFilter = "全部";
            return;
        }

        SyncDouyinTaskItems();
    }

    partial void OnDouyinTaskSearchKeywordChanged(string value)
    {
        SyncDouyinTaskItems();
    }

    [RelayCommand]
    private void SetDouyinTaskFilter(string filter)
    {
        SelectedDouyinTaskFilter = DouyinTaskFilterOptions.Contains(filter, StringComparer.Ordinal)
            ? filter
            : "全部";
    }

    private bool MatchesTaskCenterFilter(DownloadTask task)
        => MatchesSelectedTaskStatus(task) && MatchesTaskSearchKeyword(task);

    private bool MatchesSelectedTaskStatus(DownloadTask task)
        => SelectedDouyinTaskFilter switch
        {
            "进行中" => IsActiveTaskStatus(task.Status),
            "已完成" => task.Status == DownloadStatus.Completed,
            "失败" => task.Status == DownloadStatus.Failed,
            "已暂停" => task.Status == DownloadStatus.Paused,
            "已取消" => task.Status == DownloadStatus.Cancelled,
            _ => true
        };

    private static bool IsActiveTaskStatus(DownloadStatus status)
        => status is DownloadStatus.Waiting
            or DownloadStatus.Resolving
            or DownloadStatus.Downloading
            or DownloadStatus.Merging;

    private bool MatchesTaskSearchKeyword(DownloadTask task)
    {
        var keyword = DouyinTaskSearchKeyword?.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
            return true;

        return ContainsKeyword(task.Title, keyword)
            || ContainsKeyword(task.Url, keyword)
            || ContainsKeyword(task.Platform, keyword)
            || ContainsKeyword(task.ErrorMessage, keyword);
    }

    private static bool ContainsKeyword(string value, string keyword)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private static bool IsDouyinTask(DownloadTask task)
    {
        if (task.Platform.Equals("Douyin", StringComparison.OrdinalIgnoreCase)
            || task.Platform.Equals("抖音", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsDouyinUrl(task.Url);
    }

    private static bool IsDouyinHistoryItem(DownloadHistory item)
    {
        if (item.Platform.Equals("Douyin", StringComparison.OrdinalIgnoreCase)
            || item.Platform.Equals("抖音", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsDouyinUrl(item.Url);
    }

    private static bool IsDouyinUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        return IsDomainOrSubdomain(host, "douyin.com")
            || IsDomainOrSubdomain(host, "iesdouyin.com");
    }

    private static bool IsDomainOrSubdomain(string host, string domain)
    {
        return host.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);
    }
}
