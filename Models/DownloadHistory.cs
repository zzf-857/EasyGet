using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

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

    /// <summary>下载时间</summary>
    public DateTime DownloadTime { get; set; } = DateTime.Now;

    /// <summary>缩略图 URL</summary>
    public string ThumbnailUrl { get; set; } = "";

    /// <summary>文件是否存在（用于 UI 灰显判断，由 ViewModel 异步刷新）</summary>
    [ObservableProperty]
    private bool _fileExists = true;

    /// <summary>
    /// 格式化的文件大小
    /// </summary>
    public string FileSizeText
    {
        get
        {
            string[] sizes = ["B", "KB", "MB", "GB", "TB"];
            double len = FileSize;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.#} {sizes[order]}";
        }
    }

    /// <summary>
    /// 格式化的下载时间
    /// </summary>
    public string DownloadTimeText => DownloadTime == DateTime.MinValue
        ? "--"
        : DownloadTime.ToString("yyyy-MM-dd HH:mm");
}
