using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class ShareUrlExtractorTests
{
    [Theory]
    [InlineData("https://youtu.be/abc123，", "https://youtu.be/abc123")]
    [InlineData("https://www.youtube.com/watch?v=abc123。", "https://www.youtube.com/watch?v=abc123")]
    [InlineData("https://v.douyin.com/i6EpMYVJgA8/）", "https://v.douyin.com/i6EpMYVJgA8/")]
    public void Extract_RemovesTrailingShareTextPunctuation(string input, string expected)
    {
        Assert.Equal(expected, ShareUrlExtractor.Extract(input));
    }

    [Fact]
    public void Extract_ReturnsFirstUrlFromMixedShareText()
    {
        var input = "复制打开： https://youtu.be/abc123，看看这个视频";

        Assert.Equal("https://youtu.be/abc123", ShareUrlExtractor.Extract(input));
    }

    [Fact]
    public void Extract_ReturnsDouyinShortUrlFromFullShareText()
    {
        var input = "8.25 复制打开抖音，看看【意联Idealink的作品】父母眼中的“享福”四件套 # AI工具 # AI短... https://v.douyin.com/vi3b7QpNklg/ mDu:/ :2pm q@R.kC 08/19";

        Assert.Equal("https://v.douyin.com/vi3b7QpNklg/", ShareUrlExtractor.Extract(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello world")]
    [InlineData("ftp://example.com/video")]
    public void Extract_ReturnsNullWhenTextHasNoHttpUrl(string input)
    {
        Assert.Null(ShareUrlExtractor.Extract(input));
    }
}
