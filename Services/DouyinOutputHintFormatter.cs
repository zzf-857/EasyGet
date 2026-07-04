using System.IO;

namespace EasyGet.Services;

internal static class DouyinOutputHintFormatter
{
    private const string HlsPlaylistExtension = ".m3u8";

    public static int CountLiveHlsPlaylistFiles(
        string url,
        IEnumerable<string> outputFilePaths)
    {
        if (!IsDouyinLiveUrl(url))
            return 0;

        return outputFilePaths.Count(IsHlsPlaylistPath);
    }

    public static string FormatLiveHlsPlaylistSummary(int playlistCount)
        => playlistCount > 0 ? $"直播 HLS playlist {playlistCount}" : "";

    public static string FormatLiveHlsPlaylistWarning(int playlistCount)
    {
        var summary = FormatLiveHlsPlaylistSummary(playlistCount);
        return string.IsNullOrWhiteSpace(summary)
            ? ""
            : $"{summary}: 保存的是播放列表文本，不是可直接播放视频；可用 ffmpeg 转换。";
    }

    private static bool IsDouyinLiveUrl(string url)
        => DouyinUrlParser.Parse(url).Kind == DouyinUrlKind.Live;

    private static bool IsHlsPlaylistPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            return string.Equals(
                Path.GetExtension(path.Trim()),
                HlsPlaylistExtension,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
