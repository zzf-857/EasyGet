using EasyGet.Models;
using EasyGet.Services.Cookies;
using EasyGet.ViewModels;
using Xunit;

namespace EasyGet.Tests;

public sealed class CookiePlatformStatusPresenterTests
{
    private static readonly DateTime Now = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Apply_PrefersMostRecentAuthenticatedSuccessEvenWhenAnonymousIsNewer()
    {
        var presenter = new CookiePlatformStatusPresenter(
            [
                Record("youtube", CookieSourceKind.Browser, Now.AddDays(-2)),
                Record("youtube", CookieSourceKind.Anonymous, Now),
                Record("youtube", CookieSourceKind.ManagedSession, Now.AddDays(-1)),
                Record("twitter", CookieSourceKind.LegacyScoped, Now.AddDays(1))
            ], BrowserCookieLoginDetection.Empty, profileCount: 0);
        var item = CreateItem("youtube");

        Assert.True(presenter.Apply(item));

        Assert.True(item.IsAvailable);
        Assert.True(item.HasAuthenticatedSession);
        Assert.False(item.NeedsLogin);
        Assert.Equal("最近验证可用 · EasyGet 托管登录", item.StatusText);
    }

    [Fact]
    public void Apply_RejectsFailedOrUnverifiedHealthRecords()
    {
        var presenter = new CookiePlatformStatusPresenter(
            [
                Record("youtube", CookieSourceKind.Anonymous, Now.AddDays(-1)),
                Record(null!, CookieSourceKind.Browser, Now),
                Record("youtube", CookieSourceKind.Browser, null),
                Record("youtube", CookieSourceKind.ManagedSession, Now) with { ConsecutiveFailures = 1 },
                Record("youtube", CookieSourceKind.LegacyScoped, Now) with { LastFailureUtc = Now.AddSeconds(1) }
            ], BrowserCookieLoginDetection.Empty, profileCount: 0);
        var item = CreateItem("youtube");

        Assert.True(presenter.Apply(item));

        Assert.True(item.IsAvailable);
        Assert.False(item.HasAuthenticatedSession);
        Assert.Equal("最近验证可用 · 公开访问", item.StatusText);
        var otherPlatform = CreateItem("twitter");
        Assert.False(presenter.Apply(otherPlatform));
        Assert.False(otherPlatform.IsAvailable);
        Assert.True(otherPlatform.NeedsLogin);
    }

    [Fact]
    public void Apply_AcceptsRecoveryAtTheFailureTimestampAndPreservesFirstSourceOnTies()
    {
        var presenter = new CookiePlatformStatusPresenter(
            [
                Record("youtube", CookieSourceKind.Browser, Now) with { LastFailureUtc = Now },
                Record("youtube", CookieSourceKind.ManagedSession, Now)
            ], BrowserCookieLoginDetection.Empty, profileCount: 0);
        var item = CreateItem("youtube");

        Assert.True(presenter.Apply(item));
        Assert.True(item.HasAuthenticatedSession);
        Assert.Equal("最近验证可用 · 本机浏览器", item.StatusText);
    }

    [Fact]
    public void Apply_CountsVerifiedOperatingPlatformWithoutReplacingItsVisibleState()
    {
        var presenter = new CookiePlatformStatusPresenter(
            [Record("youtube", CookieSourceKind.Browser, Now)],
            BrowserCookieLoginDetection.Empty, profileCount: 0);
        var item = CreateItem("youtube");
        item.IsOperating = true;
        item.StatusText = "正在登录";
        item.NeedsLogin = true;

        Assert.True(presenter.Apply(item));

        Assert.Equal("正在登录", item.StatusText);
        Assert.False(item.IsAvailable);
        Assert.False(item.HasAuthenticatedSession);
        Assert.True(item.NeedsLogin);
    }

    private static CookieHealthRecord Record(string platform, CookieSourceKind source, DateTime? success)
        => new(platform, source, "profile", success, null, 0, CookieFailureCategory.None);

    private static CookiePlatformStatusItem CreateItem(string platform)
        => new() { PlatformId = platform, StorageKey = platform, DisplayName = platform };
}
