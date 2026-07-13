using EasyGet.Services;
using EasyGet.Services.Cookies;
using Xunit;

namespace EasyGet.Tests;

public class YtDlpCookieTests
{
    private static IReadOnlyList<string> BuildYoutubeScopedLines(string content)
        => CookieFileSerializer.BuildScopedLines(
            content,
            MediaPlatformResolver.Resolve("https://www.youtube.com/watch?v=test"),
            "www.youtube.com");

    [Fact]
    public void Source_UsesCoordinatorForMetadataPlaylistAndDownload()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "YtDlpService.cs")));

        Assert.True(
            CountOccurrences(source, "_cookieCoordinator.BuildAttemptsAsync(") >= 3,
            "metadata, playlist, and download must all build the same Cookie attempt plan");
        Assert.True(
            CountOccurrences(source, "_cookieCoordinator.AcquireArgumentsAsync(") >= 3,
            "metadata, playlist, and download must all acquire Cookie arguments through the coordinator");
        Assert.True(
            CountOccurrences(source, "acquisitionFailure.ShouldTryNextCookieSource") >= 3,
            "Cookie source access failures must fall through in all three yt-dlp flows");
        Assert.Contains("ClassifyAndRecordFailureAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HasBrowserCookies(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CookieStrategy.BrowserChrome", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CookieFilePath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[yt-dlp] args: {string.Join", source, StringComparison.Ordinal);

        var appSource = File.ReadAllText(TestRepositoryPaths.GetRootPath("App.xaml.cs"));
        Assert.Contains("AddSingleton<IBrowserProfileDiscoveryService", appSource, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<ICookieHealthStore", appSource, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<CookieAcquisitionCoordinator>", appSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--cookies", @"C:\Users\me\AppData\Local\EasyGet\temp\cookies\twitter-secret.txt")]
    [InlineData("--cookies-from-browser", @"chrome:C:\Users\me\Chrome\Profile 1")]
    public void RedactCookieArgumentValues_HidesSensitiveFileAndProfilePaths(
        string option,
        string sensitiveValue)
    {
        var line = $"ERROR: failed while reading {sensitiveValue}";

        var redacted = YtDlpService.RedactCookieArgumentValues(
            line,
            [option, sensitiveValue]);

        Assert.DoesNotContain(sensitiveValue, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[已隐藏]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCookieFileLines_PreservesNetscapeCookieFileInput()
    {
        var input = """
            # Netscape HTTP Cookie File
            .youtube.com	TRUE	/	TRUE	1811688281	__Secure-1PSID	token-value
            .youtube.com	TRUE	/	FALSE	0	PREF	tz=UTC
            """;

        var lines = BuildYoutubeScopedLines(input);

        Assert.Contains(".youtube.com\tTRUE\t/\tTRUE\t1811688281\t__Secure-1PSID\ttoken-value", lines);
        Assert.Contains(".youtube.com\tTRUE\t/\tFALSE\t0\tPREF\ttz=UTC", lines);
    }

    [Fact]
    public void BuildCookieFileLines_PreservesHttpOnlyNetscapeCookieRows()
    {
        var input = """
            # Netscape HTTP Cookie File
            #HttpOnly_.youtube.com	TRUE	/	TRUE	1811688281	__Secure-3PSID	http-only-token
            # A normal comment should stay ignored
            .youtube.com	TRUE	/	FALSE	0	PREF	tz=UTC
            """;

        var lines = BuildYoutubeScopedLines(input);

        Assert.Contains("#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1811688281\t__Secure-3PSID\thttp-only-token", lines);
        Assert.Contains(".youtube.com\tTRUE\t/\tFALSE\t0\tPREF\ttz=UTC", lines);
        Assert.DoesNotContain(lines, line => line.Contains("A normal comment", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildCookieFileLines_StripsCookieHeaderNameAndMarksYoutubeCookiesSecure()
    {
        var lines = BuildYoutubeScopedLines("Cookie: __Secure-1PSID=token-value; PREF=tz=UTC");

        Assert.Contains(".youtube.com\tTRUE\t/\tTRUE\t0\t__Secure-1PSID\ttoken-value", lines);
        Assert.Contains(".youtube.com\tTRUE\t/\tTRUE\t0\tPREF\ttz=UTC", lines);
        Assert.DoesNotContain(lines, line => line.Contains("Cookie:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildCookieFileLines_AcceptsBrowserJsonWithStringBooleanAndExpiryFields()
    {
        const string input = """
            [
              {
                "domain": ".youtube.com",
                "hostOnly": "false",
                "path": "/",
                "secure": "true",
                "expirationDate": "1811688281.75",
                "name": "__Secure-1PSID",
                "value": "token-value"
              }
            ]
            """;

        var lines = BuildYoutubeScopedLines(input);

        Assert.Contains(".youtube.com\tTRUE\t/\tTRUE\t1811688281\t__Secure-1PSID\ttoken-value", lines);
    }

    [Fact]
    public void BuildCookieFileLines_AcceptsBrowserJsonWithCookieExpiryAliases()
    {
        const string input = """
            [
              {
                "domain": ".youtube.com",
                "path": "/",
                "secure": true,
                "expires": 1811688281,
                "name": "PREF",
                "value": "tz=UTC"
              },
              {
                "domain": ".youtube.com",
                "path": "/",
                "secure": true,
                "expiry": "1811688299",
                "name": "VISITOR_INFO1_LIVE",
                "value": "visitor-token"
              }
            ]
            """;

        var lines = BuildYoutubeScopedLines(input);

        Assert.Contains(".youtube.com\tTRUE\t/\tTRUE\t1811688281\tPREF\ttz=UTC", lines);
        Assert.Contains(".youtube.com\tTRUE\t/\tTRUE\t1811688299\tVISITOR_INFO1_LIVE\tvisitor-token", lines);
    }

    [Fact]
    public void BuildCookieFileLines_AcceptsBrowserJsonWithCookieDomainAliases()
    {
        const string input = """
            [
              {
                "host": ".youtube.com",
                "path": "/",
                "secure": true,
                "name": "PREF",
                "value": "tz=UTC"
              },
              {
                "url": "https://www.youtube.com/watch?v=abc123",
                "path": "/watch",
                "secure": true,
                "name": "VISITOR_INFO1_LIVE",
                "value": "visitor-token"
              }
            ]
            """;

        var lines = BuildYoutubeScopedLines(input);

        Assert.Contains(".youtube.com\tTRUE\t/\tTRUE\t0\tPREF\ttz=UTC", lines);
        Assert.Contains("www.youtube.com\tFALSE\t/watch\tTRUE\t0\tVISITOR_INFO1_LIVE\tvisitor-token", lines);
    }

    [Fact]
    public void BuildCookieFileLines_AcceptsBrowserJsonObjectWithCookiesArray()
    {
        const string input = """
            {
              "url": "https://www.youtube.com/",
              "cookies": [
                {
                  "domain": ".youtube.com",
                  "path": "/",
                  "secure": true,
                  "name": "PREF",
                  "value": "tz=UTC"
                }
              ]
            }
            """;

        var lines = BuildYoutubeScopedLines(input);

        Assert.Contains(".youtube.com\tTRUE\t/\tTRUE\t0\tPREF\ttz=UTC", lines);
    }

    [Fact]
    public void BuildCookieFileLines_AcceptsBrowserJsonObjectWithDataArray()
    {
        const string input = """
            {
              "data": [
                {
                  "domain": ".youtube.com",
                  "path": "/",
                  "secure": true,
                  "name": "VISITOR_INFO1_LIVE",
                  "value": "visitor-token"
                }
              ]
            }
            """;

        var lines = BuildYoutubeScopedLines(input);

        Assert.Contains(".youtube.com\tTRUE\t/\tTRUE\t0\tVISITOR_INFO1_LIVE\tvisitor-token", lines);
    }

    [Fact]
    public void BuildDownloadFailureMessage_PreservesYoutubeForbiddenCauseAfterBrowserCookieFailures()
    {
        var stderrLines = new[]
        {
            "ERROR: unable to download video data: HTTP Error 403: Forbidden",
            "ERROR: Could not copy Chrome cookie database.",
            "ERROR: Failed to decrypt with DPAPI."
        };

        var message = YtDlpService.BuildDownloadFailureMessage(
            "https://www.youtube.com/watch?v=wFbtM0sfcEw",
            stderrLines,
            1);

        Assert.Contains("YouTube 下载被风控拦截", message);
    }

    [Fact]
    public void BuildDownloadFailureMessage_ExplainsBilibiliPreconditionFailure()
    {
        var stderrLines = new[]
        {
            "ERROR: [BiliBili] 1V5Eu68E5m: Unable to download JSON metadata: HTTP Error 412: Precondition Failed"
        };

        var message = YtDlpService.BuildDownloadFailureMessage(
            "https://www.bilibili.com/video/BV1V5Eu68E5m/",
            stderrLines,
            1);

        Assert.Contains("B 站", message);
        Assert.Contains("412", message);
        Assert.Contains("请求头", message);
    }

    [Fact]
    public void BuildDownloadFailureMessage_ScansStderrWithoutListSnapshot()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "YtDlpService.cs")));

        Assert.Contains("lastErrorLine", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var lines = stderrLines.ToList();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("lines.Any(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDownloadFailureMessage_ExplainsDouyinFreshCookiesAfterBrowserCookieFailures()
    {
        var stderrLines = new[]
        {
            "ERROR: [Douyin] 7621772413184822582: Fresh cookies (not necessarily logged in) are needed",
            "ERROR: Could not copy Chrome cookie database."
        };

        var message = YtDlpService.BuildDownloadFailureMessage(
            "https://v.douyin.com/i6EpMYVJgA8/",
            stderrLines,
            1);

        Assert.Contains("抖音", message);
        Assert.Contains("智能登录", message);
    }

    [Fact]
    public void BuildDouyinFallbackVideoInfo_UsesShortShareTokenAsStableTitle()
    {
        var info = YtDlpService.BuildDouyinFallbackVideoInfo("https://v.douyin.com/vi3b7QpNklg/");

        Assert.Equal("Douyin_vi3b7QpNklg", info.Title);
        Assert.Equal("Douyin", info.Platform);
        Assert.Equal("https://v.douyin.com/vi3b7QpNklg/", info.Url);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
