using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class MediaPreviewFileResolverTests
{
    [Fact]
    public void Resolve_ReturnsEmptyStringForEmptyInput()
    {
        Assert.Equal("", MediaPreviewFileResolver.Resolve(""));
    }

    [Fact]
    public void Resolve_ReturnsExistingFilePath()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"easyget-preview-file-{Guid.NewGuid():N}.mp4");

        try
        {
            File.WriteAllText(filePath, "preview target");

            Assert.Equal(filePath, MediaPreviewFileResolver.Resolve(filePath));
        }
        finally
        {
            TryDeleteFile(filePath);
        }
    }

    [Fact]
    public void Resolve_PrefersMediaFileInsideDirectory()
    {
        var directory = CreateTempDirectory();

        try
        {
            File.WriteAllText(Path.Combine(directory, "a-note.txt"), "metadata");
            File.WriteAllText(Path.Combine(directory, "b-data.json"), "{}");
            var mediaPath = Path.Combine(directory, "c-video.mp4");
            File.WriteAllText(mediaPath, "media");

            Assert.Equal(mediaPath, MediaPreviewFileResolver.Resolve(directory));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void Resolve_PrefersNonTextFileWhenDirectoryHasNoMedia()
    {
        var directory = CreateTempDirectory();

        try
        {
            File.WriteAllText(Path.Combine(directory, "a-note.txt"), "metadata");
            var dataPath = Path.Combine(directory, "b-data.json");
            File.WriteAllText(dataPath, "{}");

            Assert.Equal(dataPath, MediaPreviewFileResolver.Resolve(directory));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void Resolve_FallsBackToFirstFileWhenOnlyTextFilesExist()
    {
        var directory = CreateTempDirectory();

        try
        {
            var firstPath = Path.Combine(directory, "a-note.txt");
            File.WriteAllText(firstPath, "metadata");
            File.WriteAllText(Path.Combine(directory, "b-note.txt"), "metadata");

            Assert.Equal(firstPath, MediaPreviewFileResolver.Resolve(directory));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void Resolve_SearchesNestedDirectories()
    {
        var directory = CreateTempDirectory();

        try
        {
            var nested = Path.Combine(directory, "nested");
            Directory.CreateDirectory(nested);
            var mediaPath = Path.Combine(nested, "video.mp4");
            File.WriteAllText(mediaPath, "media");

            Assert.Equal(mediaPath, MediaPreviewFileResolver.Resolve(directory));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void Resolve_StreamsDirectoryFilesWithoutGetFilesSnapshot()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "MediaPreviewFileResolver.cs")));

        Assert.Contains("Directory.EnumerateFiles(path, \"*\", SearchOption.AllDirectories)", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".GetFiles(\"*\", SearchOption.AllDirectories)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("files.Where", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReturnsOriginalPathWhenNothingExists()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"easyget-missing-{Guid.NewGuid():N}");

        Assert.Equal(missingPath, MediaPreviewFileResolver.Resolve(missingPath));
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easyget-preview-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
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
