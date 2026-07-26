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
    public void CookieImplementation_DoesNotLogSecretsOrUsePermanentGlobalCookieFile()
    {
        var root = TestRepositoryPaths.Root;
        var sourceDirectories = new[] { "Services", "Models", "ViewModels", "Converters" };
        var sourceFiles = sourceDirectories
            .SelectMany(directory => Directory.GetFiles(
                Path.Combine(root, directory),
                "*.cs",
                SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(root, "*.cs", SearchOption.TopDirectoryOnly));
        var source = string.Join(
            Environment.NewLine,
            sourceFiles.Select(File.ReadAllText));
        var forbiddenArgumentLog = "[yt-dlp] args: {" + "string.Join";
        var forbiddenGlobalFile = "cookies" + ".txt";

        Assert.DoesNotContain(forbiddenArgumentLog, source, StringComparison.Ordinal);
        Assert.DoesNotContain(forbiddenGlobalFile, source, StringComparison.OrdinalIgnoreCase);
    }

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
    public void RedactCookieArgumentValues_HidesBrowserPathWithoutArgumentPrefix()
    {
        const string profilePath = @"C:\Users\me\Chrome\Profile 1";
        var line = $"ERROR: Could not copy Chrome cookie database from {profilePath}";

        var redacted = YtDlpService.RedactCookieArgumentValues(
            line,
            ["--cookies-from-browser", $"chrome:{profilePath}"]);

        Assert.DoesNotContain(profilePath, redacted, StringComparison.OrdinalIgnoreCase);
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
    public void BuildDownloadFailureMessage_RedactsLocalPathsAndCredentialAssignments()
    {
        const string error =
            @"ERROR: failed reading C:\Users\me\Secret Profile\Cookies; SID=secret-value";

        var message = YtDlpService.BuildDownloadFailureMessage(
            "https://media.example.org/watch/1",
            [error],
            1);

        Assert.DoesNotContain(@"C:\Users\me", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-value", message, StringComparison.Ordinal);
        Assert.Contains("[已隐藏]", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDownloadFailureMessage_RedactsCompleteCredentialHeaders()
    {
        const string cookieSecret = "second-cookie-secret";
        const string bearerSecret = "bearer-secret-value";
        const string error =
            $"ERROR: Cookie: SID=first-cookie-secret; SAPISID={cookieSecret}\n" +
            $"Authorization: Bearer {bearerSecret}";

        var message = YtDlpService.BuildDownloadFailureMessage(
            "https://media.example.org/watch/1",
            [error],
            1);

        Assert.DoesNotContain(cookieSecret, message, StringComparison.Ordinal);
        Assert.DoesNotContain(bearerSecret, message, StringComparison.Ordinal);
        Assert.Contains("[已隐藏]", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadPipeline_StoresOnlyRedactedStderrForTaskErrors()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "YtDlpService.cs")));

        Assert.Contains(
            "stderrLines.Add(RedactCookieArgumentValues(",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("stderrLines.Add(line);", source, StringComparison.Ordinal);
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
        Assert.Contains("刚刷新", message);
        Assert.Contains("不一定需要登录", message);
    }

    [Fact]
    public void BuildDouyinFallbackVideoInfo_UsesShortShareTokenAsDeterministicTitle()
    {
        var info = YtDlpService.BuildDouyinFallbackVideoInfo("https://v.douyin.com/vi3b7QpNklg/");

        Assert.Equal("Douyin_vi3b7QpNklg", info.Title);
        Assert.Equal("Douyin", info.Platform);
        Assert.Equal("https://v.douyin.com/vi3b7QpNklg/", info.Url);
    }

    [Theory]
    [InlineData("https://www.douyin.com/video/7621772413184822582", "Douyin_7621772413184822582")]
    [InlineData("https://www.douyin.com/note/7383344556677889900", "Douyin_7383344556677889900")]
    [InlineData("https://www.douyin.com/gallery/7383344556677889901", "Douyin_7383344556677889901")]
    [InlineData("https://www.iesdouyin.com/share/video/7621772413184822583", "Douyin_7621772413184822583")]
    [InlineData("https://www.douyin.com/?modal_id=7621772413184822584", "Douyin_7621772413184822584")]
    public void BuildDouyinFallbackVideoInfo_UsesParserContentId(
        string url,
        string expectedTitle)
    {
        var info = YtDlpService.BuildPlatformFallbackVideoInfo(url);

        Assert.NotNull(info);
        Assert.Equal(expectedTitle, info.Title);
        Assert.Equal("Douyin", info.Platform);
    }

    [Theory]
    [InlineData("https://www.tiktok.com/@creator/video/7524567890123456789", "TikTok_7524567890123456789")]
    [InlineData("https://www.tiktok.com/embed/7524567890123456790", "TikTok_7524567890123456790")]
    [InlineData("https://vm.tiktok.com/ZMExample1/", "TikTok_ZMExample1")]
    [InlineData("https://vt.tiktok.com/ZSMExample2/", "TikTok_ZSMExample2")]
    [InlineData("https://www.tiktok.com/t/ZTExample3/", "TikTok_ZTExample3")]
    public void BuildTikTokFallbackVideoInfo_UsesCanonicalIdOrShortToken(
        string url,
        string expectedTitle)
    {
        var info = YtDlpService.BuildPlatformFallbackVideoInfo(url);

        Assert.NotNull(info);
        Assert.Equal(expectedTitle, info.Title);
        Assert.Equal("TikTok", info.Platform);
        Assert.Equal(url, info.Url);
    }

    [Fact]
    public void BuildPlatformFallbackVideoInfo_RejectsLookalikeTikTokHost()
    {
        var info = YtDlpService.BuildPlatformFallbackVideoInfo(
            "https://eviltiktok.com/@creator/video/7524567890123456789");

        Assert.Null(info);
    }

    [Fact]
    public void BuildDownloadFailureMessage_ExplainsTikTokLoginAfterBrowserCookieFailures()
    {
        var stderrLines = new[]
        {
            "ERROR: [TikTok] 7524567890123456789: Log in for access",
            "ERROR: Could not copy Chrome cookie database."
        };

        var message = YtDlpService.BuildDownloadFailureMessage(
            "https://www.tiktok.com/@creator/video/7524567890123456789",
            stderrLines,
            1);

        Assert.Contains("TikTok", message);
        Assert.Contains("智能登录", message);
        Assert.Contains("重新登录", message);
    }

    [Fact]
    public void BuildDownloadFailureMessage_ExplainsTikTokIpBlockWithoutSuggestingLogin()
    {
        var message = YtDlpService.BuildDownloadFailureMessage(
            "https://vt.tiktok.com/ZSMExample2/",
            ["ERROR: [TikTok] Your IP address is blocked from accessing this post"],
            1);

        Assert.Contains("IP", message);
        Assert.Contains("代理", message);
        Assert.DoesNotContain("登录", message);
    }

    [Theory]
    [InlineData("ERROR: Connection timed out")]
    [InlineData("ERROR: Unsupported URL")]
    public void BuildDownloadFailureMessageForAttempts_PrefersDefinitiveTerminalFailure(
        string terminalError)
    {
        var message = YtDlpService.BuildDownloadFailureMessageForAttempts(
            "https://www.douyin.com/video/7621772413184822582",
            [
                "ERROR: Fresh cookies (not necessarily logged in) are needed",
                terminalError
            ],
            [terminalError],
            1);

        Assert.Contains(terminalError, message, StringComparison.Ordinal);
        Assert.DoesNotContain("刚刷新", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDownloadFailureMessageForAttempts_PreservesAuthenticationCauseForCookieReadFailure()
    {
        var message = YtDlpService.BuildDownloadFailureMessageForAttempts(
            "https://www.youtube.com/watch?v=wFbtM0sfcEw",
            [
                "ERROR: HTTP Error 403: Forbidden",
                "ERROR: Failed to decrypt with DPAPI"
            ],
            ["ERROR: Failed to decrypt with DPAPI"],
            1);

        Assert.Contains("YouTube 下载被风控拦截", message, StringComparison.Ordinal);
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
