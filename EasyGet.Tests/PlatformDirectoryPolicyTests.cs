using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class PlatformDirectoryPolicyTests
{
    [Theory]
    [InlineData("Youtube", "YouTube")]
    [InlineData("YoutubeTab", "YouTube")]
    [InlineData("YoutubePlaylist", "YouTube")]
    [InlineData("BiliBili", "哔哩哔哩")]
    [InlineData("BiliBiliBangumi", "哔哩哔哩")]
    [InlineData("Douyin", "抖音")]
    [InlineData("DouyinUser", "抖音")]
    [InlineData("TikTok", "TikTok")]
    [InlineData("TikTokUser", "TikTok")]
    [InlineData("Twitter", "Twitter(X)")]
    [InlineData("TwitterSpaces", "Twitter(X)")]
    [InlineData("Space", "Twitter(X)")]
    [InlineData("X", "Twitter(X)")]
    [InlineData("Instagram", "Instagram")]
    [InlineData("Facebook", "Facebook")]
    [InlineData("Weibo", "微博")]
    [InlineData("XiaoHongShu", "小红书")]
    [InlineData("Kuaishou", "快手")]
    [InlineData("Iqiyi", "爱奇艺")]
    [InlineData("Youku", "优酷")]
    [InlineData("TencentVideo", "腾讯视频")]
    [InlineData("TwitchVod", "Twitch")]
    [InlineData("NicoNicoUser", "NicoNico")]
    [InlineData("Vimeo", "Vimeo")]
    [InlineData("Yangshipin", "央视频")]
    [InlineData("Telegram", "Telegram")]
    [InlineData("M3U8", "M3U8")]
    public void TryResolveCanonicalFolder_MapsKnownExtractorVariants(
        string platform,
        string expectedFolder)
    {
        var resolved = PlatformDirectoryPolicy.TryResolveCanonicalFolder(
            platform,
            url: null,
            out var folder);

        Assert.True(resolved);
        Assert.Equal(expectedFolder, folder);
    }

    [Theory]
    [InlineData("DouyinUser", "https://www.tiktok.com/@creator/video/123", "TikTok")]
    [InlineData("TikTokUser", "https://www.douyin.com/video/123", "抖音")]
    [InlineData("TikTok", "https://www.iesdouyin.com/share/video/123", "抖音")]
    public void TryResolveCanonicalFolder_TrustedUrlWinsOverExtractor(
        string platform,
        string url,
        string expectedFolder)
    {
        var resolved = PlatformDirectoryPolicy.TryResolveCanonicalFolder(
            platform,
            url,
            out var folder);

        Assert.True(resolved);
        Assert.Equal(expectedFolder, folder);
    }

    [Fact]
    public void TryResolveCanonicalFolder_UsesCompleteTrustedHostBoundary()
    {
        var resolved = PlatformDirectoryPolicy.TryResolveCanonicalFolder(
            "TikTokUser",
            "https://douyin.com.example.test/video/123",
            out var folder);

        Assert.True(resolved);
        Assert.Equal("TikTok", folder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Generic")]
    [InlineData("UnknownExtractor")]
    public void TryResolveCanonicalFolder_DoesNotCategorizeUnknownPlatform(string? platform)
    {
        var resolved = PlatformDirectoryPolicy.TryResolveCanonicalFolder(
            platform,
            "https://media.example.test/video",
            out var folder);

        Assert.False(resolved);
        Assert.Empty(folder);
    }

    [Theory]
    [InlineData("抖音", "抖音")]
    [InlineData("抖音视频下载", "抖音")]
    [InlineData("YouTube", "YouTube")]
    [InlineData("Youtube视频下载", "YouTube")]
    [InlineData("X", "Twitter(X)")]
    [InlineData("Twitter(X)", "Twitter(X)")]
    public void TryCanonicalizeDirectoryName_MapsOnlyCanonicalAndLegacyNames(
        string directoryName,
        string expectedFolder)
    {
        var resolved = PlatformDirectoryPolicy.TryCanonicalizeDirectoryName(
            directoryName,
            out var folder);

        Assert.True(resolved);
        Assert.Equal(expectedFolder, folder);
        Assert.Equal(-1, folder.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    [Theory]
    [InlineData("抖音下载")]
    [InlineData("YouTube视频")]
    [InlineData("b站视频素材")]
    [InlineData("Twitter")]
    [InlineData(" 抖音 ")]
    public void TryCanonicalizeDirectoryName_RejectsUnregisteredAliases(string directoryName)
    {
        var resolved = PlatformDirectoryPolicy.TryCanonicalizeDirectoryName(
            directoryName,
            out var folder);

        Assert.False(resolved);
        Assert.Empty(folder);
    }

    [Fact]
    public void ResolveCategorizedDirectory_ReplacesLegacyLeafAndIsIdempotent()
    {
        var root = Path.Combine("D:\\", "Videos");
        var legacyDirectory = Path.Combine(root, "Youtube视频下载");

        var firstResolution = PlatformDirectoryPolicy.ResolveCategorizedDirectory(
            legacyDirectory,
            "YouTube");
        var secondResolution = PlatformDirectoryPolicy.ResolveCategorizedDirectory(
            firstResolution,
            "YouTube");

        var expected = Path.Combine(root, "YouTube");
        Assert.Equal(expected, firstResolution);
        Assert.Equal(expected, secondResolution);
    }

    [Fact]
    public void ResolveCategorizedDirectory_ReplacesOtherCanonicalPlatformLeaf()
    {
        var root = Path.Combine("D:\\", "Videos");
        var selectedPlatformDirectory = Path.Combine(root, "TikTok");

        var resolved = PlatformDirectoryPolicy.ResolveCategorizedDirectory(
            selectedPlatformDirectory,
            "抖音");

        Assert.Equal(Path.Combine(root, "抖音"), resolved);
    }

    [Fact]
    public void ResolveCategorizedDirectory_AppendsToOrdinaryBaseDirectory()
    {
        var root = Path.Combine("D:\\", "Videos");

        var resolved = PlatformDirectoryPolicy.ResolveCategorizedDirectory(root, "抖音");

        Assert.Equal(Path.Combine(root, "抖音"), resolved);
    }

    [Fact]
    public void ResolveCategorizedDirectory_RejectsUnknownDirectoryName()
    {
        Assert.Throws<ArgumentException>(() =>
            PlatformDirectoryPolicy.ResolveCategorizedDirectory(
                Path.Combine("D:\\", "Videos"),
                "UnknownPlatform"));
    }
}
