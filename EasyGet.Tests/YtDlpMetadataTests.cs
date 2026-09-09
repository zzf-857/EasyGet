using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class YtDlpMetadataTests
{
    [Fact]
    public void ParsePlaylistFetchOutput_IgnoresLogsAndPrefersFirstErrorOverEarlierWarning()
    {
        var result = YtDlpMetadataParser.ParsePlaylistFetchOutput(
            "[debug] extracting\n{\"entries\":[{\"url\":\"https://example.test/video\"}]}\nfinished",
            "  WARNING: retrying\n\n ERROR: private playlist\nERROR: later error",
            1,
            "https://example.test/list");

        Assert.False(result.IsSuccess);
        Assert.Equal("ERROR: private playlist", result.ErrorMessage);
        Assert.Equal("https://example.test/video", Assert.Single(result.Info.Entries).Url);
    }

    [Fact]
    public void ParsePlaylistInfoJson_DeduplicatesResourceAliasesAndKeepsFirstSeenOrder()
    {
        const string json = """
            {
              "entries": [{
                "url": "https://example.test/video",
                "attachments": [{"url":"https://example.test/notes.pdf", "title":"Original"}],
                "resources": [
                  {"url":"https://EXAMPLE.test/NOTES.pdf", "title":"Duplicate"},
                  {"url":"https://example.test/slides.pdf", "title":"Slides"}
                ],
                "files": [{"url":"https://example.test/slides.pdf"}, {"url":"file:///unsafe.txt"}]
              }]
            }
            """;

        var resources = Assert.Single(YtDlpMetadataParser.ParsePlaylistInfoJson(
            json, "https://example.test/list").Entries).Resources;

        Assert.Equal(["Original", "Slides"], resources.Select(resource => resource.Title));
        Assert.Equal([1, 2], resources.Select(resource => resource.OriginalIndex));
    }

    [Fact]
    public void ParseVideoInfoJson_NormalizesOverflowedDurationAndFormatMetrics()
    {
        const string json = """
            {
              "duration": 1e400,
              "formats": [{"format_id":"video", "vcodec":"h264", "fps":1e400, "tbr":1e400}]
            }
            """;

        var info = YtDlpMetadataParser.ParseVideoInfoJson(json, "https://example.test/video");

        Assert.NotNull(info);
        Assert.Equal(0, info.Duration);
        var format = Assert.Single(info.AvailableFormats);
        Assert.Equal(0, format.FramesPerSecond);
        Assert.Equal(0, format.TotalBitrateKilobytesPerSecond);
    }

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

        var url = YtDlpMetadataParser.ExtractPlaylistUrlFromJson(json);

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

        var url = YtDlpMetadataParser.ExtractPlaylistUrlFromJson(json);

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

        var info = YtDlpMetadataParser.ParsePlaylistInfoJson(json, "https://example.test/playlist");

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
    public void ParsePlaylistInfoJson_PreservesBilibiliCollectionAndEntryIdentities()
    {
        const string sourceUrl = "https://space.bilibili.com/3707014188370127/lists/8920135?type=season";
        const string json = """
            {
              "_type": "playlist",
              "id": "3707014188370127_8920135",
              "title": "合集·动画讲解后端深水区，编程如此简单！",
              "extractor": "BilibiliCollectionList",
              "extractor_key": "BilibiliCollectionList",
              "entries": [
                {
                  "_type": "url",
                  "id": "BV1af846kEA7",
                  "ie_key": "BiliBili",
                  "url": "https://www.bilibili.com/video/BV1af846kEA7"
                }
              ]
            }
            """;

        var info = YtDlpMetadataParser.ParsePlaylistInfoJson(json, sourceUrl);

        Assert.Equal("3707014188370127_8920135", info.Id);
        Assert.Equal("BilibiliCollectionList", info.ExtractorKey);
        Assert.Equal(
            "extractor:bilibilicollectionlist:3707014188370127_8920135",
            info.CanonicalKey);
        var entry = Assert.Single(info.Entries);
        Assert.Equal("BV1af846kEA7", entry.Id);
        Assert.Equal("BiliBili", entry.IeKey);
        Assert.Equal("", entry.ExtractorKey);
        Assert.Equal("extractor:bilibili:BV1af846kEA7", entry.StableKey);
    }

    [Fact]
    public void ParsePlaylistInfoJson_UsesEntryExtractorKeyWhenIeKeyIsMissing()
    {
        const string json = """
            {
              "entries": [
                {
                  "id": "video-42",
                  "extractor_key": "ExampleVideo",
                  "url": "https://example.test/watch/42"
                }
              ]
            }
            """;

        var info = YtDlpMetadataParser.ParsePlaylistInfoJson(json, "https://example.test/list");

        var entry = Assert.Single(info.Entries);
        Assert.Equal("ExampleVideo", entry.ExtractorKey);
        Assert.Equal("extractor:examplevideo:video-42", entry.StableKey);
    }

    [Fact]
    public void PlaylistIdentity_FallsBackToCanonicalUrlOnlyWhenExtractorOrIdIsMissing()
    {
        var playlistFromId = new PlaylistInfo
        {
            ExtractorKey = "  YouTubePlaylist  ",
            Id = " PL123 ",
            SourceUrl = "https://EXAMPLE.test:443/list?ignored=true#fragment"
        };
        var playlistFromUrl = new PlaylistInfo
        {
            SourceUrl = "  HTTPS://EXAMPLE.test:443/list?id=PL123#chapter  "
        };
        var entryFromId = new PlaylistEntryInfo
        {
            IeKey = "  YouTube  ",
            Id = " AbC123 ",
            Url = "https://EXAMPLE.test:443/watch?v=ignored#fragment"
        };
        var entryFromUrl = new PlaylistEntryInfo
        {
            IeKey = "YouTube",
            Url = "  HTTPS://EXAMPLE.test:443/watch?v=AbC123#chapter  "
        };

        Assert.Equal("extractor:youtubeplaylist:PL123", playlistFromId.CanonicalKey);
        Assert.Equal("url:https://example.test/list?id=PL123", playlistFromUrl.CanonicalKey);
        Assert.Equal("extractor:youtube:AbC123", entryFromId.StableKey);
        Assert.Equal("url:https://example.test/watch?v=AbC123", entryFromUrl.StableKey);
    }

    [Fact]
    public void ParsePlaylistInfoJson_DeduplicatesEntriesByStableIdBeforeUrl()
    {
        const string json = """
            {
              "entries": [
                { "id": "BV1stable", "ie_key": "BiliBili", "url": "https://www.bilibili.com/video/BV1stable?from=one" },
                { "id": "BV1stable", "ie_key": "BiliBili", "url": "https://www.bilibili.com/video/BV1stable?from=two" }
              ]
            }
            """;

        var info = YtDlpMetadataParser.ParsePlaylistInfoJson(json, "https://example.test/list");

        Assert.Single(info.Entries);
        Assert.Equal("https://www.bilibili.com/video/BV1stable?from=one", info.Entries[0].Url);
    }

    [Fact]
    public void ParsePlaylistFetchOutput_ReturnsSuccessfulEmptySnapshotForValidJson()
    {
        var result = YtDlpMetadataParser.ParsePlaylistFetchOutput(
            "{\"id\":\"empty-list\",\"extractor_key\":\"Example\",\"entries\":[]}",
            "",
            0,
            "https://example.test/list");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Info);
        Assert.Empty(result.Info.Entries);
        Assert.Equal("extractor:example:empty-list", result.Info.CanonicalKey);
        Assert.Equal("", result.ErrorMessage);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void ParsePlaylistFetchOutput_ReportsFailureAndKeepsInfoNonNull()
    {
        var result = YtDlpMetadataParser.ParsePlaylistFetchOutput(
            "not json",
            "ERROR: collection is unavailable\nmore details",
            1,
            "https://example.test/list");

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Info);
        Assert.Equal("https://example.test/list", result.Info.SourceUrl);
        Assert.Equal("ERROR: collection is unavailable", result.ErrorMessage);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void ParsePlaylistFetchOutput_DoesNotApplyParsedSnapshotWhenProcessFailed()
    {
        const string json = """
            { "entries": [{ "id": "BV1partial", "ie_key": "BiliBili", "url": "https://www.bilibili.com/video/BV1partial" }] }
            """;

        var result = YtDlpMetadataParser.ParsePlaylistFetchOutput(
            json,
            "ERROR: incomplete playlist",
            1,
            "https://example.test/list");

        Assert.False(result.IsSuccess);
        Assert.Single(result.Info.Entries);
        Assert.Equal("ERROR: incomplete playlist", result.ErrorMessage);
    }

    [Fact]
    public void ParsePlaylistInfoJson_PreservesOriginalTitlesOrderSectionsAndResources()
    {
        const string json = """
            {
              "title": "课程目录",
              "entries": [
                {
                  "url": "https://example.test/lesson-1",
                  "title": "第一章：基础",
                  "playlist_index": 3,
                  "section_title": "第一章",
                  "attachments": [
                    { "url": "https://example.test/notes.pdf", "filename": "讲义.pdf", "ext": "pdf", "mime_type": "application/pdf" }
                  ]
                },
                {
                  "url": "https://example.test/lesson-2",
                  "title": "第二章：实践",
                  "playlist_index": 7,
                  "section_title": "第二章"
                }
              ]
            }
            """;

        var info = YtDlpMetadataParser.ParsePlaylistInfoJson(json, "https://example.test/course");

        Assert.Equal([3, 7], info.Entries.Select(entry => entry.OriginalIndex).ToArray());
        Assert.Equal(["第一章：基础", "第二章：实践"], info.Entries.Select(entry => entry.OriginalTitle).ToArray());
        Assert.Equal(["第一章", "第二章"], info.Entries.Select(entry => entry.SectionTitle).ToArray());
        var resource = Assert.Single(info.Entries[0].Resources);
        Assert.Equal("讲义.pdf", resource.Title);
        Assert.Equal("pdf", resource.Extension);
        Assert.Equal(info.Urls, info.Entries.Select(entry => entry.Url).ToList());
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

        var info = YtDlpMetadataParser.ParseVideoInfoJson(json, "https://example.test/watch");

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

        var info = YtDlpMetadataParser.ParseVideoInfoJson(json, "https://example.test/watch");

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
