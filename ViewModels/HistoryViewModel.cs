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
    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchOrFilterActive))]
    private string _searchKeyword = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchOrFilterActive))]
    private string _selectedMediaFilter = "全部";

    public bool IsSearchOrFilterActive => !string.IsNullOrEmpty(SearchKeyword) || SelectedMediaFilter != "全部";

    [ObservableProperty] private int _totalHistoryCount;
    [ObservableProperty] private string _storageStatusText = "磁盘空间获取中";
    [ObservableProperty] private double _storageFreePercentage = 0;

    partial void OnSearchKeywordChanged(string value)
    {
        var previousSearchCts = _searchCts;
        previousSearchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        previousSearchCts?.Dispose();

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
        var items = await _historyService.GetAllAsync(
            string.IsNullOrWhiteSpace(SearchKeyword) ? null : SearchKeyword);

        TotalHistoryCount = items.Count;
        var filteredItems = items
            .Where(item => MatchesMediaFilter(item, SelectedMediaFilter))
            .ToList();

        var fileExistsResults = await Task.Run(() => filteredItems
            .Select(item => new
            {
                Item = item,
                FileExists = HasExistingHistoryFile(item)
            })
            .ToList());

        HistoryItems.Clear();
        foreach (var result in fileExistsResults)
        {
            result.Item.FileExists = result.FileExists;
            HistoryItems.Add(result.Item);
        }
    }

    [RelayCommand]
    private async Task SetMediaFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            filter = "全部";

        SelectedMediaFilter = filter;
        await LoadHistory();
    }

    public Func<string, string, bool>? ConfirmFunc { get; set; } = (msg, title) =>
    {
        if (System.Windows.Application.Current == null)
            return true; // 单测默认返回 true
        var result = System.Windows.MessageBox.Show(
            msg,
            title,
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        return result == System.Windows.MessageBoxResult.Yes;
    };

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
        HistoryItems.Clear();
        TotalHistoryCount = 0;
    }

    /// <summary>
    /// 清除筛选和搜索词
    /// </summary>
    [RelayCommand]
    private async Task ClearFilterAndSearch()
    {
        SearchKeyword = "";
        SelectedMediaFilter = "全部";
        await LoadHistory();
    }

    /// <summary>
    /// 删除单条记录
    /// </summary>
    [RelayCommand]
    private async Task DeleteItem(long id)
    {
        await _historyService.DeleteAsync(id);
        var item = HistoryItems.FirstOrDefault(h => h.Id == id);
        if (item != null) HistoryItems.Remove(item);
        if (TotalHistoryCount > 0) TotalHistoryCount--;
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

    private static bool HasExistingHistoryFile(DownloadHistory item)
        => PathExists(item.FilePath) || item.AttachmentFilePaths.Any(PathExists);

    private static bool PathExists(string path)
        => !string.IsNullOrWhiteSpace(path)
           && (System.IO.File.Exists(path) || System.IO.Directory.Exists(path));
}
