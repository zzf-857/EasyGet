using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public sealed class DownloadDuplicateDetectorTests
{
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
