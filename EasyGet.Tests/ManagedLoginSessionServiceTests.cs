using EasyGet.Services.Cookies;
using Xunit;

namespace EasyGet.Tests;

public sealed class ManagedLoginSessionServiceTests
{
    [Fact]
    public async Task GetCookiesAsync_ReusesPersistedCookiesWithoutShowingLoginWindow()
    {
        using var root = new TestDirectory();
        var window = new FakeManagedLoginWindow(
            storedCookies:
            [
                new BrowserCookie(".x.com", "/", "auth_token", "value", true, 0)
            ]);
        var factory = new FakeManagedLoginWindowFactory(window);
        var service = new ManagedLoginSessionService(factory, root.DirectoryPath);

        var cookies = await service.GetCookiesAsync(
            MediaPlatformResolver.Resolve("https://x.com/user/status/1"),
            CancellationToken.None);

        Assert.Single(cookies);
        Assert.Equal(0, window.VisibleShowCount);
        Assert.Equal(root.Path("twitter"), factory.LastSessionDirectory);
        Assert.True(window.IsDisposed);
    }

    [Fact]
    public async Task GetCookiesAsync_ShowsOneRealLoginWhenStoredSessionIsEmpty()
    {
        using var root = new TestDirectory();
        var window = new FakeManagedLoginWindow(
            storedCookies: [],
            loginCookies:
            [
                new BrowserCookie(".x.com", "/", "auth_token", "value", true, 0)
            ]);
        var service = new ManagedLoginSessionService(
            new FakeManagedLoginWindowFactory(window),
            root.DirectoryPath);

        var cookies = await service.GetCookiesAsync(
            MediaPlatformResolver.Resolve("https://x.com/user/status/1"),
            CancellationToken.None);

        Assert.Single(cookies);
        Assert.Equal(1, window.VisibleShowCount);
        Assert.True(window.IsDisposed);
    }

    [Fact]
    public async Task GetCookiesAsync_IgnoresPassiveSiteCookiesAndShowsLogin()
    {
        using var root = new TestDirectory();
        var window = new FakeManagedLoginWindow(
            storedCookies:
            [
                new BrowserCookie(".google.com", "/", "NID", "preference", true, 0)
            ],
            loginCookies:
            [
                new BrowserCookie(".google.com", "/", "SAPISID", "authenticated", true, 0)
            ]);
        var service = new ManagedLoginSessionService(
            new FakeManagedLoginWindowFactory(window),
            root.DirectoryPath);

        var cookies = await service.GetCookiesAsync(
            MediaPlatformResolver.Resolve("https://www.youtube.com/watch?v=test"),
            CancellationToken.None);

        var cookie = Assert.Single(cookies);
        Assert.Equal("SAPISID", cookie.Name);
        Assert.Equal(1, window.VisibleShowCount);
    }

    [Fact]
    public async Task GetCookiesAsync_ReturnsEmptyWhenInteractiveLoginOnlyAddsPassiveCookies()
    {
        using var root = new TestDirectory();
        var window = new FakeManagedLoginWindow(
            storedCookies: [],
            loginCookies:
            [
                new BrowserCookie(".google.com", "/", "CONSENT", "accepted", true, 0)
            ]);
        var service = new ManagedLoginSessionService(
            new FakeManagedLoginWindowFactory(window),
            root.DirectoryPath);

        var cookies = await service.GetCookiesAsync(
            MediaPlatformResolver.Resolve("https://www.youtube.com/watch?v=test"),
            CancellationToken.None);

        Assert.Empty(cookies);
        Assert.Equal(1, window.VisibleShowCount);
    }

    [Fact]
    public async Task GetCookiesAsync_DropsCookiesOutsideRequestedPlatformDomains()
    {
        using var root = new TestDirectory();
        var window = new FakeManagedLoginWindow(
            storedCookies:
            [
                new BrowserCookie(".x.com", "/", "auth_token", "x", true, 0),
                new BrowserCookie(".instagram.com", "/", "sessionid", "ig", true, 0)
            ]);
        var service = new ManagedLoginSessionService(
            new FakeManagedLoginWindowFactory(window),
            root.DirectoryPath);

        var cookies = await service.GetCookiesAsync(
            MediaPlatformResolver.Resolve("https://x.com/user/status/1"),
            CancellationToken.None);

        var cookie = Assert.Single(cookies);
        Assert.Equal("auth_token", cookie.Name);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=test", ".google.com", "SAPISID")]
    [InlineData("https://www.bilibili.com/video/BV1test", ".bilibili.com", "SESSDATA")]
    [InlineData("https://www.douyin.com/video/1", ".douyin.com", "sessionid")]
    [InlineData("https://www.tiktok.com/@user/video/1", ".tiktok.com", "sessionid")]
    [InlineData("https://x.com/user/status/1", ".x.com", "auth_token")]
    [InlineData("https://www.instagram.com/p/test/", ".instagram.com", "sessionid")]
    [InlineData("https://www.facebook.com/watch/?v=1", ".facebook.com", "c_user")]
    [InlineData("https://www.kuaishou.com/short-video/1", ".kuaishou.com", "userId")]
    [InlineData("https://www.xiaohongshu.com/explore/1", ".xiaohongshu.com", "web_session")]
    [InlineData("https://weibo.com/tv/show/1", ".weibo.com", "SUB")]
    [InlineData("https://www.twitch.tv/videos/1", ".twitch.tv", "auth-token")]
    public void ManagedLoginCookieValidator_RecognizesKnownAuthenticationCookies(
        string url,
        string domain,
        string cookieName)
    {
        var platform = MediaPlatformResolver.Resolve(url);

        var authenticated = ManagedLoginCookieValidator.HasAuthenticatedSession(
            platform,
            [new BrowserCookie(domain, "/", cookieName, "value", true, 0)]);

        Assert.True(authenticated);
    }

    [Fact]
    public void ManagedLoginCookieValidator_RejectsExpiredAuthenticationCookie()
    {
        var platform = MediaPlatformResolver.Resolve(
            "https://www.youtube.com/watch?v=test");
        var expired = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();

        var authenticated = ManagedLoginCookieValidator.HasAuthenticatedSession(
            platform,
            [new BrowserCookie(".google.com", "/", "SAPISID", "value", true, expired)]);

        Assert.False(authenticated);
    }

    [Fact]
    public async Task ClearAsync_DeletesOnlyRequestedPlatformSessionDirectory()
    {
        using var root = new TestDirectory();
        Directory.CreateDirectory(root.Path("twitter"));
        Directory.CreateDirectory(root.Path("youtube"));
        await File.WriteAllTextAsync(root.Path("twitter", "state.bin"), "x");
        await File.WriteAllTextAsync(root.Path("youtube", "state.bin"), "yt");
        var service = new ManagedLoginSessionService(
            new FakeManagedLoginWindowFactory(new FakeManagedLoginWindow()),
            root.DirectoryPath);

        await service.ClearAsync("twitter", CancellationToken.None);

        Assert.False(Directory.Exists(root.Path("twitter")));
        Assert.True(Directory.Exists(root.Path("youtube")));
    }

    [Fact]
    public void ManagedLoginWindowFactory_ImplementsProductionWindowAbstraction()
    {
        IManagedLoginWindowFactory factory = new ManagedLoginWindowFactory();

        Assert.IsType<ManagedLoginWindowFactory>(factory);
    }

    [Fact]
    public void ManagedLoginWindowXaml_ExposesLoginCompletionCancellationAndDomainDisclosure()
    {
        var xaml = File.ReadAllText(TestRepositoryPaths.GetViewPath("ManagedLoginWindow.xaml"));

        Assert.Contains("WebView2", xaml, StringComparison.Ordinal);
        Assert.Contains("已完成登录，继续", xaml, StringComparison.Ordinal);
        Assert.Contains("取消", xaml, StringComparison.Ordinal);
        Assert.Contains("AllowedDomainsText", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedLoginWindow_PreparesWithoutActivatingBeforeInitializingWebView2()
    {
        var source = File.ReadAllText(
            TestRepositoryPaths.GetViewPath("ManagedLoginWindow.xaml.cs"));
        var backgroundHostIndex = source.IndexOf("Opacity = 1;", StringComparison.Ordinal);
        var showIndex = source.IndexOf("Show();", StringComparison.Ordinal);
        var initializeBrowserIndex = source.IndexOf(
            "EnsureCoreWebView2Async",
            StringComparison.Ordinal);

        Assert.True(backgroundHostIndex >= 0, "Stored sessions should be checked without activating a normal window.");
        Assert.True(showIndex >= 0, "The managed login window must be shown during initialization.");
        Assert.True(initializeBrowserIndex >= 0, "The managed login window must initialize WebView2 explicitly.");
        Assert.True(
            backgroundHostIndex < showIndex && showIndex < initializeBrowserIndex,
            "WPF WebView2 must enter the visible tree before EnsureCoreWebView2Async is awaited.");
        Assert.Contains("ShowActivated = false", source, StringComparison.Ordinal);
        Assert.Contains("ShowInTaskbar = false", source, StringComparison.Ordinal);
        Assert.Contains("正在检查已保存的登录状态", source, StringComparison.Ordinal);
        Assert.Contains(
            "ManagedLoginCookieValidator.HasAuthenticatedSession",
            source,
            StringComparison.Ordinal);
        Assert.True(
            source.Split(".WaitAsync(cancellationToken)", StringSplitOptions.None).Length - 1 >= 2,
            "Environment creation and WebView2 initialization must stop waiting when the last consumer cancels.");
        Assert.Contains("ContinueButton.IsEnabled = false", source, StringComparison.Ordinal);
        Assert.Contains("CancelButton.IsEnabled = false", source, StringComparison.Ordinal);
        Assert.Contains("if (_isPreparingSession && !_disposed)", source, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = true", source, StringComparison.Ordinal);
        Assert.Contains("ContinueButton.IsEnabled = true", source, StringComparison.Ordinal);
        Assert.Contains("CancelButton.IsEnabled = true", source, StringComparison.Ordinal);
        var cancelHandlerIndex = source.IndexOf("_cancelLogin = CancelLogin", StringComparison.Ordinal);
        var unlockIndex = source.IndexOf("_isPreparingSession = false", StringComparison.Ordinal);
        Assert.True(
            cancelHandlerIndex >= 0 && unlockIndex > cancelHandlerIndex,
            "The background window must remain non-closable until its cancellation handler is active.");
        Assert.DoesNotContain("EnsureHandle()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void App_RegistersRealManagedLoginFallback()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath("App.xaml.cs"));

        Assert.Contains("IManagedLoginWindowFactory, ManagedLoginWindowFactory", source, StringComparison.Ordinal);
        Assert.Contains("new ManagedLoginSessionService", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IManagedLoginSessionService, EmptyManagedLoginSessionService",
            source,
            StringComparison.Ordinal);
    }

    private sealed class FakeManagedLoginWindowFactory(FakeManagedLoginWindow window)
        : IManagedLoginWindowFactory
    {
        public string? LastSessionDirectory { get; private set; }

        public Task<IManagedLoginWindow> CreateAsync(
            MediaPlatformDefinition platform,
            string sessionDirectory,
            CancellationToken cancellationToken)
        {
            LastSessionDirectory = sessionDirectory;
            return Task.FromResult<IManagedLoginWindow>(window);
        }
    }

    private sealed class FakeManagedLoginWindow(
        IReadOnlyList<BrowserCookie>? storedCookies = null,
        IReadOnlyList<BrowserCookie>? loginCookies = null) : IManagedLoginWindow
    {
        public int VisibleShowCount { get; private set; }
        public bool IsDisposed { get; private set; }

        public Task<IReadOnlyList<BrowserCookie>> ReadCookiesAsync(
            IReadOnlyList<string> allowedDomains,
            CancellationToken cancellationToken)
            => Task.FromResult(storedCookies ?? (IReadOnlyList<BrowserCookie>)[]);

        public Task<IReadOnlyList<BrowserCookie>> ShowForLoginAsync(
            IReadOnlyList<string> allowedDomains,
            CancellationToken cancellationToken)
        {
            VisibleShowCount++;
            return Task.FromResult(loginCookies ?? (IReadOnlyList<BrowserCookie>)[]);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
