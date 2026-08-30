using CommunityToolkit.Mvvm.ComponentModel;

namespace EasyGet.Models;

public enum PlaylistEntryKind
{
    Video,
    Audio,
    Resource,
    Unknown
}

/// <summary>
/// 播放列表中的一个可选条目。OriginalIndex 是来源平台的原始顺序，不因筛选改变。
/// </summary>
public partial class PlaylistEntryInfo : ObservableObject
{
    public string Url { get; set; } = "";

    public string OriginalTitle { get; set; } = "";

    public int OriginalIndex { get; set; }

    public string SectionTitle { get; set; } = "";

    public string ParentTitle { get; set; } = "";

    public int Level { get; set; }

    public PlaylistEntryKind Kind { get; set; }

    public IReadOnlyList<MediaResourceInfo> Resources { get; set; } = [];

    [ObservableProperty]
    private bool _isSelected = true;

    public string DisplayIndex => OriginalIndex > 0 ? $"{OriginalIndex:00}" : "--";

    public string DisplayTitle => string.IsNullOrWhiteSpace(OriginalTitle)
        ? Url
        : OriginalTitle;

    public string KindText => Kind switch
    {
        PlaylistEntryKind.Video => "视频",
        PlaylistEntryKind.Audio => "音频",
        PlaylistEntryKind.Resource => "资源",
        _ => "条目"
    };
}

public partial class MediaResourceInfo : ObservableObject
{
    public string Url { get; set; } = "";

    public string Title { get; set; } = "";

    public string Extension { get; set; } = "";

    public string MimeType { get; set; } = "";

    public PlaylistEntryKind Kind { get; set; } = PlaylistEntryKind.Resource;

    public int OriginalIndex { get; set; }

    [ObservableProperty]
    private bool _isSelected = true;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Url : Title;
}

public partial class PlaylistSectionInfo : ObservableObject
{
    public string Title { get; set; } = "未分组";

    public int EntryCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private int _selectedEntryCount;

    public string DisplayText => $"{Title} ({SelectedEntryCount}/{EntryCount})";
}
