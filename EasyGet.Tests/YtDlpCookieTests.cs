using EasyGet.Services;
using System.Reflection;
using Xunit;

namespace EasyGet.Tests;

public class YtDlpCookieTests
{
    [Fact]
    public void BuildCookieFileLines_PreservesNetscapeCookieFileInput()
    {
        var input = """
            # Netscape HTTP Cookie File
            .youtube.com	TRUE	/	TRUE	1811688281	__Secure-1PSID	token-value
            .youtube.com	TRUE	/	FALSE	0	PREF	tz=UTC
            """;

        var lines = YtDlpService.BuildCookieFileLines(input);

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

        var lines = YtDlpService.BuildCookieFileLines(input);

        Assert.Contains("#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1811688281\t__Secure-3PSID\thttp-only-token", lines);
        Assert.Contains(".youtube.com\tTRUE\t/\tFALSE\t0\tPREF\ttz=UTC", lines);
        Assert.DoesNotContain(lines, line => line.Contains("A normal comment", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildCookieFileLines_StripsCookieHeaderNameAndMarksYoutubeCookiesSecure()
    {
        var lines = YtDlpService.BuildCookieFileLines("Cookie: __Secure-1PSID=token-value; PREF=tz=UTC");

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

        var lines = YtDlpService.BuildCookieFileLines(input);

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

        var lines = YtDlpService.BuildCookieFileLines(input);

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

        var lines = YtDlpService.BuildCookieFileLines(input);

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

        var lines = YtDlpService.BuildCookieFileLines(input);

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

        var lines = YtDlpService.BuildCookieFileLines(input);

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
    public void ShouldRetryWithNextCookieStrategy_RetriesDouyinAfterBrowserCookieDatabaseFailure()
    {
        var method = typeof(YtDlpService).GetMethod(
            "ShouldRetryWithNextCookieStrategy",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var shouldRetry = (bool)method!.Invoke(null, new object[]
        {
            "https://v.douyin.com/i6EpMYVJgA8/",
            new List<string> { "ERROR: Could not copy Chrome cookie database." }
        })!;

        Assert.True(shouldRetry);
    }

    [Fact]
    public void ShouldRetryWithNextCookieStrategy_RetriesYoutubeAfterAgeGate()
    {
        var method = typeof(YtDlpService).GetMethod(
            "ShouldRetryWithNextCookieStrategy",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var shouldRetry = (bool)method!.Invoke(null, new object[]
        {
            "https://www.youtube.com/watch?v=wFbtM0sfcEw",
            new List<string>
            {
                "ERROR: [youtube] wFbtM0sfcEw: Sign in to confirm your age. This video may be inappropriate for some users."
            }
        })!;

        Assert.True(shouldRetry);
    }

    [Fact]
    public void BuildCookieStrategies_AddsBrowserStrategiesForYoutubeWhenAvailable()
    {
        var strategies = YtDlpService.BuildCookieStrategies(
            "https://www.youtube.com/watch?v=wFbtM0sfcEw",
            chromeCookiesAvailable: true,
            edgeCookiesAvailable: true);

        Assert.Equal(
            [
                YtDlpService.CookieStrategy.Default,
                YtDlpService.CookieStrategy.BrowserChrome,
                YtDlpService.CookieStrategy.BrowserEdge
            ],
            strategies);
    }

    [Fact]
    public void BuildCookieStrategies_KeepsGenericSitesOnDefaultStrategy()
    {
        var strategies = YtDlpService.BuildCookieStrategies(
            "https://example.com/video",
            chromeCookiesAvailable: true,
            edgeCookiesAvailable: true);

        Assert.Equal([YtDlpService.CookieStrategy.Default], strategies);
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
        Assert.Contains("最新 Cookie", message);
    }
}
