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
    private const int MaxRecentAuthorItems = 8;
    private const int DefaultDouyinHotBoardLimit = 30;
    private const int DefaultDouyinSearchMax = 50;

    private readonly ConfigService _configService;
    private readonly DownloadManager _downloadManager;
    private readonly IDouyinSpecialDownloadService _douyinSpecialDownloadService;
    private readonly HashSet<DownloadTask> _subscribedTasks = [];

    public DownloadViewModel Download { get; }
    public BatchDownloadViewModel Batch { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }

    public string[] DouyinTaskFilterOptions { get; } = ["全部", "进行中", "已完成", "失败", "已暂停", "已取消"];
    public string[] DouyinArchiveTypeFilterOptions { get; } = ["全部", "视频", "图文", "音乐"];

    [ObservableProperty]
    private string _selectedDouyinTaskFilter = "全部";

    [ObservableProperty]
    private string _douyinTaskSearchKeyword = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDouyinArchiveFilterActive))]
    private string _selectedDouyinArchiveTypeFilter = "全部";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDouyinArchiveFilterActive))]
    private string _douyinArchiveSearchKeyword = "";

    [ObservableProperty]
    private string _douyinDiscoveryKeyword = "";

    [ObservableProperty]
    private int _douyinDiscoverySearchMax = DefaultDouyinSearchMax;

    [ObservableProperty]
    private string _douyinDiscoveryStatusText = "尚未加载发现结果";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDouyinDiscoveryError))]
    private string _douyinDiscoveryErrorMessage = "";

    [ObservableProperty]
    private bool _isDouyinDiscoveryLoading;

    public IEnumerable<DownloadTask> DouyinTasks => DouyinTaskItems;
    public ObservableCollection<DownloadTask> DouyinTaskItems { get; } = [];
    public ObservableCollection<DownloadHistory> DouyinHistoryItems { get; } = [];
    public ObservableCollection<DownloadHistory> DouyinManifestSummaryItems { get; } = [];
    public ObservableCollection<DouyinRecentAuthorItem> DouyinRecentAuthorItems { get; } = [];
    public ObservableCollection<DouyinDiscoveryItem> DouyinDiscoveryItems { get; } = [];

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

    public int DouyinArchiveCount => CountDouyinHistoryItems();

    public int FilteredDouyinArchiveCount => DouyinHistoryItems.Count;

    public bool HasDouyinArchiveItems => DouyinArchiveCount > 0;

    public bool HasFilteredDouyinArchiveItems => FilteredDouyinArchiveCount > 0;

    public bool HasDouyinRecentAuthorItems => DouyinRecentAuthorItems.Count > 0;

    public int DouyinDiscoveryResultCount => DouyinDiscoveryItems.Count;

    public bool HasDouyinDiscoveryItems => DouyinDiscoveryItems.Count > 0;

    public bool HasDouyinDiscoveryError => !string.IsNullOrWhiteSpace(DouyinDiscoveryErrorMessage);

    public bool IsDouyinArchiveFilterActive
        => !string.IsNullOrWhiteSpace(DouyinArchiveSearchKeyword)
           || SelectedDouyinArchiveTypeFilter != "全部";

    public DouyinViewModel(
        ConfigService configService,
        DownloadManager downloadManager,
        DownloadViewModel download,
        BatchDownloadViewModel batch,
        HistoryViewModel history,
        SettingsViewModel settings,
        IDouyinSpecialDownloadService? douyinSpecialDownloadService = null)
    {
        ArgumentNullException.ThrowIfNull(configService);

        _configService = configService;
        _downloadManager = downloadManager;
        _douyinSpecialDownloadService = douyinSpecialDownloadService ?? new DouyinSpecialDownloadService();
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
            if (MatchesArchiveFilter(item))
                DouyinHistoryItems.Add(item);

            if (!string.IsNullOrWhiteSpace(item.DouyinManifestSummaryText))
            {
                DouyinManifestSummaryItems.Add(item);
            }
        }

        OnPropertyChanged(nameof(DouyinHistoryItems));
        OnPropertyChanged(nameof(DouyinArchiveCount));
        OnPropertyChanged(nameof(FilteredDouyinArchiveCount));
        OnPropertyChanged(nameof(HasDouyinArchiveItems));
        OnPropertyChanged(nameof(HasFilteredDouyinArchiveItems));
        OnPropertyChanged(nameof(DouyinManifestSummaryItems));
        OnPropertyChanged(nameof(DouyinManifestSummaryCount));
        SyncDouyinRecentAuthorItems();
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

    partial void OnSelectedDouyinArchiveTypeFilterChanged(string value)
    {
        if (!DouyinArchiveTypeFilterOptions.Contains(value, StringComparer.Ordinal))
        {
            SelectedDouyinArchiveTypeFilter = "全部";
            return;
        }

        SyncDouyinHistoryItems();
    }

    partial void OnDouyinArchiveSearchKeywordChanged(string value)
    {
        SyncDouyinHistoryItems();
    }

    [RelayCommand]
    private void SetDouyinTaskFilter(string filter)
    {
        SelectedDouyinTaskFilter = DouyinTaskFilterOptions.Contains(filter, StringComparer.Ordinal)
            ? filter
            : "全部";
    }

    [RelayCommand]
    private void SetDouyinArchiveTypeFilter(string filter)
    {
        SelectedDouyinArchiveTypeFilter = DouyinArchiveTypeFilterOptions.Contains(filter, StringComparer.Ordinal)
            ? filter
            : "全部";
    }

    [RelayCommand]
    private void ClearDouyinArchiveFilters()
    {
        DouyinArchiveSearchKeyword = "";
        SelectedDouyinArchiveTypeFilter = "全部";
        SyncDouyinHistoryItems();
    }

    [RelayCommand]
    private void SetDouyinArchiveAuthorFilter(string authorName)
    {
        if (string.IsNullOrWhiteSpace(authorName))
            return;

        DouyinArchiveSearchKeyword = authorName.Trim();
        SelectedDouyinArchiveTypeFilter = "全部";
        SyncDouyinHistoryItems();
    }

    [RelayCommand]
    private async Task LoadDouyinWorkspace()
    {
        await History.LoadAllHistoryForWorkspace();
        SyncDouyinHistoryItems();
    }

    [RelayCommand]
    private async Task LoadDouyinHotBoard()
    {
        await RunDouyinDiscoveryAsync(
            new DouyinDiscoveryRequest(
                DouyinDiscoveryType.HotBoard,
                GetDiscoveryOutputDirectory(),
                Limit: DefaultDouyinHotBoardLimit));
    }

    [RelayCommand]
    private async Task SearchDouyinDiscovery()
    {
        var keyword = DouyinDiscoveryKeyword.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            ClearDouyinDiscoveryResults();
            DouyinDiscoveryErrorMessage = "请输入搜索关键词";
            DouyinDiscoveryStatusText = "等待关键词";
            NotifyDouyinDiscoveryStateChanged();
            return;
        }

        await RunDouyinDiscoveryAsync(
            new DouyinDiscoveryRequest(
                DouyinDiscoveryType.Search,
                GetDiscoveryOutputDirectory(),
                Keyword: keyword,
                SearchMax: Math.Max(1, DouyinDiscoverySearchMax)));
    }

    [RelayCommand]
    private async Task AddDouyinDiscoveryItemToQueue(DouyinDiscoveryItem? item)
    {
        if (item is null)
            return;

        var url = BuildDouyinDiscoveryDownloadUrl(item);
        if (string.IsNullOrWhiteSpace(url))
        {
            DouyinDiscoveryErrorMessage = "当前发现条目缺少可下载链接";
            DouyinDiscoveryStatusText = "无法加入下载队列";
            NotifyDouyinDiscoveryStateChanged();
            return;
        }

        if (_downloadManager.Tasks.Any(task => IsSameUrl(task.Url, url)))
        {
            DouyinDiscoveryErrorMessage = "";
            DouyinDiscoveryStatusText = "已跳过重复下载任务";
            NotifyDouyinDiscoveryStateChanged();
            return;
        }

        var displayTitle = BuildDouyinDiscoveryTaskTitle(item);
        var task = new DownloadTask
        {
            Url = url,
            Title = displayTitle,
            Platform = "Douyin",
            Format = _configService.Config.DefaultFormat,
            Quality = _configService.Config.DefaultQuality,
            OutputDirectory = GetDiscoveryOutputDirectory()
        };

        await _downloadManager.EnqueueAsync(task);
        if (!string.IsNullOrWhiteSpace(displayTitle))
            task.Title = displayTitle;

        DouyinDiscoveryErrorMessage = "";
        DouyinDiscoveryStatusText = $"已加入下载队列：{displayTitle}";
        NotifyDouyinTaskStateChanged();
        NotifyDouyinDiscoveryStateChanged();
    }

    private async Task RunDouyinDiscoveryAsync(DouyinDiscoveryRequest request)
    {
        IsDouyinDiscoveryLoading = true;
        DouyinDiscoveryErrorMessage = "";
        DouyinDiscoveryStatusText = "正在加载发现结果";
        ClearDouyinDiscoveryResults();

        try
        {
            var result = await _douyinSpecialDownloadService.DiscoverAsync(
                request,
                _configService.Config);

            foreach (var item in result.Items)
            {
                DouyinDiscoveryItems.Add(item);
            }

            DouyinDiscoveryStatusText = FormatDouyinDiscoveryStatus(result);
        }
        catch (OperationCanceledException)
        {
            DouyinDiscoveryErrorMessage = "抖音发现任务已取消";
            DouyinDiscoveryStatusText = "发现任务已取消";
        }
        catch (Exception ex)
        {
            DouyinDiscoveryErrorMessage = ex.Message;
            DouyinDiscoveryStatusText = "发现结果加载失败";
        }
        finally
        {
            IsDouyinDiscoveryLoading = false;
            NotifyDouyinDiscoveryStateChanged();
        }
    }

    private string GetDiscoveryOutputDirectory()
        => string.IsNullOrWhiteSpace(_configService.Config.DefaultDownloadPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : _configService.Config.DefaultDownloadPath.Trim();

    private static string FormatDouyinDiscoveryStatus(DouyinDiscoveryResult result)
    {
        var count = Math.Max(0, result.ItemCount);
        return result.DiscoveryType.Equals("search", StringComparison.OrdinalIgnoreCase)
            ? $"已加载 {count} 条搜索结果"
            : $"已加载 {count} 条热榜结果";
    }

    private void ClearDouyinDiscoveryResults()
    {
        DouyinDiscoveryItems.Clear();
        NotifyDouyinDiscoveryStateChanged();
    }

    private void NotifyDouyinDiscoveryStateChanged()
    {
        OnPropertyChanged(nameof(DouyinDiscoveryItems));
        OnPropertyChanged(nameof(DouyinDiscoveryResultCount));
        OnPropertyChanged(nameof(HasDouyinDiscoveryItems));
        OnPropertyChanged(nameof(HasDouyinDiscoveryError));
    }

    private static string BuildDouyinDiscoveryDownloadUrl(DouyinDiscoveryItem item)
    {
        var url = item.Url.Trim();
        if (!string.IsNullOrWhiteSpace(url))
            return url;

        var awemeId = item.AwemeId.Trim();
        return string.IsNullOrWhiteSpace(awemeId) || !awemeId.All(char.IsDigit)
            ? ""
            : $"https://www.douyin.com/video/{awemeId}";
    }

    private static string BuildDouyinDiscoveryTaskTitle(DouyinDiscoveryItem item)
        => SelectFirstNonEmpty(
            item.Description,
            item.Word,
            item.AwemeId,
            "抖音发现作品");

    private static bool IsSameUrl(string left, string right)
        => string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string SelectFirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

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

    private int CountDouyinHistoryItems()
        => History.HistoryItems.Count(IsDouyinHistoryItem);

    private void SyncDouyinRecentAuthorItems()
    {
        var authors = History.HistoryItems
            .Where(IsDouyinHistoryItem)
            .SelectMany(item => EnumerateManifestAuthorSummaries(item)
                .Select(author => new
                {
                    author.AuthorName,
                    author.WorkCount,
                    item.DownloadTime
                }))
            .GroupBy(item => item.AuthorName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DouyinRecentAuthorItem(
                group.First().AuthorName,
                group.Sum(item => item.WorkCount),
                group.Max(item => item.DownloadTime)))
            .OrderByDescending(item => item.WorkCount)
            .ThenByDescending(item => item.LatestDownloadTime)
            .ThenBy(item => item.AuthorName, StringComparer.Ordinal)
            .Take(MaxRecentAuthorItems)
            .ToList();

        DouyinRecentAuthorItems.Clear();
        foreach (var author in authors)
        {
            DouyinRecentAuthorItems.Add(author);
        }

        OnPropertyChanged(nameof(DouyinRecentAuthorItems));
        OnPropertyChanged(nameof(HasDouyinRecentAuthorItems));
    }

    private static IEnumerable<DouyinManifestAuthorSummary> EnumerateManifestAuthorSummaries(DownloadHistory item)
    {
        if (item.DouyinManifestSummary?.Authors.Count > 0)
        {
            foreach (var author in item.DouyinManifestSummary.Authors)
            {
                if (!string.IsNullOrWhiteSpace(author.AuthorName) && author.WorkCount > 0)
                    yield return new DouyinManifestAuthorSummary(author.AuthorName.Trim(), author.WorkCount);
            }

            yield break;
        }

        foreach (var group in item.DouyinManifestItems
                     .Where(manifestItem => !string.IsNullOrWhiteSpace(manifestItem.AuthorName))
                     .GroupBy(manifestItem => manifestItem.AuthorName.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            yield return new DouyinManifestAuthorSummary(group.First().AuthorName.Trim(), group.Count());
        }
    }

    private bool MatchesArchiveFilter(DownloadHistory item)
        => MatchesArchiveTypeFilter(item) && MatchesArchiveSearchKeyword(item);

    private bool MatchesArchiveTypeFilter(DownloadHistory item)
    {
        var summary = item.DouyinManifestSummary;
        if (summary is not null)
            return SelectedDouyinArchiveTypeFilter switch
            {
                "视频" => summary.VideoCount > 0,
                "图文" => summary.GalleryCount > 0,
                "音乐" => summary.MusicCount > 0,
                _ => true
            };

        return SelectedDouyinArchiveTypeFilter switch
        {
            "视频" => IsVideoFormat(item.Format),
            "图文" => IsImageFormat(item.Format),
            "音乐" => IsAudioFormat(item.Format),
            _ => true
        };
    }

    private bool MatchesArchiveSearchKeyword(DownloadHistory item)
    {
        var keyword = DouyinArchiveSearchKeyword?.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
            return true;

        return ContainsKeyword(item.Title, keyword)
            || ContainsKeyword(item.Url, keyword)
            || ContainsKeyword(item.Platform, keyword)
            || ContainsKeyword(item.Format, keyword)
            || ContainsKeyword(item.Quality, keyword)
            || ContainsKeyword(item.DouyinManifestSummaryText, keyword)
            || ContainsKeyword(item.DouyinManifestSummary?.SearchText ?? "", keyword)
            || item.DouyinManifestItems.Any(manifestItem => MatchesManifestItemSearchKeyword(manifestItem, keyword));
    }

    private static bool MatchesManifestItemSearchKeyword(DouyinManifestItem item, string keyword)
        => ContainsKeyword(item.AwemeId, keyword)
           || ContainsKeyword(item.MediaTypeText, keyword)
           || ContainsKeyword(item.Description, keyword)
           || ContainsKeyword(item.AuthorName, keyword)
           || ContainsKeyword(item.DateText, keyword)
           || ContainsKeyword(item.TagsText, keyword)
           || ContainsKeyword(item.FileNamesText, keyword)
           || ContainsKeyword(item.FileRoleSummaryText, keyword);

    private static bool IsVideoFormat(string format)
        => IsFormat(format, "mp4", "mkv", "webm", "avi", "mov", "flv", "wmv", "m4v");

    private static bool IsImageFormat(string format)
        => IsFormat(format, "jpg", "jpeg", "png", "webp", "gif");

    private static bool IsAudioFormat(string format)
        => IsFormat(format, "mp3", "m4a", "wav", "flac", "aac", "opus", "ogg");

    private static bool IsFormat(string format, params string[] values)
    {
        if (string.IsNullOrWhiteSpace(format))
            return false;

        var normalized = format.Trim().TrimStart('.').ToLowerInvariant();
        return values.Contains(normalized, StringComparer.Ordinal);
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
