namespace EasyGet.ViewModels;

public enum DownloadPageState
{
    Idle,
    Parsing,
    Ready,
    Scheduled,
    Downloading,
    Completed,
    Failed
}
