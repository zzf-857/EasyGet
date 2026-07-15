using EasyGet.Services.Cookies;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EasyGet.Tests;

public sealed class BrowserCookieLoginDetectorTests
{
    private const long ChromeToUnixEpochSeconds = 11_644_473_600;

    [Fact]
    public void App_RegistersBrowserCookieLoginDetector()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath("App.xaml.cs"));

        Assert.Contains(
            "AddSingleton<IBrowserCookieLoginDetector, BrowserCookieLoginDetector>",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetectAsync_FindsChromiumAuthenticationCookieFromMetadataOnly()
    {
        using var root = new TestDirectory();
        var profilePath = root.Path("Chrome", "Default");
        var databasePath = Path.Combine(profilePath, "Network", "Cookies");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await CreateChromiumDatabaseAsync(
            databasePath,
            (".youtube.com", "SID", ChromeExpiry(DateTimeOffset.UtcNow.AddHours(1))));
        var profile = new BrowserProfile(
            "chrome",
            "Chrome",
            "Default",
            profilePath,
            DateTime.UtcNow);
        var detector = new BrowserCookieLoginDetector();
        var youtube = Platform("youtube");
        var twitter = Platform("twitter");

        var result = await detector.DetectAsync(
            [profile],
            [youtube, twitter],
            CancellationToken.None);

        Assert.True(result.TryGetProfile(youtube.StorageKey, out var matchedProfile));
        Assert.Equal(profile.StableId, matchedProfile.StableId);
        Assert.False(result.TryGetProfile(twitter.StorageKey, out _));
        Assert.Equal(1, result.ReadableProfileCount);
        Assert.Equal(0, result.UnreadableProfileCount);
    }

    [Fact]
    public async Task DetectAsync_IgnoresExpiredAndWrongDomainCookies()
    {
        using var root = new TestDirectory();
        var profilePath = root.Path("Edge", "Default");
        var databasePath = Path.Combine(profilePath, "Network", "Cookies");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await CreateChromiumDatabaseAsync(
            databasePath,
            (".youtube.com", "SID", ChromeExpiry(DateTimeOffset.UtcNow.AddMinutes(-5))),
            (".example.com", "SID", ChromeExpiry(DateTimeOffset.UtcNow.AddHours(1))));
        var detector = new BrowserCookieLoginDetector();

        var result = await detector.DetectAsync(
            [new BrowserProfile(
                "edge",
                "Edge",
                "Default",
                profilePath,
                DateTime.UtcNow)],
            [Platform("youtube")],
            CancellationToken.None);

        Assert.Empty(result.AuthenticatedProfiles);
        Assert.Equal(1, result.ReadableProfileCount);
    }

    [Fact]
    public async Task DetectAsync_FindsFirefoxAuthenticationCookie()
    {
        using var root = new TestDirectory();
        var profilePath = root.Path("Firefox", "work.default-release");
        var databasePath = Path.Combine(profilePath, "cookies.sqlite");
        Directory.CreateDirectory(profilePath);
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE moz_cookies (host TEXT, name TEXT, expiry INTEGER);
                INSERT INTO moz_cookies (host, name, expiry)
                VALUES ('.x.com', 'auth_token', $expiry);
                """;
            command.Parameters.AddWithValue(
                "$expiry",
                DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync();
        }
        var profile = new BrowserProfile(
            "firefox",
            "Firefox",
            "work.default-release",
            profilePath,
            DateTime.UtcNow);
        var detector = new BrowserCookieLoginDetector();
        var twitter = Platform("twitter");

        var result = await detector.DetectAsync(
            [profile],
            [twitter],
            CancellationToken.None);

        Assert.True(result.TryGetProfile(twitter.StorageKey, out _));
        Assert.Equal(1, result.ReadableProfileCount);
    }

    [Fact]
    public async Task DetectAsync_InvalidatesReadableCacheWhenCookieActivityChanges()
    {
        using var root = new TestDirectory();
        var profilePath = root.Path("Chrome", "Default");
        var databasePath = Path.Combine(profilePath, "Network", "Cookies");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await CreateChromiumDatabaseAsync(databasePath);
        var firstActivity = DateTime.UtcNow.AddMinutes(-1);
        var profile = new BrowserProfile(
            "chrome",
            "Chrome",
            "Default",
            profilePath,
            firstActivity);
        var detector = new BrowserCookieLoginDetector();
        var youtube = Platform("youtube");

        var beforeLogin = await detector.DetectAsync(
            [profile],
            [youtube],
            CancellationToken.None);
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO cookies (host_key, name, expires_utc)
                VALUES ('.youtube.com', 'SID', $expiry);
                """;
            insert.Parameters.AddWithValue(
                "$expiry",
                ChromeExpiry(DateTimeOffset.UtcNow.AddHours(1)));
            await insert.ExecuteNonQueryAsync();
        }

        var unchangedActivity = await detector.DetectAsync(
            [profile],
            [youtube],
            CancellationToken.None);
        var afterActivityChange = await detector.DetectAsync(
            [profile with { LastActivityUtc = firstActivity.AddMinutes(1) }],
            [youtube],
            CancellationToken.None);

        Assert.Empty(beforeLogin.AuthenticatedProfiles);
        Assert.Empty(unchangedActivity.AuthenticatedProfiles);
        Assert.True(afterActivityChange.TryGetProfile(youtube.StorageKey, out _));
    }

    [Fact]
    public async Task DetectAsync_TreatsMalformedDatabaseAsUnreadableWithoutThrowing()
    {
        using var root = new TestDirectory();
        var profilePath = root.Path("Chrome", "Default");
        var databasePath = Path.Combine(profilePath, "Network", "Cookies");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await File.WriteAllTextAsync(databasePath, "not a sqlite database");
        var detector = new BrowserCookieLoginDetector();

        var result = await detector.DetectAsync(
            [new BrowserProfile(
                "chrome",
                "Chrome",
                "Default",
                profilePath,
                DateTime.UtcNow)],
            [Platform("youtube")],
            CancellationToken.None);

        Assert.Empty(result.AuthenticatedProfiles);
        Assert.Equal(0, result.ReadableProfileCount);
        Assert.Equal(1, result.UnreadableProfileCount);
    }

    private static MediaPlatformDefinition Platform(string id)
        => MediaPlatformResolver.KnownPlatforms.Single(platform =>
            string.Equals(platform.Id, id, StringComparison.Ordinal));

    private static long ChromeExpiry(DateTimeOffset value)
        => checked((value.ToUnixTimeSeconds() + ChromeToUnixEpochSeconds) * 1_000_000);

    private static async Task CreateChromiumDatabaseAsync(
        string databasePath,
        params (string Host, string Name, long Expiry)[] cookies)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE cookies (host_key TEXT, name TEXT, expires_utc INTEGER);";
            await create.ExecuteNonQueryAsync();
        }

        foreach (var cookie in cookies)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO cookies (host_key, name, expires_utc)
                VALUES ($host, $name, $expiry);
                """;
            insert.Parameters.AddWithValue("$host", cookie.Host);
            insert.Parameters.AddWithValue("$name", cookie.Name);
            insert.Parameters.AddWithValue("$expiry", cookie.Expiry);
            await insert.ExecuteNonQueryAsync();
        }
    }
}
