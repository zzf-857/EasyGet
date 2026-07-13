using System.IO;

namespace EasyGet.Services.Cookies;

public interface IManagedLoginWindowFactory
{
    Task<IManagedLoginWindow> CreateAsync(
        MediaPlatformDefinition platform,
        string sessionDirectory,
        CancellationToken cancellationToken);
}

public interface IManagedLoginWindow : IAsyncDisposable
{
    Task<IReadOnlyList<BrowserCookie>> ReadCookiesAsync(
        IReadOnlyList<string> allowedDomains,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BrowserCookie>> ShowForLoginAsync(
        IReadOnlyList<string> allowedDomains,
        CancellationToken cancellationToken);
}

public sealed class ManagedLoginSessionService : IManagedLoginSessionService
{
    private readonly IManagedLoginWindowFactory _windowFactory;
    private readonly string _sessionRoot;

    public ManagedLoginSessionService(
        IManagedLoginWindowFactory windowFactory,
        string sessionRoot)
    {
        ArgumentNullException.ThrowIfNull(windowFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        _windowFactory = windowFactory;
        _sessionRoot = sessionRoot;
    }

    public async Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
        MediaPlatformDefinition platform,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(platform);
        CookieStorageKey.ValidatePlatformId(platform.StorageKey);
        cancellationToken.ThrowIfCancellationRequested();

        var sessionDirectory = Path.Combine(_sessionRoot, platform.StorageKey);
        await using var window = await _windowFactory.CreateAsync(
            platform,
            sessionDirectory,
            cancellationToken);
        var storedCookies = FilterCookies(
            await window.ReadCookiesAsync(
                platform.CookieDomains,
                cancellationToken),
            platform.CookieDomains);
        if (ManagedLoginCookieValidator.HasAuthenticatedSession(platform, storedCookies))
            return storedCookies;

        var loginCookies = FilterCookies(
            await window.ShowForLoginAsync(
                platform.CookieDomains,
                cancellationToken),
            platform.CookieDomains);
        return ManagedLoginCookieValidator.HasAuthenticatedSession(platform, loginCookies)
            ? loginCookies
            : [];
    }

    public async Task ClearAsync(string platformId, CancellationToken cancellationToken)
    {
        CookieStorageKey.ValidatePlatformId(platformId);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(_sessionRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var sessionDirectory = Path.GetFullPath(Path.Combine(root, platformId));
        if (!sessionDirectory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Platform session path escapes its storage root.", nameof(platformId));

        await Task.Run(
            () =>
            {
                if (Directory.Exists(sessionDirectory))
                    Directory.Delete(sessionDirectory, recursive: true);
            },
            cancellationToken);
    }

    private static IReadOnlyList<BrowserCookie> FilterCookies(
        IReadOnlyList<BrowserCookie> cookies,
        IReadOnlyList<string> allowedDomains)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        ArgumentNullException.ThrowIfNull(allowedDomains);
        return cookies
            .Where(cookie =>
            {
                var domain = cookie.Domain.Trim().TrimStart('.');
                return domain.Length > 0
                       && allowedDomains.Any(allowed =>
                           MediaPlatformResolver.HostMatches(domain, allowed));
            })
            .ToArray();
    }
}
