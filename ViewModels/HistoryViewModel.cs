using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyGet.Models;
using EasyGet.Services;

namespace EasyGet.ViewModels;

/// <summary>
/// 历史记录页 ViewModel
/// </summary>
public partial class HistoryViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(300);
    private const int DefaultHistoryCardColumnCount = 4;

    private readonly HistoryService _historyService;
    private readonly ConfigService _configService;
    private readonly Action<ProcessStartInfo> _startProcess;
    private readonly Action<Func<Task>> _scheduleHistoryUpdate;
    private readonly DownloadFileDeletionService _fileDeletionService;
    private readonly HistoryDirectoryDiscoveryService _directoryDiscoveryService;
    private readonly HistoryThumbnailService _thumbnailService;
    private readonly SemaphoreSlim _historyLoadSemaphore = new(1, 1);
    private readonly object _initialHistoryLoadGate = new();
    private readonly object _historyAddedGate = new();
    private readonly object _thumbnailHydrationGate = new();
    private readonly Dictionary<long, DownloadHistory> _pendingHistoryAdded = [];
    private readonly HashSet<long> _recentlyCompletedHistoryIds = [];
    private CancellationTokenSource? _searchCts;
    private ThumbnailHydrationSession? _thumbnailHydrationSession;
    private Task? _initialHistoryLoadTask;
    private bool _hasLoadedHistory;
    private bool _historyAddedDrainScheduled;
    private int _historyLoadRequestVersion;
    private bool _suppressSelectionRefresh;
    private bool _suppressLocationRefresh;
    private int _historyCardColumnCount = DefaultHistoryCardColumnCount;
    private bool _isDisposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchOrFilterActive))]
    private string _searchKeyword = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchOrFilterActive))]
    private string _selectedMediaFilter = "全部";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchOrFilterActive))]
    [NotifyPropertyChangedFor(nameof(IsShowingAllFolders))]
    [NotifyPropertyChangedFor(nameof(IsShowingUnfiled))]
    [NotifyPropertyChangedFor(nameof(SelectedFolderTitle))]
    private long? _selectedFolderId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchOrFilterActive))]
    [NotifyPropertyChangedFor(nameof(IsAtHistoryRoot))]
    [NotifyPropertyChangedFor(nameof(HasActiveLocation))]
    [NotifyPropertyChangedFor(nameof(IsShowingAllFolders))]
    [NotifyPropertyChangedFor(nameof(SelectedFolderTitle))]
    private string? _selectedBatchKey;

    [ObservableProperty] private bool _isLoadingHistory;
    [ObservableProperty] private int _visibleHistoryCount;
    [ObservableProperty] private int _unfiledHistoryCount;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _newFolderName = "";
    [ObservableProperty] private HistoryMoveTarget? _bulkTargetFolder;

    public bool IsSearchOrFilterActive
        => !string.IsNullOrEmpty(SearchKeyword)
           || SelectedMediaFilter != "全部"
           || SelectedFolderId is not null
           || !string.IsNullOrWhiteSpace(SelectedBatchKey);

    public bool IsShowingAllFolders => IsAtHistoryRoot;
    public bool IsShowingUnfiled => SelectedFolderId == 0;
    public bool IsAtHistoryRoot => SelectedFolderId is null && string.IsNullOrWhiteSpace(SelectedBatchKey);
    public bool HasActiveLocation => !IsAtHistoryRoot;
    public bool HasSelection => SelectedCount > 0;
    public bool HasVisibleHistory => VisibleHistoryCount > 0;
    public bool HasHistoryFolders => HistoryFolders.Count > 0;
    public bool HasBulkTargetFolders => BulkTargetFolders.Count > 0;
    public bool HasBatchFolders => BatchFolderCards.Count > 0;
    public bool HasWorkspaceFolders => HistoryFolders.Count > 0 || BatchFolderCards.Count > 0;
    public bool HasDisplayedHistoryCards => HistoryCardRows.Count > 0;
    public bool ShouldShowFolderOnlyHint => HasVisibleHistory && !HasDisplayedHistoryCards;
    public bool CanCreateFolder => !string.IsNullOrWhiteSpace(NewFolderName);
    public string SelectionSummaryText => $"已选择 {SelectedCount} 项";
    public bool AreAllVisibleItemsSelected
    {
        get
        {
            var items = GetCurrentLocationItems().ToList();
            return items.Count > 0 && items.All(item => item.IsSelected);
        }
    }
    public string SelectAllVisibleActionText
        => AreAllVisibleItemsSelected ? "取消全选" : "全选当前";
    public string SelectAllVisibleActionGlyph
        => AreAllVisibleItemsSelected ? "\uE711" : "\uE73A";
    public string SelectAllVisibleActionDescription
        => AreAllVisibleItemsSelected
            ? "取消选择当前目录中的全部历史记录"
            : "选择当前目录中的全部历史记录";
    public string BulkTargetFolderPlaceholderText
        => HasBulkTargetFolders ? "选择目标文件夹" : "暂无可用目标文件夹";
    public string SelectedFolderTitle
        => !string.IsNullOrWhiteSpace(SelectedBatchKey)
            ? BatchFolderCards.FirstOrDefault(group => group.Key == SelectedBatchKey)?.Name ?? "批量文件夹"
            : SelectedFolderId switch
            {
                null => "全部下载",
                0 => "未整理",
                _ => HistoryFolders.FirstOrDefault(folder => folder.Id == SelectedFolderId)?.Name ?? "整理文件夹"
            };
    public string WorkspaceSummaryText
        => $"{HistoryFolders.Count} 个自定义文件夹 · {BatchFolderCards.Count} 个批量文件夹";
    public string CurrentLocationPathText => ResolveCurrentLocationPath();
    public string CurrentLocationFileCountText => GetCurrentLocationSummaryItems().Count.ToString("N0");
    public string CurrentLocationSizeText
        => ByteSizeFormatter.FormatClampZero(SumFileSizes(GetCurrentLocationSummaryItems()));

    [ObservableProperty] private int _totalHistoryCount;
    [ObservableProperty] private string _storageStatusText = "磁盘空间获取中";
    [ObservableProperty] private double _storageFreePercentage = 0;

    partial void OnNewFolderNameChanged(string value)
        => CreateFolderCommand.NotifyCanExecuteChanged();

    partial void OnBulkTargetFolderChanged(HistoryMoveTarget? value)
        => MoveSelectedToFolderCommand.NotifyCanExecuteChanged();

    partial void OnSelectedFolderIdChanged(long? value)
    {
        ClearSelectedBatchWithoutRefresh();
        foreach (var folder in HistoryFolders)
            folder.IsSelected = value == folder.Id;
        ClearSelection();
        RebuildHistoryGroups();
        NotifyLocationState();
    }

    partial void OnSelectedBatchKeyChanged(string? value)
    {
        if (_suppressLocationRefresh)
            return;

        foreach (var group in BatchFolderCards)
            group.IsSelected = string.Equals(value, group.Key, StringComparison.Ordinal);
        ClearSelection();
        RebuildHistoryGroups();
        NotifyLocationState();
    }

    partial void OnSearchKeywordChanged(string value)
    {
        CancelPendingSearch();
        _searchCts = new CancellationTokenSource();

        _ = DebouncedLoadHistoryAsync(_searchCts.Token);
    }

    private async Task DebouncedLoadHistoryAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SearchDebounceDelay, token);

            if (token.IsCancellationRequested)
                return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                await await dispatcher.InvokeAsync(LoadHistory);
                return;
            }

            await LoadHistory();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    public string[] MediaFilterOptions { get; } = ["全部", "视频", "音频"];
    public ObservableCollection<DownloadHistory> HistoryItems { get; } = [];
    public ObservableCollection<DownloadHistoryGroup> HistoryGroups { get; } = [];
    public ObservableCollection<HistoryCardRow> HistoryCardRows { get; } = [];
    public ObservableCollection<DownloadHistoryGroup> BatchFolderCards { get; } = [];
    public ObservableCollection<HistoryFolder> HistoryFolders { get; } = [];
    public ObservableCollection<HistoryMoveTarget> BulkTargetFolders { get; } = [];

    public event Action<string, bool>? RequestShowNotification;

    public HistoryViewModel(HistoryService historyService)
        : this(historyService, new ConfigService(), StartProcess)
    {
    }

    public HistoryViewModel(
        HistoryService historyService,
        ConfigService configService,
        DownloadFileDeletionService? fileDeletionService = null,
        HistoryThumbnailService? thumbnailService = null,
        HistoryDirectoryDiscoveryService? directoryDiscoveryService = null)
        : this(
            historyService,
            configService,
            StartProcess,
            null,
            fileDeletionService,
            thumbnailService,
            directoryDiscoveryService)
    {
    }

    internal HistoryViewModel(HistoryService historyService, Action<ProcessStartInfo> startProcess)
        : this(historyService, new ConfigService(), startProcess)
    {
    }

    internal HistoryViewModel(
        HistoryService historyService,
        ConfigService configService,
        Action<ProcessStartInfo> startProcess,
        Action<Func<Task>>? scheduleHistoryUpdate = null,
        DownloadFileDeletionService? fileDeletionService = null,
        HistoryThumbnailService? thumbnailService = null,
        HistoryDirectoryDiscoveryService? directoryDiscoveryService = null)
    {
        _historyService = historyService;
        _configService = configService;
        _startProcess = startProcess;
        _scheduleHistoryUpdate = scheduleHistoryUpdate ?? ScheduleHistoryUpdate;
        _fileDeletionService = fileDeletionService ?? new DownloadFileDeletionService();
        _directoryDiscoveryService = directoryDiscoveryService
            ?? new HistoryDirectoryDiscoveryService();
        _thumbnailService = thumbnailService
            ?? new HistoryThumbnailService(configService, new EnvironmentService());
        _historyService.HistoryAdded += OnHistoryAdded;
    }

    private void RestartThumbnailHydration(IEnumerable<DownloadHistory> items)
    {
        var snapshot = items.ToList();
        if (snapshot.Count == 0)
        {
            CancelThumbnailHydration();
            return;
        }

        ThumbnailHydrationSession? previousSession;
        lock (_thumbnailHydrationGate)
        {
            if (_isDisposed)
                return;

            previousSession = _thumbnailHydrationSession;
            var nextSession = new ThumbnailHydrationSession();
            _thumbnailHydrationSession = nextSession;
            nextSession.Track(HydrateThumbnailsSafelyAsync(snapshot, nextSession.Token));
        }

        previousSession?.Retire();
    }

    public void RetryLocalThumbnails()
    {
        if (!Volatile.Read(ref _hasLoadedHistory))
            return;

        RestartThumbnailHydration(HistoryItems);
    }

    private void ContinueThumbnailHydration(IEnumerable<DownloadHistory> items)
    {
        var snapshot = items.ToList();
        if (snapshot.Count == 0)
            return;

        lock (_thumbnailHydrationGate)
        {
            if (_isDisposed)
                return;

            var session = _thumbnailHydrationSession ??= new ThumbnailHydrationSession();
            session.Track(HydrateThumbnailsSafelyAsync(
                snapshot,
                session.Token));
        }
    }

    private async Task HydrateThumbnailsSafelyAsync(
        IReadOnlyCollection<DownloadHistory> items,
        CancellationToken cancellationToken)
    {
        try
        {
            var orderedItems = items
                .Select(item => (
                    Item: item,
                    Paths: EnumerateLocalFilePaths(item).ToArray()))
                .Where(candidate => candidate.Paths.Length > 0)
                .OrderBy(candidate => string.IsNullOrWhiteSpace(candidate.Item.ThumbnailUrl) ? 0 : 1)
                .ToList();
            await Parallel.ForEachAsync(
                orderedItems,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 2,
                    CancellationToken = cancellationToken
                },
                async (candidate, ct) =>
                {
                    var localThumbnail = await _thumbnailService.ResolveLocalThumbnailAsync(
                        candidate.Paths,
                        ct);
                    if (string.IsNullOrWhiteSpace(localThumbnail))
                        return;

                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher is null || dispatcher.CheckAccess())
                    {
                        candidate.Item.ThumbnailUrl = localThumbnail;
                    }
                    else
                    {
                        await dispatcher.InvokeAsync(
                            () => candidate.Item.ThumbnailUrl = localThumbnail,
                            System.Windows.Threading.DispatcherPriority.Background,
                            ct);
                    }
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer history query or application shutdown retired this hydration pass.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HistoryViewModel] Thumbnail hydration failed: {ex.Message}");
        }
    }

    private static void ScheduleHistoryUpdate(Func<Task> update)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            _ = dispatcher.InvokeAsync(() => RunHistoryUpdateSafelyAsync(update));
            return;
        }

        _ = Task.Run(() => RunHistoryUpdateSafelyAsync(update));
    }

    private static async Task RunHistoryUpdateSafelyAsync(Func<Task> update)
    {
        try
        {
            await update();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HistoryViewModel] Incremental history update failed: {ex.Message}");
        }
    }

    private void OnHistoryAdded(DownloadHistory history)
    {
        if (history.Id <= 0 || Volatile.Read(ref _isDisposed))
            return;

        lock (_historyAddedGate)
        {
            if (_isDisposed)
                return;

            _pendingHistoryAdded[history.Id] = history;
            _recentlyCompletedHistoryIds.Add(history.Id);
        }

        SchedulePendingHistoryAdded();
    }

    private void SchedulePendingHistoryAdded()
    {
        var shouldSchedule = false;
        lock (_historyAddedGate)
        {
            if (!_isDisposed
                && Volatile.Read(ref _hasLoadedHistory)
                && _pendingHistoryAdded.Count > 0
                && !_historyAddedDrainScheduled)
            {
                _historyAddedDrainScheduled = true;
                shouldSchedule = true;
            }
        }

        if (!shouldSchedule)
            return;

        try
        {
            _scheduleHistoryUpdate(DrainPendingHistoryAddedAsync);
        }
        catch
        {
            lock (_historyAddedGate)
                _historyAddedDrainScheduled = false;
            throw;
        }
    }

    private async Task DrainPendingHistoryAddedAsync()
    {
        try
        {
            while (await ApplyPendingHistoryAddedAsync())
            {
            }
        }
        finally
        {
            var shouldReschedule = false;
            lock (_historyAddedGate)
            {
                _historyAddedDrainScheduled = false;
                if (!_isDisposed
                    && Volatile.Read(ref _hasLoadedHistory)
                    && _pendingHistoryAdded.Count > 0)
                {
                    _historyAddedDrainScheduled = true;
                    shouldReschedule = true;
                }
            }

            if (shouldReschedule)
                _scheduleHistoryUpdate(DrainPendingHistoryAddedAsync);
        }
    }

    private async Task<bool> ApplyPendingHistoryAddedAsync()
    {
        await _historyLoadSemaphore.WaitAsync();
        try
        {
            if (Volatile.Read(ref _isDisposed))
                return false;

            List<DownloadHistory> pendingItems;
            lock (_historyAddedGate)
            {
                if (_pendingHistoryAdded.Count == 0)
                    return false;

                pendingItems = _pendingHistoryAdded.Values.ToList();
                _pendingHistoryAdded.Clear();
            }

            TotalHistoryCount = await _historyService.GetCountAsync();
            UnfiledHistoryCount = await _historyService.GetUnfiledCountAsync();
            var folders = await _historyService.GetFoldersAsync();
            if (Volatile.Read(ref _isDisposed))
                return false;
            var bulkTargetSnapshotTask = LoadBulkTargetFolderSnapshotAsync();

            var folderNames = folders.ToDictionary(folder => folder.Id, folder => folder.Name);

            foreach (var folder in HistoryFolders)
            {
                var refreshedFolder = folders.FirstOrDefault(candidate => candidate.Id == folder.Id);
                if (refreshedFolder is not null)
                    folder.ItemCount = refreshedFolder.ItemCount;
            }

            var existingIds = HistoryItems.Select(item => item.Id).ToHashSet();
            var candidates = pendingItems
                .Where(item => item.Id > 0 && !existingIds.Contains(item.Id))
                .GroupBy(item => item.Id)
                .Select(group => group.Last())
                .ToList();
            var enrichmentTask = Task.Run(() => candidates
                .Select(EnrichHistoryItem)
                .ToList());
            await Task.WhenAll(bulkTargetSnapshotTask, enrichmentTask);
            var bulkTargetSnapshot = await bulkTargetSnapshotTask;
            var enrichedItems = await enrichmentTask;
            if (Volatile.Read(ref _isDisposed))
                return false;

            var shouldRebuild = false;
            var addedItems = new List<DownloadHistory>();
            foreach (var pendingItem in pendingItems)
            {
                var existingItem = HistoryItems.FirstOrDefault(item => item.Id == pendingItem.Id);
                if (existingItem is null || existingItem.IsRecentlyCompleted)
                    continue;

                existingItem.IsRecentlyCompleted = true;
                shouldRebuild = true;
            }

            foreach (var result in enrichedItems)
            {
                if (!MatchesCurrentHistoryQuery(result.Item)
                    || HistoryItems.Any(item => item.Id == result.Item.Id))
                {
                    continue;
                }

                ApplyHistoryItemEnrichment(result, folderNames);
                result.Item.IsRecentlyCompleted = true;
                result.Item.PropertyChanged += OnHistoryItemPropertyChanged;
                HistoryItems.Add(result.Item);
                addedItems.Add(result.Item);
                shouldRebuild = true;
            }

            if (shouldRebuild)
            {
                var orderedItems = HistoryItems
                    .GroupBy(item => item.Id)
                    .Select(group => group.First())
                    .OrderByDescending(item => item.DownloadTime)
                    .ThenByDescending(item => item.Id)
                    .ToList();
                HistoryItems.Clear();
                foreach (var item in orderedItems)
                    HistoryItems.Add(item);

                RebuildHistoryGroups();
            }
            else
            {
                NotifyLocationState();
            }

            RebuildBulkTargetFolders(
                bulkTargetSnapshot.Directories,
                bulkTargetSnapshot.ExistingCollectionFolders);
            ContinueThumbnailHydration(addedItems);

            return true;
        }
        finally
        {
            _historyLoadSemaphore.Release();
        }
    }

    private bool MatchesCurrentHistoryQuery(DownloadHistory item)
        => MatchesSearchKeyword(item, SearchKeyword)
           && MatchesMediaFilter(item, SelectedMediaFilter);

    private static bool MatchesSearchKeyword(DownloadHistory item, string? searchKeyword)
    {
        if (string.IsNullOrWhiteSpace(searchKeyword))
            return true;

        return ContainsSearchKeyword(item.Title, searchKeyword)
               || ContainsSearchKeyword(item.Url, searchKeyword)
               || ContainsSearchKeyword(item.Platform, searchKeyword)
               || ContainsSearchKeyword(item.BatchName, searchKeyword);
    }

    private static bool ContainsSearchKeyword(string? value, string keyword)
        => value?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true;

    public void RefreshStorageStatus()
    {
        var downloadPath = _configService.Config.DefaultDownloadPath;

        _ = Task.Run(() =>
        {
            var status = DescribeStorageStatus(downloadPath);
            var percentage = GetStorageFreePercentage(downloadPath);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                StorageStatusText = status;
                StorageFreePercentage = percentage;
            }
            else
            {
                dispatcher.InvokeAsync(() =>
                {
                    StorageStatusText = status;
                    StorageFreePercentage = percentage;
                });
            }
        });
    }

    private static double GetStorageFreePercentage(string downloadPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(downloadPath);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                return 0;

            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0)
                return 0;

            return ((double)drive.AvailableFreeSpace / drive.TotalSize) * 100.0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 加载/刷新历史记录
    /// </summary>
    [RelayCommand]
    public async Task LoadHistory()
    {
        await LoadHistoryCore(
            string.IsNullOrWhiteSpace(SearchKeyword) ? null : SearchKeyword,
            SelectedMediaFilter);
    }

    public Task EnsureHistoryLoadedAsync()
    {
        lock (_initialHistoryLoadGate)
        {
            if (_hasLoadedHistory)
                return RefreshBulkTargetFoldersAsync();

            if (_initialHistoryLoadTask is null || _initialHistoryLoadTask.IsCompleted)
                _initialHistoryLoadTask = LoadHistory();

            return _initialHistoryLoadTask;
        }
    }

    public Task LoadAllHistoryForWorkspace()
    {
        CancelPendingSearch();
        return LoadHistoryCore(null, "全部");
    }

    private void CancelPendingSearch()
    {
        var previousSearchCts = _searchCts;
        _searchCts = null;
        previousSearchCts?.Cancel();
        previousSearchCts?.Dispose();
    }

    private async Task LoadHistoryCore(string? searchKeyword, string mediaFilter)
    {
        var requestVersion = Interlocked.Increment(ref _historyLoadRequestVersion);
        var loadedSuccessfully = false;
        await _historyLoadSemaphore.WaitAsync();
        IsLoadingHistory = true;
        try
        {
            if (requestVersion != Volatile.Read(ref _historyLoadRequestVersion))
                return;

            var totalCount = await _historyService.GetCountAsync();
            var unfiledCount = await _historyService.GetUnfiledCountAsync();
            var folders = await _historyService.GetFoldersAsync();
            var items = await _historyService.GetAllAsync(searchKeyword);
            var allItemsForBulkTargets = string.IsNullOrWhiteSpace(searchKeyword)
                ? items
                : await _historyService.GetAllAsync();
            var filteredItems = items
                .Where(item => MatchesMediaFilter(item, mediaFilter))
                .GroupBy(item => item.Id)
                .Select(group => group.First())
                .OrderByDescending(item => item.DownloadTime)
                .ThenByDescending(item => item.Id)
                .ToList();
            var folderNames = folders.ToDictionary(folder => folder.Id, folder => folder.Name);

            var bulkTargetSnapshotTask = LoadBulkTargetFolderSnapshotAsync(allItemsForBulkTargets);
            var enrichmentTask = Task.Run(() => filteredItems
                .Select(EnrichHistoryItem)
                .ToList());
            await Task.WhenAll(bulkTargetSnapshotTask, enrichmentTask);
            var bulkTargetSnapshot = await bulkTargetSnapshotTask;
            var fileExistsResults = await enrichmentTask;

            if (requestVersion != Volatile.Read(ref _historyLoadRequestVersion))
                return;

            TotalHistoryCount = totalCount;
            UnfiledHistoryCount = unfiledCount;
            UnsubscribeHistoryItems();
            HistoryItems.Clear();
            foreach (var result in fileExistsResults)
            {
                ApplyHistoryItemEnrichment(result, folderNames);
                lock (_historyAddedGate)
                    result.Item.IsRecentlyCompleted = _recentlyCompletedHistoryIds.Contains(result.Item.Id);
                result.Item.PropertyChanged += OnHistoryItemPropertyChanged;
                HistoryItems.Add(result.Item);
            }

            HistoryFolders.Clear();
            foreach (var folder in folders)
            {
                folder.IsSelected = folder.Id == SelectedFolderId;
                HistoryFolders.Add(folder);
            }

            if (SelectedFolderId > 0 && HistoryFolders.All(folder => folder.Id != SelectedFolderId))
                SelectedFolderId = null;

            OnPropertyChanged(nameof(HasHistoryFolders));
            OnPropertyChanged(nameof(HasWorkspaceFolders));
            OnPropertyChanged(nameof(WorkspaceSummaryText));
            OnPropertyChanged(nameof(SelectedFolderTitle));
            ClearSelection();
            RebuildHistoryGroups();
            RebuildBulkTargetFolders(
                bulkTargetSnapshot.Directories,
                bulkTargetSnapshot.ExistingCollectionFolders);
            RestartThumbnailHydration(HistoryItems);
            lock (_initialHistoryLoadGate)
                Volatile.Write(ref _hasLoadedHistory, true);
            loadedSuccessfully = true;
        }
        catch (Exception ex)
        {
            if (requestVersion == Volatile.Read(ref _historyLoadRequestVersion))
                RequestShowNotification?.Invoke($"加载下载历史失败：{ex.Message}", false);
        }
        finally
        {
            IsLoadingHistory = false;
            _historyLoadSemaphore.Release();
            if (loadedSuccessfully)
                SchedulePendingHistoryAdded();
        }
    }

    private void RebuildHistoryGroups()
    {
        HistoryGroups.Clear();
        BatchFolderCards.Clear();

        var visibleItems = HistoryItems
            .Where(MatchesSelectedFolder)
            .ToList();

        var groupsByKey = new Dictionary<string, List<DownloadHistory>>(StringComparer.Ordinal);
        var legacyGroupNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var orderedKeys = new List<string>();
        foreach (var item in visibleItems)
        {
            string key;
            if (item.IsBatchHistory)
            {
                key = $"batch:{item.BatchId}";
            }
            else if (BatchDownloadOrganizer.TryDescribeCollectionUrl(
                         item.Url,
                         out var legacyCollectionKey,
                         out var legacyDisplayName))
            {
                key = $"legacy:{legacyCollectionKey}";
                legacyGroupNames[key] = legacyDisplayName;
            }
            else
            {
                // 普通单条历史共用一个展示组，让 WrapPanel 能在同一行排列多张卡片。
                key = "standalone";
            }
            if (!groupsByKey.TryGetValue(key, out var groupItems))
            {
                groupItems = [];
                groupsByKey.Add(key, groupItems);
                orderedKeys.Add(key);
            }
            groupItems.Add(item);
        }

        foreach (var key in orderedKeys)
        {
            var items = groupsByKey[key];
            var first = items[0];
            var isLegacyCollection = key.StartsWith("legacy:", StringComparison.Ordinal);
            var isBatch = first.IsBatchHistory || (isLegacyCollection && items.Count > 1);
            var inferredCollectionTitle = items
                .Select(item => CollectionNamingService.TryExtractCollectionTitle(
                    item.Title,
                    out var title)
                    ? title
                    : "")
                .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title));
            var name = first.IsBatchHistory
                ? ResolveBatchName(first, inferredCollectionTitle)
                : inferredCollectionTitle
                    ?? legacyGroupNames.GetValueOrDefault(key, first.Title);
            var group = new DownloadHistoryGroup
            {
                Key = key,
                BatchId = first.BatchId,
                Name = name,
                Directory = first.IsBatchHistory
                    ? first.BatchDirectory
                    : ResolveCommonOutputDirectory(items),
                IsBatch = isBatch,
                Items = items,
                IsExpanded = !isBatch,
                IsSelected = string.Equals(SelectedBatchKey, key, StringComparison.Ordinal)
            };

            if (isBatch)
                BatchFolderCards.Add(group);
            else
                HistoryGroups.Add(group);
        }

        var selectedBatch = string.IsNullOrWhiteSpace(SelectedBatchKey)
            ? null
            : BatchFolderCards.FirstOrDefault(group => group.Key == SelectedBatchKey);
        if (selectedBatch is not null)
        {
            selectedBatch.IsExpanded = true;
            HistoryGroups.Clear();
            HistoryGroups.Add(selectedBatch);
            VisibleHistoryCount = selectedBatch.ItemCount;
        }
        else
        {
            ClearSelectedBatchWithoutRefresh();
            VisibleHistoryCount = visibleItems.Count;
        }

        RebuildHistoryCardRows();

        OnPropertyChanged(nameof(HasVisibleHistory));
        OnPropertyChanged(nameof(HasBatchFolders));
        OnPropertyChanged(nameof(HasWorkspaceFolders));
        OnPropertyChanged(nameof(WorkspaceSummaryText));
        OnPropertyChanged(nameof(HasDisplayedHistoryCards));
        OnPropertyChanged(nameof(ShouldShowFolderOnlyHint));
        NotifyVisibleSelectionState();
        NotifyLocationState();
    }

    private async Task RefreshBulkTargetFoldersAsync()
    {
        await _historyLoadSemaphore.WaitAsync();
        try
        {
            if (Volatile.Read(ref _isDisposed))
                return;

            var snapshot = await LoadBulkTargetFolderSnapshotAsync();
            if (Volatile.Read(ref _isDisposed))
                return;

            RebuildBulkTargetFolders(
                snapshot.Directories,
                snapshot.ExistingCollectionFolders);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HistoryViewModel] Target-folder refresh failed: {ex.Message}");
        }
        finally
        {
            _historyLoadSemaphore.Release();
        }
    }

    private async Task<BulkTargetFolderSnapshot> LoadBulkTargetFolderSnapshotAsync(
        IReadOnlyList<DownloadHistory>? allHistoryItems = null)
    {
        var historyItems = allHistoryItems ?? await _historyService.GetAllAsync();
        var existingCollectionFolders = await _historyService.GetExistingCollectionFoldersAsync();
        var knownDirectories = existingCollectionFolders
            .Select(folder => folder.Directory)
            .Concat(historyItems.Select(item => item.BatchDirectory))
            .Concat(historyItems
                .Where(item => string.IsNullOrWhiteSpace(item.BatchDirectory))
                .Select(item => BatchDownloadOrganizer.ResolveOutputDirectory(item.FilePath)))
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToArray();
        var discoveredDirectories = await _directoryDiscoveryService.DiscoverAsync(
            _configService.Config.DefaultDownloadPath,
            knownDirectories);
        return new BulkTargetFolderSnapshot(
            discoveredDirectories,
            existingCollectionFolders);
    }

    private void RebuildBulkTargetFolders(
        IReadOnlyList<string> discoveredDirectories,
        IReadOnlyList<ExistingCollectionFolder> existingCollectionFolders)
    {
        var previousTarget = BulkTargetFolder;
        var targets = new List<HistoryMoveTarget>();
        targets.AddRange(HistoryFolders.Select(folder => new HistoryMoveTarget
        {
            Kind = HistoryMoveTargetKind.Organizer,
            FolderId = folder.Id,
            Name = folder.Name
        }));

        foreach (var directory in discoveredDirectories)
        {
            var visibleBatch = BatchFolderCards.FirstOrDefault(group =>
                ExistingCollectionFolderStore.PathsEqual(group.Directory, directory));
            var persistedBatch = existingCollectionFolders.FirstOrDefault(folder =>
                ExistingCollectionFolderStore.PathsEqual(folder.Directory, directory));
            var displayName = HistoryDirectoryDiscoveryService.DescribeDirectory(
                directory,
                _configService.Config.DefaultDownloadPath);
            var batchName = !string.IsNullOrWhiteSpace(visibleBatch?.Name)
                ? visibleBatch.Name
                : !string.IsNullOrWhiteSpace(persistedBatch?.Name)
                    ? persistedBatch.Name
                    : displayName;
            var batchId = !string.IsNullOrWhiteSpace(visibleBatch?.BatchId)
                ? visibleBatch.BatchId
                : !string.IsNullOrWhiteSpace(persistedBatch?.BatchId)
                    ? persistedBatch.BatchId
                    : BatchDownloadOrganizer.CreateDirectoryGroupId(directory);
            targets.Add(new HistoryMoveTarget
            {
                Kind = HistoryMoveTargetKind.LocalDirectory,
                BatchId = batchId,
                BatchName = batchName,
                Name = displayName,
                Directory = directory
            });
        }

        BulkTargetFolders.Clear();
        foreach (var target in targets)
            BulkTargetFolders.Add(target);

        BulkTargetFolder = previousTarget?.Kind switch
        {
            HistoryMoveTargetKind.Organizer => BulkTargetFolders.FirstOrDefault(target =>
                target.IsOrganizer && target.FolderId == previousTarget.FolderId),
            HistoryMoveTargetKind.LocalDirectory => BulkTargetFolders.FirstOrDefault(target =>
                !target.IsOrganizer
                && ExistingCollectionFolderStore.PathsEqual(
                    target.Directory,
                    previousTarget.Directory)),
            _ => null
        };
        OnPropertyChanged(nameof(HasBulkTargetFolders));
        OnPropertyChanged(nameof(BulkTargetFolderPlaceholderText));
    }

    private bool MatchesSelectedFolder(DownloadHistory item)
        => SelectedFolderId switch
        {
            null => item.FolderId == 0 || !string.IsNullOrWhiteSpace(SearchKeyword),
            0 => item.FolderId == 0,
            var folderId => item.FolderId == folderId
        };

    private IEnumerable<DownloadHistory> GetCurrentLocationItems()
        => HistoryGroups.SelectMany(group => group.Items);

    private IReadOnlyList<DownloadHistory> GetCurrentLocationSummaryItems()
    {
        if (!string.IsNullOrWhiteSpace(SelectedBatchKey))
        {
            var selectedBatch = BatchFolderCards.FirstOrDefault(
                group => string.Equals(group.Key, SelectedBatchKey, StringComparison.Ordinal));
            if (selectedBatch is not null)
                return selectedBatch.Items;
        }

        if (SelectedFolderId is null)
            return HistoryItems.ToList();

        return HistoryItems
            .Where(item => item.FolderId == SelectedFolderId.Value)
            .ToList();
    }

    private string ResolveCurrentLocationPath()
    {
        var items = GetCurrentLocationSummaryItems();
        if (!string.IsNullOrWhiteSpace(SelectedBatchKey))
        {
            var selectedBatch = BatchFolderCards.FirstOrDefault(
                group => string.Equals(group.Key, SelectedBatchKey, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(selectedBatch?.Directory))
                return selectedBatch.Directory.Trim();

            var inferredDirectory = ResolveCommonOutputDirectory(items);
            return !string.IsNullOrWhiteSpace(inferredDirectory)
                ? inferredDirectory
                : items.Count == 0 ? "暂无本地路径" : "多个本地位置";
        }

        if (SelectedFolderId > 0)
        {
            var commonDirectory = ResolveCommonOutputDirectory(items);
            return !string.IsNullOrWhiteSpace(commonDirectory)
                ? commonDirectory
                : items.Count == 0 ? "暂无本地路径" : "多个本地位置";
        }

        return string.IsNullOrWhiteSpace(_configService.Config.DefaultDownloadPath)
            ? "未设置下载路径"
            : _configService.Config.DefaultDownloadPath;
    }

    private static long SumFileSizes(IEnumerable<DownloadHistory> items)
    {
        var total = 0L;
        foreach (var item in items)
        {
            var size = Math.Max(0, item.FileSize);
            if (total > long.MaxValue - size)
                return long.MaxValue;
            total += size;
        }
        return total;
    }

    public void SetHistoryCardColumnCount(int columnCount)
    {
        var normalized = Math.Clamp(columnCount, 1, 8);
        if (_historyCardColumnCount == normalized)
            return;

        _historyCardColumnCount = normalized;
        RebuildHistoryCardRows();
    }

    private void RebuildHistoryCardRows()
    {
        var displayedItems = GetCurrentLocationItems().ToList();
        HistoryCardRows.Clear();
        for (var index = 0; index < displayedItems.Count; index += _historyCardColumnCount)
        {
            HistoryCardRows.Add(new HistoryCardRow
            {
                Items = displayedItems
                    .Skip(index)
                    .Take(_historyCardColumnCount)
                    .ToArray()
            });
        }
    }

    private void NotifyLocationState()
    {
        OnPropertyChanged(nameof(IsAtHistoryRoot));
        OnPropertyChanged(nameof(HasActiveLocation));
        OnPropertyChanged(nameof(SelectedFolderTitle));
        OnPropertyChanged(nameof(CurrentLocationPathText));
        OnPropertyChanged(nameof(CurrentLocationFileCountText));
        OnPropertyChanged(nameof(CurrentLocationSizeText));
        OnPropertyChanged(nameof(IsSearchOrFilterActive));
    }

    private void ClearSelectedBatchWithoutRefresh()
    {
        if (string.IsNullOrWhiteSpace(SelectedBatchKey))
            return;

        _suppressLocationRefresh = true;
        try
        {
            SelectedBatchKey = null;
        }
        finally
        {
            _suppressLocationRefresh = false;
        }
    }

    private void OnHistoryItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadHistory.IsSelected))
        {
            if (!_suppressSelectionRefresh)
                RefreshSelectionState();
        }
    }

    private void UnsubscribeHistoryItems()
    {
        foreach (var item in HistoryItems)
            item.PropertyChanged -= OnHistoryItemPropertyChanged;
    }

    private IReadOnlyList<DownloadHistory> GetSelectedItems()
        => HistoryItems.Where(item => item.IsSelected).ToList();

    private void RefreshSelectionState()
    {
        SelectedCount = HistoryItems.Count(item => item.IsSelected);
        foreach (var group in BatchFolderCards.Concat(HistoryGroups).Distinct())
            group.NotifySelectionStateChanged();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummaryText));
        NotifyVisibleSelectionState();
        MoveSelectedToFolderCommand.NotifyCanExecuteChanged();
        RemoveSelectedFromFolderCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
    }

    private void NotifyVisibleSelectionState()
    {
        OnPropertyChanged(nameof(AreAllVisibleItemsSelected));
        OnPropertyChanged(nameof(SelectAllVisibleActionText));
        OnPropertyChanged(nameof(SelectAllVisibleActionGlyph));
        OnPropertyChanged(nameof(SelectAllVisibleActionDescription));
    }

    private static string ResolveCommonOutputDirectory(IReadOnlyList<DownloadHistory> items)
        => BatchDownloadOrganizer.ResolveCommonOutputDirectory(
            items.Select(item => item.FilePath));

    private static string ResolveBatchName(
        DownloadHistory history,
        string? inferredCollectionTitle)
    {
        if (!string.IsNullOrWhiteSpace(history.BatchName)
            && (!history.BatchName.StartsWith("Bilibili 合集 ·", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(inferredCollectionTitle)))
        {
            return history.BatchName;
        }

        if (!string.IsNullOrWhiteSpace(inferredCollectionTitle))
            return inferredCollectionTitle;

        if (!string.IsNullOrWhiteSpace(history.BatchDirectory))
        {
            try
            {
                var directoryName = Path.GetFileName(
                    history.BatchDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(directoryName))
                    return directoryName;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return "批量下载";
    }

    [RelayCommand]
    private async Task SetMediaFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            filter = "全部";

        SelectedMediaFilter = filter;
        ClearSelection();
        await LoadHistory();
    }

    [RelayCommand]
    private void ShowAllFolders()
        => SelectedFolderId = null;

    [RelayCommand]
    private void ShowUnfiled()
        => SelectedFolderId = 0;

    [RelayCommand]
    private void SelectFolder(HistoryFolder? folder)
    {
        if (folder is not null)
            SelectedFolderId = SelectedFolderId == folder.Id ? null : folder.Id;
    }

    [RelayCommand]
    private void SelectBatchFolder(DownloadHistoryGroup? group)
    {
        if (group is null || !group.IsBatch)
            return;

        SelectedBatchKey = string.Equals(SelectedBatchKey, group.Key, StringComparison.Ordinal)
            ? null
            : group.Key;
    }

    [RelayCommand]
    private void ReturnToHistoryRoot()
    {
        if (SelectedBatchKey is not null)
            SelectedBatchKey = null;
        if (SelectedFolderId is not null)
            SelectedFolderId = null;
    }

    [RelayCommand(CanExecute = nameof(CanCreateFolder))]
    private async Task CreateFolder()
    {
        try
        {
            var folder = await _historyService.CreateFolderAsync(NewFolderName);
            NewFolderName = "";
            await LoadHistory();
            SelectedFolderId = folder.Id;
            RequestShowNotification?.Invoke($"已创建整理文件夹“{folder.Name}”", true);
        }
        catch (Exception ex)
        {
            RequestShowNotification?.Invoke(
                ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                    ? "已存在同名整理文件夹"
                    : ex.Message,
                false);
        }
    }

    [RelayCommand]
    private void BeginRenameFolder(HistoryFolder? folder)
    {
        if (folder is null)
            return;

        foreach (var item in HistoryFolders)
            item.IsRenaming = false;
        folder.EditName = folder.Name;
        folder.IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRenameFolder(HistoryFolder? folder)
    {
        if (folder is not null)
            folder.IsRenaming = false;
    }

    [RelayCommand]
    private async Task SaveRenameFolder(HistoryFolder? folder)
    {
        if (folder is null)
            return;

        try
        {
            await _historyService.RenameFolderAsync(folder.Id, folder.EditName);
            folder.Name = folder.EditName.Trim();
            folder.IsRenaming = false;
            foreach (var item in HistoryItems.Where(item => item.FolderId == folder.Id))
                item.OrganizerFolderName = folder.Name;
            OnPropertyChanged(nameof(SelectedFolderTitle));
            RequestShowNotification?.Invoke("整理文件夹已重命名", true);
        }
        catch (Exception ex)
        {
            RequestShowNotification?.Invoke(
                ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                    ? "已存在同名整理文件夹"
                    : ex.Message,
                false);
        }
    }

    [RelayCommand]
    private async Task DeleteFolder(HistoryFolder? folder)
    {
        if (folder is null)
            return;

        if (ConfirmFunc != null
            && !ConfirmFunc(
                $"确定删除整理文件夹“{folder.Name}”吗？其中 {folder.ItemCount} 条历史会移回“未整理”，不会删除本地文件。",
                "确认删除整理文件夹"))
        {
            return;
        }

        await _historyService.DeleteFolderAsync(folder.Id);
        if (SelectedFolderId == folder.Id)
            SelectedFolderId = null;
        await LoadHistory();
        RequestShowNotification?.Invoke("整理文件夹已删除，本地文件未受影响", true);
    }

    [RelayCommand]
    private void SelectAllVisible()
        => SetVisibleSelection(true);

    [RelayCommand]
    private void ToggleSelectAllVisible()
        => SetVisibleSelection(!AreAllVisibleItemsSelected);

    private void SetVisibleSelection(bool isSelected)
    {
        _suppressSelectionRefresh = true;
        try
        {
            foreach (var item in GetCurrentLocationItems())
                item.IsSelected = isSelected;
        }
        finally
        {
            _suppressSelectionRefresh = false;
            RefreshSelectionState();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ClearSelection()
    {
        _suppressSelectionRefresh = true;
        try
        {
            foreach (var item in HistoryItems.Where(item => item.IsSelected))
                item.IsSelected = false;
        }
        finally
        {
            _suppressSelectionRefresh = false;
            RefreshSelectionState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveSelectedToFolder))]
    private async Task MoveSelectedToFolder()
    {
        if (BulkTargetFolder is null)
            return;

        var historyIds = GetSelectedItems().Select(item => item.Id).ToList();
        if (BulkTargetFolder.IsOrganizer)
        {
            await MoveItemsToFolderAsync(historyIds, BulkTargetFolder.FolderId);
            return;
        }

        await MoveItemsToDirectoryGroupAsync(historyIds, BulkTargetFolder);
    }

    private bool CanMoveSelectedToFolder()
    {
        if (BulkTargetFolder is null)
            return false;

        return HistoryItems.Any(item => item.IsSelected
            && (BulkTargetFolder.IsOrganizer
                ? item.FolderId != BulkTargetFolder.FolderId
                : item.FolderId > 0
                  || !string.Equals(
                      item.BatchId,
                      BulkTargetFolder.BatchId,
                      StringComparison.Ordinal)
                  || !ExistingCollectionFolderStore.PathsEqual(
                      item.BatchDirectory,
                      BulkTargetFolder.Directory)));
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedFromFolder))]
    private async Task RemoveSelectedFromFolder()
        => await MoveItemsToFolderAsync(
            GetSelectedItems().Select(item => item.Id).ToList(),
            0);

    private bool CanRemoveSelectedFromFolder()
        => HistoryItems.Any(item => item.IsSelected && item.FolderId > 0);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteSelected()
    {
        var selected = GetSelectedItems();
        if (selected.Count == 0)
            return;

        if (ConfirmFunc != null
            && !ConfirmFunc(
                $"确定删除选中的 {selected.Count} 条历史记录吗？不会删除已经下载的本地文件。",
                "确认批量删除历史"))
        {
            return;
        }

        await _historyService.DeleteManyAsync(selected.Select(item => item.Id));
        await LoadHistory();
        RequestShowNotification?.Invoke($"已删除 {selected.Count} 条历史记录，本地文件未受影响", true);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteSelectedFilesAndRecords()
    {
        var selected = GetSelectedItems();
        if (selected.Count == 0)
            return;

        if (ConfirmFunc?.Invoke(
                $"将永久删除选中 {selected.Count} 项的本地文件，并删除对应历史记录。此操作不可恢复。是否继续？",
                "删除本地文件和历史") != true)
        {
            return;
        }

        var allowedRoots = new[] { _configService.Config.DefaultDownloadPath }
            .Concat(selected.Select(item => item.BatchDirectory));
        var result = await Task.Run(() => _fileDeletionService.DeleteFiles(selected, allowedRoots));
        await _historyService.DeleteManyAsync(selected.Select(item => item.Id));
        await LoadHistory();

        var message = $"已删除 {result.DeletedFileCount} 个本地文件和 {selected.Count} 条历史记录";
        if (result.SkippedUnsafePathCount > 0 || result.Errors.Count > 0)
            message += $"；{result.SkippedUnsafePathCount + result.Errors.Count} 个文件因路径或权限问题未删除";
        RequestShowNotification?.Invoke(message, result.Errors.Count == 0);
    }

    [RelayCommand]
    private async Task CleanMissingHistoryRecords()
    {
        var allItems = await _historyService.GetAllAsync();
        var missing = allItems.Where(item =>
            {
                var candidates = EnumerateLocalFilePaths(item).ToArray();
                return candidates.Length > 0
                       && candidates.All(path => !File.Exists(path) && !Directory.Exists(path));
            })
            .ToList();
        if (missing.Count == 0)
        {
            RequestShowNotification?.Invoke("没有发现失效的历史记录。", true);
            return;
        }

        if (ConfirmFunc?.Invoke(
                $"发现 {missing.Count} 条记录对应的本地文件已不存在。是否清理这些记录？",
                "清理失效历史") != true)
        {
            return;
        }

        await _historyService.DeleteManyAsync(missing.Select(item => item.Id));
        await LoadHistory();
        RequestShowNotification?.Invoke($"已清理 {missing.Count} 条失效历史记录。", true);
    }

    private static IEnumerable<string> EnumerateLocalFilePaths(DownloadHistory item)
    {
        if (!string.IsNullOrWhiteSpace(item.FilePath))
            yield return item.FilePath;
        foreach (var path in item.AttachmentFilePaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
                yield return path;
        }
    }

    public IReadOnlyList<long> PrepareHistoryDrag(long itemId)
    {
        var item = HistoryItems.FirstOrDefault(candidate => candidate.Id == itemId);
        if (item is null)
            return [];

        if (!item.IsSelected)
        {
            ClearSelection();
            item.IsSelected = true;
        }

        return GetSelectedItems().Select(candidate => candidate.Id).ToList();
    }

    public async Task MoveItemsToFolderAsync(IReadOnlyCollection<long> historyIds, long folderId)
    {
        if (historyIds.Count == 0 || folderId < 0)
            return;

        await _historyService.MoveToFolderAsync(historyIds, folderId);
        var destinationName = folderId == 0
            ? "历史首页"
            : HistoryFolders.FirstOrDefault(folder => folder.Id == folderId)?.Name ?? "整理文件夹";
        await LoadHistory();
        if (folderId == 0)
        {
            SelectedBatchKey = null;
            SelectedFolderId = null;
        }
        else
        {
            SelectedFolderId = folderId;
        }
        RequestShowNotification?.Invoke(
            $"已将 {historyIds.Count} 项整理到“{destinationName}”（本地文件未移动）",
            true);
    }

    private async Task MoveItemsToDirectoryGroupAsync(
        IReadOnlyCollection<long> historyIds,
        HistoryMoveTarget target)
    {
        if (historyIds.Count == 0
            || target.IsOrganizer
            || string.IsNullOrWhiteSpace(target.Directory))
        {
            return;
        }

        await _historyService.MoveToDirectoryGroupAsync(
            historyIds,
            target.BatchId,
            target.BatchName,
            target.Directory);
        await LoadHistory();
        SelectedFolderId = null;
        SelectedBatchKey = $"batch:{target.BatchId}";
        RequestShowNotification?.Invoke(
            $"已将 {historyIds.Count} 项归类到“{target.Name}”（本地文件未移动）",
            true);
    }

    public Func<string, string, bool>? ConfirmFunc { get; set; } = ConfirmationDialogService.Show;

    /// <summary>
    /// 清空全部历史
    /// </summary>
    [RelayCommand]
    private async Task ClearAll()
    {
        if (ConfirmFunc != null && !ConfirmFunc("确定要清空全部下载历史记录吗？此操作不可恢复。", "确认清空记录"))
        {
            return;
        }

        await _historyLoadSemaphore.WaitAsync();
        try
        {
            CancelThumbnailHydration();
            await _historyService.ClearAllAsync();
            lock (_historyAddedGate)
            {
                _pendingHistoryAdded.Clear();
                _recentlyCompletedHistoryIds.Clear();
                _historyAddedDrainScheduled = false;
            }

            UnsubscribeHistoryItems();
            HistoryItems.Clear();
            ReturnToHistoryRoot();
            HistoryGroups.Clear();
            HistoryCardRows.Clear();
            BatchFolderCards.Clear();
            TotalHistoryCount = 0;
            VisibleHistoryCount = 0;
            UnfiledHistoryCount = 0;
            foreach (var folder in HistoryFolders)
                folder.ItemCount = 0;
            ClearSelection();
            OnPropertyChanged(nameof(HasVisibleHistory));
            OnPropertyChanged(nameof(HasBatchFolders));
            OnPropertyChanged(nameof(HasWorkspaceFolders));
            OnPropertyChanged(nameof(WorkspaceSummaryText));
            OnPropertyChanged(nameof(HasDisplayedHistoryCards));
            OnPropertyChanged(nameof(ShouldShowFolderOnlyHint));
            NotifyLocationState();
        }
        finally
        {
            _historyLoadSemaphore.Release();
        }
    }

    private void CancelThumbnailHydration()
    {
        ThumbnailHydrationSession? session;
        lock (_thumbnailHydrationGate)
        {
            session = _thumbnailHydrationSession;
            _thumbnailHydrationSession = null;
        }

        session?.Retire();
    }

    public void Dispose()
    {
        lock (_thumbnailHydrationGate)
        {
            if (_isDisposed)
                return;

            Volatile.Write(ref _isDisposed, true);
        }

        _historyService.HistoryAdded -= OnHistoryAdded;
        lock (_historyAddedGate)
        {
            Volatile.Write(ref _hasLoadedHistory, false);
            _pendingHistoryAdded.Clear();
            _recentlyCompletedHistoryIds.Clear();
            _historyAddedDrainScheduled = false;
        }
        CancelPendingSearch();
        CancelThumbnailHydration();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 清除筛选和搜索词
    /// </summary>
    [RelayCommand]
    private async Task ClearFilterAndSearch()
    {
        SearchKeyword = "";
        SelectedMediaFilter = "全部";
        SelectedBatchKey = null;
        SelectedFolderId = null;
        await LoadHistory();
    }

    /// <summary>
    /// 删除单条记录
    /// </summary>
    [RelayCommand]
    private async Task DeleteItem(long id)
    {
        await _historyService.DeleteAsync(id);
        await LoadHistory();
    }

    [RelayCommand]
    private void ToggleHistoryGroup(DownloadHistoryGroup? group)
    {
        if (group is not null && group.IsBatch)
            group.IsExpanded = !group.IsExpanded;
    }

    [RelayCommand]
    private void SelectHistoryGroup(DownloadHistoryGroup? group)
    {
        if (group is null)
            return;

        var shouldSelect = group.Items.Any(item => !item.IsSelected);
        _suppressSelectionRefresh = true;
        try
        {
            foreach (var item in group.Items)
                item.IsSelected = shouldSelect;
        }
        finally
        {
            _suppressSelectionRefresh = false;
            RefreshSelectionState();
        }
    }

    [RelayCommand]
    private async Task DeleteBatch(DownloadHistoryGroup? group)
    {
        if (group is null || !group.IsBatch)
            return;

        if (ConfirmFunc != null
            && !ConfirmFunc(
                $"确定删除“{group.Name}”的 {group.ItemCount} 条历史记录吗？不会删除已经下载的文件。",
                "确认删除批次记录"))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(group.BatchId))
        {
            await _historyService.DeleteBatchAsync(group.BatchId);
        }
        else
        {
            foreach (var item in group.Items)
                await _historyService.DeleteAsync(item.Id);
        }
        await LoadHistory();
    }

    [RelayCommand]
    private async Task OpenDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;

        await Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(directory))
                    return;

                _startProcess(new ProcessStartInfo
                {
                    FileName = directory,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        });
    }

    /// <summary>
    /// 打开文件所在文件夹
    /// </summary>
    [RelayCommand]
    private async Task OpenFolder(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;

        // 放到后台线程去查询目录和启动 Explorer，避免卡死 UI 线程
        await Task.Run(() =>
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(filePath);
                if (dir != null && System.IO.Directory.Exists(dir))
                {
                    _startProcess(CreateOpenFolderStartInfo(filePath));
                }
            }
            catch { }
        });
    }

    [RelayCommand]
    private async Task PreviewFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        await Task.Run(() =>
        {
            try
            {
                var targetPath = MediaPreviewFileResolver.Resolve(filePath);
                if (!System.IO.File.Exists(targetPath))
                    return;

                _startProcess(new ProcessStartInfo
                {
                    FileName = targetPath,
                    UseShellExecute = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(targetPath) ?? ""
                });
            }
            catch
            {
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanOpenSourceUrl))]
    private async Task OpenSourceUrl(string url)
    {
        if (!CanOpenSourceUrl(url)) return;

        await Task.Run(() =>
        {
            try
            {
                _startProcess(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        });
    }

    private static bool CanOpenSourceUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static void StartProcess(ProcessStartInfo startInfo)
    {
        Process.Start(startInfo);
    }

    internal static ProcessStartInfo CreateOpenFolderStartInfo(string filePath)
        => new()
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{filePath}\"",
            UseShellExecute = true
        };

    private static string DescribeStorageStatus(string downloadPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(downloadPath);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                return "磁盘空间不可用";

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return "磁盘空间不可用";

            return $"{drive.Name} {FormatAvailableSpace(drive.AvailableFreeSpace)}";
        }
        catch
        {
            return "磁盘空间不可用";
        }
    }

    internal static string FormatAvailableSpace(long bytes)
        => ByteSizeFormatter.FormatClampZero(bytes, " 可用");

    private static bool MatchesMediaFilter(DownloadHistory item, string filter)
    {
        return filter switch
        {
            "音频" => IsAudioFormat(item.Format),
            "视频" => !IsAudioFormat(item.Format),
            _ => true
        };
    }

    private static bool IsAudioFormat(string? format)
    {
        return format?.Trim().ToLowerInvariant() switch
        {
            "mp3" or "m4a" or "wav" or "flac" or "aac" or "opus" or "ogg" => true,
            _ => false
        };
    }

    private static HistoryItemEnrichment EnrichHistoryItem(DownloadHistory item)
        => new(
            item,
            ResolveExistingHistoryPath(item),
            BuildDouyinManifestSummary(item));

    private static void ApplyHistoryItemEnrichment(
        HistoryItemEnrichment result,
        IReadOnlyDictionary<long, string> folderNames)
    {
        result.Item.AvailableFilePath = result.AvailableFilePath;
        result.Item.FileExists = !string.IsNullOrWhiteSpace(result.AvailableFilePath);
        result.Item.DouyinManifestSummary = result.DouyinManifestSummary.Summary;
        result.Item.DouyinManifestSummaryText = result.DouyinManifestSummary.SummaryText;
        result.Item.OrganizerFolderName = folderNames.GetValueOrDefault(result.Item.FolderId, "");
    }

    private static string ResolveExistingHistoryPath(DownloadHistory item)
    {
        if (!IsDouyinManifestPath(item.FilePath) && PathExists(item.FilePath))
            return item.FilePath;

        return item.AttachmentFilePaths
            .FirstOrDefault(path => !IsDouyinManifestPath(path) && PathExists(path))
            ?? "";
    }

    private static DouyinManifestSummaryResult BuildDouyinManifestSummary(DownloadHistory item)
    {
        var manifestPath = ResolveSafeDouyinManifestPath(item);
        if (string.IsNullOrWhiteSpace(manifestPath))
            return DouyinManifestSummaryResult.Empty;

        var summary = DouyinManifestReader.ReadSummary(manifestPath);
        if (summary is null)
            return DouyinManifestSummaryResult.Empty;

        var attachmentCount = item.AttachmentFilePaths
            .Count(path => !IsDouyinManifestPath(path));
        return new DouyinManifestSummaryResult(
            FormatDouyinManifestSummary(summary, attachmentCount),
            summary);
    }

    private static string ResolveSafeDouyinManifestPath(DownloadHistory item)
    {
        var anchorPaths = ResolveExistingNonManifestAnchorPaths(item);
        if (anchorPaths.Count == 0)
            return "";

        foreach (var rawPath in EnumerateDouyinManifestCandidatePaths(item))
        {
            if (!IsDouyinManifestPath(rawPath))
                continue;

            try
            {
                var fullPath = Path.GetFullPath(rawPath.Trim());
                var manifestDirectory = Path.GetDirectoryName(fullPath);
                if (File.Exists(fullPath)
                    && !string.IsNullOrWhiteSpace(manifestDirectory)
                    && IsSafeDouyinManifestParentDirectory(manifestDirectory)
                    && anchorPaths.All(anchorPath => IsDirectoryAncestorOfPathOrSelf(manifestDirectory, anchorPath)))
                {
                    return fullPath;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return "";
    }

    private static IEnumerable<string> EnumerateDouyinManifestCandidatePaths(DownloadHistory item)
    {
        if (!string.IsNullOrWhiteSpace(item.FilePath))
            yield return item.FilePath;

        foreach (var path in item.AttachmentFilePaths)
            yield return path;
    }

    private static List<string> ResolveExistingNonManifestAnchorPaths(DownloadHistory item)
    {
        var anchorPaths = new List<string>();
        AddExistingNonManifestAnchorPath(anchorPaths, item.FilePath);

        foreach (var path in item.AttachmentFilePaths)
            AddExistingNonManifestAnchorPath(anchorPaths, path);

        return anchorPaths;
    }

    private static void AddExistingNonManifestAnchorPath(List<string> anchorPaths, string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || IsDouyinManifestPath(rawPath))
            return;

        try
        {
            var fullPath = Path.GetFullPath(rawPath.Trim());
            if (File.Exists(fullPath) && !anchorPaths.Any(path => AreEquivalentPaths(path, fullPath)))
                anchorPaths.Add(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
    }

    internal static bool IsSafeDouyinManifestParentDirectory(string manifestDirectory)
    {
        if (string.IsNullOrWhiteSpace(manifestDirectory))
            return false;

        try
        {
            var fullDirectory = Path.GetFullPath(manifestDirectory.Trim());
            var root = Path.GetPathRoot(fullDirectory);
            if (string.IsNullOrWhiteSpace(root))
                return false;

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return !string.Equals(
                TrimTrailingDirectorySeparators(fullDirectory),
                TrimTrailingDirectorySeparators(root),
                comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsDirectoryAncestorOfPathOrSelf(string ancestorDirectory, string path)
    {
        try
        {
            var fullAncestor = Path.GetFullPath(ancestorDirectory);
            var fullPath = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (string.Equals(fullAncestor, fullPath, comparison))
                return true;

            var ancestorWithSeparator = fullAncestor.EndsWith(Path.DirectorySeparatorChar)
                || fullAncestor.EndsWith(Path.AltDirectorySeparatorChar)
                    ? fullAncestor
                    : fullAncestor + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(ancestorWithSeparator, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string TrimTrailingDirectorySeparators(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string FormatDouyinManifestSummary(DouyinManifestSummary summary, int attachmentCount)
    {
        var itemCountText = summary.IsTruncated
            ? $"{summary.ItemCount}+"
            : summary.ItemCount.ToString();
        var parts = new List<string> { $"作品 {itemCountText}" };
        if (summary.VideoCount > 0)
            parts.Add($"视频 {summary.VideoCount}");
        if (summary.GalleryCount > 0)
            parts.Add($"图文 {summary.GalleryCount}");
        if (summary.MusicCount > 0)
            parts.Add($"音乐 {summary.MusicCount}");
        parts.Add($"附属 {Math.Max(0, attachmentCount)}");
        return string.Join(" / ", parts);
    }

    private static bool IsDouyinManifestPath(string path)
        => DouyinSpecialDownloadService.IsDouyinManifestPath(path);

    private static bool AreEquivalentPaths(string left, string right)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }
    }

    private static bool PathExists(string path)
        => !string.IsNullOrWhiteSpace(path)
           && (System.IO.File.Exists(path) || System.IO.Directory.Exists(path));

    private sealed class ThumbnailHydrationSession
    {
        private readonly CancellationTokenSource _source = new();
        private Task _completion = Task.CompletedTask;

        public CancellationToken Token => _source.Token;

        public void Track(Task task)
            => _completion = _completion.IsCompletedSuccessfully
                ? task
                : Task.WhenAll(_completion, task);

        public void Retire()
        {
            _source.Cancel();
            _ = DisposeWhenCompleteAsync();
        }

        private async Task DisposeWhenCompleteAsync()
        {
            try
            {
                await _completion;
            }
            catch
            {
                // Cancellation and extraction errors are already handled by the hydration task.
            }
            finally
            {
                _source.Dispose();
            }
        }
    }

    private sealed record HistoryItemEnrichment(
        DownloadHistory Item,
        string AvailableFilePath,
        DouyinManifestSummaryResult DouyinManifestSummary);

    private sealed record BulkTargetFolderSnapshot(
        IReadOnlyList<string> Directories,
        IReadOnlyList<ExistingCollectionFolder> ExistingCollectionFolders);

    private sealed record DouyinManifestSummaryResult(
        string SummaryText,
        DouyinManifestSummary? Summary)
    {
        public static DouyinManifestSummaryResult Empty { get; } = new("", null);
    }
}
