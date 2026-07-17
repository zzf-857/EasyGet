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
public partial class HistoryViewModel : ObservableObject
{
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(300);

    private readonly HistoryService _historyService;
    private readonly ConfigService _configService;
    private readonly Action<ProcessStartInfo> _startProcess;
    private readonly SemaphoreSlim _historyLoadSemaphore = new(1, 1);
    private CancellationTokenSource? _searchCts;
    private int _historyLoadRequestVersion;
    private bool _suppressSelectionRefresh;

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

    [ObservableProperty] private bool _isLoadingHistory;
    [ObservableProperty] private int _visibleHistoryCount;
    [ObservableProperty] private int _unfiledHistoryCount;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _newFolderName = "";
    [ObservableProperty] private HistoryFolder? _bulkTargetFolder;

    public bool IsSearchOrFilterActive
        => !string.IsNullOrEmpty(SearchKeyword)
           || SelectedMediaFilter != "全部"
           || SelectedFolderId is not null;

    public bool IsShowingAllFolders => SelectedFolderId is null;
    public bool IsShowingUnfiled => SelectedFolderId == 0;
    public bool HasSelection => SelectedCount > 0;
    public bool HasVisibleHistory => VisibleHistoryCount > 0;
    public bool HasHistoryFolders => HistoryFolders.Count > 0;
    public bool CanCreateFolder => !string.IsNullOrWhiteSpace(NewFolderName);
    public string SelectionSummaryText => $"已选择 {SelectedCount} 项";
    public string SelectedFolderTitle => SelectedFolderId switch
    {
        null => "全部记录",
        0 => "未整理",
        _ => HistoryFolders.FirstOrDefault(folder => folder.Id == SelectedFolderId)?.Name ?? "整理文件夹"
    };

    [ObservableProperty] private int _totalHistoryCount;
    [ObservableProperty] private string _storageStatusText = "磁盘空间获取中";
    [ObservableProperty] private double _storageFreePercentage = 0;

    partial void OnNewFolderNameChanged(string value)
        => CreateFolderCommand.NotifyCanExecuteChanged();

    partial void OnBulkTargetFolderChanged(HistoryFolder? value)
        => MoveSelectedToFolderCommand.NotifyCanExecuteChanged();

    partial void OnSelectedFolderIdChanged(long? value)
    {
        var selectedCardId = value ?? -1;
        foreach (var folder in FolderCards)
            folder.IsSelected = selectedCardId == folder.Id;
        ClearSelection();
        RebuildHistoryGroups();
        OnPropertyChanged(nameof(SelectedFolderTitle));
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
    public ObservableCollection<HistoryFolder> HistoryFolders { get; } = [];
    public ObservableCollection<HistoryFolder> FolderCards { get; } = [];

    public event Action<string, bool>? RequestShowNotification;

    public HistoryViewModel(HistoryService historyService)
        : this(historyService, new ConfigService(), StartProcess)
    {
    }

    public HistoryViewModel(HistoryService historyService, ConfigService configService)
        : this(historyService, configService, StartProcess)
    {
    }

    internal HistoryViewModel(HistoryService historyService, Action<ProcessStartInfo> startProcess)
        : this(historyService, new ConfigService(), startProcess)
    {
    }

    internal HistoryViewModel(HistoryService historyService, ConfigService configService, Action<ProcessStartInfo> startProcess)
    {
        _historyService = historyService;
        _configService = configService;
        _startProcess = startProcess;
    }

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
            var filteredItems = items
                .Where(item => MatchesMediaFilter(item, mediaFilter))
                .ToList();
            var folderNames = folders.ToDictionary(folder => folder.Id, folder => folder.Name);

            var fileExistsResults = await Task.Run(() => filteredItems
                .Select(item => new
                {
                    Item = item,
                    AvailableFilePath = ResolveExistingHistoryPath(item),
                    DouyinManifestSummary = BuildDouyinManifestSummary(item)
                })
                .ToList());

            if (requestVersion != Volatile.Read(ref _historyLoadRequestVersion))
                return;

            TotalHistoryCount = totalCount;
            UnfiledHistoryCount = unfiledCount;
            UnsubscribeHistoryItems();
            HistoryItems.Clear();
            foreach (var result in fileExistsResults)
            {
                result.Item.AvailableFilePath = result.AvailableFilePath;
                result.Item.FileExists = !string.IsNullOrWhiteSpace(result.AvailableFilePath);
                result.Item.DouyinManifestSummary = result.DouyinManifestSummary.Summary;
                result.Item.DouyinManifestSummaryText = result.DouyinManifestSummary.SummaryText;
                result.Item.OrganizerFolderName = folderNames.GetValueOrDefault(result.Item.FolderId, "");
                result.Item.PropertyChanged += OnHistoryItemPropertyChanged;
                HistoryItems.Add(result.Item);
            }

            HistoryFolders.Clear();
            FolderCards.Clear();
            FolderCards.Add(new HistoryFolder
            {
                Id = -1,
                Name = "全部记录",
                ItemCount = totalCount,
                IsSystemFolder = true,
                IsSelected = SelectedFolderId is null
            });
            FolderCards.Add(new HistoryFolder
            {
                Id = 0,
                Name = "未整理",
                ItemCount = unfiledCount,
                IsSystemFolder = true,
                IsSelected = SelectedFolderId == 0
            });
            foreach (var folder in folders)
            {
                folder.IsSelected = folder.Id == SelectedFolderId;
                HistoryFolders.Add(folder);
                FolderCards.Add(folder);
            }

            if (BulkTargetFolder is not null)
            {
                BulkTargetFolder = HistoryFolders.FirstOrDefault(
                    folder => folder.Id == BulkTargetFolder.Id);
            }

            if (SelectedFolderId > 0 && HistoryFolders.All(folder => folder.Id != SelectedFolderId))
                SelectedFolderId = null;

            OnPropertyChanged(nameof(HasHistoryFolders));
            OnPropertyChanged(nameof(SelectedFolderTitle));
            ClearSelection();
            RebuildHistoryGroups();
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
        }
    }

    private void RebuildHistoryGroups()
    {
        var previousExpansion = HistoryGroups
            .ToDictionary(group => group.Key, group => group.IsExpanded, StringComparer.Ordinal);
        HistoryGroups.Clear();

        var visibleItems = HistoryItems
            .Where(MatchesSelectedFolder)
            .ToList();
        VisibleHistoryCount = visibleItems.Count;
        OnPropertyChanged(nameof(HasVisibleHistory));

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
            HistoryGroups.Add(new DownloadHistoryGroup
            {
                Key = key,
                BatchId = first.BatchId,
                Name = name,
                Directory = first.IsBatchHistory
                    ? first.BatchDirectory
                    : ResolveCommonOutputDirectory(items),
                IsBatch = isBatch,
                Items = items,
                IsExpanded = previousExpansion.TryGetValue(key, out var expanded)
                    ? expanded
                    : !isBatch
            });
        }
    }

    private bool MatchesSelectedFolder(DownloadHistory item)
        => SelectedFolderId switch
        {
            null => true,
            0 => item.FolderId == 0,
            var folderId => item.FolderId == folderId
        };

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
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummaryText));
        MoveSelectedToFolderCommand.NotifyCanExecuteChanged();
        RemoveSelectedFromFolderCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
    }

    private static string ResolveCommonOutputDirectory(IReadOnlyList<DownloadHistory> items)
    {
        var directories = items
            .Select(item => TryGetParentDirectory(item.FilePath))
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToList();
        return directories.Count == 1 ? directories[0] : "";
    }

    private static string TryGetParentDirectory(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "";

        try
        {
            return Path.GetDirectoryName(filePath) ?? "";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

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
            SelectedFolderId = folder.Id < 0 ? null : folder.Id;
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
    {
        _suppressSelectionRefresh = true;
        try
        {
            foreach (var item in HistoryItems.Where(MatchesSelectedFolder))
                item.IsSelected = true;
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

        await MoveItemsToFolderAsync(
            GetSelectedItems().Select(item => item.Id).ToList(),
            BulkTargetFolder.Id);
    }

    private bool CanMoveSelectedToFolder()
        => BulkTargetFolder is not null
           && HistoryItems.Any(item => item.IsSelected && item.FolderId != BulkTargetFolder.Id);

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
            ? "未整理"
            : HistoryFolders.FirstOrDefault(folder => folder.Id == folderId)?.Name ?? "整理文件夹";
        await LoadHistory();
        SelectedFolderId = folderId == 0 ? 0 : folderId;
        RequestShowNotification?.Invoke(
            $"已将 {historyIds.Count} 项整理到“{destinationName}”（本地文件未移动）",
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
        await _historyService.ClearAllAsync();
        UnsubscribeHistoryItems();
        HistoryItems.Clear();
        HistoryGroups.Clear();
        TotalHistoryCount = 0;
        VisibleHistoryCount = 0;
        UnfiledHistoryCount = 0;
        foreach (var folder in FolderCards)
            folder.ItemCount = 0;
        ClearSelection();
        OnPropertyChanged(nameof(HasVisibleHistory));
    }

    /// <summary>
    /// 清除筛选和搜索词
    /// </summary>
    [RelayCommand]
    private async Task ClearFilterAndSearch()
    {
        SearchKeyword = "";
        SelectedMediaFilter = "全部";
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

    private static bool IsAudioFormat(string format)
    {
        return format.Trim().ToLowerInvariant() switch
        {
            "mp3" or "m4a" or "wav" or "flac" or "aac" or "opus" or "ogg" => true,
            _ => false
        };
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

    private sealed record DouyinManifestSummaryResult(
        string SummaryText,
        DouyinManifestSummary? Summary)
    {
        public static DouyinManifestSummaryResult Empty { get; } = new("", null);
    }
}
