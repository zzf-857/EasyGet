using System.Collections;
using EasyGet.Services.Cookies;
using Xunit;

namespace EasyGet.Tests;

public sealed class CookieFailureClassifierTests
{
    [Theory]
    [InlineData("youtube", "Sign in to confirm you’re not a bot", CookieFailureCategory.BotChallenge, true)]
    [InlineData("youtube", "Sign in to confirm you're not a bot", CookieFailureCategory.BotChallenge, true)]
    [InlineData("youtube", "Sign in to confirm your age", CookieFailureCategory.AuthenticationRequired, true)]
    [InlineData("youtube", "HTTP Error 403: Forbidden", CookieFailureCategory.BotChallenge, true)]
    [InlineData("douyin", "Fresh cookies (not necessarily logged in) are needed", CookieFailureCategory.CookieExpired, true)]
    [InlineData("instagram", "login required", CookieFailureCategory.AuthenticationRequired, true)]
    [InlineData("generic", "Login required to view this content", CookieFailureCategory.AuthenticationRequired, true)]
    [InlineData("twitter", "This post is not available without authentication", CookieFailureCategory.AuthenticationRequired, true)]
    [InlineData("bilibili", "HTTP Error 412: Precondition Failed", CookieFailureCategory.BotChallenge, true)]
    [InlineData("generic", "Could not copy Chrome cookie database", CookieFailureCategory.CookieStoreLocked, true)]
    [InlineData("generic", "database is locked while reading cookies", CookieFailureCategory.CookieStoreLocked, true)]
    [InlineData("generic", "Failed to decrypt with DPAPI", CookieFailureCategory.CookieDecryptFailed, true)]
    [InlineData("generic", "Key not valid for use in specified state", CookieFailureCategory.CookieDecryptFailed, true)]
    [InlineData("generic", "HTTP Error 429: Too Many Requests", CookieFailureCategory.RateLimited, false)]
    [InlineData("generic", "No space left on device", CookieFailureCategory.UnrelatedFailure, false)]
    [InlineData("generic", "Requested format is not available", CookieFailureCategory.UnrelatedFailure, false)]
    [InlineData("generic", "Connection timed out", CookieFailureCategory.NetworkFailure, false)]
    [InlineData("generic", "Proxy Authentication Required", CookieFailureCategory.NetworkFailure, false)]
    [InlineData("generic", "Temporary failure in name resolution", CookieFailureCategory.NetworkFailure, false)]
    public void Classify_ReturnsExpectedCategory(
        string platformId,
        string error,
        CookieFailureCategory expectedCategory,
        bool expectedRetry)
    {
        var result = CookieFailureClassifier.Classify(platformId, [error]);

        Assert.Equal(expectedCategory, result.Category);
        Assert.Equal(expectedRetry, result.ShouldTryNextCookieSource);
    }

    [Fact]
    public void Classify_DoesNotTreatGenericForbiddenAsAuthenticationFailure()
    {
        var result = CookieFailureClassifier.Classify(
            "generic",
            ["ERROR: HTTP Error 403: Forbidden"]);

        Assert.Equal(CookieFailureCategory.UnrelatedFailure, result.Category);
        Assert.False(result.ShouldTryNextCookieSource);
    }

    [Fact]
    public void Classify_PreservesSiteAuthenticationCauseAfterBrowserCookieFailures()
    {
        var result = CookieFailureClassifier.Classify(
            "youtube",
            [
                "ERROR: Sign in to confirm your age",
                "ERROR: Could not copy Chrome cookie database",
                "ERROR: Failed to decrypt with DPAPI"
            ]);

        Assert.Equal(CookieFailureCategory.AuthenticationRequired, result.Category);
        Assert.True(result.ShouldTryNextCookieSource);
        Assert.Equal("ERROR: Failed to decrypt with DPAPI", result.LastErrorLine);
    }

    [Fact]
    public void Classify_RateLimitTakesPrecedenceAndStopsCookieRetries()
    {
        var result = CookieFailureClassifier.Classify(
            "instagram",
            [
                "ERROR: login required",
                "ERROR: HTTP Error 429: Too Many Requests"
            ]);

        Assert.Equal(CookieFailureCategory.RateLimited, result.Category);
        Assert.False(result.ShouldTryNextCookieSource);
    }

    [Fact]
    public void Classify_ReturnsNoneForEmptyOutput()
    {
        var result = CookieFailureClassifier.Classify("youtube", []);

        Assert.Equal(CookieFailureCategory.None, result.Category);
        Assert.False(result.ShouldTryNextCookieSource);
        Assert.Null(result.LastErrorLine);
    }

    [Fact]
    public void Classify_EnumeratesProcessOutputOnlyOnce()
    {
        var lines = new SingleUseEnumerable(
        [
            "WARNING: retrying",
            "ERROR: Fresh cookies are needed"
        ]);

        var result = CookieFailureClassifier.Classify("douyin", lines);

        Assert.Equal(CookieFailureCategory.CookieExpired, result.Category);
        Assert.Equal(1, lines.EnumerationCount);
    }

    private sealed class SingleUseEnumerable(IEnumerable<string> lines) : IEnumerable<string>
    {
        private readonly IEnumerable<string> _lines = lines;
        public int EnumerationCount { get; private set; }

        public IEnumerator<string> GetEnumerator()
        {
            EnumerationCount++;
            if (EnumerationCount > 1)
                throw new InvalidOperationException("Process output may only be enumerated once.");
            return _lines.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
