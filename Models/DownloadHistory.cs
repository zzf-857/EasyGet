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

    public string AttachmentSummaryText => !string.IsNullOrWhiteSpace(DouyinManifestSummaryText)
        ? DouyinManifestSummaryText
        : AttachmentCountText;
}
