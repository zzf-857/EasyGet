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
