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

    public DownloadViewModel Download { get; }
    public BatchDownloadViewModel Batch { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }

    public IEnumerable<DownloadTask> DouyinTasks => _downloadManager.Tasks.Where(IsDouyinTask);
    public ObservableCollection<DownloadHistory> DouyinHistoryItems { get; } = [];

    public int DouyinTaskCount => CountDouyinTasks();

    public int ActiveDouyinTaskCount => CountDouyinTasks(task =>
        task.Status is DownloadStatus.Waiting
            or DownloadStatus.Resolving
            or DownloadStatus.Downloading
            or DownloadStatus.Merging);

    public int CompletedDouyinTaskCount => CountDouyinTasks(task =>
        task.Status == DownloadStatus.Completed);

    public int FailedDouyinTaskCount => CountDouyinTasks(task =>
        task.Status == DownloadStatus.Failed);

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
            task.PropertyChanged += OnTaskPropertyChanged;
        }

        History.HistoryItems.CollectionChanged += OnHistoryItemsCollectionChanged;
        SyncDouyinHistoryItems();
    }

    private void OnTasksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (DownloadTask task in e.NewItems)
            {
                task.PropertyChanged += OnTaskPropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (DownloadTask task in e.OldItems)
            {
                task.PropertyChanged -= OnTaskPropertyChanged;
            }
        }

        NotifyDouyinTaskStateChanged();
    }

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadTask.Status) or nameof(DownloadTask.Platform))
        {
            NotifyDouyinTaskStateChanged();
        }
    }

    private void NotifyDouyinTaskStateChanged()
    {
        OnPropertyChanged(nameof(DouyinTasks));
        OnPropertyChanged(nameof(DouyinTaskCount));
        OnPropertyChanged(nameof(ActiveDouyinTaskCount));
        OnPropertyChanged(nameof(CompletedDouyinTaskCount));
        OnPropertyChanged(nameof(FailedDouyinTaskCount));
    }

    private void OnHistoryItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncDouyinHistoryItems();
    }

    private void SyncDouyinHistoryItems()
    {
        DouyinHistoryItems.Clear();
        foreach (var item in History.HistoryItems.Where(IsDouyinHistoryItem))
        {
            DouyinHistoryItems.Add(item);
        }

        OnPropertyChanged(nameof(DouyinHistoryItems));
    }

    private int CountDouyinTasks(Func<DownloadTask, bool>? predicate = null)
    {
        return _downloadManager.Tasks.Count(task =>
            IsDouyinTask(task) && (predicate is null || predicate(task)));
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
