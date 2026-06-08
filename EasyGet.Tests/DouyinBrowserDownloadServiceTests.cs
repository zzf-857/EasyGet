using EasyGet.Services;
using EasyGet.Models;
using Xunit;

namespace EasyGet.Tests;

public class DouyinBrowserDownloadServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"easyget-douyin-{Guid.NewGuid():N}");

    [Fact]
    public void TryExtractVideoUrlFromCdpMessage_ReturnsDouyinMp4ResponseUrl()
    {
        const string message = """
            {
              "method": "Network.responseReceived",
              "params": {
                "response": {
                  "mimeType": "video/mp4",
                  "url": "https://v26-web.douyinvod.com/path/video.mp4?a=6383"
                }
              }
            }
            """;

        var found = DouyinBrowserDownloadService.TryExtractVideoUrlFromCdpMessage(message, out var url);

        Assert.True(found);
        Assert.Equal("https://v26-web.douyinvod.com/path/video.mp4?a=6383", url);
    }

    [Fact]
    public void TryExtractThumbnailUrlFromCdpMessage_ReturnsDouyinImageResponseUrl()
    {
        const string message = """
            {
              "method": "Network.responseReceived",
              "params": {
                "response": {
                  "mimeType": "image/jpeg",
                  "url": "https://p3-pc-sign.douyinpic.com/image-cut-tos/cover.jpeg"
                }
              }
            }
            """;

        var found = DouyinBrowserDownloadService.TryExtractThumbnailUrlFromCdpMessage(message, out var url);

        Assert.True(found);
        Assert.Equal("https://p3-pc-sign.douyinpic.com/image-cut-tos/cover.jpeg", url);
    }

    [Fact]
    public void BuildOutputPath_AppendsCounterWhenFileExists()
    {
        Directory.CreateDirectory(_tempDir);
        var existing = Path.Combine(_tempDir, "title.mp4");
        File.WriteAllText(existing, "existing");

        var outputPath = DouyinBrowserDownloadService.BuildOutputPath(_tempDir, "title");

        Assert.Equal(Path.Combine(_tempDir, "title (1).mp4"), outputPath);
    }

    [Fact]
    public void NormalizeDouyinTitle_RemovesPlatformSuffixAndQuotes()
    {
        var title = DouyinBrowserDownloadService.NormalizeDouyinTitle(
            "《队友祭天，法力无边》@哈哈电竞俱乐部（上分点我）#抖音游戏趣讲作者团 - 抖音");

        Assert.Equal("队友祭天，法力无边@哈哈电竞俱乐部（上分点我）#抖音游戏趣讲作者团", title);
    }

    [Fact]
    public void ApplyCapturedMetadata_UpdatesTaskTitlePlatformAndThumbnail()
    {
        var task = new DownloadTask
        {
            Title = "",
            Platform = "",
            ThumbnailUrl = ""
        };

        DouyinBrowserDownloadService.ApplyCapturedMetadata(
            task,
            new DouyinBrowserCaptureResult(
                "https://v26-web.douyinvod.com/video.mp4",
                "《测试视频》 - 抖音",
                "https://p3-pc-sign.douyinpic.com/cover.jpeg"));

        Assert.Equal("测试视频", task.Title);
        Assert.Equal("Douyin", task.Platform);
        Assert.Equal("https://p3-pc-sign.douyinpic.com/cover.jpeg", task.ThumbnailUrl);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
