using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>
/// Shared, normalized list of collection directories discovered from download history.
/// </summary>
public sealed partial class ExistingCollectionFolderStore : ObservableObject, IDisposable
{
    private const int RefreshDebounceMilliseconds = 150;

    private readonly HistoryService? _historyService;
    private readonly ConfigService? _configService;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private readonly ObservableCollection<ExistingCollectionFolder> _folders = [];
    private int _refreshRequestVersion;
    private int _refreshAfterLoad;
    private int _loadInProgress;
    private int _disposed;
    private volatile bool _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder))]
    private bool _isLoading;

    public ExistingCollectionFolderStore(
        HistoryService? historyService = null,
        ConfigService? configService = null)
    {
        _historyService = historyService;
        _configService = configService;
        Folders = new ReadOnlyObservableCollection<ExistingCollectionFolder>(_folders);
        if (_historyService is not null)
        {
            _historyService.HistoryAdded += OnHistoryAdded;
            _historyService.HistoryInvalidated += OnHistoryInvalidated;
        }

        if (_configService is not null)
            _configService.CollectionDirectoriesChanged += OnCollectionDirectoriesChanged;
    }

    public ReadOnlyObservableCollection<ExistingCollectionFolder> Folders { get; }
    public bool HasFolders => _folders.Count > 0;
    public string Placeholder => IsLoading
        ? "正在读取合集..."
        : HasFolders
            ? "不加入已有合集"
            : "暂无可用的已有合集";

    public event EventHandler? FoldersRefreshing;
    public event EventHandler? FoldersRefreshed;

    public Task EnsureLoadedAsync()
        => LoadAsync(forceRefresh: false);

    public Task RefreshAsync()
    {
        Interlocked.Increment(ref _refreshRequestVersion);
        return LoadAsync(forceRefresh: true);
    }

    public async Task<ExistingCollectionFolder> RegisterCollectionAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fullPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(directory.Trim()));
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Collection directory was not found: {fullPath}");

        if (_configService is not null)
        {
            await _configService.RegisterCollectionDirectoryAsync(
                fullPath,
                cancellationToken);
        }

        await LoadAsync(forceRefresh: true);
        return FindByDirectory(fullPath)
               ?? throw new InvalidOperationException(
                   "The selected collection directory could not be loaded.");
    }

    public ExistingCollectionFolder? FindByDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        var existing = _folders.FirstOrDefault(folder =>
            PathsEqual(folder.Directory, directory));
        if (existing is not null)
            return existing;

        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(directory.Trim()));
            if (!Directory.Exists(fullPath))
                return null;

            var name = Path.GetFileName(fullPath);
            return new ExistingCollectionFolder
            {
                BatchId = BatchDownloadOrganizer.CreateDirectoryGroupId(fullPath),
                Name = string.IsNullOrWhiteSpace(name) ? "合集" : name,
                Directory = fullPath
            };
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
            return true;
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return false;
        }
    }

    private async Task LoadAsync(bool forceRefresh)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _loadGate.WaitAsync();
        var refreshAfterLoad = false;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!forceRefresh && _isLoaded)
                return;

            Volatile.Write(ref _loadInProgress, 1);
            IsLoading = true;
            List<ExistingCollectionFolder> historyFolders = _historyService is null
                ? []
                : await _historyService.GetExistingCollectionFoldersAsync();
            var configuredFolders = CreateConfiguredFolders();
            var availableFolders = await Task.Run(() =>
                Normalize(historyFolders.Concat(configuredFolders)));
            lock (_lifecycleGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;

                var hadFolders = HasFolders;
                FoldersRefreshing?.Invoke(this, EventArgs.Empty);
                _folders.Clear();
                foreach (var folder in availableFolders)
                    _folders.Add(folder);

                NotifyFolderAvailabilityChanged(hadFolders);

                _isLoaded = true;
                FoldersRefreshed?.Invoke(this, EventArgs.Empty);
            }
            Volatile.Write(ref _loadInProgress, 0);
            refreshAfterLoad = Interlocked.Exchange(ref _refreshAfterLoad, 0) != 0;
        }
        finally
        {
            Volatile.Write(ref _loadInProgress, 0);
            IsLoading = false;
            _loadGate.Release();
        }

        if (refreshAfterLoad)
            OnHistoryInvalidated();
    }

    private void OnHistoryAdded(DownloadHistory history)
    {
        if (!CanProcessHistoryChange())
            return;

        var refreshVersion = Volatile.Read(ref _refreshRequestVersion);
        _ = AddHistoryFolderAsync(history, refreshVersion);
    }

    private async Task AddHistoryFolderAsync(DownloadHistory history, int refreshVersion)
    {
        try
        {
            var candidate = new ExistingCollectionFolder
            {
                BatchId = history.BatchId,
                Name = history.BatchName,
                Directory = history.BatchDirectory,
                ExistingItemCount = 1,
                LastDownloadTime = history.DownloadTime
            };
            var normalized = await Task.Run(() => Normalize([candidate]).FirstOrDefault());
            if (normalized is null
                || refreshVersion != Volatile.Read(ref _refreshRequestVersion)
                || !_isLoaded
                || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            await InvokeOnApplicationDispatcherAsync(() =>
            {
                lock (_lifecycleGate)
                {
                    if (!_isLoaded
                        || refreshVersion != Volatile.Read(ref _refreshRequestVersion)
                        || Volatile.Read(ref _disposed) != 0
                        || _folders.Any(folder => PathsEqual(folder.Directory, normalized.Directory)))
                    {
                        return;
                    }

                    var hadFolders = HasFolders;
                    _folders.Insert(0, normalized);
                    NotifyFolderAvailabilityChanged(hadFolders);
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ExistingCollectionFolderStore] Incremental update failed: {ex.Message}");
        }
    }

    private void OnHistoryInvalidated()
    {
        if (!CanProcessHistoryChange())
            return;

        var version = Interlocked.Increment(ref _refreshRequestVersion);
        _ = RefreshAfterInvalidationAsync(version);
    }

    private void OnCollectionDirectoriesChanged()
        => OnHistoryInvalidated();

    private bool CanProcessHistoryChange()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;
        if (Volatile.Read(ref _loadInProgress) == 0)
            return _isLoaded;

        Interlocked.Exchange(ref _refreshAfterLoad, 1);
        return false;
    }

    private async Task RefreshAfterInvalidationAsync(int version)
    {
        try
        {
            await Task.Delay(RefreshDebounceMilliseconds);
            if (version != Volatile.Read(ref _refreshRequestVersion)
                || !_isLoaded
                || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            await InvokeOnApplicationDispatcherAsync(RefreshAsync);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ExistingCollectionFolderStore] Refresh after history change failed: {ex.Message}");
        }
    }

    private void NotifyFolderAvailabilityChanged(bool previousValue)
    {
        if (previousValue == HasFolders)
            return;

        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(Placeholder));
    }

    private static Task InvokeOnApplicationDispatcherAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private static Task InvokeOnApplicationDispatcherAsync(Func<Task> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            return action();

        return dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    private static IReadOnlyList<ExistingCollectionFolder> Normalize(
        IEnumerable<ExistingCollectionFolder> folders)
    {
        var normalized = new List<ExistingCollectionFolder>();
        foreach (var folder in folders)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder.BatchId)
                    || string.IsNullOrWhiteSpace(folder.Directory))
                {
                    continue;
                }

                var fullPath = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(folder.Directory.Trim()));
                if (!Directory.Exists(fullPath))
                    continue;

                var name = string.IsNullOrWhiteSpace(folder.Name)
                    ? Path.GetFileName(fullPath)
                    : folder.Name.Trim();
                normalized.Add(new ExistingCollectionFolder
                {
                    BatchId = folder.BatchId.Trim(),
                    Name = string.IsNullOrWhiteSpace(name) ? "已有合集" : name,
                    Directory = fullPath,
                    ExistingItemCount = Math.Max(0, folder.ExistingItemCount),
                    LastDownloadTime = folder.LastDownloadTime
                });
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or NotSupportedException
                                       or PathTooLongException
                                       or IOException
                                       or UnauthorizedAccessException)
            {
                // Stale or malformed history entries are not valid selection targets.
            }
        }

        return normalized
            .GroupBy(folder => folder.Directory, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group
                    .OrderByDescending(folder => folder.LastDownloadTime)
                    .First();
                var itemCount = group.Sum(folder => (long)folder.ExistingItemCount);
                return new ExistingCollectionFolder
                {
                    BatchId = latest.BatchId,
                    Name = latest.Name,
                    Directory = latest.Directory,
                    ExistingItemCount = (int)Math.Min(int.MaxValue, itemCount),
                    LastDownloadTime = latest.LastDownloadTime
                };
            })
            .OrderByDescending(folder => folder.LastDownloadTime)
            .ThenBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<ExistingCollectionFolder> CreateConfiguredFolders()
    {
        if (_configService is null)
            return [];

        return _configService.Config.CollectionDirectories
            .Select(directory =>
            {
                var name = Path.GetFileName(directory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
                return new ExistingCollectionFolder
                {
                    BatchId = BatchDownloadOrganizer.CreateDirectoryGroupId(directory),
                    Name = string.IsNullOrWhiteSpace(name) ? "合集" : name,
                    Directory = directory,
                    ExistingItemCount = 0,
                    LastDownloadTime = DateTime.MinValue
                };
            })
            .ToList();
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            Volatile.Write(ref _disposed, 1);
            _isLoaded = false;
            FoldersRefreshing = null;
            FoldersRefreshed = null;
        }

        Interlocked.Increment(ref _refreshRequestVersion);
        if (_historyService is not null)
        {
            _historyService.HistoryAdded -= OnHistoryAdded;
            _historyService.HistoryInvalidated -= OnHistoryInvalidated;
        }
        if (_configService is not null)
            _configService.CollectionDirectoriesChanged -= OnCollectionDirectoriesChanged;

        // In-flight refreshes still release the managed gate after observing _disposed.
        GC.SuppressFinalize(this);
    }
}
