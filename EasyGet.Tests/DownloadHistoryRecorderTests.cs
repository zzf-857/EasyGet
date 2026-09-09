using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class DownloadHistoryRecorderTests
{
    [Fact]
    public async Task SaveIfCompletedAsync_PreservesMetadataAndAttachmentOrderWhileExcludingAliasesAndUnsafePaths()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var recorder = new DownloadHistoryRecorder(history);
        var primaryPath = root.Path("downloads", "video.mp4");
        var firstAttachment = root.Path("downloads", "nested", "..", "notes.pdf");
        var secondAttachment = root.Path("downloads", "slides.pdf");
        var task = new DownloadTask
        {
            Url = "https://example.test/video",
            Title = "Video title",
            Platform = "Example",
            Format = "mp4",
            Quality = "1080p",
            FileSize = 1_024,
            OutputDirectory = root.Path("downloads"),
            OutputFilePath = primaryPath,
            OutputFilePaths =
            [
                primaryPath,
                root.Path("downloads", ".", "video.mp4"),
                firstAttachment,
                root.Path("downloads", "notes.pdf"),
                root.Path("downloads-other", "outside.pdf"),
                root.Path("downloads", "..", "outside.pdf"),
                "invalid\0path",
                root.Path("downloads"),
                " ",
                secondAttachment
            ],
            BatchId = "collection-id",
            BatchName = "Collection",
            BatchDirectory = root.Path("downloads"),
            ThumbnailUrl = "https://example.test/cover.jpg",
            Status = DownloadStatus.Completed
        };

        await recorder.SaveIfCompletedAsync(task);

        var saved = Assert.Single(await history.GetAllAsync());
        Assert.Equal([firstAttachment, secondAttachment], saved.AttachmentFilePaths);
        Assert.Equal(task.Url, saved.Url);
        Assert.Equal(task.Title, saved.Title);
        Assert.Equal(task.Platform, saved.Platform);
        Assert.Equal(task.Format, saved.Format);
        Assert.Equal(task.Quality, saved.Quality);
        Assert.Equal(task.FileSize, saved.FileSize);
        Assert.Equal(primaryPath, saved.FilePath);
        Assert.Equal(task.BatchId, saved.BatchId);
        Assert.Equal(task.BatchName, saved.BatchName);
        Assert.Equal(task.BatchDirectory, saved.BatchDirectory);
        Assert.Equal(task.ThumbnailUrl, saved.ThumbnailUrl);
    }

    [Theory]
    [InlineData(DownloadStatus.Downloading)]
    [InlineData(DownloadStatus.Paused)]
    [InlineData(DownloadStatus.Failed)]
    [InlineData(DownloadStatus.Cancelled)]
    public async Task SaveIfCompletedAsync_DoesNotRecordUnfinishedOrFailedTasks(DownloadStatus status)
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var recorder = new DownloadHistoryRecorder(history);

        await recorder.SaveIfCompletedAsync(new DownloadTask { Status = status });

        Assert.Empty(await history.GetAllAsync());
    }

    [Fact]
    public async Task SaveIfCompletedAsync_RejectsAttachmentsWhenOutputDirectoryIsInvalid()
    {
        using var root = new TestDirectory();
        using var history = new HistoryService(root.Path("history.db"));
        var recorder = new DownloadHistoryRecorder(history);
        var task = new DownloadTask
        {
            Url = "https://example.test/video",
            Status = DownloadStatus.Completed,
            OutputDirectory = "invalid\0directory",
            OutputFilePaths = [root.Path("attachment.pdf")]
        };

        await recorder.SaveIfCompletedAsync(task);

        Assert.Empty(Assert.Single(await history.GetAllAsync()).AttachmentFilePaths);
    }
}
