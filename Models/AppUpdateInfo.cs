namespace EasyGet.Models;

public class AppUpdateInfo
{
    public string CurrentVersion { get; init; } = "";
    public string LatestVersion { get; init; } = "";
    public bool IsUpdateAvailable { get; init; }
    public Uri? ReleasePageUrl { get; init; }
    public Uri? InstallerDownloadUrl { get; init; }
    public string InstallerFileName { get; init; } = "";
    public long InstallerSize { get; init; }
}
