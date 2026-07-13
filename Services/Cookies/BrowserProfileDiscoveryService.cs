using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace EasyGet.Services.Cookies;

public sealed record BrowserProfile(
    string BrowserId,
    string BrowserName,
    string DisplayName,
    string ProfilePath,
    DateTime LastActivityUtc,
    bool IsDefaultBrowser = false)
{
    public string YtDlpArgument => $"{BrowserId}:{ProfilePath}";

    public string StableId
    {
        get
        {
            var normalizedPath = Path.GetFullPath(ProfilePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (OperatingSystem.IsWindows())
                normalizedPath = normalizedPath.ToUpperInvariant();

            return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes($"{BrowserId}|{normalizedPath}")));
        }
    }
}

public interface IBrowserProfileDiscoveryService
{
    IReadOnlyList<BrowserProfile> Discover();
}

public sealed class BrowserProfileDiscoveryService : IBrowserProfileDiscoveryService
{
    private static readonly ChromiumBrowserDefinition[] ChromiumBrowsers =
    [
        new("chrome", "Chrome", Path.Combine("Google", "Chrome", "User Data")),
        new("edge", "Edge", Path.Combine("Microsoft", "Edge", "User Data")),
        new("brave", "Brave", Path.Combine("BraveSoftware", "Brave-Browser", "User Data")),
        new("chromium", "Chromium", Path.Combine("Chromium", "User Data")),
        new("vivaldi", "Vivaldi", Path.Combine("Vivaldi", "User Data"))
    ];

    private static readonly OperaBrowserDefinition[] OperaBrowsers =
    [
        new("Opera", "Opera Stable", Path.Combine("Opera Software", "Opera Stable")),
        new("Opera GX", "Opera GX Stable", Path.Combine("Opera Software", "Opera GX Stable"))
    ];

    private readonly string _localAppData;
    private readonly string _roamingAppData;
    private readonly Func<string?> _defaultBrowserProgId;

    public BrowserProfileDiscoveryService()
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ReadDefaultBrowserProgId)
    {
    }

    internal BrowserProfileDiscoveryService(string localAppData, string roamingAppData)
        : this(localAppData, roamingAppData, ReadDefaultBrowserProgId)
    {
    }

    internal BrowserProfileDiscoveryService(
        string localAppData,
        string roamingAppData,
        Func<string?> defaultBrowserProgId)
    {
        ArgumentNullException.ThrowIfNull(localAppData);
        ArgumentNullException.ThrowIfNull(roamingAppData);
        ArgumentNullException.ThrowIfNull(defaultBrowserProgId);

        _localAppData = localAppData;
        _roamingAppData = roamingAppData;
        _defaultBrowserProgId = defaultBrowserProgId;
    }

    public IReadOnlyList<BrowserProfile> Discover()
    {
        var defaultBrowserId = ResolveDefaultBrowserId();

        return DiscoverChromiumProfiles(defaultBrowserId)
            .Concat(DiscoverFirefoxProfiles(defaultBrowserId))
            .Concat(DiscoverOperaProfiles(defaultBrowserId))
            .OrderByDescending(profile => profile.IsDefaultBrowser)
            .ThenByDescending(profile => profile.LastActivityUtc)
            .ThenBy(profile => profile.BrowserName, StringComparer.Ordinal)
            .ThenBy(profile => profile.DisplayName, StringComparer.Ordinal)
            .ThenBy(profile => profile.ProfilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IEnumerable<BrowserProfile> DiscoverChromiumProfiles(string? defaultBrowserId)
    {
        foreach (var browser in ChromiumBrowsers)
        {
            var userDataRoot = Path.Combine(_localAppData, browser.RelativeRoot);
            foreach (var profilePath in EnumerateDirectoriesSafely(userDataRoot))
            {
                var displayName = Path.GetFileName(profilePath);
                if (!IsChromiumProfileName(displayName)
                    || !TryGetCookieActivity(profilePath, out var lastActivityUtc))
                {
                    continue;
                }

                yield return new BrowserProfile(
                    browser.Id,
                    browser.Name,
                    displayName,
                    profilePath,
                    lastActivityUtc,
                    string.Equals(browser.Id, defaultBrowserId, StringComparison.Ordinal));
            }
        }
    }

    private IEnumerable<BrowserProfile> DiscoverFirefoxProfiles(string? defaultBrowserId)
    {
        var profilesRoot = Path.Combine(_roamingAppData, "Mozilla", "Firefox", "Profiles");
        foreach (var profilePath in EnumerateDirectoriesSafely(profilesRoot))
        {
            var cookieDatabase = Path.Combine(profilePath, "cookies.sqlite");
            if (!TryGetLastWriteTimeUtc(cookieDatabase, out var lastActivityUtc))
                continue;

            yield return new BrowserProfile(
                "firefox",
                "Firefox",
                Path.GetFileName(profilePath),
                profilePath,
                lastActivityUtc,
                string.Equals("firefox", defaultBrowserId, StringComparison.Ordinal));
        }
    }

    private IEnumerable<BrowserProfile> DiscoverOperaProfiles(string? defaultBrowserId)
    {
        foreach (var browser in OperaBrowsers)
        {
            var profilePath = Path.Combine(_roamingAppData, browser.RelativeRoot);
            if (!TryGetCookieActivity(profilePath, out var lastActivityUtc))
                continue;

            yield return new BrowserProfile(
                "opera",
                browser.Name,
                browser.DisplayName,
                profilePath,
                lastActivityUtc,
                string.Equals("opera", defaultBrowserId, StringComparison.Ordinal));
        }
    }

    private static bool IsChromiumProfileName(string displayName)
        => string.Equals(displayName, "Default", StringComparison.OrdinalIgnoreCase)
           || (displayName.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)
               && displayName.Length > "Profile ".Length);

    private static bool TryGetCookieActivity(string profilePath, out DateTime lastActivityUtc)
    {
        lastActivityUtc = default;
        var found = false;

        foreach (var cookieDatabase in new[]
                 {
                     Path.Combine(profilePath, "Network", "Cookies"),
                     Path.Combine(profilePath, "Cookies")
                 })
        {
            if (!TryGetLastWriteTimeUtc(cookieDatabase, out var writeTimeUtc))
                continue;

            if (!found || writeTimeUtc > lastActivityUtc)
                lastActivityUtc = writeTimeUtc;
            found = true;
        }

        return found;
    }

    private static bool TryGetLastWriteTimeUtc(string path, out DateTime lastWriteTimeUtc)
    {
        try
        {
            if (!File.Exists(path))
            {
                lastWriteTimeUtc = default;
                return false;
            }

            lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or SecurityException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            lastWriteTimeUtc = default;
            return false;
        }
    }

    private static IReadOnlyList<string> EnumerateDirectoriesSafely(string root)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.GetDirectories(root)
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or SecurityException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            return Array.Empty<string>();
        }
    }

    private string? ResolveDefaultBrowserId()
    {
        try
        {
            return MapProgIdToBrowserId(_defaultBrowserProgId());
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or SecurityException
                                   or ArgumentException)
        {
            return null;
        }
    }

    private static string? MapProgIdToBrowserId(string? progId)
    {
        if (string.IsNullOrWhiteSpace(progId))
            return null;

        if (progId.Contains("MSEdge", StringComparison.OrdinalIgnoreCase)) return "edge";
        if (progId.Contains("Brave", StringComparison.OrdinalIgnoreCase)) return "brave";
        if (progId.Contains("Chromium", StringComparison.OrdinalIgnoreCase)) return "chromium";
        if (progId.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "chrome";
        if (progId.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase)) return "vivaldi";
        if (progId.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "firefox";
        if (progId.Contains("Opera", StringComparison.OrdinalIgnoreCase)) return "opera";
        return null;
    }

    private static string? ReadDefaultBrowserProgId()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        const string keyPath =
            @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice";
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return key?.GetValue("ProgId") as string;
    }

    private sealed record ChromiumBrowserDefinition(string Id, string Name, string RelativeRoot);
    private sealed record OperaBrowserDefinition(string Name, string DisplayName, string RelativeRoot);
}
