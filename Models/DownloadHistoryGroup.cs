using CommunityToolkit.Mvvm.ComponentModel;
using EasyGet.Services;

namespace EasyGet.Models;

/// <summary>
/// 历史页中的一个批次分组；旧记录和单条下载各自形成一个展开分组。
/// </summary>
public partial class DownloadHistoryGroup : ObservableObject
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Directory { get; init; } = "";
    public string BatchId { get; init; } = "";
    public bool IsBatch { get; init; }
    public IReadOnlyList<DownloadHistory> Items { get; init; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";
    public int ItemCount => Items.Count;

    public string SummaryText
    {
        get
        {
            var totalBytes = Items.Aggregate(
                0L,
                static (total, item) => total > long.MaxValue - Math.Max(0, item.FileSize)
                    ? long.MaxValue
                    : total + Math.Max(0, item.FileSize));
            var latestTime = Items.Count == 0
                ? DateTime.MinValue
                : Items.Max(item => item.DownloadTime);
            var latest = latestTime == DateTime.MinValue
                ? "--"
                : latestTime.ToString("yyyy-MM-dd HH:mm");
            return $"{ItemCount} 个项目 · {ByteSizeFormatter.FormatClampZero(totalBytes)} · {latest}";
        }
    }
}
