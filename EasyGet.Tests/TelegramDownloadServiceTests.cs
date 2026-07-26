using System;
using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class TelegramDownloadServiceTests
{
    [Theory]
    [InlineData("https://t.me/durov/123")]
    [InlineData("http://T.ME/durov/123")]
    [InlineData("https://www.t.me/durov/123")]
    [InlineData("https://t.me/durov/123/")]
    [InlineData("tg://resolve?domain=durov&post=123")]
    [InlineData("https://t.me/c/1234567890/456")]
    [InlineData("tg://private?channel=1234567890&post=456")]
    public void IsTelegramUrl_ReturnsTrueForTelegramUrls(string url)
    {
        Assert.True(TelegramDownloadService.IsTelegramUrl(url));
    }

    [Theory]
    [InlineData("https://t.me/durov/123", "durov", 123, null)]
    [InlineData("https://t.me/durov/123-125", "durov", 123, 125)]
    [InlineData("https://www.t.me/durov/123?single", "durov", 123, null)]
    [InlineData("https://t.me/durov/123/", "durov", 123, null)]
    [InlineData("tg://resolve?domain=durov&post=123", "durov", 123, null)]
    [InlineData("https://t.me/c/1234567890/456", "-1001234567890", 456, null)]
    [InlineData("https://t.me/c/1234567890/456-460", "-1001234567890", 456, 460)]
    [InlineData("tg://private?channel=1234567890&post=456", "-1001234567890", 456, null)]
    [InlineData("tg://private?channel=1234567890&post=456_460", "-1001234567890", 456, 460)]
    public void ParseTelegramLink_CorrectlyParsesValidLinks(string link, string expectedChat, int expectedStart, int? expectedEnd)
    {
        var result = TelegramDownloadService.ParseTelegramLink(link);
        Assert.NotNull(result);
        Assert.Equal(expectedChat, result.Value.chatTarget);
        Assert.Equal(expectedStart, result.Value.startId);
        Assert.Equal(expectedEnd, result.Value.endId);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://telegram.me/durov/123")]
    [InlineData("https://t.me/durov")]
    [InlineData("https://t.me/c/1234567890")]
    [InlineData("https://evil.example/path/t.me/durov/123")]
    [InlineData("https://t.me.evil.example/durov/123")]
    [InlineData("https://t.me@evil.example/durov/123")]
    [InlineData("https://t.me/durov/123/extra")]
    [InlineData("https://t.me/durov/not-a-number")]
    [InlineData("https://t.me/durov/0")]
    [InlineData("https://t.me/durov/2147483648")]
    [InlineData("https://t.me/durov/125-123")]
    [InlineData("tg://join?domain=durov&post=123")]
    [InlineData("tg://resolve?domain=durov")]
    [InlineData("tg://private?channel=1234567890")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TelegramUrlValidation_RejectsInvalidLinks(string? link)
    {
        var result = TelegramDownloadService.ParseTelegramLink(link);

        Assert.Null(result);
        Assert.False(TelegramDownloadService.IsTelegramUrl(link));
    }

    [Theory]
    [InlineData("..\\outside.bin", "outside.bin")]
    [InlineData("C:\\Temp\\outside.bin", "outside.bin")]
    [InlineData("folder/subfolder/video.mp4", "video.mp4")]
    [InlineData("bad:name?.mp4", "bad：name？.mp4")]
    public void BuildSafeMediaFilePath_ConfinesRemoteFileNameToSaveDirectory(
        string remoteFileName,
        string expectedFileName)
    {
        var savePath = Path.Combine(Path.GetTempPath(), "easyget-telegram-message");

        var result = TelegramDownloadService.BuildSafeMediaFilePath(
            savePath,
            remoteFileName,
            "media.bin");

        Assert.Equal(Path.GetFullPath(savePath), Path.GetDirectoryName(result));
        Assert.Equal(expectedFileName, Path.GetFileName(result));
    }

    [Fact]
    public void BuildSafeMediaFilePath_UsesSanitizedFallbackAndPrefix()
    {
        var savePath = Path.Combine(Path.GetTempPath(), "easyget-telegram-message");

        var result = TelegramDownloadService.BuildSafeMediaFilePath(
            savePath,
            "   ",
            "media:1.bin",
            "0001_");

        Assert.Equal("0001_media：1.bin", Path.GetFileName(result));
    }

    [Fact]
    public void CreateCancellableProgressCallback_CancelledTokenStopsBeforeReporting()
    {
        using var cancellation = new CancellationTokenSource();
        var reportCount = 0;
        var callback = TelegramDownloadService.CreateCancellableProgressCallback(
            cancellation.Token,
            (_, _) => reportCount++);

        callback(10, 100);
        cancellation.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(() => callback(20, 100));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, reportCount);
    }

    [Fact]
    public async Task DownloadAsync_PreCancelledTokenMarksTaskCancelledWithoutConnecting()
    {
        using var service = new TelegramDownloadService(new TestConfigService());
        using var cancellation = new CancellationTokenSource();
        var task = new DownloadTask
        {
            Url = "https://t.me/durov/123",
            OutputDirectory = Path.GetTempPath()
        };
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.DownloadAsync(task, ct: cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(DownloadStatus.Cancelled, task.Status);
    }
}
