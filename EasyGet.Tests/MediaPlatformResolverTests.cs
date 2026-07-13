using EasyGet.Services.Cookies;
using Xunit;

namespace EasyGet.Tests;

public class MediaPlatformResolverTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=video", "youtube", "https://accounts.google.com/ServiceLogin?service=youtube", "youtube.com|google.com")]
    [InlineData("https://space.bilibili.com/123", "bilibili", "https://passport.bilibili.com/login", "bilibili.com")]
    [InlineData("https://www.douyin.com/video/123", "douyin", "https://www.douyin.com/", "douyin.com")]
    [InlineData("https://www.tiktok.com/@creator/video/123", "tiktok", "https://www.tiktok.com/login", "tiktok.com")]
    [InlineData("https://x.com/user/status/123", "twitter", "https://x.com/i/flow/login", "x.com|twitter.com")]
    [InlineData("https://www.instagram.com/reel/example/", "instagram", "https://www.instagram.com/accounts/login/", "instagram.com")]
    [InlineData("https://www.facebook.com/watch/?v=123", "facebook", "https://www.facebook.com/login", "facebook.com")]
    [InlineData("https://www.kuaishou.com/short-video/123", "kuaishou", "https://www.kuaishou.com/", "kuaishou.com")]
    [InlineData("https://www.xiaohongshu.com/explore/123", "xiaohongshu", "https://www.xiaohongshu.com/", "xiaohongshu.com")]
    [InlineData("https://weibo.com/tv/show/123", "weibo", "https://weibo.com/login.php", "weibo.com")]
    [InlineData("https://www.twitch.tv/example", "twitch", "https://www.twitch.tv/login", "twitch.tv")]
    public void Resolve_MapsKnownPlatforms(
        string url,
        string expectedId,
        string expectedLoginUri,
        string expectedCookieDomains)
    {
        var definition = MediaPlatformResolver.Resolve(url);

        Assert.Equal(expectedId, definition.Id);
        Assert.Equal(new Uri(expectedLoginUri), definition.LoginUri);
        Assert.Equal(expectedCookieDomains.Split('|'), definition.CookieDomains);
        Assert.True(definition.AnonymousFirst);
    }

    [Theory]
    [InlineData("https://youtu.be/video", "youtube")]
    [InlineData("https://b23.tv/example", "bilibili")]
    [InlineData("https://xhslink.com/a/example", "xiaohongshu")]
    public void Resolve_MapsShortLinkHosts(string url, string expectedId)
    {
        var definition = MediaPlatformResolver.Resolve(url);

        Assert.Equal(expectedId, definition.Id);
    }

    [Fact]
    public void Resolve_UsesCompleteHostBoundaries()
    {
        var definition = MediaPlatformResolver.Resolve("https://evilyoutube.com/watch?v=video");

        Assert.Equal("generic", definition.Id);
        Assert.Equal("evilyoutube.com", definition.DisplayName);
    }

    [Fact]
    public void Resolve_ReturnsHostScopedDefinitionForUnknownHttpUrl()
    {
        var definition = MediaPlatformResolver.Resolve("http://media.example:8080/path?q=1");

        Assert.Equal("generic", definition.Id);
        Assert.Equal("media.example", definition.DisplayName);
        Assert.Equal(new Uri("http://media.example:8080/"), definition.LoginUri);
        Assert.Equal(new[] { "media.example" }, definition.CookieDomains);
        Assert.True(definition.AnonymousFirst);
    }

    [Fact]
    public void Resolve_RemovesCredentialsFromUnknownLoginUri()
    {
        var definition = MediaPlatformResolver.Resolve("https://user:secret@media.example:8443/path");

        Assert.Empty(definition.LoginUri.UserInfo);
        Assert.Equal("https://media.example:8443/", definition.LoginUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a URL")]
    [InlineData("ftp://example.com/video")]
    [InlineData("//youtube.com/watch?v=video")]
    public void Resolve_ReturnsStableGenericDefinitionForInvalidOrNonHttpUrls(string url)
    {
        var definition = MediaPlatformResolver.Resolve(url);

        Assert.Equal("generic", definition.Id);
        Assert.Empty(definition.CookieDomains);
        Assert.True(definition.AnonymousFirst);
    }
}
