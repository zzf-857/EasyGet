using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class TelegramLinkParserTests
{
    [Theory]
    [InlineData("https://t.me/durov/123", "durov", 123, null)]
    [InlineData("https://telegram.me/durov/123?single", "durov", 123, null)]
    [InlineData("https://WWW.TELEGRAM.ME/durov/123/", "durov", 123, null)]
    [InlineData("https://t.me/s/durov/123?single", "durov", 123, null)]
    [InlineData("https://t.me/s/durov/123-125", "durov", 123, 125)]
    [InlineData("https://t.me/c/1234567890/456/789", "-1001234567890", 789, null)]
    [InlineData("https://telegram.me/c/1234567890/456/789?single", "-1001234567890", 789, null)]
    [InlineData("https://t.me/c/1234567890/456/789_791/", "-1001234567890", 789, 791)]
    [InlineData("https://t.me/durov/456/789", "durov", 789, null)]
    [InlineData("https://t.me/durov/456/789-791?single", "durov", 789, 791)]
    [InlineData("https://t.me/durov/789?thread=456&single&t=20#ignored", "durov", 789, null)]
    [InlineData("tg://privatepost?channel=1234567890&post=789&thread=456&single", "-1001234567890", 789, null)]
    [InlineData("TG://PRIVATEPOST?channel=1234567890&post=789_791", "-1001234567890", 789, 791)]
    [InlineData("tg://private?channel=1234567890&post=789", "-1001234567890", 789, null)]
    [InlineData("tg://resolve?domain=durov&post=789&thread=456", "durov", 789, null)]
    [InlineData("tg://resolve?domain=durov&post=%37%38%39", "durov", 789, null)]
    [InlineData(" https://t.me/c/000123/789 ", "-100123", 789, null)]
    public void Parse_ResolvesMessageTarget(string link, string expectedTarget, int expectedStart, int? expectedEnd)
    {
        var parsed = TelegramLinkParser.Parse(link);

        Assert.NotNull(parsed);
        Assert.Equal(expectedTarget, parsed.Value.chatTarget);
        Assert.Equal(expectedStart, parsed.Value.startId);
        Assert.Equal(expectedEnd, parsed.Value.endId);
    }

    [Theory]
    [InlineData("https://t.me/c/1234567890/456?comment=789")]
    [InlineData("https://t.me/durov/456?comment=789")]
    [InlineData("https://t.me/durov/456?comment")]
    [InlineData("https://t.me/durov/456?%63omment=789")]
    [InlineData("tg://privatepost?channel=1234567890&post=456&comment=789")]
    [InlineData("tg://resolve?domain=durov&post=456&COMMENT=789")]
    public void Parse_RejectsCommentsInsteadOfDownloadingOriginalChannelPost(string link)
    {
        Assert.Null(TelegramLinkParser.Parse(link));
    }

    [Theory]
    [InlineData("https://t.me/c/1234567890")]
    [InlineData("https://t.me/durov")]
    [InlineData("https://t.me/s/durov")]
    [InlineData("https://t.me/s/123")]
    [InlineData("https://t.me/+123456")]
    [InlineData("https://t.me/joinchat/123456")]
    [InlineData("https://t.me/addstickers/123456")]
    [InlineData("tg://join?invite=123456")]
    [InlineData("tg://privatepost?channel=1234567890&thread=456")]
    [InlineData("tg://resolve?domain=durov&thread=456")]
    [InlineData("https://t.me/durov/s/123")]
    public void Parse_RequiresAMessageLink(string link)
    {
        Assert.Null(TelegramLinkParser.Parse(link));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("https://t.me.evil.example/durov/123")]
    [InlineData("https://telegram.me.evil.example/c/123/456")]
    [InlineData("https://evil.example/t.me/c/123/456")]
    [InlineData("https://t.me@evil.example/c/123/456")]
    [InlineData("https://user@t.me/c/123/456")]
    [InlineData("https://t.me:8443/c/123/456")]
    [InlineData("https://t.me/c/123/0/456")]
    [InlineData("https://t.me/c/123/2147483648/456")]
    [InlineData("https://t.me/c/123/456/789/999")]
    [InlineData("https://t.me/c/123//456")]
    [InlineData("https://t.me/c/123/456//")]
    [InlineData("https://t.me/c/0/456")]
    [InlineData("https://t.me/c/9223372036854775807/456")]
    [InlineData("https://t.me/durov/0")]
    [InlineData("https://t.me/durov/2147483648")]
    [InlineData("https://t.me/durov/125-123")]
    [InlineData("https://t.me/durov/123-124-125")]
    [InlineData("https://t.me/durov/456/789?thread=0")]
    [InlineData("tg://privatepost.evil.example?channel=123&post=456")]
    [InlineData("tg://privatepost/other?channel=123&post=456")]
    [InlineData("tg://privatepost?channel=-100123&post=456")]
    [InlineData("tg://privatepost?channel=123&post=456&post=789")]
    [InlineData("tg://privatepost?channel=123&channel=789&post=456")]
    [InlineData("tg://resolve?domain=durov&post")]
    [InlineData("tg://resolve?domain=durov&post=456%ZZ")]
    [InlineData("tg://resolve?domain=durov&post=456&thread=")]
    public void Parse_RejectsMalformedOrAmbiguousLinks(string? link)
    {
        Assert.Null(TelegramLinkParser.Parse(link));
    }
}
