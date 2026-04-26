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

    [ObservableProperty] private string _searchKeyword = "";
    public ObservableCollection<DownloadHistory> HistoryItems { get; } = [];

    public HistoryViewModel(HistoryService historyService)
    {
        _historyService = historyService;
    }

    /// <summary>
    /// 加载/刷新历史记录
    /// </summary>
    [RelayCommand]
    public async Task LoadHistory()
    {
        var items = await _historyService.GetAllAsync(
            string.IsNullOrWhiteSpace(SearchKeyword) ? null : SearchKeyword);

        HistoryItems.Clear();
        if (items == null) return;
        foreach (var item in items)
            HistoryItems.Add(item);

        // 异步检查文件是否存在，避免在 UI 线程同步读取磁盘/网络驱动器导致卡顿
        _ = Task.Run(() =>
        {
            foreach (var item in items)
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

    /// <summary>
    /// 清空全部历史
    /// </summary>
    [RelayCommand]
    private async Task ClearAll()
    {
        await _historyService.ClearAllAsync();
        HistoryItems.Clear();
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
}
