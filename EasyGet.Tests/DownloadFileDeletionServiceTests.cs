using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class DownloadFileDeletionServiceTests
{
    [Fact]
    public void DeleteFiles_DeletesOnlyFilesInsideAllowedRoots()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "easyget-downloads"));
        var safe = Path.Combine(root, "video.mp4");
        var unsafePath = Path.GetFullPath(Path.Combine(root, "..", "private.txt"));
        var deleted = new List<string>();
        var service = new DownloadFileDeletionService(_ => true, deleted.Add);

        var result = service.DeleteFiles(
            [new DownloadHistory { FilePath = safe, AttachmentFilePaths = [unsafePath] }],
            [root]);

        Assert.Equal([safe], deleted);
        Assert.Equal(1, result.DeletedFileCount);
        Assert.Equal(1, result.SkippedUnsafePathCount);
    }

    [Theory]
    [InlineData("folder/file.mp4", true)]
    [InlineData("../outside.mp4", false)]
    public void IsWithinRoot_RejectsTraversal(string relativePath, bool expected)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "easyget-root"));
        var path = Path.GetFullPath(Path.Combine(root, relativePath));

        Assert.Equal(expected, DownloadFileDeletionService.IsWithinRoot(path, root));
    }
}
