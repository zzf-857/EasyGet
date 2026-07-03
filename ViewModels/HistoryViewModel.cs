using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
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
    private const int MaxDouyinManifestLines = 1000;

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
                AvailableFilePath = ResolveExistingHistoryPath(item),
                DouyinManifestSummaryText = BuildDouyinManifestSummaryText(item)
            })
            .ToList());

        HistoryItems.Clear();
        foreach (var result in fileExistsResults)
        {
            result.Item.AvailableFilePath = result.AvailableFilePath;
            result.Item.FileExists = !string.IsNullOrWhiteSpace(result.AvailableFilePath);
            result.Item.DouyinManifestSummaryText = result.DouyinManifestSummaryText;
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

    private static string ResolveExistingHistoryPath(DownloadHistory item)
    {
        if (!IsDouyinManifestPath(item.FilePath) && PathExists(item.FilePath))
            return item.FilePath;

        return item.AttachmentFilePaths
            .FirstOrDefault(path => !IsDouyinManifestPath(path) && PathExists(path))
            ?? "";
    }

    private static string BuildDouyinManifestSummaryText(DownloadHistory item)
    {
        var manifestPath = ResolveSafeDouyinManifestPath(item);
        if (string.IsNullOrWhiteSpace(manifestPath))
            return "";

        var summary = TryReadDouyinManifestSummary(manifestPath);
        if (summary is null)
            return "";

        var attachmentCount = item.AttachmentFilePaths
            .Count(path => !IsDouyinManifestPath(path));
        return FormatDouyinManifestSummary(summary, attachmentCount);
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

    private static DouyinManifestHistorySummary? TryReadDouyinManifestSummary(string manifestPath)
    {
        try
        {
            var fileInfo = new FileInfo(manifestPath);
            if (!fileInfo.Exists)
                return null;

            var itemCount = 0;
            var videoCount = 0;
            var galleryCount = 0;
            var musicCount = 0;
            using var reader = new StreamReader(manifestPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            for (var lineIndex = 0; lineIndex < MaxDouyinManifestLines; lineIndex++)
            {
                var line = reader.ReadLine();
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        continue;

                    itemCount++;
                    switch (NormalizeManifestMediaType(root))
                    {
                        case "video":
                            videoCount++;
                            break;
                        case "gallery":
                            galleryCount++;
                            break;
                        case "music":
                            musicCount++;
                            break;
                    }
                }
                catch (JsonException)
                {
                }
            }

            var isTruncated = reader.ReadLine() is not null;
            return itemCount > 0
                ? new DouyinManifestHistorySummary(itemCount, videoCount, galleryCount, musicCount, isTruncated)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string NormalizeManifestMediaType(JsonElement root)
    {
        if (root.TryGetProperty("media_type", out var mediaTypeElement)
            && mediaTypeElement.ValueKind == JsonValueKind.String)
        {
            return (mediaTypeElement.GetString() ?? "").Trim().ToLowerInvariant();
        }

        return "unknown";
    }

    private static string FormatDouyinManifestSummary(DouyinManifestHistorySummary summary, int attachmentCount)
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

    private sealed record DouyinManifestHistorySummary(
        int ItemCount,
        int VideoCount,
        int GalleryCount,
        int MusicCount,
        bool IsTruncated);
}
