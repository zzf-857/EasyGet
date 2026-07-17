using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>
/// 控制多个下载任务共享的连接预算，避免“任务数 × 单任务线程数”无限放大。
/// </summary>
internal static class DownloadConcurrencyPolicy
{
    internal static int ResolvePerTaskConnections(
        int configuredConnections,
        int maxConcurrentDownloads)
    {
        var connections = Math.Clamp(
            configuredConnections,
            AppConfig.MinConcurrentFragments,
            AppConfig.MaxConcurrentFragments);
        var concurrentDownloads = Math.Clamp(
            maxConcurrentDownloads,
            AppConfig.MinConcurrentDownloadLimit,
            AppConfig.MaxConcurrentDownloadLimit);

        return concurrentDownloads >= 8
            ? Math.Min(connections, 4)
            : connections;
    }
}
