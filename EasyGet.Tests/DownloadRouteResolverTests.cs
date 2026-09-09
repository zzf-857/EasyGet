using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class DownloadRouteResolverTests
{
    [Theory]
    [InlineData("https://t.me/c/1234567890/456/789")]
    [InlineData("https://telegram.me/c/1234567890/456/789?single")]
    [InlineData("tg://privatepost?channel=1234567890&post=789&thread=456")]
    public void Resolve_RoutesPrivateMessageLinksToTelegram(string url)
    {
        Assert.Equal(DownloadEngine.Telegram, DownloadRouteResolver.Resolve(url));
        Assert.True(DownloadRouteResolver.TryCreateLocalVideoInfo(url, out var info));
        Assert.Equal("Telegram", info.Platform);
        Assert.Equal("TG_-1001234567890_789", info.Title);
    }

    [Theory]
    [InlineData("https://t.me/durov/456?comment=789")]
    [InlineData("https://t.me/c/1234567890/456?comment=789&single")]
    [InlineData("https://telegram.me/durov/456?%63omment=789")]
    [InlineData("tg://privatepost?channel=1234567890&post=456&COMMENT=789")]
    [InlineData("tg://resolve?domain=durov&post=456&comment=789")]
    public void Resolve_RejectsCommentLinksBeforeFallingBackToAnotherEngine(string url)
    {
        var exception = Assert.Throws<NotSupportedException>(() => DownloadRouteResolver.Resolve(url));

        Assert.Contains("评论链接", exception.Message);
        Assert.Contains("请复制讨论组内具体消息链接", exception.Message);
        Assert.Throws<NotSupportedException>(() => DownloadRouteResolver.Resolve(url, resourceHint: true));
        Assert.Throws<NotSupportedException>(() => DownloadRouteResolver.TryCreateLocalVideoInfo(url, out _));
    }

    [Theory]
    [InlineData("https://example.com/durov/456?comment=789")]
    [InlineData("https://t.me.evil.example/durov/456?comment=789")]
    [InlineData("https://t.me@evil.example/durov/456?comment=789")]
    [InlineData("https://t.me/durov?comment=789")]
    [InlineData("https://t.me/joinchat/456?comment=789")]
    [InlineData("https://t.me/c/1234567890?comment=789")]
    [InlineData("tg://privatepost?channel=1234567890&comment=789")]
    public void Resolve_DoesNotTreatUnrelatedOrNonMessageLinksAsTelegramComments(string url)
    {
        Assert.False(TelegramLinkParser.IsTelegramMessageLinkWithComment(url));
        Assert.Equal(DownloadEngine.YtDlp, DownloadRouteResolver.Resolve(url));
    }
}
