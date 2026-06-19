using System;
using System.Collections.Generic;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class M3u8DownloadServiceTests
{
    [Theory]
    [InlineData("http://example.com/video.m3u8")]
    [InlineData("https://example.com/playlist.m3n8")]
    [InlineData("https://example.com/path/video.m3u8?auth=token123")]
    [InlineData("https://example.com/some.m3u8/stream")]
    public void IsM3u8Url_ReturnsTrueForM3u8Urls(string url)
    {
        Assert.True(M3u8DownloadService.IsM3u8Url(url));
    }

    [Theory]
    [InlineData("http://example.com/video.mp4")]
    [InlineData("https://example.com/video.mkv?format=m3u8_fallback")] // 应该排除仅含 m3u8_fallback 类似关键字但后缀不对的 mp4 
    [InlineData("")]
    [InlineData("   ")]
    public void IsM3u8Url_ReturnsFalseForOtherUrls(string url)
    {
        // 只有在路径段中真正包含 .m3u8 或 .m3n8 时才判定为 true。
        // 原判定包含 .m3u8，如果 url 是 https://example.com/video.mkv?format=m3u8_fallback 依然会被匹配为 true。
        // 但对于我们而言，只要包含这个后缀就能开始用 M3u8DownloadService 尝试下载。
        // 对于完全无关的 mp4 应该返回 false。
        Assert.False(M3u8DownloadService.IsM3u8Url(url));
    }

    [Fact]
    public void ParseSegments_CorrectlyResolvesRelativeUrls()
    {
        const string m3u8Url = "https://example.com/path/index.m3u8";
        const string m3u8Content = """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:10
            #EXTINF:10.0,
            segment0.ts
            #EXTINF:10.0,
            /absolute/segment1.ts
            #EXTINF:9.8,
            https://otherdomain.com/segment2.ts
            """;

        var segments = M3u8DownloadService.ParseSegments(m3u8Content, m3u8Url);

        Assert.Equal(3, segments.Count);
        Assert.Equal("https://example.com/path/segment0.ts", segments[0]);
        Assert.Equal("https://example.com/absolute/segment1.ts", segments[1]);
        Assert.Equal("https://otherdomain.com/segment2.ts", segments[2]);
    }

    [Fact]
    public void ParseSegments_ThrowsNotSupportedExceptionForEncryptedStreams()
    {
        const string m3u8Url = "https://example.com/path/index.m3u8";
        const string m3u8Content = """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-KEY:METHOD=AES-128,URI="key.key"
            #EXTINF:10.0,
            segment0.ts
            """;

        var ex = Assert.Throws<NotSupportedException>(() => 
            M3u8DownloadService.ParseSegments(m3u8Content, m3u8Url));

        Assert.Contains("被加密", ex.Message);
    }
}
