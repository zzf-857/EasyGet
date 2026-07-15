using EasyGet.Services.Cookies;
using Xunit;

namespace EasyGet.Tests;

public sealed class BrowserProfileDiscoveryServiceTests
{
    [Fact]
    public void Discover_FindsChromiumProfilesAndFirefoxProfiles()
    {
        using var root = new TestDirectory();
        root.Touch("Local/Google/Chrome/User Data/Default/Network/Cookies");
        root.Touch("Local/Google/Chrome/User Data/Profile 2/Network/Cookies");
        root.Touch("Roaming/Mozilla/Firefox/Profiles/work.default-release/cookies.sqlite");

        var profiles = CreateService(root).Discover();

        Assert.Collection(
            profiles.OrderBy(item => item.BrowserId).ThenBy(item => item.DisplayName),
            item => Assert.Equal(("chrome", "Default"), (item.BrowserId, item.DisplayName)),
            item => Assert.Equal(("chrome", "Profile 2"), (item.BrowserId, item.DisplayName)),
            item => Assert.Equal(("firefox", "work.default-release"), (item.BrowserId, item.DisplayName)));
        Assert.All(profiles, item => Assert.DoesNotContain("Cookies", item.ProfilePath));
        Assert.All(profiles, item => Assert.DoesNotContain("cookies.sqlite", item.ProfilePath));
    }

    [Fact]
    public void Discover_FindsEverySupportedChromiumAndOperaFamily()
    {
        using var root = new TestDirectory();
        root.Touch("Local/Google/Chrome/User Data/Default/Network/Cookies");
        root.Touch("Local/Microsoft/Edge/User Data/Default/Cookies");
        root.Touch("Local/BraveSoftware/Brave-Browser/User Data/Profile 1/Network/Cookies");
        root.Touch("Local/Chromium/User Data/Default/Network/Cookies");
        root.Touch("Local/Vivaldi/User Data/Profile 3/Network/Cookies");
        root.Touch("Roaming/Opera Software/Opera Stable/Network/Cookies");
        root.Touch("Roaming/Opera Software/Opera GX Stable/Cookies");

        var profiles = CreateService(root).Discover();

        Assert.Equal(
            ["brave", "chrome", "chromium", "edge", "opera", "opera", "vivaldi"],
            profiles.Select(item => item.BrowserId).Order(StringComparer.Ordinal));
        Assert.Contains(profiles, item => item.DisplayName == "Opera Stable");
        Assert.Contains(profiles, item => item.DisplayName == "Opera GX Stable");
    }

    [Fact]
    public void Discover_IgnoresUnsupportedProfileNamesAndProfilesWithoutCookieDatabase()
    {
        using var root = new TestDirectory();
        root.Touch("Local/Google/Chrome/User Data/Guest Profile/Network/Cookies");
        Directory.CreateDirectory(root.Path("Local", "Google", "Chrome", "User Data", "Default"));
        root.Touch("Roaming/Mozilla/Firefox/Profiles/empty.default-release/prefs.js");

        Assert.Empty(CreateService(root).Discover());
    }

    [Fact]
    public void Discover_ReturnsOneProfileWhenBothChromiumCookieLocationsExist()
    {
        using var root = new TestDirectory();
        root.Touch("Local/Google/Chrome/User Data/Default/Network/Cookies");
        root.Touch("Local/Google/Chrome/User Data/Default/Cookies");

        var profile = Assert.Single(CreateService(root).Discover());

        Assert.Equal("Default", profile.DisplayName);
    }

    [Fact]
    public void Discover_PrioritizesDefaultBrowserBeforeMoreRecentProfiles()
    {
        using var root = new TestDirectory();
        root.Touch("Local/Google/Chrome/User Data/Default/Network/Cookies");
        root.Touch("Local/Microsoft/Edge/User Data/Default/Network/Cookies");
        File.SetLastWriteTimeUtc(
            root.Path("Local", "Google", "Chrome", "User Data", "Default", "Network", "Cookies"),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(
            root.Path("Local", "Microsoft", "Edge", "User Data", "Default", "Network", "Cookies"),
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var profiles = CreateService(root, () => "ChromeHTML").Discover();

        Assert.Equal("chrome", profiles[0].BrowserId);
        Assert.True(profiles[0].IsDefaultBrowser);
        Assert.False(profiles[1].IsDefaultBrowser);
    }

    [Fact]
    public void Discover_OrdersNonDefaultProfilesByCookieActivityDescending()
    {
        using var root = new TestDirectory();
        root.Touch("Local/Google/Chrome/User Data/Default/Network/Cookies");
        root.Touch("Local/Google/Chrome/User Data/Profile 1/Network/Cookies");
        File.SetLastWriteTimeUtc(
            root.Path("Local", "Google", "Chrome", "User Data", "Default", "Network", "Cookies"),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(
            root.Path("Local", "Google", "Chrome", "User Data", "Profile 1", "Network", "Cookies"),
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var profiles = CreateService(root).Discover();

        Assert.Equal(["Profile 1", "Default"], profiles.Select(item => item.DisplayName));
    }

    [Fact]
    public void Discover_UsesWalTimestampForRecentChromiumCookieActivity()
    {
        using var root = new TestDirectory();
        root.Touch("Local/Google/Chrome/User Data/Default/Network/Cookies");
        root.Touch("Local/Google/Chrome/User Data/Default/Network/Cookies-wal");
        var databasePath = root.Path(
            "Local",
            "Google",
            "Chrome",
            "User Data",
            "Default",
            "Network",
            "Cookies");
        var walPath = $"{databasePath}-wal";
        var databaseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var walTime = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(databasePath, databaseTime);
        File.SetLastWriteTimeUtc(walPath, walTime);

        var profile = Assert.Single(CreateService(root).Discover());

        Assert.Equal(walTime, profile.LastActivityUtc);
    }

    [Fact]
    public void Discover_SurvivesDefaultBrowserLookupFailure()
    {
        using var root = new TestDirectory();
        root.Touch("Local/Google/Chrome/User Data/Default/Network/Cookies");
        var service = CreateService(root, () => throw new UnauthorizedAccessException("registry denied"));

        var profile = Assert.Single(service.Discover());

        Assert.False(profile.IsDefaultBrowser);
    }

    [Fact]
    public void BrowserProfile_ProvidesStableIdentifierAndYtDlpArgument()
    {
        var profile = new BrowserProfile(
            "chrome",
            "Chrome",
            "Default",
            @"C:\Profiles\Default",
            DateTime.UnixEpoch);

        Assert.Equal(@"chrome:C:\Profiles\Default", profile.YtDlpArgument);
        Assert.Equal(profile.StableId, profile.StableId);
        Assert.Matches("^[0-9A-F]{64}$", profile.StableId);
    }

    private static BrowserProfileDiscoveryService CreateService(
        TestDirectory root,
        Func<string?>? defaultBrowserProgId = null)
        => new(
            root.Path("Local"),
            root.Path("Roaming"),
            defaultBrowserProgId ?? (() => null));
}
