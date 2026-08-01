using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class BatchDownloadOrganizerTests
{
    [Fact]
    public void ReuseExisting_UsesSelectedDirectoryWithoutCreatingChildFolder()
    {
        using var root = new TestDirectory();
        var selectedDirectory = root.Path("downloads", "RAG course");
        Directory.CreateDirectory(selectedDirectory);

        var batch = BatchDownloadOrganizer.ReuseExisting(
            selectedDirectory,
            "batch-rag",
            "RAG course",
            "RAG course");

        Assert.Equal("batch-rag", batch.Id);
        Assert.Equal("RAG course", batch.Name);
        Assert.Equal("RAG course", batch.CollectionTitle);
        Assert.Equal(Path.GetFullPath(selectedDirectory), batch.Directory);
        Assert.Empty(Directory.EnumerateDirectories(selectedDirectory));
    }
}
