namespace EasyGet.ViewModels;

public enum DownloadPageState
{
    Idle,
    Parsing,
    Ready,
    Downloading,
    Completed,
    Failed
}
