using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyGet.Models;
using EasyGet.Services;

namespace EasyGet.ViewModels;

public partial class CollectionUpdateItemViewModel : ObservableObject
{
    public CollectionUpdateItemViewModel(CollectionSubscriptionItem item, bool isSelected = true)
    {
        Item = item;
        _isSelected = isSelected;
    }

    public CollectionSubscriptionItem Item { get; }

    [ObservableProperty] private bool _isSelected;

    public string Title => string.IsNullOrWhiteSpace(Item.Title) ? Item.Url : Item.Title;
    public string Url => Item.Url;
    public string PositionText => Item.Position > 0 ? $"{Item.Position:00}" : "--";
    public string StateText => Item.State == CollectionSubscriptionItemState.Failed ? "上次失败" : "新增";
}

public partial class CollectionUpdatesViewModel : ObservableObject, IDisposable
{
    private readonly CollectionRefreshService _refreshService;
    private readonly DownloadManager _downloadManager;
    private readonly DownloadPreflightService _preflightService;
    private readonly Func<string, string?> _selectDirectory;
    private readonly Func<Task<List<CollectionSubscription>>> _loadSubscriptions;
    private readonly Func<long, IReadOnlyCollection<CollectionSubscriptionItemStateUpdate>, Task<int>>
        _updateItemStates;
    private readonly Func<CollectionSubscription, Task<CollectionSubscription>> _updateSubscription;
    private readonly Func<long, Task<bool>> _deleteSubscription;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _initializeLock = new();
    private readonly Dictionary<(long SubscriptionId, string EntryKey), bool> _selectionStates = [];
    private TaskCompletionSource? _initializeCompletion;
    private bool _reloadRequested;
    private long _selectionRequestVersion;

    [ObservableProperty] private CollectionSubscription? _selectedSubscription;
    private bool _isLoading;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isOperating;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
                OnPropertyChanged(nameof(IsBusy));
        }
    }

    public CollectionUpdatesViewModel(
        HistoryService historyService,
        CollectionRefreshService refreshService,
        DownloadManager downloadManager,
        DownloadPreflightService preflightService)
        : this(
            historyService,
            refreshService,
            downloadManager,
            preflightService,
            SelectDirectory)
    {
    }

    internal CollectionUpdatesViewModel(
        HistoryService historyService,
        CollectionRefreshService refreshService,
        DownloadManager downloadManager,
        DownloadPreflightService preflightService,
        Func<string, string?> selectDirectory,
        Func<Task<List<CollectionSubscription>>>? loadSubscriptions = null,
        Func<long, IReadOnlyCollection<CollectionSubscriptionItemStateUpdate>, Task<int>>?
            updateItemStates = null,
        Func<CollectionSubscription, Task<CollectionSubscription>>? updateSubscription = null,
        Func<long, Task<bool>>? deleteSubscription = null)
    {
        _refreshService = refreshService;
        _downloadManager = downloadManager;
        _preflightService = preflightService;
        _selectDirectory = selectDirectory;
        _loadSubscriptions = loadSubscriptions ?? historyService.GetCollectionSubscriptionsAsync;
        _updateItemStates = updateItemStates
                            ?? ((subscriptionId, updates) =>
                                historyService.UpdateCollectionSubscriptionItemStatesAsync(
                                    subscriptionId,
                                    updates));
        _updateSubscription = updateSubscription ?? historyService.UpdateCollectionSubscriptionAsync;
        _deleteSubscription = deleteSubscription ?? historyService.DeleteCollectionSubscriptionAsync;
        PendingItems.CollectionChanged += (_, _) => RefreshSelectionState();
        _downloadManager.TaskFinished += OnDownloadTaskFinished;
    }

    public ObservableCollection<CollectionSubscription> Subscriptions { get; } = [];
    public ObservableCollection<CollectionUpdateItemViewModel> PendingItems { get; } = [];

    public int PendingCount => Subscriptions.Sum(subscription => subscription.PendingNewCount);
    public int SelectedCount => PendingItems.Count(item => item.IsSelected);
    public bool HasSubscriptions => Subscriptions.Count > 0;
    public bool HasPendingItems => PendingItems.Count > 0;
    public bool HasSelectedItems => SelectedCount > 0;
    public bool IsBusy => IsLoading || IsOperating;
    public string PendingSummary => SelectedSubscription is null
        ? "选择一个已跟踪合集"
        : PendingItems.Count == 0
            ? "当前没有待下载的新视频"
            : $"发现 {PendingItems.Count} 个更新，已选择 {SelectedCount} 个";
    public string SavedDirectory => SelectedSubscription?.OutputDirectory ?? "";
    public bool IsSavedDirectoryAvailable => !string.IsNullOrWhiteSpace(SavedDirectory)
                                             && Directory.Exists(SavedDirectory);

    public event Action<string, bool>? RequestShowNotification;
    public event Action<int>? PendingCountChanged;
    public Func<string, string, bool>? ConfirmFunc { get; set; } = ConfirmationDialogService.Show;

    public Task InitializeAsync()
    {
        TaskCompletionSource completion;
        lock (_initializeLock)
        {
            _reloadRequested = true;
            if (_initializeCompletion is not null)
                return _initializeCompletion.Task;

            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _initializeCompletion = completion;
        }

        _ = RunInitializeLoopAsync(completion);
        return completion.Task;
    }

    private async Task RunInitializeLoopAsync(TaskCompletionSource completion)
    {
        IsLoading = true;
        while (true)
        {
            lock (_initializeLock)
                _reloadRequested = false;

            try
            {
                await LoadSubscriptionsOnceAsync();
            }
            catch (Exception ex)
            {
                RequestShowNotification?.Invoke($"读取合集更新失败：{ex.Message}", false);
            }

            lock (_initializeLock)
            {
                if (_reloadRequested)
                    continue;

                // Update the backing field before exposing the loop as idle. If a new request
                // starts immediately after the lock is released, observers still read true.
                _isLoading = false;
                _initializeCompletion = null;
            }

            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsBusy));
            completion.TrySetResult();
            return;
        }
    }

    private async Task LoadSubscriptionsOnceAsync()
    {
        var selectedId = SelectedSubscription?.Id;
        var subscriptions = await _loadSubscriptions();
        Subscriptions.Clear();
        foreach (var subscription in subscriptions
                     .OrderByDescending(item => item.PendingNewCount)
                     .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            Subscriptions.Add(subscription);
        }

        PruneSelectionStates();
        SelectedSubscription = Subscriptions.FirstOrDefault(item => item.Id == selectedId)
                               ?? Subscriptions.FirstOrDefault();
        NotifySubscriptionStateChanged();
    }

    public async Task SelectSubscriptionAsync(long subscriptionId)
    {
        var requestVersion = Interlocked.Increment(ref _selectionRequestVersion);
        await InitializeAsync();
        if (requestVersion != Volatile.Read(ref _selectionRequestVersion))
            return;

        SelectedSubscription = Subscriptions.FirstOrDefault(item => item.Id == subscriptionId)
                               ?? SelectedSubscription;
    }

    partial void OnSelectedSubscriptionChanged(CollectionSubscription? value)
        => RebuildPendingItems(value);

    private void RebuildPendingItems(CollectionSubscription? value)
    {
        foreach (var item in PendingItems)
            item.PropertyChanged -= OnPendingItemPropertyChanged;
        PendingItems.Clear();

        if (value is not null)
        {
            foreach (var item in value.Items
                         .Where(item => item.IsPresent
                                        && item.State is CollectionSubscriptionItemState.New
                                            or CollectionSubscriptionItemState.Failed)
                         .OrderBy(item => item.Position)
                         .ThenBy(item => item.LocalSequence))
            {
                var key = (value.Id, item.EntryKey);
                var isSelected = !_selectionStates.TryGetValue(key, out var savedSelection)
                                 || savedSelection;
                _selectionStates[key] = isSelected;
                var viewModel = new CollectionUpdateItemViewModel(item, isSelected);
                viewModel.PropertyChanged += OnPendingItemPropertyChanged;
                PendingItems.Add(viewModel);
            }
        }

        NotifySubscriptionStateChanged();
    }

    private void OnDownloadTaskFinished(DownloadTask task)
    {
        if (task.CollectionSubscriptionId <= 0
            || string.IsNullOrWhiteSpace(task.CollectionEntryKey))
        {
            return;
        }

        var state = task.Status switch
        {
            DownloadStatus.Completed => CollectionSubscriptionItemState.Downloaded,
            DownloadStatus.Failed => CollectionSubscriptionItemState.Failed,
            DownloadStatus.Cancelled => CollectionSubscriptionItemState.New,
            _ => (CollectionSubscriptionItemState?)null
        };
        if (state is null)
            return;

        void Apply()
        {
            var subscription = Subscriptions.FirstOrDefault(candidate =>
                candidate.Id == task.CollectionSubscriptionId);
            var item = subscription?.Items.FirstOrDefault(candidate =>
                string.Equals(candidate.EntryKey, task.CollectionEntryKey, StringComparison.Ordinal));
            if (item is null
                || !string.Equals(item.TaskId, task.Id, StringComparison.Ordinal))
            {
                return;
            }

            item.State = state.Value;
            item.TaskId = "";
            if (SelectedSubscription?.Id == subscription!.Id)
                RebuildPendingItems(subscription);
            else
                NotifySubscriptionStateChanged();
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Apply();
        else
            dispatcher.BeginInvoke((Action)Apply);
    }

    private void OnPendingItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CollectionUpdateItemViewModel.IsSelected)
            || sender is not CollectionUpdateItemViewModel item)
        {
            return;
        }

        _selectionStates[(item.Item.SubscriptionId, item.Item.EntryKey)] = item.IsSelected;
        RefreshSelectionState();
    }

    [RelayCommand]
    private Task RefreshSelected()
        => SelectedSubscription is null
            ? Task.CompletedTask
            : RunOperationAsync("合集刷新失败", async () =>
            {
                var subscriptionId = SelectedSubscription.Id;
                IsRefreshing = true;
                try
                {
                    var outcome = await _refreshService.RefreshAsync(subscriptionId);
                    await InitializeAsync();
                    SelectedSubscription = Subscriptions.FirstOrDefault(item =>
                        item.Id == outcome.SubscriptionId);
                    RequestShowNotification?.Invoke(
                        outcome.IsSuccess
                            ? outcome.NewItems.Count == 0
                                ? "合集已是最新"
                                : $"发现 {outcome.NewItems.Count} 个新视频"
                            : $"合集刷新失败：{outcome.ErrorMessage}",
                        outcome.IsSuccess);
                }
                finally
                {
                    IsRefreshing = false;
                }
            });

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in PendingItems)
            item.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in PendingItems)
            item.IsSelected = false;
    }

    [RelayCommand]
    private Task DownloadAll()
        => RunOperationAsync(
            "下载合集更新失败",
            () => EnqueueAsync(PendingItems.ToArray()));

    [RelayCommand]
    private Task DownloadSelected()
        => RunOperationAsync(
            "下载合集更新失败",
            () => EnqueueAsync(PendingItems.Where(item => item.IsSelected).ToArray()));

    [RelayCommand]
    private Task IgnoreSelected()
        => SelectedSubscription is null
            ? Task.CompletedTask
            : RunOperationAsync("忽略合集更新失败", async () =>
            {
                var subscriptionId = SelectedSubscription.Id;
                var selected = PendingItems.Where(item => item.IsSelected).ToArray();
                if (selected.Length == 0)
                    return;

                await _updateItemStates(
                    subscriptionId,
                    selected.Select(item => new CollectionSubscriptionItemStateUpdate
                    {
                        EntryKey = item.Item.EntryKey,
                        State = CollectionSubscriptionItemState.Ignored,
                        TaskId = ""
                    }).ToArray());
                await InitializeAsync();
                RequestShowNotification?.Invoke($"已忽略 {selected.Length} 个更新", true);
            });

    [RelayCommand]
    private Task RelinkDirectory()
        => SelectedSubscription is null
            ? Task.CompletedTask
            : RunOperationAsync("更新合集下载目录失败", RelinkDirectoryCoreAsync);

    private async Task RelinkDirectoryCoreAsync()
    {
        var subscription = SelectedSubscription!;
        var selected = _selectDirectory(subscription.OutputDirectory);
        if (string.IsNullOrWhiteSpace(selected))
            return;

        var preflight = _preflightService.Check(selected);
        if (!preflight.CanProceed)
        {
            RequestShowNotification?.Invoke(preflight.BlockingMessage, false);
            return;
        }

        var originalDirectory = subscription.OutputDirectory;
        subscription.OutputDirectory = preflight.OutputDirectory;
        NotifySubscriptionStateChanged();
        try
        {
            SelectedSubscription = await _updateSubscription(subscription);
            await InitializeAsync();
            RequestShowNotification?.Invoke("已更新合集下载目录", true);
        }
        catch
        {
            subscription.OutputDirectory = originalDirectory;
            if (SelectedSubscription?.Id == subscription.Id)
                SelectedSubscription.OutputDirectory = originalDirectory;
            NotifySubscriptionStateChanged();
            throw;
        }
    }

    [RelayCommand]
    private Task DeleteSubscription()
        => SelectedSubscription is null
            ? Task.CompletedTask
            : RunOperationAsync("停止跟踪合集失败", async () =>
            {
                var subscription = SelectedSubscription;
                if (ConfirmFunc?.Invoke(
                        $"确定停止跟踪“{subscription.Title}”吗？已下载文件和历史记录会保留。",
                        "停止跟踪合集") != true)
                {
                    return;
                }

                await _deleteSubscription(subscription.Id);
                await InitializeAsync();
                RequestShowNotification?.Invoke("已停止跟踪该合集", true);
            });

    private async Task EnqueueAsync(IReadOnlyCollection<CollectionUpdateItemViewModel> items)
    {
        if (SelectedSubscription is null || items.Count == 0)
        {
            RequestShowNotification?.Invoke("请先选择要下载的新视频", false);
            return;
        }

        var subscription = SelectedSubscription;
        if (!Path.IsPathFullyQualified(subscription.OutputDirectory)
            || !Directory.Exists(subscription.OutputDirectory))
        {
            RequestShowNotification?.Invoke(
                "原下载目录已不存在，请先重新选择目录。",
                false);
            return;
        }

        var preflight = _preflightService.Check(subscription.OutputDirectory);
        if (!preflight.CanProceed)
        {
            RequestShowNotification?.Invoke(
                $"原下载目录不可用，请先重新选择目录。{preflight.BlockingMessage}",
                false);
            return;
        }

        var queued = 0;
        var alreadyActive = 0;
        var duplicateUrlSkipped = 0;
        var stateConflicts = 0;
        foreach (var itemViewModel in items)
        {
            var item = itemViewModel.Item;
            if (string.IsNullOrWhiteSpace(item.Url))
                continue;

            var itemIndex = item.LocalSequence > 0 ? item.LocalSequence : item.Position;
            var itemCount = subscription.Items.Count(candidate => candidate.IsPresent);
            var resolvedTitle = item.Title.Trim();
            var matchingTasks = _downloadManager.Tasks.Where(task =>
                    task.CollectionSubscriptionId == subscription.Id
                    && string.Equals(
                        task.CollectionEntryKey,
                        item.EntryKey,
                        StringComparison.Ordinal))
                .ToArray();
            var activeTask = FindPreferredMatchingTask(
                matchingTasks,
                item.TaskId,
                IsActiveDownloadTask);
            if (activeTask is not null)
            {
                if (!await TryMarkItemQueuedAsync(
                        subscription.Id,
                        item.EntryKey,
                        item.TaskId,
                        activeTask.Id))
                {
                    stateConflicts++;
                    continue;
                }

                item.TaskId = activeTask.Id;
                alreadyActive++;
                continue;
            }

            var unrelatedActiveTask = _downloadManager.Tasks.LastOrDefault(task =>
                IsActiveDownloadTask(task)
                && string.Equals(task.Url, item.Url, StringComparison.OrdinalIgnoreCase));
            if (unrelatedActiveTask is not null)
            {
                // Correlating an already-running task requires an atomic queue + database update,
                // which the current stores cannot provide. Keep this item pending instead.
                duplicateUrlSkipped++;
                continue;
            }

            var retryTask = FindPreferredMatchingTask(
                matchingTasks,
                item.TaskId,
                task => task.Status is DownloadStatus.Failed or DownloadStatus.Cancelled);
            var task = retryTask ?? new DownloadTask();
            if (!await TryMarkItemQueuedAsync(
                    subscription.Id,
                    item.EntryKey,
                    item.TaskId,
                    task.Id))
            {
                stateConflicts++;
                continue;
            }

            item.TaskId = task.Id;
            ApplyCurrentTaskSettings(
                task,
                subscription,
                item,
                preflight.OutputDirectory,
                itemIndex,
                itemCount,
                resolvedTitle);
            try
            {
                if (retryTask is not null)
                {
                    await _downloadManager.RetryAsync(task.Id);
                }
                else
                {
                    await _downloadManager.EnqueueAsync(
                        task,
                        string.IsNullOrWhiteSpace(resolvedTitle)
                            ? null
                            : new VideoInfo
                            {
                                Url = item.Url,
                                Title = task.Title,
                                Platform = subscription.Platform
                            });
                }

                queued++;
            }
            catch
            {
                var rollbackTaskId = retryTask?.Id ?? "";
                await _updateItemStates(
                    subscription.Id,
                    [new CollectionSubscriptionItemStateUpdate
                    {
                        EntryKey = item.EntryKey,
                        State = CollectionSubscriptionItemState.Failed,
                        TaskId = rollbackTaskId,
                        ExpectedTaskId = task.Id
                    }]);
                item.TaskId = rollbackTaskId;
                throw;
            }
        }

        await InitializeAsync();
        var skippedParts = new List<string>();
        if (duplicateUrlSkipped > 0)
            skippedParts.Add($"已跳过 {duplicateUrlSkipped} 个正在下载的同链接任务");
        if (stateConflicts > 0)
            skippedParts.Add($"{stateConflicts} 个条目状态已变化");
        var skippedSuffix = skippedParts.Count > 0
            ? $"，{string.Join("，", skippedParts)}"
            : "";
        RequestShowNotification?.Invoke(
            queued > 0
                ? $"已加入 {queued} 个合集更新任务{skippedSuffix}"
                : alreadyActive > 0
                    ? $"所选视频已在下载队列中{skippedSuffix}"
                    : skippedParts.Count > 0
                        ? string.Join("，", skippedParts)
                        : "没有可加入的更新任务",
            queued > 0 || alreadyActive > 0);
    }

    private async Task<bool> TryMarkItemQueuedAsync(
        long subscriptionId,
        string entryKey,
        string expectedTaskId,
        string taskId)
    {
        var affected = await _updateItemStates(
            subscriptionId,
            [new CollectionSubscriptionItemStateUpdate
            {
                EntryKey = entryKey,
                State = CollectionSubscriptionItemState.Queued,
                TaskId = taskId,
                ExpectedTaskId = expectedTaskId
            }]);
        return affected == 1;
    }

    private static DownloadTask? FindPreferredMatchingTask(
        IReadOnlyList<DownloadTask> tasks,
        string preferredTaskId,
        Func<DownloadTask, bool> predicate)
        => tasks.FirstOrDefault(task =>
               string.Equals(task.Id, preferredTaskId, StringComparison.Ordinal)
               && predicate(task))
           ?? tasks.LastOrDefault(predicate);

    private static bool IsActiveDownloadTask(DownloadTask task)
        => task.Status is DownloadStatus.Waiting
            or DownloadStatus.Resolving
            or DownloadStatus.Downloading
            or DownloadStatus.Merging
            or DownloadStatus.Paused
            or DownloadStatus.Scheduled;

    private static void ApplyCurrentTaskSettings(
        DownloadTask task,
        CollectionSubscription subscription,
        CollectionSubscriptionItem item,
        string outputDirectory,
        int itemIndex,
        int itemCount,
        string resolvedTitle)
    {
        task.Url = item.Url;
        task.Title = string.IsNullOrWhiteSpace(resolvedTitle)
            ? ""
            : CollectionNamingService.BuildItemTitle(
                resolvedTitle,
                subscription.Title,
                itemIndex,
                itemCount);
        task.Platform = subscription.Platform;
        task.Format = subscription.Format;
        task.Quality = subscription.Quality;
        task.Subtitle = subscription.Subtitle;
        task.OutputDirectory = outputDirectory;
        task.BatchId = subscription.BatchId;
        task.BatchName = subscription.BatchName;
        task.BatchDirectory = outputDirectory;
        task.CollectionTitle = subscription.Title;
        task.CollectionItemIndex = itemIndex;
        task.CollectionItemCount = itemCount;
        task.CollectionSubscriptionId = subscription.Id;
        task.CollectionEntryKey = item.EntryKey;
        task.ScheduledStartTimeUtc = null;
    }

    private async Task RunOperationAsync(string failureMessage, Func<Task> operation)
    {
        if (!await _operationGate.WaitAsync(0))
            return;

        IsOperating = true;
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            RequestShowNotification?.Invoke($"{failureMessage}：{ex.Message}", false);
        }
        finally
        {
            IsOperating = false;
            _operationGate.Release();
        }
    }

    private void PruneSelectionStates()
    {
        var pendingKeys = Subscriptions
            .SelectMany(subscription => subscription.Items
                .Where(item => item.IsPresent
                               && item.State is CollectionSubscriptionItemState.New
                                   or CollectionSubscriptionItemState.Failed)
                .Select(item => (subscription.Id, item.EntryKey)))
            .ToHashSet();

        foreach (var key in _selectionStates.Keys.Where(key => !pendingKeys.Contains(key)).ToArray())
            _selectionStates.Remove(key);
    }

    private void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelectedItems));
        OnPropertyChanged(nameof(PendingSummary));
        DownloadSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsOperatingChanged(bool value)
        => OnPropertyChanged(nameof(IsBusy));

    private void NotifySubscriptionStateChanged()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasSubscriptions));
        OnPropertyChanged(nameof(HasPendingItems));
        OnPropertyChanged(nameof(PendingSummary));
        OnPropertyChanged(nameof(SavedDirectory));
        OnPropertyChanged(nameof(IsSavedDirectoryAvailable));
        PendingCountChanged?.Invoke(PendingCount);
    }

    private static string? SelectDirectory(string currentDirectory)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "重新选择合集下载目录",
            InitialDirectory = Directory.Exists(currentDirectory) ? currentDirectory : ""
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public void Dispose()
        => _downloadManager.TaskFinished -= OnDownloadTaskFinished;
}
