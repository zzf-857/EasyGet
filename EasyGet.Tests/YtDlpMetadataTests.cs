using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class YtDlpMetadataTests
{
    [Fact]
    public void MetadataParsing_StreamsProcessOutputWithoutLineArraySnapshot()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "YtDlpService.cs")));

        Assert.Contains("EnumerateProcessLines(result.StandardOutput)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("result.StandardOutput.Split('\\n', StringSplitOptions.RemoveEmptyEntries)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractPlaylistUrlFromJson_FallsBackToWebpageUrlWhenUrlFieldIsNotString()
    {
        const string json = """
            {
              "url": { "id": "abc123" },
              "webpage_url": "https://www.youtube.com/watch?v=abc123"
            }
            """;

        var url = YtDlpService.ExtractPlaylistUrlFromJson(json);

        Assert.Equal("https://www.youtube.com/watch?v=abc123", url);
    }

    [Fact]
    public void ExtractPlaylistUrlFromJson_ExpandsYoutubeVideoIdWhenFlatPlaylistOmitsWebpageUrl()
    {
        const string json = """
            {
              "url": "abc123XYZ09",
              "ie_key": "Youtube"
            }
            """;

        var url = YtDlpService.ExtractPlaylistUrlFromJson(json);

        Assert.Equal("https://www.youtube.com/watch?v=abc123XYZ09", url);
    }

    [Fact]
    public void ParsePlaylistInfoJson_ReadsRootTitleAndEntryUrls()
    {
        const string json = """
            {
              "title": "真实合集标题",
              "entries": [
                { "url": "https://www.bilibili.com/video/BV1test?p=1" },
                { "url": "abc123XYZ09", "ie_key": "Youtube" },
                { "url": "https://www.bilibili.com/video/BV1test?p=1" }
              ]
            }
            """;

        var info = YtDlpService.ParsePlaylistInfoJson(json, "https://example.test/playlist");

        Assert.Equal("真实合集标题", info.Title);
        Assert.Equal("https://example.test/playlist", info.SourceUrl);
        Assert.Equal(
            [
                "https://www.bilibili.com/video/BV1test?p=1",
                "https://www.youtube.com/watch?v=abc123XYZ09"
            ],
            info.Urls);
    }

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

    [Fact]
    public void ParseVideoInfoJson_ExposesOnlyDownloadableFormatsWithRealMetadata()
    {
        const string json = """
            {
              "title": "Formats",
              "formats": [
                {
                  "format_id": "137",
                  "ext": "mp4",
                  "vcodec": "avc1.640028",
                  "acodec": "none",
                  "width": 1920,
                  "height": 1080,
                  "fps": 30,
                  "tbr": 4500,
                  "filesize_approx": 104857600,
                  "format_note": "1080p"
                },
                {
                  "format_id": "140",
                  "ext": "m4a",
                  "vcodec": "none",
                  "acodec": "mp4a.40.2",
                  "abr": 129
                },
                {
                  "format_id": "story board",
                  "ext": "mhtml",
                  "vcodec": "none",
                  "acodec": "none"
                },
                {
                  "format_id": "137",
                  "ext": "webm",
                  "vcodec": "vp9",
                  "acodec": "none"
                }
              ]
            }
            """;

        var info = YtDlpService.ParseVideoInfoJson(json, "https://example.test/watch");

        Assert.NotNull(info);
        Assert.Collection(
            info!.AvailableFormats,
            video =>
            {
                Assert.Equal("137", video.FormatId);
                Assert.Equal("mp4", video.Extension);
                Assert.Equal(1080, video.Height);
                Assert.True(video.HasVideo);
                Assert.False(video.HasAudio);
                Assert.Equal(104857600, video.FileSize);
            },
            audio =>
            {
                Assert.Equal("140", audio.FormatId);
                Assert.False(audio.HasVideo);
                Assert.True(audio.HasAudio);
                Assert.Equal(129, audio.AudioBitrateKilobytesPerSecond);
            });
    }
}
