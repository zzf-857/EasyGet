using System;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class TelegramDownloadServiceTests
{
    [Theory]
    [InlineData("https://t.me/durov/123")]
    [InlineData("http://T.ME/durov/123")]
    [InlineData("tg://resolve?domain=durov&post=123")]
    [InlineData("https://t.me/c/1234567890/456")]
    [InlineData("tg://private?channel=1234567890&post=456")]
    public void IsTelegramUrl_ReturnsTrueForTelegramUrls(string url)
    {
        Assert.True(TelegramDownloadService.IsTelegramUrl(url));
    }

    [Theory]
    [InlineData("https://example.com/video.mp4")]
    [InlineData("https://youtube.com/watch?v=123")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsTelegramUrl_ReturnsFalseForOtherUrls(string url)
    {
        Assert.False(TelegramDownloadService.IsTelegramUrl(url));
    }

    [Theory]
    [InlineData("https://t.me/durov/123", "durov", 123, null)]
    [InlineData("https://t.me/durov/123-125", "durov", 123, 125)]
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
    [InlineData("https://t.me/durov")]
    [InlineData("https://t.me/c/1234567890")]
    public void ParseTelegramLink_ReturnsNullForInvalidLinks(string link)
    {
        var result = TelegramDownloadService.ParseTelegramLink(link);
        Assert.Null(result);
    }
}
