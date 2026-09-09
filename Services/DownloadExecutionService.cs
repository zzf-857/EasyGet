using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>Selects download engines and owns fallback behavior and output reservations.</summary>
internal sealed class DownloadExecutionService
{
    private readonly IYtDlpDownloadService _ytDlpService;
    private readonly M3u8DownloadService _m3u8DownloadService;
    private readonly TelegramDownloadService _telegramDownloadService;
    private readonly HttpResourceDownloadService _resourceDownloadService;

    internal DownloadExecutionService(
        IYtDlpDownloadService ytDlpService,
        ConfigService configService,
        M3u8DownloadService? m3u8DownloadService = null,
        TelegramDownloadService? telegramDownloadService = null,
        HttpResourceDownloadService? resourceDownloadService = null)
    {
        _ytDlpService = ytDlpService;
        _m3u8DownloadService = m3u8DownloadService ?? new M3u8DownloadService(configService, new EnvironmentService());
        _telegramDownloadService = telegramDownloadService ?? new TelegramDownloadService(configService);
        _resourceDownloadService = resourceDownloadService ?? new HttpResourceDownloadService(configService);
    }

    internal Task<VideoInfo?> GetVideoInfoAsync(string url, CancellationToken cancellationToken)
        => _ytDlpService.GetVideoInfoAsync(url, cancellationToken);

    internal async Task DownloadAsync(
        DownloadTask task,
        IProgress<DownloadProgress> progress,
        Action<string> log,
        CancellationToken token)
    {
        var engine = DownloadRouteResolver.Resolve(task.Url, task.IsNonVideoResource);

        if (engine == DownloadEngine.M3u8)
        {
            try
            {
                await _m3u8DownloadService.DownloadAsync(task, progress, log, token);
                return;
            }
            catch (NotSupportedException ex)
            {
                log($"[m3u8] {ex.Message}");
                log("[m3u8] 尝试自动回退到默认下载器 (yt-dlp)...");
                task.Status = DownloadStatus.Downloading;
                task.ErrorMessage = string.Empty; // 必须清空错误信息，否则 UI 会一直显示红字导致用户误解

                await DownloadWithReservedYtDlpOutputAsync(task, progress, log, token);
                return;
            }
        }

        if (engine == DownloadEngine.Telegram)
        {
            await _telegramDownloadService.DownloadAsync(task, progress, log, token);
            return;
        }

        if (engine == DownloadEngine.Resource)
        {
            await _resourceDownloadService.DownloadAsync(task, progress, log, token);
            return;
        }

        await DownloadWithReservedYtDlpOutputAsync(task, progress, log, token);
    }

    private async Task DownloadWithReservedYtDlpOutputAsync(
        DownloadTask task,
        IProgress<DownloadProgress> progress,
        Action<string> log,
        CancellationToken token)
    {
        var requestedFileName =
            $"{DownloadFileNameBuilder.SanitizeResolvedTitle(task.OutputFileNameOverride ?? task.Title)}{ResolveExpectedYtDlpExtension(task.Format)}";
        using var reservation = DownloadOutputPathReservation.Reserve(
            task.OutputDirectory,
            requestedFileName);
        var previous = task.OutputFileNameOverride;
        task.OutputFileNameOverride = System.IO.Path.GetFileNameWithoutExtension(reservation.Path);
        try
        {
            await _ytDlpService.DownloadAsync(task, progress, log, token);
        }
        finally
        {
            task.OutputFileNameOverride = previous;
        }
    }

    private static string ResolveExpectedYtDlpExtension(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "mp3" => ".mp3",
            "m4a" => ".m4a",
            "mkv" => ".mkv",
            "webm" => ".webm",
            _ => ".mp4"
        };

}

internal interface IYtDlpDownloadService
{
    Task<VideoInfo?> GetVideoInfoAsync(string url, CancellationToken cancellationToken = default);

    Task DownloadAsync(
        DownloadTask task,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null,
        CancellationToken cancellationToken = default);
}

internal sealed class YtDlpDownloadServiceAdapter(YtDlpService ytDlpService) : IYtDlpDownloadService
{
    public Task<VideoInfo?> GetVideoInfoAsync(string url, CancellationToken cancellationToken = default)
        => ytDlpService.GetVideoInfoAsync(url, cancellationToken);

    public Task DownloadAsync(
        DownloadTask task,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null,
        CancellationToken cancellationToken = default)
        => ytDlpService.DownloadAsync(task, progress, logCallback, cancellationToken);
}
