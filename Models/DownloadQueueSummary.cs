namespace EasyGet.Models;

/// <summary>A single-pass snapshot reused by queue labels, filters and action availability.</summary>
internal sealed class DownloadQueueSummary
{
    public int Total { get; private set; }
    public int Waiting { get; private set; }
    public int Resolving { get; private set; }
    public int Downloading { get; private set; }
    public int Merging { get; private set; }
    public int Completed { get; private set; }
    public int Failed { get; private set; }
    public int Cancelled { get; private set; }
    public int Paused { get; private set; }
    public int Scheduled { get; private set; }
    public int Running => Resolving + Downloading + Merging;
    public int Finished => Completed + Failed + Cancelled;
    public int Remaining => Total - Finished;
    public bool CanStop => Waiting + Running + Paused + Scheduled > 0;
    public double OverallProgress { get; private set; }
    public double AggregateSpeed { get; private set; }

    internal static DownloadQueueSummary Capture(IEnumerable<DownloadTask> tasks)
    {
        var summary = new DownloadQueueSummary();
        double progress = 0;
        foreach (var task in tasks)
        {
            summary.Total++;
            switch (task.Status)
            {
                case DownloadStatus.Waiting: summary.Waiting++; break;
                case DownloadStatus.Resolving: summary.Resolving++; break;
                case DownloadStatus.Downloading: summary.Downloading++; break;
                case DownloadStatus.Merging: summary.Merging++; break;
                case DownloadStatus.Completed: summary.Completed++; break;
                case DownloadStatus.Failed: summary.Failed++; break;
                case DownloadStatus.Cancelled: summary.Cancelled++; break;
                case DownloadStatus.Paused: summary.Paused++; break;
                case DownloadStatus.Scheduled: summary.Scheduled++; break;
            }
            progress += task.Status == DownloadStatus.Completed ? 100
                : double.IsFinite(task.Progress) ? Math.Clamp(task.Progress, 0, 100) : 0;
            if (task.Status == DownloadStatus.Downloading && double.IsFinite(task.Speed))
                summary.AggregateSpeed += Math.Max(0, task.Speed);
        }
        summary.OverallProgress = summary.Total == 0 ? 0 : progress / summary.Total;
        return summary;
    }
}
