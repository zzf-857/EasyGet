using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class DouyinManifestReaderTests
{
    [Fact]
    public async Task ReadSummary_ParsesCountsAndDetailItemsFromJsonLines()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"easyget-manifest-reader-{Guid.NewGuid():N}");
        var manifestPath = Path.Combine(outputDir, "download_manifest.jsonl");

        try
        {
            Directory.CreateDirectory(outputDir);
            await File.WriteAllTextAsync(
                manifestPath,
                """
                {"aweme_id":"v1","media_type":"video","desc":"视频标题","author_name":"作者 A","date":"2026-07-01","recorded_at":"2026-07-03T10:00:00","file_names":["video.mp4"],"tags":["旅行","美食"]}
                {"aweme_id":"g1","media_type":"gallery","desc":"图文标题","author_name":"作者 B","file_paths":["gallery/1.jpg","gallery/2.jpg"],"tags":["摄影"]}
                {"aweme_id":"v1","media_type":"music","desc":"原声","author_name":"作者 A","file_names":["music.mp3"]}
                {"aweme_id":"u1","media_type":"","desc":"未知类型","file_names":["data.json"]}
                {malformed json
                []
                """);

            var summary = DouyinManifestReader.ReadSummary(manifestPath);

            Assert.NotNull(summary);
            Assert.Equal(4, summary!.ItemCount);
            Assert.Equal(3, summary.UniqueWorkCount);
            Assert.Equal(1, summary.VideoCount);
            Assert.Equal(1, summary.GalleryCount);
            Assert.Equal(1, summary.MusicCount);
            Assert.Equal(1, summary.UnknownCount);
            Assert.Equal(5, summary.FileCount);
            Assert.False(summary.IsTruncated);

            Assert.Collection(
                summary.Items,
                item =>
                {
                    Assert.Equal("v1", item.AwemeId);
                    Assert.Equal("视频", item.MediaTypeText);
                    Assert.Equal("视频标题", item.Description);
                    Assert.Equal("作者 A", item.AuthorName);
                    Assert.Equal("2026-07-01", item.DateText);
                    Assert.Equal("2 个标签", item.TagCountText);
                    Assert.Equal("旅行、 美食", item.TagsText);
                    Assert.Equal("1 个文件", item.FileCountText);
                    Assert.Equal("video.mp4", item.FileNamesText);
                },
                item =>
                {
                    Assert.Equal("图文", item.MediaTypeText);
                    Assert.Equal(2, item.FileCount);
                    Assert.Equal("gallery/1.jpg, gallery/2.jpg", item.FileNamesText);
                },
                item => Assert.Equal("音乐", item.MediaTypeText),
                item => Assert.Equal("未知", item.MediaTypeText));
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task ReadSummary_TruncatesAfterConfiguredLineLimit()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"easyget-manifest-reader-limit-{Guid.NewGuid():N}");
        var manifestPath = Path.Combine(outputDir, "download_manifest.jsonl");

        try
        {
            Directory.CreateDirectory(outputDir);
            await File.WriteAllTextAsync(
                manifestPath,
                """
                {"aweme_id":"v1","media_type":"video"}
                {"aweme_id":"v2","media_type":"video"}
                {"aweme_id":"v3","media_type":"video"}
                """);

            var summary = DouyinManifestReader.ReadSummary(manifestPath, maxLines: 2, maxItems: 1);

            Assert.NotNull(summary);
            Assert.True(summary!.IsTruncated);
            Assert.Equal(2, summary.ItemCount);
            var item = Assert.Single(summary.Items);
            Assert.Equal("v1", item.AwemeId);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task ReadSummary_BuildsSearchTextFromItemsBeyondDisplayedLimit()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"easyget-manifest-reader-search-{Guid.NewGuid():N}");
        var manifestPath = Path.Combine(outputDir, "download_manifest.jsonl");

        try
        {
            Directory.CreateDirectory(outputDir);
            var lines = Enumerable.Range(1, 25)
                .Select(index => index > 20
                    ? $$"""{"aweme_id":"hidden-gallery-{{index}}","media_type":"gallery","desc":"第 {{index}} 条图文","author_name":"作者 Z","file_names":["gallery_{{index}}.jpg"],"tags":["隐藏标签"]}"""
                    : $$"""{"aweme_id":"video-{{index}}","media_type":"video","desc":"展示视频 {{index}}","author_name":"作者 A","file_names":["video_{{index}}.mp4"],"tags":["视频"]}""");
            await File.WriteAllTextAsync(manifestPath, string.Join(Environment.NewLine, lines));

            var summary = DouyinManifestReader.ReadSummary(manifestPath, maxItems: 1);

            Assert.NotNull(summary);
            Assert.Equal(25, summary!.ItemCount);
            Assert.Equal(20, summary.VideoCount);
            Assert.Equal(5, summary.GalleryCount);
            var displayedItem = Assert.Single(summary.Items);
            Assert.Equal("video-1", displayedItem.AwemeId);
            Assert.Contains("hidden-gallery-25", summary.SearchText, StringComparison.Ordinal);
            Assert.Contains("作者 Z", summary.SearchText, StringComparison.Ordinal);
            Assert.Contains("隐藏标签", summary.SearchText, StringComparison.Ordinal);
            Assert.Collection(
                summary.Authors,
                author =>
                {
                    Assert.Equal("作者 A", author.AuthorName);
                    Assert.Equal(20, author.WorkCount);
                },
                author =>
                {
                    Assert.Equal("作者 Z", author.AuthorName);
                    Assert.Equal(5, author.WorkCount);
                });
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task ReadSummary_ClassifiesDouyinSidecarFileRoles()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"easyget-manifest-reader-roles-{Guid.NewGuid():N}");
        var manifestPath = Path.Combine(outputDir, "download_manifest.jsonl");

        try
        {
            Directory.CreateDirectory(outputDir);
            await File.WriteAllTextAsync(
                manifestPath,
                """
                {"aweme_id":"v1","media_type":"video","file_names":["v1.mp4","v1_cover.jpg","v1_music.mp3","v1_avatar.jpg","v1_comments.json","v1_data.json","v1_room.json","v1_live_1.mp4","v1.transcript.txt","v1.transcript.json"]}
                {"aweme_id":"g1","media_type":"gallery","file_names":["g1_1.jpg","g1_2.webp","g1_3.gif","g1_live_1.mp4"]}
                {"aweme_id":"m1","media_type":"music","file_paths":["music/m1.m4a","music\\m2.opus"]}
                {"aweme_id":"u1","media_type":"","file_names":["my_live_demo.mp4","stem_live_1.jpg","stem_comments.txt","stem_data.backup.json"]}
                {"aweme_id":"f1","media_type":"video","file_names":["f1.flv","f1_live_2.flv"]}
                """);

            var summary = DouyinManifestReader.ReadSummary(manifestPath);

            Assert.NotNull(summary);
            Assert.Collection(
                summary!.Items,
                item => Assert.Equal("视频、 封面、 音频、 头像、 评论、 数据、 直播元数据、 实况、 转写", item.FileRoleSummaryText),
                item => Assert.Equal("图片、 实况", item.FileRoleSummaryText),
                item => Assert.Equal("音乐", item.FileRoleSummaryText),
                item => Assert.Equal("评论", item.FileRoleSummaryText),
                item => Assert.Equal("视频、 实况", item.FileRoleSummaryText));
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
