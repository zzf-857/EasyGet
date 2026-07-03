using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class DouyinUrlParserTests
{
    [Theory]
    [InlineData("https://v.douyin.com/i6EpMYVJgA8/", "i6EpMYVJgA8")]
    [InlineData("https://v.iesdouyin.com/ZM8AqvE/", "ZM8AqvE")]
    [InlineData("v.douyin.com/share-token/", "share-token")]
    public void Parse_RecognizesShortLinks(string url, string expectedToken)
    {
        var info = DouyinUrlParser.Parse(url);

        Assert.Equal(DouyinUrlKind.ShortLink, info.Kind);
        Assert.Equal(expectedToken, info.Id);
        Assert.True(info.RequiresExpansion);
        Assert.True(info.IsRecognized);
    }

    [Theory]
    [InlineData("https://www.douyin.com/video/7621772413184822582", "Video", "7621772413184822582")]
    [InlineData("https://www.douyin.com/note/7383344556677889900?previous_page=app_code_link", "Note", "7383344556677889900")]
    [InlineData("https://www.douyin.com/gallery/7333344556677889900", "Gallery", "7333344556677889900")]
    [InlineData("https://www.douyin.com/slides/7223344556677889900", "Slides", "7223344556677889900")]
    [InlineData("https://www.douyin.com/user/MS4wLjABAAAAsec_uid-test_123", "User", "MS4wLjABAAAAsec_uid-test_123")]
    [InlineData("https://www.douyin.com/user/self?showTab=favorite_collection", "User", "self")]
    [InlineData("https://www.douyin.com/collection/7123456789012345678", "Collection", "7123456789012345678")]
    [InlineData("https://www.douyin.com/mix/7123456789012345678", "Mix", "7123456789012345678")]
    [InlineData("https://www.douyin.com/music/7123456789012345678", "Music", "7123456789012345678")]
    [InlineData("https://live.douyin.com/123456789", "Live", "123456789")]
    [InlineData("https://www.douyin.com/follow/live/123456789", "Live", "123456789")]
    public void Parse_RecognizesTypedDouyinUrls(string url, string expectedKind, string expectedId)
    {
        var info = DouyinUrlParser.Parse(url);

        Assert.Equal(expectedKind, info.Kind.ToString());
        Assert.Equal(expectedId, info.Id);
        Assert.False(info.RequiresExpansion);
        Assert.True(info.IsRecognized);
    }

    [Fact]
    public void Parse_RecognizesDouyinModalIdVideoUrl()
    {
        var info = DouyinUrlParser.Parse("https://www.douyin.com/?modal_id=7621772413184822582");

        Assert.Equal(DouyinUrlKind.Video, info.Kind);
        Assert.Equal("7621772413184822582", info.Id);
        Assert.False(info.RequiresExpansion);
        Assert.True(info.IsRecognized);
    }

    [Fact]
    public void Parse_RecognizesNestedShareVideoUrl()
    {
        var info = DouyinUrlParser.Parse("https://www.iesdouyin.com/share/video/7621772413184822582");

        Assert.Equal(DouyinUrlKind.Video, info.Kind);
        Assert.Equal("7621772413184822582", info.Id);
        Assert.False(info.RequiresExpansion);
        Assert.True(info.IsRecognized);
    }

    [Fact]
    public void Parse_RecognizesNestedShareUserUrl()
    {
        var info = DouyinUrlParser.Parse("https://www.iesdouyin.com/share/user/MS4wLjABAAAAsec_uid-test_123");

        Assert.Equal(DouyinUrlKind.User, info.Kind);
        Assert.Equal("MS4wLjABAAAAsec_uid-test_123", info.Id);
        Assert.False(info.RequiresExpansion);
        Assert.True(info.IsRecognized);
    }

    [Fact]
    public void Parse_RecognizesNestedShareNoteUrl()
    {
        var info = DouyinUrlParser.Parse("https://www.iesdouyin.com/share/note/7383344556677889900");

        Assert.Equal(DouyinUrlKind.Note, info.Kind);
        Assert.Equal("7383344556677889900", info.Id);
        Assert.False(info.RequiresExpansion);
        Assert.True(info.IsRecognized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com/video/7621772413184822582")]
    [InlineData("https://www.douyin.com/challenge/123456")]
    [InlineData("https://notdouyin.com/video/7621772413184822582")]
    [InlineData("https://live.douyin.com/")]
    public void Parse_ReturnsUnknownForUnsupportedOrIncompleteUrls(string url)
    {
        var info = DouyinUrlParser.Parse(url);

        Assert.Equal(DouyinUrlKind.Unknown, info.Kind);
        Assert.Null(info.Id);
        Assert.False(info.RequiresExpansion);
        Assert.False(info.IsRecognized);
    }

    [Fact]
    public void TryParse_ReturnsTrueOnlyForRecognizedUrls()
    {
        Assert.True(DouyinUrlParser.TryParse("https://www.douyin.com/video/7621772413184822582", out var recognized));
        Assert.Equal(DouyinUrlKind.Video, recognized.Kind);

        Assert.False(DouyinUrlParser.TryParse("https://example.com/video/7621772413184822582", out var unknown));
        Assert.Equal(DouyinUrlKind.Unknown, unknown.Kind);
    }
}
