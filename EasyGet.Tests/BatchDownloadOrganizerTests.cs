using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class BatchDownloadOrganizerTests
{
    [Fact]
    public void Create_CommonBilibiliParts_UsesCollectionFolderAndAvoidsCollisions()
    {
        using var root = new TestDirectory();
        var output = root.Path("downloads");
        var urls = new[]
        {
            "https://www.bilibili.com/video/BV1ddN76xEQY/?p=1",
            "https://www.bilibili.com/video/BV1ddN76xEQY/?p=2"
        };
        var now = new DateTime(2026, 7, 17, 12, 34, 56);

        var first = BatchDownloadOrganizer.Create(output, urls, now: now);
        var second = BatchDownloadOrganizer.Create(output, urls, now: now);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(32, first!.Id.Length);
        Assert.Contains("Bilibili 合集 · BV1ddN76xEQY", first.Name, StringComparison.Ordinal);
        Assert.Equal(
            "Bilibili合集_BV1ddN76xEQY_20260717_123456",
            Path.GetFileName(first.Directory));
        Assert.Equal(
            "Bilibili合集_BV1ddN76xEQY_20260717_123456_2",
            Path.GetFileName(second!.Directory));
        Assert.True(Directory.Exists(first.Directory));
        Assert.True(Directory.Exists(second.Directory));
    }

    [Fact]
    public void Create_SingleManualUrl_DoesNotCreateBatchDirectory()
    {
        using var root = new TestDirectory();
        var output = root.Path("downloads");

        var batch = BatchDownloadOrganizer.Create(
            output,
            ["https://example.com/video/1"],
            now: new DateTime(2026, 7, 17));

        Assert.Null(batch);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Create_SinglePlaylistItem_StillCreatesCollectionDirectory()
    {
        using var root = new TestDirectory();
        var output = root.Path("downloads");

        var batch = BatchDownloadOrganizer.Create(
            output,
            ["https://www.youtube.com/watch?v=abc"],
            "https://www.youtube.com/playlist?list=PL_safe-id",
            new DateTime(2026, 7, 17, 8, 0, 0));

        Assert.NotNull(batch);
        Assert.StartsWith(
            "YouTube播放列表_PL_safe-id_20260717_080000",
            Path.GetFileName(batch!.Directory),
            StringComparison.Ordinal);
    }
}
