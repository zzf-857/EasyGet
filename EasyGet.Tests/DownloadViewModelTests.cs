using EasyGet.ViewModels;
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
}
