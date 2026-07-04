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
    private readonly List<DouyinDiscoveryItem> _subscribedDiscoveryItems = [];

    public DownloadViewModel Download { get; }
    public BatchDownloadViewModel Batch { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }

    public string[] DouyinTaskFilterOptions { get; } = ["全部", "进行中", "已完成", "失败", "已暂停", "已取消"];
    public string[] DouyinArchiveTypeFilterOptions { get; } = ["全部", "视频", "图文", "音乐"];
    public string[] DouyinDiscoverySortOptions { get; } = ["默认", "热度高到低", "作者", "描述"];
    public string[] DouyinDiscoveryQueueFilterOptions { get; } = ["全部", "未入队", "已在队列", "不可入队"];

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
    [NotifyPropertyChangedFor(nameof(IsDouyinDiscoveryFilterActive))]
    private string _douyinDiscoveryFilterKeyword = "";

    [ObservableProperty]
    private string _selectedDouyinDiscoverySortOption = "默认";

    [ObservableProperty]
    private string _selectedDouyinDiscoveryQueueFilter = "全部";

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
    public ObservableCollection<DouyinDiscoveryItem> FilteredDouyinDiscoveryItems { get; } = [];

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

    public int SuccessfulDouyinWorkCount => CountSuccessfulDouyinWorks();

    public int FailedDouyinWorkCount => CountFailedDouyinWorks();

    public int SkippedDouyinWorkCount => _downloadManager.Tasks
        .Where(IsDouyinTask)
        .Sum(task => Math.Max(0, task.DouyinSkippedCount));

    public int DouyinManifestSummaryCount => DouyinManifestSummaryItems.Count;

    public int DouyinArchiveCount => CountDouyinHistoryItems();

    public int FilteredDouyinArchiveCount => DouyinHistoryItems.Count;

    public bool HasDouyinArchiveItems => DouyinArchiveCount > 0;

    public bool HasFilteredDouyinArchiveItems => FilteredDouyinArchiveCount > 0;

    public bool HasDouyinRecentAuthorItems => DouyinRecentAuthorItems.Count > 0;

    public int DouyinDiscoveryResultCount => DouyinDiscoveryItems.Count;

    public bool HasDouyinDiscoveryItems => DouyinDiscoveryItems.Count > 0;

    public int FilteredDouyinDiscoveryResultCount => FilteredDouyinDiscoveryItems.Count;

    public bool HasFilteredDouyinDiscoveryItems => FilteredDouyinDiscoveryItems.Count > 0;

    public bool IsDouyinDiscoveryFilterActive => !string.IsNullOrWhiteSpace(DouyinDiscoveryFilterKeyword);

    public int SelectedDouyinDiscoveryItemCount => DouyinDiscoveryItems.Count(item => item.IsSelected);

    public bool HasSelectedDouyinDiscoveryItems => SelectedDouyinDiscoveryItemCount > 0;

    public bool HasDouyinDiscoveryError => !string.IsNullOrWhiteSpace(DouyinDiscoveryErrorMessage);

    public string DouyinQuickDownloadEngineStatusText => Settings.EnableDouyinSpecialEngine
        ? $"专项引擎已启用 · {Settings.DouyinMode.Trim()}"
        : "专项引擎未启用";

    public string DouyinQuickDownloadModeLabelText => $"当前内容：{DescribeDouyinQuickDownloadMode(Settings.DouyinMode)}";

    public string DouyinQuickDownloadCookieStatusText => DouyinCookieHealthReporter.Describe(Settings.CookieContent);

    public string DouyinQuickDownloadProxyStatusText => Settings.UseProxy
        ? $"代理 {Settings.ProxyAddress.Trim()}"
        : "代理未启用";

    public string DouyinQuickDownloadLinkInsightText => BuildQuickDownloadLinkInsightText(Download.Url);

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
        Download.PropertyChanged += OnDownloadPropertyChanged;
        Settings.PropertyChanged += OnSettingsPropertyChanged;
        DouyinDiscoveryItems.CollectionChanged += OnDouyinDiscoveryItemsCollectionChanged;
        SyncDouyinHistoryItems();
    }

    private void OnDownloadPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Download.Url))
            OnPropertyChanged(nameof(DouyinQuickDownloadLinkInsightText));
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Settings.EnableDouyinSpecialEngine) or nameof(Settings.DouyinMode))
        {
            OnPropertyChanged(nameof(DouyinQuickDownloadEngineStatusText));
            OnPropertyChanged(nameof(DouyinQuickDownloadModeLabelText));
        }

        if (e.PropertyName == nameof(Settings.CookieContent))
            OnPropertyChanged(nameof(DouyinQuickDownloadCookieStatusText));

        if (e.PropertyName is nameof(Settings.UseProxy) or nameof(Settings.ProxyAddress))
            OnPropertyChanged(nameof(DouyinQuickDownloadProxyStatusText));
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
            SyncDouyinDiscoveryQueueStates();
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
        SyncDouyinDiscoveryQueueStates();
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
        else if (e.PropertyName is nameof(DownloadTask.DouyinSuccessCount)
                 or nameof(DownloadTask.DouyinFailedCount)
                 or nameof(DownloadTask.DouyinSkippedCount))
        {
            NotifyDouyinWorkOutcomeStateChanged();
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

    private void OnDouyinDiscoveryItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ClearDouyinDiscoveryItemSubscriptions();
            foreach (var item in DouyinDiscoveryItems)
            {
                SubscribeDouyinDiscoveryItem(item);
            }
        }
        else
        {
            if (e.OldItems is not null)
            {
                foreach (DouyinDiscoveryItem item in e.OldItems)
                {
                    UnsubscribeDouyinDiscoveryItem(item);
                }
            }

            if (e.NewItems is not null)
            {
                foreach (DouyinDiscoveryItem item in e.NewItems)
                {
                    SubscribeDouyinDiscoveryItem(item);
                }
            }
        }

        NotifyDouyinDiscoveryStateChanged();
        NotifyDouyinDiscoverySelectionChanged();
        SyncDouyinDiscoveryQueueStates();
        SyncDouyinDiscoveryFilteredItems();
    }

    private void OnDouyinDiscoveryItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DouyinDiscoveryItem.IsSelected))
            NotifyDouyinDiscoverySelectionChanged();
    }

    private void SubscribeDouyinDiscoveryItem(DouyinDiscoveryItem item)
    {
        if (_subscribedDiscoveryItems.Any(existing => ReferenceEquals(existing, item)))
            return;

        _subscribedDiscoveryItems.Add(item);
        item.PropertyChanged += OnDouyinDiscoveryItemPropertyChanged;
    }

    private void UnsubscribeDouyinDiscoveryItem(DouyinDiscoveryItem item)
    {
        var existing = _subscribedDiscoveryItems.FirstOrDefault(candidate => ReferenceEquals(candidate, item));
        if (existing is null)
            return;

        existing.PropertyChanged -= OnDouyinDiscoveryItemPropertyChanged;
        _subscribedDiscoveryItems.Remove(existing);
    }

    private void ClearDouyinDiscoveryItemSubscriptions()
    {
        foreach (var item in _subscribedDiscoveryItems)
        {
            item.PropertyChanged -= OnDouyinDiscoveryItemPropertyChanged;
        }

        _subscribedDiscoveryItems.Clear();
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
        NotifyDouyinWorkOutcomeStateChanged();
    }

    private void NotifyDouyinWorkOutcomeStateChanged()
    {
        OnPropertyChanged(nameof(SuccessfulDouyinWorkCount));
        OnPropertyChanged(nameof(FailedDouyinWorkCount));
        OnPropertyChanged(nameof(SkippedDouyinWorkCount));
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

    private int CountSuccessfulDouyinWorks()
        => _downloadManager.Tasks
            .Where(IsDouyinTask)
            .Sum(task => HasDouyinOutcomeCounters(task)
                ? Math.Max(0, task.DouyinSuccessCount)
                : task.Status == DownloadStatus.Completed
                    ? 1
                    : 0);

    private int CountFailedDouyinWorks()
        => _downloadManager.Tasks
            .Where(IsDouyinTask)
            .Sum(task => HasDouyinOutcomeCounters(task)
                ? Math.Max(0, task.DouyinFailedCount)
                : task.Status == DownloadStatus.Failed
                    ? 1
                    : 0);

    private static bool HasDouyinOutcomeCounters(DownloadTask task)
        => task.DouyinSuccessCount > 0
           || task.DouyinFailedCount > 0
           || task.DouyinSkippedCount > 0;

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

    partial void OnDouyinDiscoveryFilterKeywordChanged(string value)
    {
        SyncDouyinDiscoveryFilteredItems();
    }

    partial void OnSelectedDouyinDiscoverySortOptionChanged(string value)
    {
        if (!DouyinDiscoverySortOptions.Contains(value, StringComparer.Ordinal))
        {
            SelectedDouyinDiscoverySortOption = "默认";
            return;
        }

        SyncDouyinDiscoveryFilteredItems();
    }

    partial void OnSelectedDouyinDiscoveryQueueFilterChanged(string value)
    {
        if (!DouyinDiscoveryQueueFilterOptions.Contains(value, StringComparer.Ordinal))
        {
            SelectedDouyinDiscoveryQueueFilter = "全部";
            return;
        }

        SyncDouyinDiscoveryFilteredItems();
    }

    [RelayCommand]
    private void SetDouyinTaskFilter(string filter)
    {
        SelectedDouyinTaskFilter = DouyinTaskFilterOptions.Contains(filter, StringComparer.Ordinal)
            ? filter
            : "全部";
    }

    [RelayCommand]
    private void SetDouyinQuickDownloadMode(string mode)
    {
        var normalizedMode = mode.Trim();
        if (!Settings.DouyinModeOptions.Contains(normalizedMode, StringComparer.Ordinal))
            return;

        Settings.EnableDouyinSpecialEngine = true;
        Settings.DouyinMode = normalizedMode;
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
    private async Task SearchDouyinDiscoveryItemWord(DouyinDiscoveryItem? item)
    {
        var keyword = item?.Word.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(keyword))
        {
            DouyinDiscoveryErrorMessage = "当前发现条目缺少可搜索关键词";
            DouyinDiscoveryStatusText = "无法搜索发现词条";
            NotifyDouyinDiscoveryStateChanged();
            return;
        }

        DouyinDiscoveryKeyword = keyword;
        await SearchDouyinDiscovery();
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

    [RelayCommand]
    private async Task AddSelectedDouyinDiscoveryItemsToQueue()
    {
        var selectedItems = DouyinDiscoveryItems
            .Where(item => item.IsSelected)
            .ToList();

        if (selectedItems.Count == 0)
        {
            DouyinDiscoveryErrorMessage = "请先选择要加入下载队列的发现结果";
            DouyinDiscoveryStatusText = "尚未选择发现结果";
            NotifyDouyinDiscoveryStateChanged();
            return;
        }

        await AddDouyinDiscoveryItemsToQueueAsync(selectedItems, "选中入队完成");
    }

    [RelayCommand]
    private async Task AddAllDouyinDiscoveryItemsToQueue()
    {
        if (DouyinDiscoveryItems.Count == 0)
        {
            DouyinDiscoveryErrorMessage = "暂无发现结果可加入下载队列";
            DouyinDiscoveryStatusText = "暂无发现结果";
            NotifyDouyinDiscoveryStateChanged();
            return;
        }

        await AddDouyinDiscoveryItemsToQueueAsync(DouyinDiscoveryItems.ToList(), "批量入队完成");
    }

    [RelayCommand]
    private async Task AddFilteredDouyinDiscoveryItemsToQueue()
    {
        if (FilteredDouyinDiscoveryItems.Count == 0)
        {
            DouyinDiscoveryErrorMessage = "当前筛选没有发现结果可加入下载队列";
            DouyinDiscoveryStatusText = "当前筛选无发现结果";
            NotifyDouyinDiscoveryStateChanged();
            return;
        }

        await AddDouyinDiscoveryItemsToQueueAsync(FilteredDouyinDiscoveryItems.ToList(), "筛选入队完成");
    }

    [RelayCommand]
    private void SelectAllDouyinDiscoveryItems()
    {
        foreach (var item in DouyinDiscoveryItems)
        {
            item.IsSelected = true;
        }

        NotifyDouyinDiscoverySelectionChanged();
    }

    [RelayCommand]
    private void SelectFilteredDouyinDiscoveryItems()
    {
        var filteredItems = FilteredDouyinDiscoveryItems.ToList();
        if (filteredItems.Count == 0)
        {
            DouyinDiscoveryErrorMessage = "当前筛选没有发现结果可选择";
            DouyinDiscoveryStatusText = "当前筛选无发现结果";
            NotifyDouyinDiscoveryStateChanged();
            return;
        }

        foreach (var item in DouyinDiscoveryItems)
        {
            item.IsSelected = filteredItems.Any(filtered => ReferenceEquals(filtered, item));
        }

        DouyinDiscoveryErrorMessage = "";
        DouyinDiscoveryStatusText = $"已选择 {filteredItems.Count} 条当前筛选发现结果";
        NotifyDouyinDiscoveryStateChanged();
    }

    [RelayCommand]
    private void SelectDownloadableDouyinDiscoveryItems()
    {
        var visibleItems = FilteredDouyinDiscoveryItems.ToList();
        var selected = 0;
        foreach (var item in DouyinDiscoveryItems)
        {
            var isVisible = visibleItems.Any(visible => ReferenceEquals(visible, item));
            var isDownloadable = isVisible && !string.IsNullOrWhiteSpace(BuildDouyinDiscoveryDownloadUrl(item));
            item.IsSelected = isDownloadable;
            if (isDownloadable)
                selected++;
        }

        DouyinDiscoveryErrorMessage = "";
        var scopeText = IsDouyinDiscoveryFilterActive
            ? "当前筛选可下载发现结果"
            : "可下载发现结果";
        DouyinDiscoveryStatusText = selected > 0
            ? $"已选择 {selected} 条{scopeText}"
            : IsDouyinDiscoveryFilterActive
                ? "当前筛选没有可下载链接"
                : "当前发现结果没有可下载链接";
        if (selected == 0)
        {
            DouyinDiscoveryErrorMessage = IsDouyinDiscoveryFilterActive
                ? "当前筛选没有可下载链接"
                : "当前发现结果没有可下载链接";
        }

        NotifyDouyinDiscoveryStateChanged();
    }

    [RelayCommand]
    private void ClearDouyinDiscoverySelection()
    {
        foreach (var item in DouyinDiscoveryItems)
        {
            item.IsSelected = false;
        }

        NotifyDouyinDiscoverySelectionChanged();
    }

    [RelayCommand]
    private void ClearDouyinDiscoveryFilter()
    {
        DouyinDiscoveryFilterKeyword = "";
        SyncDouyinDiscoveryFilteredItems();
    }

    private async Task AddDouyinDiscoveryItemsToQueueAsync(
        IReadOnlyCollection<DouyinDiscoveryItem> items,
        string statusPrefix)
    {
        var added = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var item in items)
        {
            var url = BuildDouyinDiscoveryDownloadUrl(item);
            if (string.IsNullOrWhiteSpace(url))
            {
                failed++;
                continue;
            }

            if (_downloadManager.Tasks.Any(task => IsSameUrl(task.Url, url)))
            {
                skipped++;
                continue;
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

            added++;
        }

        DouyinDiscoveryStatusText = $"{statusPrefix}：已加入 {added}，跳过 {skipped}，失败 {failed}";
        DouyinDiscoveryErrorMessage = failed > 0
            ? $"{failed} 条发现结果缺少可下载链接"
            : "";
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
        FilteredDouyinDiscoveryItems.Clear();
        NotifyDouyinDiscoveryStateChanged();
    }

    private void SyncDouyinDiscoveryFilteredItems()
    {
        FilteredDouyinDiscoveryItems.Clear();
        foreach (var item in SortDouyinDiscoveryItems(DouyinDiscoveryItems.Where(MatchesDiscoveryFilter)))
        {
            FilteredDouyinDiscoveryItems.Add(item);
        }

        OnPropertyChanged(nameof(FilteredDouyinDiscoveryItems));
        OnPropertyChanged(nameof(FilteredDouyinDiscoveryResultCount));
        OnPropertyChanged(nameof(HasFilteredDouyinDiscoveryItems));
    }

    private void SyncDouyinDiscoveryQueueStates()
    {
        foreach (var item in DouyinDiscoveryItems)
        {
            var url = BuildDouyinDiscoveryDownloadUrl(item);
            var isDownloadable = !string.IsNullOrWhiteSpace(url);
            var isQueued = isDownloadable && _downloadManager.Tasks.Any(task => IsSameUrl(task.Url, url));
            item.QueueStateText = !isDownloadable
                ? "不可入队"
                : isQueued
                    ? "已在队列"
                    : "未入队";
            item.CanAddToQueue = isDownloadable && !isQueued;
        }

        SyncDouyinDiscoveryFilteredItems();
    }

    private void NotifyDouyinDiscoveryStateChanged()
    {
        OnPropertyChanged(nameof(DouyinDiscoveryItems));
        OnPropertyChanged(nameof(DouyinDiscoveryResultCount));
        OnPropertyChanged(nameof(HasDouyinDiscoveryItems));
        OnPropertyChanged(nameof(FilteredDouyinDiscoveryItems));
        OnPropertyChanged(nameof(FilteredDouyinDiscoveryResultCount));
        OnPropertyChanged(nameof(HasFilteredDouyinDiscoveryItems));
        OnPropertyChanged(nameof(IsDouyinDiscoveryFilterActive));
        OnPropertyChanged(nameof(HasDouyinDiscoveryError));
        NotifyDouyinDiscoverySelectionChanged();
    }

    private void NotifyDouyinDiscoverySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedDouyinDiscoveryItemCount));
        OnPropertyChanged(nameof(HasSelectedDouyinDiscoveryItems));
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

    private bool MatchesDiscoveryFilter(DouyinDiscoveryItem item)
    {
        if (SelectedDouyinDiscoveryQueueFilter != "全部"
            && !string.Equals(item.QueueStateText, SelectedDouyinDiscoveryQueueFilter, StringComparison.Ordinal))
        {
            return false;
        }

        var keyword = DouyinDiscoveryFilterKeyword?.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
            return true;

        return ContainsKeyword(item.Word, keyword)
               || ContainsKeyword(item.Description, keyword)
               || ContainsKeyword(item.AuthorNickname, keyword)
               || ContainsKeyword(item.SecUid, keyword)
               || ContainsKeyword(item.AwemeId, keyword)
               || ContainsKeyword(item.Url, keyword)
               || ContainsKeyword(item.Position?.ToString() ?? "", keyword)
               || ContainsKeyword(item.HotValue?.ToString() ?? "", keyword);
    }

    private IEnumerable<DouyinDiscoveryItem> SortDouyinDiscoveryItems(IEnumerable<DouyinDiscoveryItem> items)
        => SelectedDouyinDiscoverySortOption switch
        {
            "热度高到低" => items
                .OrderByDescending(item => item.HotValue ?? long.MinValue)
                .ThenBy(item => item.Position ?? int.MaxValue)
                .ThenBy(item => SelectFirstNonEmpty(item.Description, item.Word, item.AwemeId), StringComparer.OrdinalIgnoreCase),
            "作者" => items
                .OrderBy(item => SelectFirstNonEmpty(item.AuthorNickname, item.SecUid, item.Description, item.Word), StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => SelectFirstNonEmpty(item.Description, item.Word, item.AwemeId), StringComparer.OrdinalIgnoreCase),
            "描述" => items
                .OrderBy(item => SelectFirstNonEmpty(item.Description, item.Word, item.AwemeId), StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => SelectFirstNonEmpty(item.AuthorNickname, item.SecUid), StringComparer.OrdinalIgnoreCase),
            _ => items
        };

    private static bool IsSameUrl(string left, string right)
        => string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string SelectFirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static string DescribeDouyinQuickDownloadMode(string mode)
        => mode.Trim() switch
        {
            "post" => "作品",
            "like" => "喜欢",
            "mix" => "合集",
            "music" => "音乐",
            "post,like,mix,music" => "全量",
            "collect" => "收藏",
            "collectmix" => "收藏合集",
            var value when string.IsNullOrWhiteSpace(value) => "未选择",
            var value => value
        };

    private static string BuildQuickDownloadLinkInsightText(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "等待抖音链接";

        var candidate = DownloadViewModel.ExtractUrl(input) ?? input.Trim();
        var info = DouyinUrlParser.Parse(candidate);
        if (!info.IsRecognized)
            return "未识别为抖音专项链接";

        var kindText = info.Kind switch
        {
            DouyinUrlKind.ShortLink => "短链",
            DouyinUrlKind.Video or DouyinUrlKind.Note or DouyinUrlKind.Gallery or DouyinUrlKind.Slides => "单作品",
            DouyinUrlKind.User => "用户主页",
            DouyinUrlKind.Collection => "收藏夹",
            DouyinUrlKind.Mix => "合集",
            DouyinUrlKind.Music => "音乐",
            DouyinUrlKind.Live => "直播",
            _ => "抖音链接"
        };

        var suffix = info.Kind switch
        {
            DouyinUrlKind.ShortLink => " · 需要解析展开",
            DouyinUrlKind.Live => " · 专项引擎暂不支持",
            _ => ""
        };

        return string.IsNullOrWhiteSpace(info.Id)
            ? $"已识别：{kindText}{suffix}"
            : $"已识别：{kindText} · {info.Id}{suffix}";
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
