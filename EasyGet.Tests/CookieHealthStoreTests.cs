using EasyGet.Services.Cookies;
using Xunit;

namespace EasyGet.Tests;

public sealed class CookieHealthStoreTests
{
    [Fact]
    public async Task RecordSuccessAsync_PersistsStableIdWithoutProfilePathOrCookieValue()
    {
        using var root = new TestDirectory();
        var store = new CookieHealthStore(root.DirectoryPath);
        var profile = new BrowserProfile(
            "chrome",
            "Chrome",
            "Profile 1",
            @"C:\Users\me\Profile 1",
            DateTime.UtcNow);

        await store.RecordSuccessAsync(
            "youtube",
            CookieSourceKind.Browser,
            profile,
            CancellationToken.None);

        var json = await File.ReadAllTextAsync(root.Path("cookie-health.json"));
        Assert.Contains(profile.StableId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(profile.ProfilePath, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordFailureAsync_IncrementsConsecutiveFailuresAndReloadsMetadata()
    {
        using var root = new TestDirectory();
        var profile = new BrowserProfile(
            "firefox",
            "Firefox",
            "default-release",
            @"C:\Profiles\Firefox",
            DateTime.UtcNow);
        var store = new CookieHealthStore(root.DirectoryPath);

        await store.RecordFailureAsync(
            "twitter",
            CookieSourceKind.Browser,
            profile,
            CookieFailureCategory.CookieExpired,
            CancellationToken.None);
        await store.RecordFailureAsync(
            "twitter",
            CookieSourceKind.Browser,
            profile,
            CookieFailureCategory.BotChallenge,
            CancellationToken.None);

        var reloaded = new CookieHealthStore(root.DirectoryPath);
        var record = Assert.Single(reloaded.Snapshot());
        Assert.Equal("twitter", record.PlatformId);
        Assert.Equal(profile.StableId, record.SourceId);
        Assert.Equal(2, record.ConsecutiveFailures);
        Assert.Equal(CookieFailureCategory.BotChallenge, record.LastFailureCategory);
        Assert.NotNull(record.LastFailureUtc);
        Assert.Null(record.LastSuccessUtc);
    }

    [Fact]
    public async Task ClearPlatformAsync_RemovesOnlyRequestedPlatformRecords()
    {
        using var root = new TestDirectory();
        var store = new CookieHealthStore(root.DirectoryPath);
        await store.RecordSuccessAsync(
            "youtube",
            CookieSourceKind.Anonymous,
            profile: null,
            CancellationToken.None);
        await store.RecordSuccessAsync(
            "twitter",
            CookieSourceKind.ManagedSession,
            profile: null,
            CancellationToken.None);

        await store.ClearPlatformAsync("youtube", CancellationToken.None);

        var record = Assert.Single(new CookieHealthStore(root.DirectoryPath).Snapshot());
        Assert.Equal("twitter", record.PlatformId);
        Assert.DoesNotContain(
            store.Snapshot(),
            item => item.PlatformId == "youtube");
    }

    [Fact]
    public void CookieHealthStore_ImplementsCoordinatorFacingInterface()
    {
        using var root = new TestDirectory();

        ICookieHealthStore store = new CookieHealthStore(root.DirectoryPath);

        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public async Task RecordFailureAsync_ClampsTamperedFailureCountInsteadOfOverflowing()
    {
        using var root = new TestDirectory();
        await root.WriteAsync(
            "cookie-health.json",
            $$"""
            [{
              "platformId": "youtube",
              "source": "anonymous",
              "sourceId": "Anonymous",
              "lastSuccessUtc": null,
              "lastFailureUtc": "2026-07-14T00:00:00Z",
              "consecutiveFailures": {{int.MaxValue}},
              "lastFailureCategory": "networkFailure"
            }]
            """);
        var store = new CookieHealthStore(root.DirectoryPath);

        await store.RecordFailureAsync(
            "youtube",
            CookieSourceKind.Anonymous,
            profile: null,
            CookieFailureCategory.NetworkFailure,
            CancellationToken.None);

        Assert.Equal(int.MaxValue, Assert.Single(store.Snapshot()).ConsecutiveFailures);
    }
}
