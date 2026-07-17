using CommunityToolkit.Mvvm.ComponentModel;
using EasyGet.Services;

namespace EasyGet.Models;

/// <summary>
/// 下载历史记录模型
/// </summary>
public partial class DownloadHistory : ObservableObject
{
    /// <summary>主键 ID</summary>
    public long Id { get; set; }

    /// <summary>视频 URL</summary>
    public string Url { get; set; } = "";

    /// <summary>视频标题</summary>
    public string Title { get; set; } = "";

    /// <summary>平台名称</summary>
    public string Platform { get; set; } = "";

    /// <summary>下载格式</summary>
    public string Format { get; set; } = "";

    /// <summary>画质</summary>
    public string Quality { get; set; } = "";

    /// <summary>文件大小 (bytes)</summary>
    public long FileSize { get; set; }

    /// <summary>输出文件路径</summary>
    public string FilePath { get; set; } = "";

    /// <summary>批量/合集任务的稳定批次 ID；旧记录和单条下载为空</summary>
    public string BatchId { get; set; } = "";

    /// <summary>批量/合集任务名称</summary>
    public string BatchName { get; set; } = "";

    /// <summary>批量/合集任务根目录</summary>
    public string BatchDirectory { get; set; } = "";

    /// <summary>历史资料库中的自定义整理文件夹 ID；0 表示未整理</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOrganized))]
    private long _folderId;

    /// <summary>当前是否被批量选中（仅用于界面，不持久化）</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>当前整理文件夹名称（仅用于界面，不持久化）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOrganizerFolder))]
    private string _organizerFolderName = "";

    public bool IsOrganized => FolderId > 0;
    public bool HasOrganizerFolder => !string.IsNullOrWhiteSpace(OrganizerFolderName);

    public bool IsBatchHistory => !string.IsNullOrWhiteSpace(BatchId);

    private List<string> _attachmentFilePaths = [];

    /// <summary>主文件之外的附属文件路径</summary>
    public List<string> AttachmentFilePaths
    {
        get => _attachmentFilePaths;
        set
        {
            if (SetProperty(ref _attachmentFilePaths, value ?? []))
            {
                OnPropertyChanged(nameof(HasAttachmentFiles));
                OnPropertyChanged(nameof(AttachmentCountText));
                OnPropertyChanged(nameof(HasAttachmentSummary));
                OnPropertyChanged(nameof(AttachmentSummaryText));
            }
        }
    }

    private string _douyinManifestSummaryText = "";

    /// <summary>抖音 manifest 的非持久化展示摘要，由历史 ViewModel 加载时刷新</summary>
    public string DouyinManifestSummaryText
    {
        get => _douyinManifestSummaryText;
        set
        {
            if (SetProperty(ref _douyinManifestSummaryText, value ?? ""))
            {
                OnPropertyChanged(nameof(HasAttachmentSummary));
                OnPropertyChanged(nameof(AttachmentSummaryText));
            }
        }
    }

    private DouyinManifestSummary? _douyinManifestSummary;

    /// <summary>抖音 manifest 的非持久化结构化摘要，由历史 ViewModel 加载时刷新</summary>
    public DouyinManifestSummary? DouyinManifestSummary
    {
        get => _douyinManifestSummary;
        set
        {
            if (SetProperty(ref _douyinManifestSummary, value))
            {
                OnPropertyChanged(nameof(DouyinManifestItems));
                OnPropertyChanged(nameof(HasDouyinManifestDetails));
            }
        }
    }

    public IReadOnlyList<DouyinManifestItem> DouyinManifestItems => DouyinManifestSummary?.Items ?? [];

    public bool HasDouyinManifestDetails => DouyinManifestItems.Count > 0;

    /// <summary>下载时间</summary>
    public DateTime DownloadTime { get; set; } = DateTime.Now;

    /// <summary>缩略图 URL</summary>
    public string ThumbnailUrl { get; set; } = "";

    /// <summary>文件是否存在（用于 UI 灰显判断，由 ViewModel 异步刷新）</summary>
    [ObservableProperty]
    private bool _fileExists = true;

    /// <summary>当前可打开的主文件或附属文件路径（非持久化，由 ViewModel 刷新）</summary>
    [ObservableProperty]
    private string _availableFilePath = "";

    /// <summary>
    /// 格式化的文件大小
    /// </summary>
    public string FileSizeText => ByteSizeFormatter.FormatClampZero(FileSize);

    /// <summary>
    /// 格式化的下载时间
    /// </summary>
    public string DownloadTimeText => DownloadTime == DateTime.MinValue
        ? "--"
        : DownloadTime.ToString("yyyy-MM-dd HH:mm");

    public bool HasAttachmentFiles => AttachmentFilePaths.Count > 0;

    public string AttachmentCountText => HasAttachmentFiles
        ? $"附属 {AttachmentFilePaths.Count}"
        : "";

    public bool HasAttachmentSummary => !string.IsNullOrWhiteSpace(AttachmentSummaryText);

    public string AttachmentSummaryText
    {
        get
        {
            var parts = new List<string>();
            var attachmentSummary = !string.IsNullOrWhiteSpace(DouyinManifestSummaryText)
                ? DouyinManifestSummaryText
                : AttachmentCountText;
            if (!string.IsNullOrWhiteSpace(attachmentSummary))
                parts.Add(attachmentSummary);

            var hlsPlaylistSummary = DouyinOutputHintFormatter.FormatLiveHlsPlaylistSummary(
                DouyinOutputHintFormatter.CountLiveHlsPlaylistFiles(
                    Url,
                    EnumerateOutputFilePaths()));
            if (!string.IsNullOrWhiteSpace(hlsPlaylistSummary))
                parts.Add(hlsPlaylistSummary);

            return string.Join(" / ", parts);
        }
    }

    private IEnumerable<string> EnumerateOutputFilePaths()
    {
        if (!string.IsNullOrWhiteSpace(FilePath))
            yield return FilePath;

        foreach (var path in AttachmentFilePaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
                yield return path;
        }
    }
}
