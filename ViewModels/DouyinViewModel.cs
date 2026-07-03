using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
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

    public IEnumerable<DownloadTask> DouyinTasks => DouyinTaskItems;
    public ObservableCollection<DownloadTask> DouyinTaskItems { get; } = [];
    public ObservableCollection<DownloadHistory> DouyinHistoryItems { get; } = [];
    public ObservableCollection<DownloadHistory> DouyinManifestSummaryItems { get; } = [];

    public int DouyinTaskCount => DouyinTaskItems.Count;

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
            NotifyDouyinTaskStateChanged();
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
        OnPropertyChanged(nameof(ActiveDouyinTaskCount));
        OnPropertyChanged(nameof(CompletedDouyinTaskCount));
        OnPropertyChanged(nameof(FailedDouyinTaskCount));
    }

    private void SyncDouyinTaskItems()
    {
        DouyinTaskItems.Clear();
        foreach (var task in _downloadManager.Tasks.Where(IsDouyinTask))
        {
            DouyinTaskItems.Add(task);
        }

        OnPropertyChanged(nameof(DouyinTasks));
        OnPropertyChanged(nameof(DouyinTaskItems));
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
        return DouyinTaskItems.Count(task => predicate is null || predicate(task));
    }

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
