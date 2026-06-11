using EasyGet.Services;

namespace EasyGet.ViewModels;

public interface IVideoInfoProvider
{
    Task<VideoInfo?> GetVideoInfoAsync(string url, CancellationToken cancellationToken);
}

public sealed class YtDlpVideoInfoProvider : IVideoInfoProvider
{
    private readonly YtDlpService _ytDlpService;

    public YtDlpVideoInfoProvider(YtDlpService ytDlpService)
    {
        _ytDlpService = ytDlpService;
    }

    public Task<VideoInfo?> GetVideoInfoAsync(string url, CancellationToken cancellationToken)
        => _ytDlpService.GetVideoInfoAsync(url, cancellationToken);
}
