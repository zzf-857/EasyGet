using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly HistoryService _historyService;
    private readonly Action<ProcessStartInfo> _startProcess;

    [ObservableProperty] private string _searchKeyword = "";
    [ObservableProperty] private string _selectedMediaFilter = "全部";
    [ObservableProperty] private int _totalHistoryCount;

    public string[] MediaFilterOptions { get; } = ["全部", "视频", "音频"];
    public ObservableCollection<DownloadHistory> HistoryItems { get; } = [];

    public HistoryViewModel(HistoryService historyService)
        : this(historyService, StartProcess)
    {
    }

    internal HistoryViewModel(HistoryService historyService, Action<ProcessStartInfo> startProcess)
    {
        _historyService = historyService;
        _startProcess = startProcess;
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

        HistoryItems.Clear();
        foreach (var item in filteredItems)
            HistoryItems.Add(item);

        // 异步检查文件是否存在，避免在 UI 线程同步读取磁盘/网络驱动器导致卡顿
        _ = Task.Run(() =>
        {
            foreach (var item in filteredItems)
            {
                var exists = !string.IsNullOrEmpty(item.FilePath) && System.IO.File.Exists(item.FilePath);
                if (item.FileExists != exists)
                {
                    System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        item.FileExists = exists;
                    });
                }
            }
        });
    }

    /// <summary>
    /// 搜索
    /// </summary>
    [RelayCommand]
    private async Task Search()
    {
        await LoadHistory();
    }

    [RelayCommand]
    private async Task SetMediaFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            filter = "全部";

        SelectedMediaFilter = filter;
        await LoadHistory();
    }

    /// <summary>
    /// 清空全部历史
    /// </summary>
    [RelayCommand]
    private async Task ClearAll()
    {
        await _historyService.ClearAllAsync();
        HistoryItems.Clear();
        TotalHistoryCount = 0;
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
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{filePath}\"",
                        UseShellExecute = true
                    });
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
                if (!System.IO.File.Exists(filePath))
                    return;

                _startProcess(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(filePath) ?? ""
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
}
