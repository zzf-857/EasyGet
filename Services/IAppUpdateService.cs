using EasyGet.Models;

namespace EasyGet.Services;

public interface IAppUpdateService
{
    string CurrentVersion { get; }

    Task<AppUpdateInfo> CheckLatestAsync(CancellationToken ct = default);

    Task<string> DownloadInstallerAsync(
        AppUpdateInfo updateInfo,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    bool LaunchInstaller(string installerPath);
}
