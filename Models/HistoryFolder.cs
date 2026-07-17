using CommunityToolkit.Mvvm.ComponentModel;

namespace EasyGet.Models;

/// <summary>
/// 下载历史中的自定义整理文件夹。它只组织历史记录，不移动本地文件。
/// </summary>
public partial class HistoryFolder : ObservableObject
{
    public long Id { get; init; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ItemCountText))]
    private int _itemCount;

    public DateTime CreatedAt { get; init; } = DateTime.Now;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _editName = "";

    public string ItemCountText => $"{ItemCount} 个项目";
}
