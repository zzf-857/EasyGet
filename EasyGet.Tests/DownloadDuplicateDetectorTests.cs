using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public sealed class DownloadDuplicateDetectorTests
{
    [Theory]
    [InlineData("tg://privatepost?channel=1234567890&post=456&thread=99", "https://t.me/c/1234567890/456")]
    [InlineData("https://telegram.me/c/1234567890/99/456?single", "https://t.me/c/1234567890/456")]
    [InlineData("tg://resolve?domain=ExampleChannel&post=456", "https://t.me/examplechannel/456")]
    public void NormalizeUrl_UsesTheSameIdentityForTelegramMessageLinks(string url, string expected)
    {
        Assert.Equal(expected, DownloadDuplicateDetector.NormalizeUrl(url));
        Assert.True(new DownloadDuplicateDetector(_ => false).Detect(url,
            [new DownloadHistory { Url = expected }]).IsDuplicate);
    }

    [Fact]
    public void FindHistoryDuplicateUrls_IndexesHistoryOnceForTheWholeBatch()
    {
        var enumerations = 0;
        IEnumerable<DownloadHistory> History()
        {
            Assert.Equal(1, ++enumerations);
            yield return new DownloadHistory { Url = "invalid legacy URL" };
            yield return new DownloadHistory { Url = "https://t.me/c/123/456" };
            for (var index = 0; index < 1000; index++)
                yield return new DownloadHistory { Url = $"https://example.test/video?id={index}" };
        }
        var urls = Enumerable.Range(0, 1500)
            .Select(index => $"https://example.test/video?utm_source=share&id={index}")
            .Append("tg://privatepost?channel=123&post=456");

        var duplicates = DownloadDuplicateDetector.FindHistoryDuplicateUrls(urls, History());

        Assert.Equal(1001, duplicates.Count);
        Assert.Contains("tg://privatepost?channel=123&post=456", duplicates);
        Assert.DoesNotContain("https://example.test/video?utm_source=share&id=1000", duplicates);
    }

    [Fact]
    public void NormalizeUrl_RemovesTrackingAndFragmentButPreservesContentIdentifiers()
    {
        var normalized = DownloadDuplicateDetector.NormalizeUrl(
            "HTTPS://www.youtube.com/watch?utm_source=newsletter&p=2&v=video-123&list=playlist-9&fbclid=track#chapter");

        Assert.DoesNotContain("utm_", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fbclid", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#", normalized, StringComparison.Ordinal);
        Assert.Contains("v=video-123", normalized, StringComparison.Ordinal);
        Assert.Contains("list=playlist-9", normalized, StringComparison.Ordinal);
        Assert.Contains("p=2", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeUrl_MapsYouTubeShortLinksWithoutLosingPlaylistIdentity()
    {
        var shortUrl = DownloadDuplicateDetector.NormalizeUrl(
            "https://youtu.be/video-123?si=tracking&list=playlist-9");
        var fullUrl = DownloadDuplicateDetector.NormalizeUrl(
            "https://www.youtube.com/watch?list=playlist-9&v=video-123");

        Assert.Equal(fullUrl, shortUrl);
    }

    [Fact]
    public void Detect_ReturnsHistoryMatchWhenFileNoLongerExists()
    {
        var history = new DownloadHistory
        {
            Id = 17,
            Url = "https://www.bilibili.com/video/BV123?p=2",
            FilePath = "missing.mp4"
        };
        var detector = new DownloadDuplicateDetector(_ => false);

        var result = detector.Detect(
            "https://www.bilibili.com/video/BV123?spm_id_from=tracking&p=2#reply",
            [history]);

        Assert.Equal(DownloadDuplicateKind.HistoryMatch, result.Kind);
        Assert.Equal(DownloadDuplicateSuggestion.ReviewHistory, result.Suggestion);
        Assert.Same(history, result.MatchedHistory);
        Assert.Null(result.ExistingPath);
    }

    [Fact]
    public void Detect_PrioritizesExistingHistoryFileAndUsesInjectedPathProbe()
    {
        using var root = new TestDirectory();
        var existingPath = Path.GetFullPath(root.Path("virtual", "video.mp4"));
        var probedPaths = new List<string>();
        var detector = new DownloadDuplicateDetector(path =>
        {
            probedPaths.Add(path);
            return string.Equals(path, existingPath, StringComparison.OrdinalIgnoreCase);
        });
        var history = new DownloadHistory
        {
            Id = 9,
            Url = "https://example.test/watch?id=content-9&utm_medium=social",
            FilePath = existingPath
        };

        var result = detector.Detect(
            "https://example.test/watch?id=content-9#comments",
            [history]);

        Assert.Equal(DownloadDuplicateKind.FileMatch, result.Kind);
        Assert.Equal(DownloadDuplicateSuggestion.OpenExistingPath, result.Suggestion);
        Assert.Equal(existingPath, result.ExistingPath, ignoreCase: true);
        Assert.Contains(existingPath, probedPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_UsesInjectedCandidatePathsEvenWithoutHistory()
    {
        using var root = new TestDirectory();
        var candidate = Path.GetFullPath(root.Path("planned", "output.mp4"));
        var detector = new DownloadDuplicateDetector(path =>
            string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase));

        var result = detector.Detect(
            "https://example.test/media?id=new-content",
            [],
            [candidate]);

        Assert.Equal(DownloadDuplicateKind.FileMatch, result.Kind);
        Assert.Equal(candidate, result.ExistingPath, ignoreCase: true);
        Assert.Null(result.MatchedHistory);
    }

    [Fact]
    public void Detect_ReturnsNoneWhenContentIdentifierDiffers()
    {
        var detector = new DownloadDuplicateDetector(_ => false);
        var result = detector.Detect(
            "https://example.test/watch?v=second",
            [new DownloadHistory { Url = "https://example.test/watch?v=first" }]);

        Assert.Equal(DownloadDuplicateKind.None, result.Kind);
        Assert.Equal(DownloadDuplicateSuggestion.ProceedWithDownload, result.Suggestion);
        Assert.False(result.IsDuplicate);
    }
}
