using System.Collections.Concurrent;
using EasyGet.Services;

namespace EasyGet.Services.Cookies;

public interface IManagedLoginSessionService
{
    Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
        MediaPlatformDefinition platform,
        CancellationToken cancellationToken);

    Task ClearAsync(string platformId, CancellationToken cancellationToken);
}

public sealed class EmptyManagedLoginSessionService : IManagedLoginSessionService
{
    public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
        MediaPlatformDefinition platform,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(platform);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<BrowserCookie>>([]);
    }

    public Task ClearAsync(string platformId, CancellationToken cancellationToken)
    {
        CookieStorageKey.ValidatePlatformId(platformId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

public sealed record CookieAttempt(
    CookieSourceKind Source,
    MediaPlatformDefinition Platform,
    BrowserProfile? BrowserProfile = null);

public sealed class CookieArgumentLease : IAsyncDisposable
{
    private readonly IAsyncDisposable? _ownedLease;

    private CookieArgumentLease(
        IReadOnlyList<string> arguments,
        IAsyncDisposable? ownedLease)
    {
        Arguments = arguments;
        _ownedLease = ownedLease;
    }

    public static CookieArgumentLease Empty { get; } = new([], null);

    public IReadOnlyList<string> Arguments { get; }

    public static CookieArgumentLease Browser(string profileArgument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileArgument);
        return new(["--cookies-from-browser", profileArgument], null);
    }

    public static CookieArgumentLease File(CookieFileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new(lease.Arguments, lease);
    }

    public ValueTask DisposeAsync()
        => _ownedLease?.DisposeAsync() ?? ValueTask.CompletedTask;
}

public sealed class CookieAcquisitionCoordinator
{
    private readonly ConfigService _config;
    private readonly PlatformCookieVault _vault;
    private readonly IBrowserProfileDiscoveryService _profiles;
    private readonly ICookieHealthStore _health;
    private readonly IManagedLoginSessionService _managedLogin;
    private readonly string _temporaryDirectory;
    private readonly ConcurrentDictionary<
        string,
        Lazy<Task<IReadOnlyList<BrowserCookie>>>> _managedRequests = new(StringComparer.Ordinal);

    public CookieAcquisitionCoordinator(
        ConfigService config,
        PlatformCookieVault vault,
        IBrowserProfileDiscoveryService profiles,
        ICookieHealthStore health,
        IManagedLoginSessionService managedLogin,
        string temporaryDirectory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(managedLogin);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryDirectory);

        _config = config;
        _vault = vault;
        _profiles = profiles;
        _health = health;
        _managedLogin = managedLogin;
        _temporaryDirectory = temporaryDirectory;
    }

    public async Task<IReadOnlyList<CookieAttempt>> BuildAttemptsAsync(
        string url,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        cancellationToken.ThrowIfCancellationRequested();

        var platform = MediaPlatformResolver.Resolve(url);
        var attempts = new List<CookieAttempt>
        {
            new(CookieSourceKind.Anonymous, platform)
        };
        if (!_config.Config.SmartCookieEnabled)
            return attempts;

        if (await _vault.ExistsAsync(platform.StorageKey, cancellationToken)
            || (!string.IsNullOrWhiteSpace(_config.Config.CookieContent)
                && string.Equals(
                    _config.Config.LegacyCookiePlatform,
                    platform.StorageKey,
                    StringComparison.Ordinal)))
        {
            attempts.Add(new CookieAttempt(CookieSourceKind.LegacyScoped, platform));
        }

        var successfulProfiles = _health.Snapshot()
            .Where(record => string.Equals(
                                 record.PlatformId,
                                 platform.StorageKey,
                                 StringComparison.Ordinal)
                             && record.Source == CookieSourceKind.Browser
                             && record.LastSuccessUtc.HasValue
                             && record.ConsecutiveFailures == 0
                             && (!record.LastFailureUtc.HasValue
                                 || record.LastSuccessUtc.Value >= record.LastFailureUtc.Value)
                             && !string.IsNullOrWhiteSpace(record.SourceId))
            .GroupBy(record => record.SourceId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Max(record => record.LastSuccessUtc!.Value),
                StringComparer.Ordinal);

        attempts.AddRange(_profiles.Discover()
            .OrderByDescending(profile => successfulProfiles.GetValueOrDefault(profile.StableId))
            .ThenByDescending(profile => profile.LastActivityUtc)
            .ThenByDescending(profile => profile.IsDefaultBrowser)
            .ThenBy(profile => profile.BrowserName, StringComparer.Ordinal)
            .ThenBy(profile => profile.DisplayName, StringComparer.Ordinal)
            .Select(profile => new CookieAttempt(
                CookieSourceKind.Browser,
                platform,
                profile)));
        attempts.Add(new CookieAttempt(CookieSourceKind.ManagedSession, platform));
        return attempts;
    }

    public async Task<CookieArgumentLease> AcquireArgumentsAsync(
        CookieAttempt attempt,
        string url,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        cancellationToken.ThrowIfCancellationRequested();

        if (attempt.Source == CookieSourceKind.Anonymous)
            return CookieArgumentLease.Empty;
        ValidateAttemptMatchesUrl(attempt, url);
        if (attempt.Source == CookieSourceKind.Browser)
        {
            var profile = attempt.BrowserProfile
                ?? throw new ArgumentException(
                    "A browser Cookie attempt requires a browser profile.",
                    nameof(attempt));
            return CookieArgumentLease.Browser(profile.YtDlpArgument);
        }

        if (attempt.Source == CookieSourceKind.LegacyScoped)
        {
            var content = await _vault.LoadAsync(attempt.Platform.StorageKey, cancellationToken);
            if (string.IsNullOrWhiteSpace(content)
                && string.Equals(
                    _config.Config.LegacyCookiePlatform,
                    attempt.Platform.StorageKey,
                    StringComparison.Ordinal))
            {
                content = _config.Config.CookieContent;
            }

            if (string.IsNullOrWhiteSpace(content))
                return CookieArgumentLease.Empty;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("A valid HTTP media URL is required.", nameof(url));
            }

            var fileLease = await CookieFileLease.CreateLegacyAsync(
                content,
                attempt.Platform,
                uri.Host,
                _temporaryDirectory,
                cancellationToken);
            return CookieArgumentLease.File(fileLease);
        }

        if (attempt.Source == CookieSourceKind.ManagedSession)
        {
            var storageKey = attempt.Platform.StorageKey;
            CookieStorageKey.ValidatePlatformId(storageKey);
            var request = _managedRequests.GetOrAdd(
                storageKey,
                _ => new Lazy<Task<IReadOnlyList<BrowserCookie>>>(
                    () => _managedLogin.GetCookiesAsync(
                        attempt.Platform,
                        CancellationToken.None),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            IReadOnlyList<BrowserCookie> cookies;
            try
            {
                cookies = await request.Value.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                RemoveManagedRequest(storageKey, request);
                throw;
            }

            if (cookies.Count == 0)
            {
                RemoveManagedRequest(storageKey, request);
                return CookieArgumentLease.Empty;
            }

            var fileLease = await CookieFileLease.CreateCookiesAsync(
                cookies,
                attempt.Platform,
                _temporaryDirectory,
                cancellationToken);
            return CookieArgumentLease.File(fileLease);
        }

        throw new NotSupportedException($"Cookie source '{attempt.Source}' is not implemented.");
    }

    private static void ValidateAttemptMatchesUrl(CookieAttempt attempt, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("A valid HTTP media URL is required.", nameof(url));
        }

        var resolved = MediaPlatformResolver.Resolve(url);
        var matches = string.Equals(
                          attempt.Platform.Id,
                          resolved.Id,
                          StringComparison.Ordinal)
                      && (resolved.Id != "generic"
                          || attempt.Platform.CookieDomains.Any(domain =>
                              MediaPlatformResolver.HostMatches(uri.Host, domain)));
        if (!matches)
        {
            throw new ArgumentException(
                "The Cookie attempt platform does not match the media URL.",
                nameof(attempt));
        }
    }

    public async Task<CookieFailure> ClassifyAndRecordFailureAsync(
        CookieAttempt attempt,
        IEnumerable<string> lines,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(lines);
        var failure = CookieFailureClassifier.Classify(attempt.Platform.Id, lines);
        try
        {
            await _health.RecordFailureAsync(
                attempt.Platform.StorageKey,
                attempt.Source,
                attempt.BrowserProfile,
                failure.Category,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CookieCoordinator] Failed to persist health metadata: {ex.Message}");
        }
        finally
        {
            if (attempt.Source == CookieSourceKind.ManagedSession)
                _managedRequests.TryRemove(attempt.Platform.StorageKey, out _);
        }

        return failure;
    }

    public async Task RecordSuccessAsync(
        CookieAttempt attempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        try
        {
            await _health.RecordSuccessAsync(
                attempt.Platform.StorageKey,
                attempt.Source,
                attempt.BrowserProfile,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CookieCoordinator] Failed to persist health metadata: {ex.Message}");
        }
    }

    private void RemoveManagedRequest(
        string platformId,
        Lazy<Task<IReadOnlyList<BrowserCookie>>> request)
    {
        if (_managedRequests.TryGetValue(platformId, out var current)
            && ReferenceEquals(current, request))
        {
            _managedRequests.TryRemove(platformId, out _);
        }
    }
}
