using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public sealed class YangshipinUrlParserTests
{
    [Theory]
    [InlineData("https://www.yangshipin.cn/video/home?vid=b000045ctqj")]
    [InlineData("http://yangshipin.cn/video/home/?foo=1&VID=b000045ctqj")]
    [InlineData("https://m.yangshipin.cn/video/home?vid=b000045ctqj&from=share")]
    public void TryParse_AcceptsSupportedVideoPages(string url)
    {
        var parsed = YangshipinUrlParser.TryParse(url, out var info);

        Assert.True(parsed);
        Assert.Equal("b000045ctqj", info.VideoId);
        Assert.Equal(
            "https://www.yangshipin.cn/video/home?vid=b000045ctqj",
            info.PageUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://www.yangshipin.cn/video/home?vid=b000045ctqj")]
    [InlineData("https://www.yangshipin.cn/video/home")]
    [InlineData("https://www.yangshipin.cn/project/home?vid=b000045ctqj")]
    [InlineData("https://evil-yangshipin.cn/video/home?vid=b000045ctqj")]
    [InlineData("https://yangshipin.cn.evil.example/video/home?vid=b000045ctqj")]
    [InlineData("https://user:secret@www.yangshipin.cn/video/home?vid=b000045ctqj")]
    [InlineData("https://www.yangshipin.cn/video/home?vid=bad!")]
    [InlineData("https://www.yangshipin.cn/video/home?vid=%ZZbad")]
    public void TryParse_RejectsUnsupportedOrSpoofedUrls(string url)
    {
        Assert.False(YangshipinUrlParser.TryParse(url, out _));
    }
}
