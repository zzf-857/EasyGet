using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class DouyinOutputFileResolverTests
{
    [Fact]
    public void Collect_DeduplicatesNormalizedPathsWithoutChangingFirstSeenPathsOrOrder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "easyget-output-resolver");
        var firstPath = Path.Combine(directory, "sub", "..", "video.mp4");
        var normalizedPath = Path.Combine(directory, "video.mp4");
        var commentsPath = Path.Combine(directory, "comments.json");
        var resolver = new DouyinOutputFileResolver(directory);

        var result = resolver.Collect(normalizedPath,
            [$" {firstPath} ", normalizedPath, commentsPath, Path.Combine(directory, ".", "comments.json")],
            null);

        Assert.Equal([firstPath, commentsPath], result);
    }

    [Fact]
    public void Collect_FiltersTraversalInvalidPathsAndSiblingDirectories()
    {
        var directory = Path.Combine(Path.GetTempPath(), "easyget-output-resolver");
        var safePath = Path.Combine(directory, "video.mp4");
        var siblingPath = Path.Combine(directory + "-other", "video.mp4");
        var traversalPath = Path.Combine(directory, "..", "outside.mp4");
        var resolver = new DouyinOutputFileResolver(directory);

        var result = resolver.Collect(siblingPath,
            [directory, siblingPath, traversalPath, "invalid\0path", " ", safePath],
            null);

        Assert.Equal([safePath], result);
        Assert.False(resolver.IsSafeFilePath(siblingPath));
        Assert.False(resolver.IsSafeFilePath(traversalPath));
    }

    [Fact]
    public void Collect_ConsumesLargeOutputSequenceOnceAndPreservesAllUniqueOutputs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "easyget-output-resolver");
        var enumerations = 0;
        IEnumerable<string> CreateOutputs()
        {
            Assert.Equal(1, ++enumerations);
            for (var index = 0; index < 4_000; index++)
            {
                yield return Path.Combine(directory, $"{index}.mp4");
                yield return Path.Combine(directory, ".", $"{index}.mp4");
            }
        }

        var resolver = new DouyinOutputFileResolver(directory);
        var result = resolver.Collect(null, CreateOutputs(), null);

        Assert.Equal(4_000, result.Count);
        Assert.Equal(Path.Combine(directory, "0.mp4"), result[0]);
        Assert.Equal(Path.Combine(directory, "3999.mp4"), result[^1]);
    }
}
