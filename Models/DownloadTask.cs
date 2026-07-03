using CommunityToolkit.Mvvm.ComponentModel;
using EasyGet.Services;

namespace EasyGet.Models;

/// <summary>
/// 下载任务状态
/// </summary>
public enum DownloadStatus
{
    Waiting,
    Resolving,
    Downloading,
    Merging,
    Completed,
    Failed,
    Cancelled,
    Paused
}

/// <summary>
/// 下载任务模型（可观察，用于绑定 UI）
/// </summary>
public partial class DownloadTask : ObservableObject
{
    /// <summary>任务唯一 ID</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>视频 URL</summary>
    public string Url { get; set; } = "";

    /// <summary>视频标题</summary>
    [ObservableProperty] private string _title = "";

    /// <summary>平台名称</summary>
    [ObservableProperty] private string _platform = "";

    /// <summary>视频时长 (秒)</summary>
    [ObservableProperty] private double _duration;

    /// <summary>文件大小 (bytes)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileSizeText))]
    private long _fileSize;

    /// <summary>缩略图 URL</summary>
    [ObservableProperty] private string _thumbnailUrl = "";

    /// <summary>下载格式</summary>
    public string Format { get; set; } = "mp4";

    /// <summary>下载画质</summary>
    public string Quality { get; set; } = "best";

    /// <summary>字幕选项</summary>
    public string Subtitle { get; set; } = "none";

    /// <summary>下载目录</summary>
    public string OutputDirectory { get; set; } = "";

    /// <summary>输出文件路径</summary>
    [ObservableProperty] private string _outputFilePath = "";

    /// <summary>所有安全输出文件路径（Douyin sidecar 可返回多个产物）</summary>
    public List<string> OutputFilePaths { get; set; } = [];

    /// <summary>下载进度 0-100</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    [NotifyPropertyChangedFor(nameof(EtaText))]
    private double _progress;

    /// <summary>下载速度 (bytes/s)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    private double _speed;

    /// <summary>预估剩余时间 (秒)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EtaText))]
    private double _eta;

    /// <summary>已下载大小 (bytes)</summary>
    [ObservableProperty] private long _downloadedSize;

    /// <summary>当前状态</summary>
    [ObservableProperty] private DownloadStatus _status = DownloadStatus.Waiting;

    /// <summary>错误信息</summary>
    [ObservableProperty] private string _errorMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDouyinTaskOutcome))]
    [NotifyPropertyChangedFor(nameof(DouyinTaskOutcomeSummaryText))]
    private int _douyinSuccessCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDouyinTaskOutcome))]
    [NotifyPropertyChangedFor(nameof(DouyinTaskOutcomeSummaryText))]
    private int _douyinFailedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDouyinTaskOutcome))]
    [NotifyPropertyChangedFor(nameof(DouyinTaskOutcomeSummaryText))]
    private int _douyinSkippedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDouyinTaskEventLog))]
    private string _douyinTaskEventLog = "";

    /// <summary>取消令牌源</summary>
    public CancellationTokenSource? Cts { get; set; }

    /// <summary>
    /// 格式化的文件大小
    /// </summary>
    public string FileSizeText => ByteSizeFormatter.FormatClampZero(FileSize);

    public bool HasDouyinTaskOutcome
        => DouyinSuccessCount > 0 || DouyinFailedCount > 0 || DouyinSkippedCount > 0;

    public string DouyinTaskOutcomeSummaryText
    {
        get
        {
            var parts = new List<string>();
            if (DouyinSuccessCount > 0)
                parts.Add($"成功 {DouyinSuccessCount}");
            if (DouyinFailedCount > 0)
                parts.Add($"失败 {DouyinFailedCount}");
            if (DouyinSkippedCount > 0)
                parts.Add($"跳过 {DouyinSkippedCount}");

            return string.Join(" / ", parts);
        }
    }

    public bool HasDouyinTaskEventLog => !string.IsNullOrWhiteSpace(DouyinTaskEventLog);

    /// <summary>
    /// 格式化的下载速度
    /// </summary>
    public string SpeedText => $"{ByteSizeFormatter.FormatClampZero((long)Speed)}/s";

    /// <summary>
    /// 格式化的 ETA
    /// </summary>
    public string EtaText
    {
        get
        {
            if (Eta <= 0) return "--:--";
            var ts = TimeSpan.FromSeconds(Eta);
            return ts.Hours > 0 ? $"{ts:hh\\:mm\\:ss}" : $"{ts:mm\\:ss}";
        }
    }

    /// <summary>
    /// 格式化的时长
    /// </summary>
    public string DurationText
    {
        get
        {
            var ts = TimeSpan.FromSeconds(Duration);
            return ts.Hours > 0 ? $"{ts:hh\\:mm\\:ss}" : $"{ts:mm\\:ss}";
        }
    }

}
