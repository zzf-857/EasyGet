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
    /// <summary>
    /// 来源平台为条目分配的稳定 ID，例如 B 站的 BV 号。
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// yt-dlp 扁平播放列表条目使用的提取器标识。
    /// </summary>
    public string IeKey { get; set; } = "";

    /// <summary>
    /// yt-dlp 条目元数据中的提取器标识。
    /// </summary>
    public string ExtractorKey { get; set; } = "";

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

    /// <summary>
    /// 用于跨刷新识别同一条目的稳定键。平台 ID 优先，URL 仅作回退。
    /// </summary>
    public string StableKey => PlaylistIdentity.CreateKey(
        string.IsNullOrWhiteSpace(IeKey) ? ExtractorKey : IeKey,
        Id,
        Url);

    public string KindText => Kind switch
    {
        PlaylistEntryKind.Video => "视频",
        PlaylistEntryKind.Audio => "音频",
        PlaylistEntryKind.Resource => "资源",
        _ => "条目"
    };
}

public static class PlaylistIdentity
{
    public static string CreateKey(string? extractorKey, string? id, string? url)
    {
        var normalizedExtractor = extractorKey?.Trim();
        var normalizedId = id?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedExtractor)
            && !string.IsNullOrWhiteSpace(normalizedId))
        {
            return $"extractor:{normalizedExtractor.ToLowerInvariant()}:{normalizedId}";
        }

        var normalizedUrl = NormalizeUrl(url);
        return string.IsNullOrWhiteSpace(normalizedUrl)
            ? ""
            : $"url:{normalizedUrl}";
    }

    public static string NormalizeUrl(string? url)
    {
        var value = url?.Trim() ?? "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return value;
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.IdnHost.ToLowerInvariant(),
            Fragment = ""
        };

        if (uri.IsDefaultPort)
            builder.Port = -1;

        return builder.Uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.UriEscaped);
    }
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
