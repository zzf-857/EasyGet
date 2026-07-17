using CommunityToolkit.Mvvm.ComponentModel;

namespace EasyGet.Models;

/// <summary>
/// 下载历史中的自定义整理文件夹。它只组织历史记录，不移动本地文件。
/// </summary>
public partial class HistoryFolder : ObservableObject
{
    public long Id { get; init; }

    /// <summary>内置入口（全部记录/未整理）不允许重命名或删除。</summary>
    public bool IsSystemFolder { get; init; }

    public bool CanManage => !IsSystemFolder;
    public bool CanAcceptDrop => Id >= 0;

    public string IconGlyph => Id switch
    {
        -1 => "\uE8B7",
        0 => "\uE838",
        _ => "\uE8B7"
    };

    public string Caption => Id switch
    {
        -1 => "完整资料库",
        0 => "等待归类",
        _ => "自定义整理"
    };

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
