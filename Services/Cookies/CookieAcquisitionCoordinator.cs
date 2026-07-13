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
    private readonly ConcurrentDictionary<string, ManagedLoginRequest> _managedRequests =
        new(StringComparer.Ordinal);

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

        if (await _vault.ExistsAsync(platform.StorageKey, cancellationToken)
            || HasLegacyCookieForPlatform(platform, url))
        {
            attempts.Add(new CookieAttempt(CookieSourceKind.LegacyScoped, platform));
        }

        if (!_config.Config.SmartCookieEnabled)
            return attempts;

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
                && HasLegacyCookieForPlatform(attempt.Platform, url))
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
            var request = GetManagedRequest(storageKey, attempt.Platform);
            IReadOnlyList<BrowserCookie> cookies;
            try
            {
                cookies = await request.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                RemoveManagedRequest(storageKey, request, cancel: true);
                throw;
            }
            finally
            {
                if (request.ReleaseWaiterAndShouldCancel())
                    RemoveManagedRequest(storageKey, request, cancel: true);
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

    private bool HasLegacyCookieForPlatform(
        MediaPlatformDefinition platform,
        string url)
    {
        var content = _config.Config.CookieContent;
        if (string.IsNullOrWhiteSpace(content))
            return false;
        if (string.Equals(
                _config.Config.LegacyCookiePlatform,
                platform.StorageKey,
                StringComparison.Ordinal))
        {
            return true;
        }
        if (!CookieFileSerializer.HasExplicitDomainRows(content)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var lines = CookieFileSerializer.BuildScopedLines(
            content,
            platform,
            uri.Host);
        return lines.Skip(3).Any();
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
            {
                RemoveManagedRequest(
                    attempt.Platform.StorageKey,
                    expected: null,
                    cancel: true);
            }
        }

        return failure;
    }

    public async Task RecordSuccessAsync(
        CookieAttempt attempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Source == CookieSourceKind.LegacyScoped
            && !string.IsNullOrWhiteSpace(_config.Config.CookieContent))
        {
            try
            {
                await _config.CompleteLegacyCookieMigrationAsync(
                    attempt.Platform.StorageKey,
                    _vault,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CookieCoordinator] Failed to migrate legacy Cookie config: {ex.Message}");
            }
        }

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

    public async Task ClearPlatformSessionAsync(
        MediaPlatformDefinition platform,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(platform);
        CookieStorageKey.ValidatePlatformId(platform.StorageKey);
        cancellationToken.ThrowIfCancellationRequested();
        RemoveManagedRequest(platform.StorageKey, expected: null, cancel: true);
        await _managedLogin.ClearAsync(platform.StorageKey, cancellationToken);
        await _health.ClearPlatformAsync(platform.StorageKey, cancellationToken);
    }

    private ManagedLoginRequest GetManagedRequest(
        string platformId,
        MediaPlatformDefinition platform)
    {
        while (true)
        {
            var request = _managedRequests.GetOrAdd(
                platformId,
                _ => new ManagedLoginRequest(token =>
                    _managedLogin.GetCookiesAsync(platform, token)));
            if (request.TryAddWaiter())
                return request;

            RemoveManagedRequest(platformId, request, cancel: true);
        }
    }

    private void RemoveManagedRequest(
        string platformId,
        ManagedLoginRequest? expected,
        bool cancel = false)
    {
        if (!_managedRequests.TryGetValue(platformId, out var current)
            || (expected is not null && !ReferenceEquals(current, expected)))
        {
            return;
        }

        if (((ICollection<KeyValuePair<string, ManagedLoginRequest>>)_managedRequests)
            .Remove(new KeyValuePair<string, ManagedLoginRequest>(platformId, current)))
        {
            if (cancel)
                current.Cancel();
        }
    }

    private sealed class ManagedLoginRequest
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Lazy<Task<IReadOnlyList<BrowserCookie>>> _task;
        private int _waiterCount;
        private bool _acceptingWaiters = true;

        public ManagedLoginRequest(
            Func<CancellationToken, Task<IReadOnlyList<BrowserCookie>>> requestFactory)
        {
            ArgumentNullException.ThrowIfNull(requestFactory);
            _task = new Lazy<Task<IReadOnlyList<BrowserCookie>>>(
                () => requestFactory(_cancellation.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public Task<IReadOnlyList<BrowserCookie>> Task => _task.Value;

        public bool TryAddWaiter()
        {
            lock (_gate)
            {
                if (!_acceptingWaiters)
                    return false;

                _waiterCount++;
                return true;
            }
        }

        public bool ReleaseWaiterAndShouldCancel()
        {
            lock (_gate)
            {
                if (_waiterCount <= 0)
                    throw new InvalidOperationException("Managed login waiter count is unbalanced.");

                _waiterCount--;
                if (_waiterCount != 0
                    || !_task.IsValueCreated
                    || _task.Value.IsCompleted)
                {
                    return false;
                }

                _acceptingWaiters = false;
                return true;
            }
        }

        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
