using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using EasyGet.Models;
using EasyGet.Services;

namespace EasyGet.ViewModels;

/// <summary>Owns collection selection, refresh reconciliation and persistence for both download pages.</summary>
internal partial class DownloadDestinationViewModel : ObservableObject, IDisposable
{
    private readonly ConfigService _config;
    private readonly ExistingCollectionFolderStore _folders;
    private readonly Func<string, string?> _selectDirectory;
    private string _rootDirectory;
    private string? _selectionBeforeRefresh;
    private bool _applyingConfiguration;
    private bool _refreshing;
    private Task<bool> _persistence = Task.FromResult(true);

    [ObservableProperty] private string _downloadDirectory;
    [ObservableProperty] private ExistingCollectionFolder? _selectedCollectionFolder;

    public ReadOnlyObservableCollection<ExistingCollectionFolder> ExistingCollectionFolders => _folders.Folders;
    public bool IsLoadingCollectionFolders => _folders.IsLoading;
    public bool HasFolders => _folders.HasFolders;
    public string ExistingCollectionFolderPlaceholder => $"临时下载 · {_rootDirectory}";
    public event Action<string, bool>? NotificationRequested;

    internal DownloadDestinationViewModel(ConfigService config, ExistingCollectionFolderStore folders,
        Func<string, string?>? selectDirectory = null)
    {
        _config = config;
        _folders = folders;
        _selectDirectory = selectDirectory ?? SelectDirectory;
        _rootDirectory = _downloadDirectory = config.Config.DefaultDownloadPath;
        _config.DefaultDownloadPathChanged += OnRootChanged;
        _config.SelectedCollectionDirectoryChanged += OnSelectionChanged;
        _folders.PropertyChanged += OnFoldersPropertyChanged;
        _folders.FoldersRefreshing += OnRefreshing;
        _folders.FoldersRefreshed += OnRefreshed;
    }

    public void RefreshFromConfiguration()
    {
        OnRootChanged(_config.Config.DefaultDownloadPath);
        OnSelectionChanged(_config.Config.SelectedCollectionDirectory);
    }

    public async Task BrowseAsync()
    {
        var directory = _selectDirectory(DownloadDirectory);
        if (string.IsNullOrWhiteSpace(directory))
            return;
        try
        {
            SelectedCollectionFolder = await _folders.RegisterCollectionAsync(directory);
            if (!await _persistence)
                NotificationRequested?.Invoke("合集已选择，但保存失败；应用退出时将再次尝试保存。", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or NotSupportedException or InvalidOperationException)
        {
            NotificationRequested?.Invoke($"无法添加合集目录：{ex.Message}", false);
        }
    }

    public async Task LoadAsync(bool forceRefresh)
    {
        try
        {
            if (forceRefresh)
                await _folders.RefreshAsync();
            else
                await _folders.EnsureLoadedAsync();
            OnSelectionChanged(_config.Config.SelectedCollectionDirectory);
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke($"读取已有合集失败：{ex.Message}", false);
        }
    }

    partial void OnSelectedCollectionFolderChanged(ExistingCollectionFolder? value)
    {
        DownloadDirectory = value?.Directory ?? _rootDirectory;
        if (_applyingConfiguration || _refreshing)
            return;
        _config.UpdateSelectedCollectionDirectory(value?.Directory);
        _persistence = _config.SaveAsync();
    }

    private void OnRootChanged(string path) => OnUi(() =>
    {
        _rootDirectory = path;
        if (SelectedCollectionFolder is null)
            DownloadDirectory = path;
        OnPropertyChanged(nameof(ExistingCollectionFolderPlaceholder));
    });

    private void OnSelectionChanged(string directory) => OnUi(() =>
    {
        var selected = string.IsNullOrWhiteSpace(directory) ? null
            : ExistingCollectionFolderStore.PathsEqual(SelectedCollectionFolder?.Directory, directory)
              && Directory.Exists(directory) ? SelectedCollectionFolder : _folders.FindByDirectory(directory);
        _applyingConfiguration = true;
        try { SelectedCollectionFolder = selected; }
        finally { _applyingConfiguration = false; }
        DownloadDirectory = selected?.Directory ?? _rootDirectory;
        if (!string.IsNullOrWhiteSpace(directory) && selected is null)
        {
            _config.UpdateSelectedCollectionDirectory(null);
            _persistence = _config.SaveAsync();
        }
    });

    private void OnFoldersPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ExistingCollectionFolderStore.IsLoading):
                OnPropertyChanged(nameof(IsLoadingCollectionFolders));
                break;
            case nameof(ExistingCollectionFolderStore.HasFolders):
                OnPropertyChanged(nameof(HasFolders));
                break;
            case nameof(ExistingCollectionFolderStore.Placeholder):
                OnPropertyChanged(nameof(ExistingCollectionFolderPlaceholder));
                break;
        }
    }

    private void OnRefreshing(object? sender, EventArgs e)
    {
        _selectionBeforeRefresh = SelectedCollectionFolder?.Directory ?? _config.Config.SelectedCollectionDirectory;
        _refreshing = true;
    }

    private void OnRefreshed(object? sender, EventArgs e)
    {
        var selectedPath = _selectionBeforeRefresh ?? SelectedCollectionFolder?.Directory
            ?? _config.Config.SelectedCollectionDirectory;
        _selectionBeforeRefresh = null;
        try { OnSelectionChanged(selectedPath ?? ""); }
        finally { _refreshing = false; }
    }

    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    private static string? SelectDirectory(string currentDirectory)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择文件夹作为合集",
            InitialDirectory = currentDirectory
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public void Dispose()
    {
        _config.DefaultDownloadPathChanged -= OnRootChanged;
        _config.SelectedCollectionDirectoryChanged -= OnSelectionChanged;
        _folders.PropertyChanged -= OnFoldersPropertyChanged;
        _folders.FoldersRefreshing -= OnRefreshing;
        _folders.FoldersRefreshed -= OnRefreshed;
    }
}
