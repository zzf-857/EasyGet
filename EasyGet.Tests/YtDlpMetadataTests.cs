using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class YtDlpMetadataTests
{
    [Fact]
    public void ParseVideoInfoJson_IgnoresNonStringMetadataFields()
    {
        const string json = """
            {
              "title": { "text": "bad title" },
              "extractor_key": 42,
              "extractor": ["YouTube"],
              "thumbnail": { "url": "https://example.test/thumb.jpg" },
              "thumbnails": [
                { "url": { "href": "https://example.test/bad.jpg" } },
                { "url": "https://example.test/fallback.jpg" }
              ],
              "duration": 12
            }
            """;

        var info = YtDlpService.ParseVideoInfoJson(json, "https://example.test/watch");

        Assert.NotNull(info);
        Assert.Equal("", info!.Title);
        Assert.Equal("", info.Platform);
        Assert.Equal("https://example.test/fallback.jpg", info.Thumbnail);
        Assert.Equal(12, info.Duration);
        Assert.Equal("https://example.test/watch", info.Url);
    }
}
