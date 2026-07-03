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
