using EasyGet.ViewModels;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class DownloadViewModelTests
{
    [Theory]
    [InlineData("https://youtu.be/abc123，", "https://youtu.be/abc123")]
    [InlineData("https://www.youtube.com/watch?v=abc123。", "https://www.youtube.com/watch?v=abc123")]
    [InlineData("https://v.douyin.com/i6EpMYVJgA8/）", "https://v.douyin.com/i6EpMYVJgA8/")]
    public void ExtractUrl_RemovesTrailingShareTextPunctuation(string input, string expected)
    {
        Assert.Equal(expected, DownloadViewModel.ExtractUrl(input));
    }

    [Fact]
    public void ExtractUrl_RemovesTrailingPunctuationFromMixedShareText()
    {
        var input = "复制打开： https://youtu.be/abc123，看看这个视频";

        Assert.Equal("https://youtu.be/abc123", DownloadViewModel.ExtractUrl(input));
    }

    [Fact]
    public void LogTextJoinsLogLinesForSelectableTextViewer()
    {
        var configService = new ConfigService();
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "EasyGetTests",
            Guid.NewGuid().ToString("N"),
            "history.db");
        using var historyService = new HistoryService(dbPath);
        var viewModel = new DownloadViewModel(
            new DownloadManager(
                new YtDlpService(configService, new EnvironmentService()),
                historyService,
                configService),
            configService);

        viewModel.LogLines.Add("[12:00:00] 开始下载");
        viewModel.LogLines.Add("[12:00:01] 下载完成");

        Assert.Equal(
            "[12:00:00] 开始下载" + Environment.NewLine + "[12:00:01] 下载完成",
            viewModel.LogText);

        viewModel.LogLines.Clear();

        Assert.Equal("", viewModel.LogText);
    }
}
