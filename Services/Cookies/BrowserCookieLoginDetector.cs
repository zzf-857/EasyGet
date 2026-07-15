using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;

namespace EasyGet.Services.Cookies;

public sealed record BrowserCookieLoginDetection(
    IReadOnlyDictionary<string, BrowserProfile> AuthenticatedProfiles,
    int ReadableProfileCount,
    int UnreadableProfileCount)
{
    public static BrowserCookieLoginDetection Empty { get; } =
        new(new Dictionary<string, BrowserProfile>(StringComparer.Ordinal), 0, 0);

    public bool TryGetProfile(string platformStorageKey, out BrowserProfile profile)
        => AuthenticatedProfiles.TryGetValue(platformStorageKey, out profile!);
}

public interface IBrowserCookieLoginDetector
{
    Task<BrowserCookieLoginDetection> DetectAsync(
        IReadOnlyList<BrowserProfile> profiles,
        IReadOnlyList<MediaPlatformDefinition> platforms,
        CancellationToken cancellationToken);
}

/// <summary>
/// Detects likely authenticated browser sessions without reading Cookie values.
/// Chromium and Firefox keep the Cookie host, name and expiry as SQLite metadata,
/// even when the value itself is encrypted. Only those metadata columns are queried.
/// </summary>
public sealed class BrowserCookieLoginDetector : IBrowserCookieLoginDetector
{
    private const long ChromeToUnixEpochSeconds = 11_644_473_600;
    private static readonly TimeSpan UnreadableCacheDuration = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<ProfileCacheKey, CachedProfileReadResult> _cache = new();

    public Task<BrowserCookieLoginDetection> DetectAsync(
        IReadOnlyList<BrowserProfile> profiles,
        IReadOnlyList<MediaPlatformDefinition> platforms,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(platforms);
        cancellationToken.ThrowIfCancellationRequested();

        if (profiles.Count == 0 || platforms.Count == 0)
            return Task.FromResult(BrowserCookieLoginDetection.Empty);

        var profileSnapshot = profiles.ToArray();
        var platformSnapshot = platforms.ToArray();
        return Task.Run(
            () => DetectCoreAsync(
                profileSnapshot,
                platformSnapshot,
                cancellationToken),
            cancellationToken);
    }

    private async Task<BrowserCookieLoginDetection> DetectCoreAsync(
        IReadOnlyList<BrowserProfile> profiles,
        IReadOnlyList<MediaPlatformDefinition> platforms,
        CancellationToken cancellationToken)
    {
        var platformDefinitions = platforms
            .Where(platform => platform.CookieDomains.Count > 0)
            .GroupBy(platform => platform.StorageKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var remainingPlatformKeys = platformDefinitions
            .Select(platform => platform.StorageKey)
            .ToHashSet(StringComparer.Ordinal);
        var authenticatedProfiles = new Dictionary<string, BrowserProfile>(StringComparer.Ordinal);
        var readableProfileCount = 0;
        var unreadableProfileCount = 0;

        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remainingPlatformKeys.Count == 0)
                break;

            var readResult = await ReadProfileCookieMetadataAsync(
                profile,
                platformDefinitions,
                cancellationToken);
            foreach (var storageKey in readResult.AuthenticatedPlatformStorageKeys)
            {
                if (!remainingPlatformKeys.Remove(storageKey))
                    continue;

                authenticatedProfiles[storageKey] = profile;
            }

            if (readResult.WasReadable)
                readableProfileCount++;
            else
                unreadableProfileCount++;
        }

        return new BrowserCookieLoginDetection(
            authenticatedProfiles,
            readableProfileCount,
            unreadableProfileCount);
    }

    private async Task<CookieDatabaseReadResult> ReadProfileCookieMetadataAsync(
        BrowserProfile profile,
        IReadOnlyList<MediaPlatformDefinition> platforms,
        CancellationToken cancellationToken)
    {
        var platformSignature = string.Join(
            "|",
            platforms
                .Select(platform => platform.StorageKey)
                .OrderBy(storageKey => storageKey, StringComparer.Ordinal));
        var cacheKey = new ProfileCacheKey(profile.StableId, platformSignature);
        if (_cache.TryGetValue(cacheKey, out var cached)
            && cached.LastActivityUtc == profile.LastActivityUtc
            && (cached.Result.WasReadable
                || DateTime.UtcNow - cached.CapturedUtc < UnreadableCacheDuration))
        {
            return cached.Result;
        }

        var profileWasReadable = false;
        var authenticated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var database in EnumerateCookieDatabases(profile))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var readResult = await ReadCookieMetadataAsync(
                database,
                platforms,
                cancellationToken);
            profileWasReadable |= readResult.WasReadable;
            authenticated.UnionWith(readResult.AuthenticatedPlatformStorageKeys);
            if (authenticated.Count == platforms.Count)
                break;
        }

        var result = new CookieDatabaseReadResult(profileWasReadable, authenticated);
        _cache[cacheKey] = new CachedProfileReadResult(
            profile.LastActivityUtc,
            DateTime.UtcNow,
            result);
        return result;
    }

    private static IEnumerable<CookieDatabase> EnumerateCookieDatabases(BrowserProfile profile)
    {
        if (string.Equals(profile.BrowserId, "firefox", StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.Combine(profile.ProfilePath, "cookies.sqlite");
            if (File.Exists(path))
                yield return new CookieDatabase(path, CookieDatabaseKind.Firefox);
            yield break;
        }

        foreach (var path in new[]
                 {
                     Path.Combine(profile.ProfilePath, "Network", "Cookies"),
                     Path.Combine(profile.ProfilePath, "Cookies")
                 })
        {
            if (File.Exists(path))
                yield return new CookieDatabase(path, CookieDatabaseKind.Chromium);
        }
    }

    private static async Task<CookieDatabaseReadResult> ReadCookieMetadataAsync(
        CookieDatabase database,
        IReadOnlyList<MediaPlatformDefinition> platforms,
        CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = database.Path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 0
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            var selectClause = database.Kind == CookieDatabaseKind.Firefox
                ? "SELECT host, name, expiry FROM moz_cookies"
                : "SELECT host_key, name, expires_utc FROM cookies";
            var knownNames = platforms
                .Select(ManagedLoginCookieValidator.GetKnownAuthenticationCookieNames)
                .ToArray();
            if (knownNames.All(names => names.Count > 0))
            {
                var candidates = knownNames
                    .SelectMany(names => names)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var parameterNames = new string[candidates.Length];
                for (var index = 0; index < candidates.Length; index++)
                {
                    parameterNames[index] = $"$name{index}";
                    command.Parameters.AddWithValue(parameterNames[index], candidates[index]);
                }

                command.CommandText =
                    $"{selectClause} WHERE name COLLATE NOCASE IN ({string.Join(", ", parameterNames)})";
            }
            else
            {
                command.CommandText = $"{selectClause} WHERE name <> ''";
            }
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var authenticated = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                var domain = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var rawExpiry = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                var expiresUnix = database.Kind == CookieDatabaseKind.Firefox
                    ? rawExpiry
                    : ConvertChromeExpiryToUnix(rawExpiry);

                foreach (var platform in platforms)
                {
                    if (authenticated.Contains(platform.StorageKey))
                        continue;
                    if (ManagedLoginCookieValidator.IsAuthenticatedCookieMetadata(
                            platform,
                            domain,
                            name,
                            expiresUnix))
                    {
                        authenticated.Add(platform.StorageKey);
                    }
                }

                if (authenticated.Count == platforms.Count)
                    break;
            }

            return new CookieDatabaseReadResult(true, authenticated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SqliteException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException
                                   or ArgumentException
                                   or InvalidOperationException
                                   or NotSupportedException
                                   or InvalidCastException
                                   or FormatException
                                   or OverflowException)
        {
            return CookieDatabaseReadResult.Unreadable;
        }
    }

    private static long ConvertChromeExpiryToUnix(long chromeExpiry)
    {
        if (chromeExpiry <= 0)
            return 0;

        var secondsSinceChromeEpoch = chromeExpiry / 1_000_000;
        return secondsSinceChromeEpoch <= ChromeToUnixEpochSeconds
            ? 1
            : secondsSinceChromeEpoch - ChromeToUnixEpochSeconds;
    }

    private enum CookieDatabaseKind
    {
        Chromium,
        Firefox
    }

    private sealed record CookieDatabase(string Path, CookieDatabaseKind Kind);

    private sealed record ProfileCacheKey(string StableProfileId, string PlatformSignature);

    private sealed record CachedProfileReadResult(
        DateTime LastActivityUtc,
        DateTime CapturedUtc,
        CookieDatabaseReadResult Result);

    private sealed record CookieDatabaseReadResult(
        bool WasReadable,
        IReadOnlySet<string> AuthenticatedPlatformStorageKeys)
    {
        public static CookieDatabaseReadResult Unreadable { get; } =
            new(false, new HashSet<string>(StringComparer.Ordinal));
    }
}
