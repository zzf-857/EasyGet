# Smart Cookie Orchestration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a platform-scoped Cookie orchestration system that anonymously downloads public media, silently reuses installed browser sessions, and opens one managed WebView2 login only when every local strategy fails.

**Architecture:** Extract platform resolution, browser-profile discovery, failure classification, temporary Cookie leases, health metadata, and retry ordering from `YtDlpService` into focused services under `Services/Cookies`. All metadata, playlist, and download processes consume the same `CookieAcquisitionCoordinator`; the settings UI exposes truthful per-platform health without showing Cookie values.

**Tech Stack:** .NET 8, C# 12, WPF, CommunityToolkit.Mvvm, Microsoft.Web.WebView2, xUnit, yt-dlp

---

## File map

- Create `Services/Cookies/MediaPlatformResolver.cs`: URL-to-platform identity, domain scope, and login URI.
- Create `Services/Cookies/BrowserProfileDiscoveryService.cs`: installed browser/profile enumeration without reading Cookie values.
- Create `Services/Cookies/CookieFailureClassifier.cs`: normalized authentication and non-authentication failure categories.
- Create `Services/Cookies/CookieFileLease.cs`: platform-scoped Netscape serialization and guaranteed temporary-file cleanup.
- Create `Services/Cookies/PlatformCookieVault.cs`: DPAPI-protected platform-scoped manual Cookie storage.
- Create `Services/Cookies/CookieHealthStore.cs`: non-sensitive strategy success/failure metadata and ordering hints.
- Create `Services/Cookies/CookieAcquisitionCoordinator.cs`: attempt planning, shared platform authentication, and Cookie argument leases.
- Create `Services/Cookies/ManagedLoginSessionService.cs`: WebView2-backed per-platform session acquisition.
- Create `Models/CookiePlatformStatusItem.cs`: settings-page presentation model.
- Create `Views/ManagedLoginWindow.xaml` and `.xaml.cs`: real platform login surface.
- Modify `Services/YtDlpService.cs`: replace embedded Cookie logic with the coordinator in metadata, playlist, and download flows.
- Modify `Models/AppConfig.cs`, `Services/ConfigService.cs`: smart-mode settings and safe legacy migration.
- Modify `ViewModels/SettingsViewModel.cs`, `Views/SettingsView.xaml`: health/status controls and advanced manual import.
- Modify `App.xaml.cs`, `EasyGet.csproj`: dependency injection and WebView2 dependency.
- Create `EasyGet.Tests/TestDirectory.cs`: disposable isolated filesystem fixture shared by the new service tests.
- Create focused test files in `EasyGet.Tests/` for each service; update existing Cookie/config/settings tests.

### Task 1: Resolve URLs to platform-scoped authentication definitions

**Files:**
- Create: `Services/Cookies/MediaPlatformResolver.cs`
- Create: `EasyGet.Tests/TestDirectory.cs`
- Test: `EasyGet.Tests/MediaPlatformResolverTests.cs`

- [ ] **Step 1: Write the failing platform mapping tests**

```csharp
public sealed class MediaPlatformResolverTests
{
    [Theory]
    [InlineData("https://youtu.be/abc", "youtube")]
    [InlineData("https://www.bilibili.com/video/BV1xx", "bilibili")]
    [InlineData("https://v.douyin.com/abc/", "douyin")]
    [InlineData("https://x.com/user/status/1", "twitter")]
    [InlineData("https://www.instagram.com/reel/abc/", "instagram")]
    [InlineData("https://www.xiaohongshu.com/explore/abc", "xiaohongshu")]
    public void Resolve_ReturnsStablePlatformId(string url, string expected)
        => Assert.Equal(expected, MediaPlatformResolver.Resolve(url).Id);

    [Fact]
    public void Resolve_UnknownHttpHostUsesGenericScopedDefinition()
    {
        var platform = MediaPlatformResolver.Resolve("https://media.example.org/watch/1");
        Assert.Equal("generic", platform.Id);
        Assert.Equal(["media.example.org"], platform.CookieDomains);
        Assert.Equal("https://media.example.org/", platform.LoginUri.AbsoluteUri);
    }
}
```

- [ ] **Step 2: Run the test and verify the missing type failure**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter FullyQualifiedName~MediaPlatformResolverTests`

Expected: build fails because `MediaPlatformResolver` does not exist.

- [ ] **Step 3: Implement immutable platform definitions and host-boundary matching**

```csharp
namespace EasyGet.Services.Cookies;

public sealed record MediaPlatformDefinition(
    string Id,
    string DisplayName,
    Uri LoginUri,
    IReadOnlyList<string> CookieDomains,
    bool AnonymousFirst = true);

public static class MediaPlatformResolver
{
    private static readonly MediaPlatformDefinition[] Known =
    [
        new("youtube", "YouTube", new("https://accounts.google.com/ServiceLogin?service=youtube"), ["youtube.com", "google.com"]),
        new("bilibili", "哔哩哔哩", new("https://passport.bilibili.com/login"), ["bilibili.com"]),
        new("douyin", "抖音", new("https://www.douyin.com/"), ["douyin.com", "iesdouyin.com"]),
        new("tiktok", "TikTok", new("https://www.tiktok.com/login"), ["tiktok.com"]),
        new("twitter", "X / Twitter", new("https://x.com/i/flow/login"), ["x.com", "twitter.com"]),
        new("instagram", "Instagram", new("https://www.instagram.com/accounts/login/"), ["instagram.com"]),
        new("facebook", "Facebook", new("https://www.facebook.com/login"), ["facebook.com"]),
        new("kuaishou", "快手", new("https://www.kuaishou.com/"), ["kuaishou.com"]),
        new("xiaohongshu", "小红书", new("https://www.xiaohongshu.com/"), ["xiaohongshu.com", "xhslink.com"]),
        new("weibo", "微博", new("https://weibo.com/login.php"), ["weibo.com"]),
        new("twitch", "Twitch", new("https://www.twitch.tv/login"), ["twitch.tv"])
    ];

    public static MediaPlatformDefinition Resolve(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new("generic", "其他站点", new("https://localhost/"), [], true);

        return Known.FirstOrDefault(item => item.CookieDomains.Any(domain => HostMatches(uri.Host, domain)))
            ?? new("generic", uri.Host, new Uri($"{uri.Scheme}://{uri.Host}/"), [uri.Host], true);
    }

    public static bool HostMatches(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
           || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);
}
```

Add the shared test fixture at the same time:

```csharp
internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "EasyGetTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }
    public string Path(params string[] parts) => parts.Aggregate(DirectoryPath, System.IO.Path.Combine);

    public void Touch(string relativePath)
    {
        var path = Path(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }

    public Task WriteAsync(string relativePath, string content)
    {
        var path = Path(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        return File.WriteAllTextAsync(path, content);
    }

    public void Dispose()
    {
        try { Directory.Delete(DirectoryPath, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
```

- [ ] **Step 4: Run the focused tests**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter FullyQualifiedName~MediaPlatformResolverTests`

Expected: all platform resolver tests pass.

- [ ] **Step 5: Commit the platform resolver**

```powershell
git add Services/Cookies/MediaPlatformResolver.cs EasyGet.Tests/TestDirectory.cs EasyGet.Tests/MediaPlatformResolverTests.cs
git commit -m "feat: 增加流媒体平台认证映射"
```

### Task 2: Discover all supported local browser profiles

**Files:**
- Create: `Services/Cookies/BrowserProfileDiscoveryService.cs`
- Test: `EasyGet.Tests/BrowserProfileDiscoveryServiceTests.cs`

- [ ] **Step 1: Write failing discovery and ordering tests**

```csharp
[Fact]
public void Discover_FindsChromiumProfilesAndFirefoxProfiles()
{
    using var root = new TestDirectory();
    root.Touch("Local/Google/Chrome/User Data/Default/Network/Cookies");
    root.Touch("Local/Google/Chrome/User Data/Profile 2/Network/Cookies");
    root.Touch("Roaming/Mozilla/Firefox/Profiles/work.default-release/cookies.sqlite");

    var profiles = new BrowserProfileDiscoveryService(root.Path("Local"), root.Path("Roaming")).Discover();

    Assert.Collection(profiles.OrderBy(x => x.BrowserId).ThenBy(x => x.DisplayName),
        item => Assert.Equal("chrome", item.BrowserId),
        item => Assert.Equal("chrome", item.BrowserId),
        item => Assert.Equal("firefox", item.BrowserId));
    Assert.All(profiles, item => Assert.DoesNotContain("Cookies", item.ProfilePath));
}
```

- [ ] **Step 2: Verify the focused test fails because discovery is absent**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter FullyQualifiedName~BrowserProfileDiscoveryServiceTests`

Expected: build failure for `BrowserProfileDiscoveryService`.

- [ ] **Step 3: Implement browser definitions, profile validation, and deterministic sorting**

```csharp
public sealed record BrowserProfile(
    string BrowserId,
    string BrowserName,
    string DisplayName,
    string ProfilePath,
    DateTime LastActivityUtc,
    bool IsDefaultBrowser = false)
{
    public string YtDlpArgument => $"{BrowserId}:{ProfilePath}";
    public string StableId => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{BrowserId}|{ProfilePath}")));
}

public interface IBrowserProfileDiscoveryService
{
    IReadOnlyList<BrowserProfile> Discover();
}

public sealed class BrowserProfileDiscoveryService : IBrowserProfileDiscoveryService
{
    public BrowserProfileDiscoveryService()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
               Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)) { }

    internal BrowserProfileDiscoveryService(string local, string roaming)
    {
        _local = local;
        _roaming = roaming;
    }

    public IReadOnlyList<BrowserProfile> Discover()
        => DiscoverChromiumProfiles()
            .Concat(DiscoverFirefoxProfiles())
            .Concat(DiscoverOperaProfiles())
            .OrderByDescending(profile => profile.IsDefaultBrowser)
            .ThenByDescending(profile => profile.LastActivityUtc)
            .ThenBy(profile => profile.BrowserName, StringComparer.Ordinal)
            .ThenBy(profile => profile.DisplayName, StringComparer.Ordinal)
            .ToArray();
}
```

Implement Chromium roots for Chrome, Edge, Brave, Chromium, and Vivaldi; accept `Default` and `Profile *` directories only when `Network/Cookies` or `Cookies` exists. Read Firefox directories under `Mozilla/Firefox/Profiles` when `cookies.sqlite` exists. Treat Opera Stable and Opera GX Stable directories as profile roots. Resolve the current HTTP default browser from `HKCU\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice\ProgId`, map the ProgId to the supported browser ID, and set `IsDefaultBrowser` without failing discovery when the registry value is absent or inaccessible.

- [ ] **Step 4: Run discovery tests**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter FullyQualifiedName~BrowserProfileDiscoveryServiceTests`

Expected: all discovery tests pass with no real browser data dependency.

- [ ] **Step 5: Commit browser discovery**

```powershell
git add Services/Cookies/BrowserProfileDiscoveryService.cs EasyGet.Tests/BrowserProfileDiscoveryServiceTests.cs
git commit -m "feat: 自动发现本机浏览器配置"
```

### Task 3: Classify authentication failures without hiding root causes

**Files:**
- Create: `Services/Cookies/CookieFailureClassifier.cs`
- Test: `EasyGet.Tests/CookieFailureClassifierTests.cs`

- [ ] **Step 1: Write table-driven failure tests**

```csharp
[Theory]
[InlineData("youtube", "Sign in to confirm you’re not a bot", CookieFailureCategory.BotChallenge, true)]
[InlineData("youtube", "Sign in to confirm your age", CookieFailureCategory.AuthenticationRequired, true)]
[InlineData("douyin", "Fresh cookies (not necessarily logged in) are needed", CookieFailureCategory.CookieExpired, true)]
[InlineData("instagram", "login required", CookieFailureCategory.AuthenticationRequired, true)]
[InlineData("bilibili", "HTTP Error 412: Precondition Failed", CookieFailureCategory.BotChallenge, true)]
[InlineData("generic", "Could not copy Chrome cookie database", CookieFailureCategory.CookieStoreLocked, true)]
[InlineData("generic", "No space left on device", CookieFailureCategory.UnrelatedFailure, false)]
[InlineData("generic", "Connection timed out", CookieFailureCategory.NetworkFailure, false)]
public void Classify_ReturnsExpectedCategory(
    string platformId, string error, CookieFailureCategory category, bool retry)
{
    var result = CookieFailureClassifier.Classify(platformId, [error]);
    Assert.Equal(category, result.Category);
    Assert.Equal(retry, result.ShouldTryNextCookieSource);
}
```

- [ ] **Step 2: Run and observe the missing classifier failure**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter FullyQualifiedName~CookieFailureClassifierTests`

Expected: build failure because classifier types do not exist.

- [ ] **Step 3: Implement single-pass classification with precedence**

```csharp
public enum CookieFailureCategory
{
    None,
    AuthenticationRequired,
    CookieStoreLocked,
    CookieDecryptFailed,
    CookieExpired,
    BotChallenge,
    RateLimited,
    NetworkFailure,
    UnrelatedFailure
}

public sealed record CookieFailure(
    CookieFailureCategory Category,
    bool ShouldTryNextCookieSource,
    string? LastErrorLine);

public static class CookieFailureClassifier
{
    public static CookieFailure Classify(string platformId, IEnumerable<string> lines)
    {
        var category = CookieFailureCategory.UnrelatedFailure;
        string? lastError = null;
        foreach (var line in lines)
        {
            if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) lastError = line;
            category = ChooseHigherPriority(category, Match(platformId, line));
        }

        var retry = category is CookieFailureCategory.AuthenticationRequired
            or CookieFailureCategory.CookieStoreLocked
            or CookieFailureCategory.CookieDecryptFailed
            or CookieFailureCategory.CookieExpired
            or CookieFailureCategory.BotChallenge;
        return new(category, retry, lastError);
    }
}
```

Implement `Match` with the exact tested site phrases, DPAPI errors, database-copy/lock errors, HTTP 429, timeout/DNS/connectivity errors, and site-bound 403/412 rules. `ChooseHigherPriority` must preserve a meaningful site authentication error over a later browser database error while still retaining `LastErrorLine`.

- [ ] **Step 4: Run classifier and existing Cookie message tests**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter "FullyQualifiedName~CookieFailureClassifierTests|FullyQualifiedName~YtDlpCookieTests"`

Expected: all selected tests pass.

- [ ] **Step 5: Commit failure classification**

```powershell
git add Services/Cookies/CookieFailureClassifier.cs EasyGet.Tests/CookieFailureClassifierTests.cs EasyGet.Tests/YtDlpCookieTests.cs
git commit -m "feat: 统一识别 Cookie 与登录错误"
```

### Task 4: Create platform-scoped temporary Cookie leases

**Files:**
- Modify: `EasyGet.csproj`
- Modify: `App.xaml.cs`
- Create: `Services/Cookies/CookieFileLease.cs`
- Create: `Services/Cookies/PlatformCookieVault.cs`
- Test: `EasyGet.Tests/CookieFileLeaseTests.cs`
- Test: `EasyGet.Tests/PlatformCookieVaultTests.cs`
- Modify: `EasyGet.Tests/YtDlpCookieTests.cs`

- [ ] **Step 1: Write failing scoping and cleanup tests**

```csharp
[Fact]
public async Task LegacyHeader_IsWrittenOnlyForTargetPlatformAndDeletedOnDispose()
{
    using var root = new TestDirectory();
    var platform = MediaPlatformResolver.Resolve("https://x.com/user/status/1");
    string path;
    await using (var lease = await CookieFileLease.CreateLegacyAsync(
        "Cookie: auth_token=secret", platform, "x.com", root.DirectoryPath, CancellationToken.None))
    {
        path = lease.FilePath;
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains(".x.com\tTRUE\t/\tTRUE\t0\tauth_token\tsecret", text);
        Assert.DoesNotContain("youtube.com", text);
        Assert.DoesNotContain("instagram.com", text);
    }
    Assert.False(File.Exists(path));
}

[Fact]
public async Task NetscapeInput_DropsRowsOutsideAllowedPlatformDomains()
{
    const string input = "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSID\tyt\n.x.com\tTRUE\t/\tTRUE\t0\tauth_token\tx";
    var lines = CookieFileSerializer.BuildScopedLines(
        input, MediaPlatformResolver.Resolve("https://youtube.com/watch?v=1"), "youtube.com");
    Assert.Contains(lines, line => line.Contains(".youtube.com"));
    Assert.DoesNotContain(lines, line => line.Contains(".x.com"));
}

[Fact]
public async Task Vault_EncryptsManualCookieContentAndRestoresItByPlatform()
{
    using var root = new TestDirectory();
    var vault = new PlatformCookieVault(root.DirectoryPath, new ReversingTestProtector());
    await vault.SaveAsync("twitter", "auth_token=secret", CancellationToken.None);
    var stored = await File.ReadAllBytesAsync(root.Path("manual-cookies", "twitter.bin"));
    Assert.DoesNotContain("secret", Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
    Assert.Equal("auth_token=secret", await vault.LoadAsync("twitter", CancellationToken.None));
}
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter "FullyQualifiedName~CookieFileLeaseTests|FullyQualifiedName~PlatformCookieVaultTests"`

Expected: build failure for the missing lease and serializer.

- [ ] **Step 3: Implement scoped parsing and disposable files**

```csharp
public sealed class CookieFileLease : IAsyncDisposable
{
    private CookieFileLease(string filePath) => FilePath = filePath;
    public string FilePath { get; }
    public IReadOnlyList<string> Arguments => ["--cookies", FilePath];

    public static async Task<CookieFileLease> CreateLegacyAsync(
        string content, MediaPlatformDefinition platform, string targetHost,
        string rootDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(rootDirectory);
        var path = Path.Combine(rootDirectory, $"{platform.Id}-{Guid.NewGuid():N}.txt");
        var lines = CookieFileSerializer.BuildScopedLines(content, platform, targetHost);
        await File.WriteAllLinesAsync(path, lines, Encoding.UTF8, cancellationToken);
        CookieFilePermissions.RestrictToCurrentUser(path);
        return new(path);
    }

    public static Task<CookieFileLease> CreateCookiesAsync(
        IReadOnlyList<BrowserCookie> cookies,
        MediaPlatformDefinition platform,
        string rootDirectory,
        CancellationToken cancellationToken)
        => CreateLinesAsync(
            CookieFileSerializer.BuildScopedLines(cookies, platform),
            platform.Id,
            rootDirectory,
            cancellationToken);

    public ValueTask DisposeAsync()
    {
        try { File.Delete(FilePath); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }

    public static int CleanupStaleFiles(string rootDirectory, DateTime utcNow, TimeSpan maximumAge)
    {
        if (!Directory.Exists(rootDirectory)) return 0;
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(rootDirectory, "*.txt"))
        {
            if (utcNow - File.GetLastWriteTimeUtc(path) <= maximumAge) continue;
            try { File.Delete(path); deleted++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return deleted;
    }
}

public sealed record BrowserCookie(
    string Domain,
    string Path,
    string Name,
    string Value,
    bool Secure,
    long ExpiresUnix);
```

Move JSON, Netscape, and Header parsing from `YtDlpService` into `CookieFileSerializer`. Filter Netscape/JSON rows with `MediaPlatformResolver.HostMatches`. For a Header with no domain, emit rows only for `targetHost`'s registrable platform scope; never loop over unrelated platforms. Add `CookieFilePermissions.RestrictToCurrentUser` using Windows ACLs and a no-op branch for non-Windows tests.

Add the Windows protected vault and its test seam:

```csharp
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "EasyGet.PlatformCookieVault.v1"u8.ToArray();
    public byte[] Protect(byte[] plaintext)
        => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] ciphertext)
        => ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
}

public sealed class PlatformCookieVault
{
    public async Task SaveAsync(string platformId, string content, CancellationToken ct)
    {
        var path = GetPath(platformId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encrypted = _protector.Protect(Encoding.UTF8.GetBytes(content));
        await File.WriteAllBytesAsync(path, encrypted, ct);
        CookieFilePermissions.RestrictToCurrentUser(path);
    }

    public async Task<string?> LoadAsync(string platformId, CancellationToken ct)
    {
        var path = GetPath(platformId);
        if (!File.Exists(path)) return null;
        return Encoding.UTF8.GetString(_protector.Unprotect(await File.ReadAllBytesAsync(path, ct)));
    }

    public Task DeleteAsync(string platformId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        File.Delete(GetPath(platformId));
        return Task.CompletedTask;
    }
}
```

Run `dotnet add EasyGet.csproj package System.Security.Cryptography.ProtectedData --version 8.0.0` before compiling this task. `GetPath` must accept only platform IDs returned by `MediaPlatformResolver` and reject directory separators. Tests inject a reversible protector and assert the on-disk bytes do not contain the plaintext.

Call `CookieFileLease.CleanupStaleFiles(cookieTempDirectory, DateTime.UtcNow, TimeSpan.FromDays(1))` during `App.OnStartup`; add a test that creates one two-day-old file and one current file and asserts only the old file is removed.

- [ ] **Step 4: Run all Cookie serialization and lease tests**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter "FullyQualifiedName~CookieFileLeaseTests|FullyQualifiedName~PlatformCookieVaultTests|FullyQualifiedName~YtDlpCookieTests"`

Expected: selected tests pass, including cleanup after disposal.

- [ ] **Step 5: Commit secure Cookie leases**

```powershell
git add EasyGet.csproj App.xaml.cs Services/Cookies/CookieFileLease.cs Services/Cookies/PlatformCookieVault.cs EasyGet.Tests/CookieFileLeaseTests.cs EasyGet.Tests/PlatformCookieVaultTests.cs EasyGet.Tests/YtDlpCookieTests.cs
git commit -m "fix: 隔离各平台 Cookie 并清理临时文件"
```

### Task 5: Persist only non-sensitive Cookie health metadata

**Files:**
- Create: `Services/Cookies/CookieHealthStore.cs`
- Test: `EasyGet.Tests/CookieHealthStoreTests.cs`
- Modify: `Services/ConfigService.cs`

- [ ] **Step 1: Write failing persistence and redaction tests**

```csharp
[Fact]
public async Task RecordSuccess_PersistsStableIdWithoutProfilePathOrCookieValue()
{
    using var root = new TestDirectory();
    var store = new CookieHealthStore(root.DirectoryPath);
    var profile = new BrowserProfile("chrome", "Chrome", "Profile 1", @"C:\Users\me\Profile 1", DateTime.UtcNow);
    await store.RecordSuccessAsync("youtube", CookieSourceKind.Browser, profile, CancellationToken.None);

    var json = await File.ReadAllTextAsync(Path.Combine(root.DirectoryPath, "cookie-health.json"));
    Assert.Contains(profile.StableId, json);
    Assert.DoesNotContain(profile.ProfilePath, json);
    Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run and verify the store is missing**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter FullyQualifiedName~CookieHealthStoreTests`

Expected: build failure for `CookieHealthStore`.

- [ ] **Step 3: Implement atomic metadata persistence**

```csharp
public enum CookieSourceKind { Anonymous, LegacyScoped, Browser, ManagedSession }

public sealed record CookieHealthRecord(
    string PlatformId,
    CookieSourceKind Source,
    string SourceId,
    DateTime? LastSuccessUtc,
    DateTime? LastFailureUtc,
    int ConsecutiveFailures,
    CookieFailureCategory LastFailureCategory);

public interface ICookieHealthStore
{
    IReadOnlyList<CookieHealthRecord> Snapshot();
    Task RecordSuccessAsync(string platformId, CookieSourceKind source, BrowserProfile? profile, CancellationToken ct);
    Task RecordFailureAsync(string platformId, CookieSourceKind source, BrowserProfile? profile, CookieFailureCategory category, CancellationToken ct);
    Task ClearPlatformAsync(string platformId, CancellationToken ct);
}
```

Use a `SemaphoreSlim` around load/update/save, write `cookie-health.json.tmp`, then atomically replace the JSON file. Store `profile.StableId`, never `ProfilePath`. Expose `ConfigService.ConfigDirectory` so production and tests share the same application-data root.

- [ ] **Step 4: Run health-store and config tests**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter "FullyQualifiedName~CookieHealthStoreTests|FullyQualifiedName~ConfigServiceTests"`

Expected: all selected tests pass.

- [ ] **Step 5: Commit health metadata storage**

```powershell
git add Services/Cookies/CookieHealthStore.cs Services/ConfigService.cs EasyGet.Tests/CookieHealthStoreTests.cs EasyGet.Tests/ConfigServiceTests.cs
git commit -m "feat: 记录脱敏的 Cookie 健康状态"
```

### Task 6: Build unified Cookie attempt plans and shared platform authentication

**Files:**
- Create: `Services/Cookies/CookieAcquisitionCoordinator.cs`
- Test: `EasyGet.Tests/CookieAcquisitionCoordinatorTests.cs`
- Modify: `Models/AppConfig.cs`

- [ ] **Step 1: Write failing plan-order and deduplication tests**

```csharp
[Fact]
public async Task BuildAttemptsAsync_UsesAnonymousThenScopedLegacyBrowsersAndManagedSession()
{
    var chrome = new BrowserProfile("chrome", "Chrome", "Default", @"C:\Profiles\Chrome", DateTime.UtcNow);
    var firefox = new BrowserProfile("firefox", "Firefox", "default-release", @"C:\Profiles\Firefox", DateTime.UtcNow.AddMinutes(-1));
    var fixture = await CoordinatorFixture.CreateAsync("twitter", "auth=1", [chrome, firefox]);
    var attempts = await fixture.Coordinator.BuildAttemptsAsync("https://x.com/user/status/1", CancellationToken.None);
    Assert.Equal(
        [CookieSourceKind.Anonymous, CookieSourceKind.LegacyScoped, CookieSourceKind.Browser, CookieSourceKind.Browser, CookieSourceKind.ManagedSession],
        attempts.Select(x => x.Source));
}

[Fact]
public async Task AcquireArgumentsAsync_CoalescesConcurrentManagedSessionRequests()
{
    var provider = new FakeManagedLoginSessionService(delay: TimeSpan.FromMilliseconds(50));
    var fixture = await CoordinatorFixture.CreateAsync(provider: provider);
    var attempt = new CookieAttempt(
        CookieSourceKind.ManagedSession,
        MediaPlatformResolver.Resolve("https://x.com/a"));
    var requests = Enumerable.Range(0, 20)
        .Select(_ => fixture.Coordinator.AcquireArgumentsAsync(attempt, "https://x.com/a", CancellationToken.None));
    var leases = await Task.WhenAll(requests);
    Assert.Equal(1, provider.CallCount);
    foreach (var lease in leases) await lease.DisposeAsync();
}
```

`CoordinatorFixture` owns a `TestDirectory`, `ConfigService`, `PlatformCookieVault` with the reversible test protector, a stub `IBrowserProfileDiscoveryService`, an in-memory `ICookieHealthStore`, and the fake managed provider. Its `CreateAsync` method stores the supplied manual Cookie in the platform vault before constructing the coordinator. `DisposeAsync` disposes every owned temporary resource. Keep these fakes in `CookieAcquisitionCoordinatorTests.cs` so production code has no test-only branches.

- [ ] **Step 2: Run and verify the coordinator does not exist**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter FullyQualifiedName~CookieAcquisitionCoordinatorTests`

Expected: build failure for coordinator types.

- [ ] **Step 3: Implement attempt records, health ordering, leases, and request coalescing**

```csharp
public interface IManagedLoginSessionService
{
    Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(MediaPlatformDefinition platform, CancellationToken ct);
    Task ClearAsync(string platformId, CancellationToken ct);
}

public sealed record CookieAttempt(
    CookieSourceKind Source,
    MediaPlatformDefinition Platform,
    BrowserProfile? BrowserProfile = null);

public sealed class CookieArgumentLease : IAsyncDisposable
{
    private readonly IAsyncDisposable? _ownedLease;
    private CookieArgumentLease(IReadOnlyList<string> arguments, IAsyncDisposable? ownedLease)
        => (Arguments, _ownedLease) = (arguments, ownedLease);

    public static CookieArgumentLease Empty { get; } = new([], null);
    public IReadOnlyList<string> Arguments { get; }
    public static CookieArgumentLease Browser(string value)
        => new(["--cookies-from-browser", value], null);
    public static CookieArgumentLease File(CookieFileLease lease)
        => new(lease.Arguments, lease);
    public ValueTask DisposeAsync() => _ownedLease?.DisposeAsync() ?? ValueTask.CompletedTask;
}

public sealed class CookieAcquisitionCoordinator
{
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<BrowserCookie>>> _managedRequests = new();

    public async Task<IReadOnlyList<CookieAttempt>> BuildAttemptsAsync(string url, CancellationToken ct)
    {
        var platform = MediaPlatformResolver.Resolve(url);
        var attempts = new List<CookieAttempt> { new(CookieSourceKind.Anonymous, platform) };
        if (!_config.Config.SmartCookieEnabled) return attempts;

        if (await _vault.ExistsAsync(platform.Id, ct)
            || (!string.IsNullOrWhiteSpace(_config.Config.CookieContent)
                && string.Equals(_config.Config.LegacyCookiePlatform, platform.Id, StringComparison.Ordinal)))
            attempts.Add(new(CookieSourceKind.LegacyScoped, platform));

        var successes = _health.Snapshot()
            .Where(item => item.PlatformId == platform.Id && item.LastSuccessUtc is not null)
            .ToDictionary(item => item.SourceId, item => item.LastSuccessUtc!.Value, StringComparer.Ordinal);
        attempts.AddRange(_profiles.Discover()
            .OrderByDescending(profile => successes.GetValueOrDefault(profile.StableId))
            .ThenByDescending(profile => profile.LastActivityUtc)
            .Select(profile => new CookieAttempt(CookieSourceKind.Browser, platform, profile)));
        attempts.Add(new(CookieSourceKind.ManagedSession, platform));
        return attempts;
    }

    public async Task<CookieArgumentLease> AcquireArgumentsAsync(
        CookieAttempt attempt, string url, CancellationToken ct)
    {
        if (attempt.Source == CookieSourceKind.Anonymous) return CookieArgumentLease.Empty;
        if (attempt.Source == CookieSourceKind.Browser)
            return CookieArgumentLease.Browser(attempt.BrowserProfile!.YtDlpArgument);

        if (attempt.Source == CookieSourceKind.LegacyScoped)
        {
            var content = await _vault.LoadAsync(attempt.Platform.Id, ct)
                ?? _config.Config.CookieContent;
            var host = new Uri(url).Host;
            return CookieArgumentLease.File(await CookieFileLease.CreateLegacyAsync(
                content, attempt.Platform, host, _temporaryDirectory, ct));
        }

        var request = _managedRequests.GetOrAdd(
            attempt.Platform.Id,
            _ => _managedLogin.GetCookiesAsync(attempt.Platform, CancellationToken.None));
        var cookies = await request.WaitAsync(ct);
        if (cookies.Count == 0) return CookieArgumentLease.Empty;
        return CookieArgumentLease.File(await CookieFileLease.CreateCookiesAsync(
            cookies, attempt.Platform, _temporaryDirectory, ct));
    }

    public async Task<CookieFailure> ClassifyAndRecordFailureAsync(
        CookieAttempt attempt, IEnumerable<string> lines, CancellationToken ct)
    {
        var failure = CookieFailureClassifier.Classify(attempt.Platform.Id, lines);
        await _health.RecordFailureAsync(
            attempt.Platform.Id, attempt.Source, attempt.BrowserProfile, failure.Category, ct);
        if (attempt.Source == CookieSourceKind.ManagedSession)
            _managedRequests.TryRemove(attempt.Platform.Id, out _);
        return failure;
    }

    public Task RecordSuccessAsync(CookieAttempt attempt, CancellationToken ct)
        => _health.RecordSuccessAsync(
            attempt.Platform.Id, attempt.Source, attempt.BrowserProfile, ct);
}
```

Add constructor fields for the services referenced above and validate them with `ArgumentNullException.ThrowIfNull`. Managed requests remain cached after success so later tasks reuse the session; a managed authentication failure removes the cached task so re-login is possible. Add `SmartCookieEnabled = true` and `LegacyCookiePlatform = ""` to `AppConfig`.

- [ ] **Step 4: Run coordinator tests under concurrency**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter FullyQualifiedName~CookieAcquisitionCoordinatorTests`

Expected: all tests pass and the fake provider receives exactly one concurrent request per platform.

- [ ] **Step 5: Commit the coordinator**

```powershell
git add Services/Cookies/CookieAcquisitionCoordinator.cs Models/AppConfig.cs EasyGet.Tests/CookieAcquisitionCoordinatorTests.cs
git commit -m "feat: 编排智能 Cookie 获取策略"
```

### Task 7: Route metadata, playlists, and downloads through the coordinator

**Files:**
- Modify: `Services/YtDlpService.cs`
- Modify: `App.xaml.cs`
- Modify: `EasyGet.Tests/YtDlpCookieTests.cs`
- Modify: `EasyGet.Tests/YtDlpMetadataTests.cs`
- Modify: `EasyGet.Tests/YtDlpProcessTests.cs`

- [ ] **Step 1: Add failing tests for shared strategy behavior in all three flows**

```csharp
[Fact]
public void Source_UsesCoordinatorForMetadataPlaylistAndDownload()
{
    var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(Path.Combine("Services", "YtDlpService.cs")));
    Assert.Contains("BuildAttemptsAsync(url", source);
    Assert.Contains("AcquireArgumentsAsync(attempt, url", source);
    Assert.Contains("ClassifyAndRecordFailureAsync", source);
    Assert.DoesNotContain("HasBrowserCookies(", source);
    Assert.DoesNotContain("CookieStrategy.BrowserChrome", source);
}

[Fact]
public void BuildDownloadFailureMessage_PreservesOriginalSiteFailureAfterBrowserAccessFailures()
{
    var message = YtDlpService.BuildDownloadFailureMessage(
        "https://youtube.com/watch?v=1",
        ["ERROR: Sign in to confirm your age", "ERROR: Could not copy Chrome cookie database"], 1);
    Assert.Contains("年龄或登录验证", message);
}
```

- [ ] **Step 2: Run the Cookie/metadata/process tests and observe failure**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter "FullyQualifiedName~YtDlpCookieTests|FullyQualifiedName~YtDlpMetadataTests|FullyQualifiedName~YtDlpProcessTests"`

Expected: the new source and message assertions fail.

- [ ] **Step 3: Replace embedded strategy loops with coordinator attempts**

For each process attempt, use this lifetime pattern:

```csharp
foreach (var attempt in await _cookieCoordinator.BuildAttemptsAsync(url, ct))
{
    await using var cookieArguments = await _cookieCoordinator.AcquireArgumentsAsync(attempt, url, ct);
    var args = BuildVideoInfoBaseArgs();
    AddSiteCompatibilityArgs(args, url);
    AddProxyArgs(args);
    args.AddRange(cookieArguments.Arguments);
    args.Add(url);
    var result = await RunProcessAsync(GetYtDlpPath(), args, TimeSpan.FromSeconds(60), ct);
    var firstJson = EnumerateProcessLines(result.StandardOutput)
        .FirstOrDefault(line => line.StartsWith("{", StringComparison.Ordinal));
    if (!string.IsNullOrWhiteSpace(firstJson))
    {
        var value = ParseVideoInfoJson(firstJson, url);
        if (value is null) continue;
        await _cookieCoordinator.RecordSuccessAsync(attempt, ct);
        return value;
    }

    var failure = await _cookieCoordinator.ClassifyAndRecordFailureAsync(
        attempt, EnumerateProcessLines(result.StandardError), ct);
    if (!failure.ShouldTryNextCookieSource) break;
}
```

Apply the same attempt ordering to `GetVideoInfoAsync`, `GetPlaylistUrlsAsync`, and `DownloadAsync`; the managed session is already the final attempt and is reached only when the classifier authorizes Cookie fallback. Pass Cookie arguments into `BuildDownloadArgs` rather than letting it read config. Remove the old enum, `HasBrowserCookies`, permanent `CookieFilePath`, global Cookie cache, and cross-domain `ParsePlainTextCookies` logic. Redact the `--cookies` path and `--cookies-from-browser` profile path from command logging.

- [ ] **Step 4: Register Cookie services and run focused tests**

Add singleton registrations for resolver-independent services, `CookieHealthStore`, and `CookieAcquisitionCoordinator` in `App.xaml.cs`.

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter "FullyQualifiedName~YtDlpCookieTests|FullyQualifiedName~YtDlpMetadataTests|FullyQualifiedName~YtDlpProcessTests"`

Expected: all selected tests pass.

- [ ] **Step 5: Commit yt-dlp integration**

```powershell
git add Services/YtDlpService.cs App.xaml.cs EasyGet.Tests/YtDlpCookieTests.cs EasyGet.Tests/YtDlpMetadataTests.cs EasyGet.Tests/YtDlpProcessTests.cs
git commit -m "refactor: 统一视频解析与下载认证流程"
```

### Task 8: Add the WebView2 managed-login fallback

**Files:**
- Modify: `EasyGet.csproj`
- Create: `Services/Cookies/ManagedLoginSessionService.cs`
- Create: `Views/ManagedLoginWindow.xaml`
- Create: `Views/ManagedLoginWindow.xaml.cs`
- Test: `EasyGet.Tests/ManagedLoginSessionServiceTests.cs`

- [ ] **Step 1: Add the WebView2 dependency and failing provider tests**

Run: `dotnet add EasyGet.csproj package Microsoft.Web.WebView2 --version 1.0.4078.44`

Then add tests using an injected `IManagedLoginWindowFactory`:

```csharp
[Fact]
public async Task GetCookiesAsync_ReusesPersistedCookiesWithoutShowingLoginWindow()
{
    var window = new FakeManagedLoginWindow([new BrowserCookie(".x.com", "/", "auth_token", "value", true, 0)]);
    var service = CreateService(window);
    var cookies = await service.GetCookiesAsync(MediaPlatformResolver.Resolve("https://x.com/a"), CancellationToken.None);
    Assert.Single(cookies);
    Assert.Equal(0, window.VisibleShowCount);
}

[Fact]
public async Task GetCookiesAsync_ShowsOneRealLoginWhenStoredSessionIsEmpty()
{
    var window = new FakeManagedLoginWindow([], [new BrowserCookie(".x.com", "/", "auth_token", "value", true, 0)]);
    var cookies = await CreateService(window).GetCookiesAsync(
        MediaPlatformResolver.Resolve("https://x.com/a"), CancellationToken.None);
    Assert.Single(cookies);
    Assert.Equal(1, window.VisibleShowCount);
}
```

- [ ] **Step 2: Run provider tests and verify missing implementation failure**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter FullyQualifiedName~ManagedLoginSessionServiceTests`

Expected: build failure for managed-login interfaces and types.

- [ ] **Step 3: Implement the service and window abstraction**

```csharp
public sealed class ManagedLoginSessionService : IManagedLoginSessionService
{
    public async Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(MediaPlatformDefinition platform, CancellationToken ct)
    {
        var window = await _windowFactory.CreateAsync(platform, SessionDirectory(platform.Id), ct);
        var stored = await window.ReadCookiesAsync(platform.CookieDomains, ct);
        if (stored.Count > 0) return stored;
        return await window.ShowForLoginAsync(platform.CookieDomains, ct);
    }
}
```

Implement `ManagedLoginWindow` with a WebView2 control, platform title/domain disclosure, “已完成登录，继续” and “取消” buttons. Initialize `CoreWebView2Environment` with `%LocalAppData%/EasyGet/sessions/<platform>`. Hidden initialization may read persisted domain cookies; show the real platform login page only when none exist. Map `CoreWebView2Cookie` values without logging them. Cancellation closes the window on the UI dispatcher.

- [ ] **Step 4: Run managed-login tests and build WPF**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter FullyQualifiedName~ManagedLoginSessionServiceTests`

Run: `dotnet build EasyGet.csproj --configuration Release`

Expected: tests and Release build pass; WebView2 native assets appear in output.

- [ ] **Step 5: Commit managed login**

```powershell
git add EasyGet.csproj Services/Cookies/ManagedLoginSessionService.cs Views/ManagedLoginWindow.xaml Views/ManagedLoginWindow.xaml.cs EasyGet.Tests/ManagedLoginSessionServiceTests.cs
git commit -m "feat: 增加平台托管登录兜底"
```

### Task 9: Migrate legacy Cookie configuration safely

**Files:**
- Modify: `Models/AppConfig.cs`
- Modify: `Services/ConfigService.cs`
- Test: `EasyGet.Tests/ConfigServiceTests.cs`
- Test: `EasyGet.Tests/CookieMigrationTests.cs`

- [ ] **Step 1: Write failing migration tests**

```csharp
[Fact]
public async Task LoadAsync_KeepsLegacyHeaderDisabledUntilItHasPlatformScope()
{
    using var root = new TestDirectory();
    await root.WriteAsync("config.json", """{"cookieContent":"auth=secret","legacyCookiePlatform":""}""");
    var service = new ConfigService(root.DirectoryPath);
    await service.LoadAsync();
    Assert.Equal("auth=secret", service.Config.CookieContent);
    Assert.Equal("", service.Config.LegacyCookiePlatform);
}

[Fact]
public async Task CompleteLegacyMigrationAsync_BackupsConfigAndClearsPlaintext()
{
    using var root = new TestDirectory();
    var service = new ConfigService(root.DirectoryPath);
    service.Config.CookieContent = "auth=secret";
    service.Config.LegacyCookiePlatform = "twitter";
    await service.SaveAsync();
    var vault = new PlatformCookieVault(root.DirectoryPath, new ReversingTestProtector());
    await service.CompleteLegacyCookieMigrationAsync("twitter", vault, CancellationToken.None);
    Assert.Equal("", service.Config.CookieContent);
    Assert.Equal("auth=secret", await vault.LoadAsync("twitter", CancellationToken.None));
    Assert.True(File.Exists(root.Path("config.cookie-migration.backup.bin")));
    Assert.DoesNotContain("auth=secret", await File.ReadAllTextAsync(root.Path("config.json")));
    Assert.DoesNotContain("auth=secret", await File.ReadAllTextAsync(root.Path("config.backup.json")));
}
```

- [ ] **Step 2: Run migration tests and observe failure**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter "FullyQualifiedName~CookieMigrationTests|FullyQualifiedName~ConfigServiceTests"`

Expected: failure for missing migration API and properties.

- [ ] **Step 3: Implement explicit, versioned migration**

Add `ConfigVersion`, `SmartCookieEnabled`, and `LegacyCookiePlatform` to `AppConfig`. Do not guess a platform for an unscoped Header. The coordinator may use it only when `LegacyCookiePlatform` matches the target platform. After a verified successful request, `CompleteLegacyCookieMigrationAsync` must first save the content in `PlatformCookieVault`, DPAPI-encrypt the pre-migration JSON as `config.cookie-migration.backup.bin`, clear `CookieContent`, save the sanitized config, and replace `config.backup.json` with the sanitized JSON. No plaintext backup may retain the secret.

Netscape/JSON input with domain rows may be split automatically because the domain proves scope. Reject rows whose domains do not match any known or current platform.

- [ ] **Step 4: Run config and migration tests**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter "FullyQualifiedName~CookieMigrationTests|FullyQualifiedName~ConfigServiceTests"`

Expected: all selected tests pass and serialized config contains no migrated secret.

- [ ] **Step 5: Commit migration**

```powershell
git add Models/AppConfig.cs Services/ConfigService.cs EasyGet.Tests/ConfigServiceTests.cs EasyGet.Tests/CookieMigrationTests.cs
git commit -m "fix: 安全迁移旧版 Cookie 配置"
```

### Task 10: Expose truthful per-platform authentication health in Settings

**Files:**
- Create: `Models/CookiePlatformStatusItem.cs`
- Modify: `ViewModels/SettingsViewModel.cs`
- Modify: `Views/SettingsView.xaml`
- Modify: `App.xaml.cs`
- Test: `EasyGet.Tests/SettingsViewModelTests.cs`
- Test: `EasyGet.Tests/XamlBindingTests.cs`

- [ ] **Step 1: Write failing ViewModel command and XAML binding tests**

```csharp
[Fact]
public async Task RefreshCookieStatusAsync_ShowsDetectedProfilesAndHealthWithoutSecrets()
{
    var profile = new BrowserProfile("chrome", "Chrome", "Default", @"C:\Profiles\Chrome", DateTime.UtcNow);
    var health = new CookieHealthRecord(
        "youtube", CookieSourceKind.Browser, profile.StableId,
        DateTime.UtcNow, null, 0, CookieFailureCategory.None);
    var vm = CreateCookieSettingsViewModel([profile], [health]);
    await vm.RefreshCookieStatusCommand.ExecuteAsync(null);
    Assert.Contains(vm.CookiePlatformStatuses, item => item.PlatformId == "youtube" && item.IsAvailable);
    Assert.DoesNotContain("secret", vm.CookieStatusSummary, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void SettingsXaml_UsesSmartCookieCommandsAndKeepsManualImportAdvanced()
{
    var xaml = File.ReadAllText(TestRepositoryPaths.GetViewPath("SettingsView.xaml"));
    Assert.Contains("RefreshCookieStatusCommand", xaml);
    Assert.Contains("LoginPlatformCommand", xaml);
    Assert.Contains("ClearPlatformSessionCommand", xaml);
    Assert.Contains("智能登录与 Cookie", xaml);
    Assert.Contains("Expander", xaml);
}

private static SettingsViewModel CreateCookieSettingsViewModel(
    IReadOnlyList<BrowserProfile> profiles,
    IReadOnlyList<CookieHealthRecord> health)
{
    var config = new ConfigService();
    var environment = new EnvironmentService();
    var history = new HistoryService(TestTempPaths.CreateSqliteDatabasePath("cookie-settings"));
    var ytDlp = new YtDlpService(config, environment);
    var manager = new DownloadManager(ytDlp, history, config);
    return new SettingsViewModel(
        config, environment, manager, new TelegramDownloadService(config),
        cookieProfiles: new StaticBrowserProfiles(profiles),
        cookieHealthStore: new StaticCookieHealthStore(health),
        managedLogin: new EmptyManagedLoginSessionService());
}

private sealed class StaticBrowserProfiles(IReadOnlyList<BrowserProfile> profiles)
    : IBrowserProfileDiscoveryService
{
    public IReadOnlyList<BrowserProfile> Discover() => profiles;
}

private sealed class StaticCookieHealthStore(IReadOnlyList<CookieHealthRecord> records)
    : ICookieHealthStore
{
    public IReadOnlyList<CookieHealthRecord> Snapshot() => records;
    public Task RecordSuccessAsync(string p, CookieSourceKind s, BrowserProfile? b, CancellationToken ct) => Task.CompletedTask;
    public Task RecordFailureAsync(string p, CookieSourceKind s, BrowserProfile? b, CookieFailureCategory c, CancellationToken ct) => Task.CompletedTask;
    public Task ClearPlatformAsync(string p, CancellationToken ct) => Task.CompletedTask;
}

private sealed class EmptyManagedLoginSessionService : IManagedLoginSessionService
{
    public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(MediaPlatformDefinition p, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<BrowserCookie>>([]);
    public Task ClearAsync(string platformId, CancellationToken ct) => Task.CompletedTask;
}
```

- [ ] **Step 2: Run settings tests and verify new UI contract fails**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~XamlBindingTests"`

Expected: new commands, properties, and XAML text are missing.

- [ ] **Step 3: Implement the status item and ViewModel commands**

```csharp
public sealed partial class CookiePlatformStatusItem : ObservableObject
{
    public required string PlatformId { get; init; }
    public required string DisplayName { get; init; }
    [ObservableProperty] private string _statusText = "尚未检测";
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _needsLogin;
}
```

Add `SmartCookieEnabled`, `CookieStatusSummary`, `IsRefreshingCookieStatus`, and `ObservableCollection<CookiePlatformStatusItem> CookiePlatformStatuses` to `SettingsViewModel`. Implement `RefreshCookieStatus`, `LoginPlatform`, `ClearPlatformSession`, and `ClearAllManagedSessions` commands. The status source combines browser discovery and health metadata; it never includes Cookie values or full profile paths.

- [ ] **Step 4: Replace the settings card**

Replace the current primary multiline Cookie box with a card containing the smart-mode toggle, refresh action, status summary, and platform rows. Put manual import inside a collapsed `Expander`; require the `LegacyCookiePlatform` selector before saving a Header. Add per-platform login/clear buttons with automation names and disabled states during operations.

- [ ] **Step 5: Run settings, XAML, and theme tests**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~XamlBindingTests|FullyQualifiedName~ThemeStyleTests"`

Expected: all selected tests pass.

- [ ] **Step 6: Commit settings UX**

```powershell
git add Models/CookiePlatformStatusItem.cs ViewModels/SettingsViewModel.cs Views/SettingsView.xaml App.xaml.cs EasyGet.Tests/SettingsViewModelTests.cs EasyGet.Tests/XamlBindingTests.cs
git commit -m "feat: 重构智能登录与 Cookie 设置体验"
```

### Task 11: Make batch queue admission non-blocking and reuse platform authentication

**Files:**
- Modify: `Services/DownloadManager.cs`
- Modify: `ViewModels/BatchDownloadViewModel.cs`
- Modify: `Models/DownloadTask.cs`
- Test: `EasyGet.Tests/DownloadManagerTests.cs`
- Test: `EasyGet.Tests/BatchDownloadViewModelTests.cs`

- [ ] **Step 1: Write a failing non-blocking queue-admission test**

```csharp
[Fact]
public async Task StartBatchDownload_AddsAllTasksBeforeMetadataResolutionCompletes()
{
    var dbPath = TestTempPaths.CreateSqliteDatabasePath("batch-admission");
    using var history = new HistoryService(dbPath);
    var config = new ConfigService();
    var blocker = new BlockingYtDlpDownloadService();
    var manager = new DownloadManager(blocker, history, config);
    var concreteYtDlp = new YtDlpService(config, new EnvironmentService());
    var viewModel = new BatchDownloadViewModel(manager, config, concreteYtDlp)
    {
        UrlsText = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"https://x.com/user/status/{i}"))
    };

    var command = viewModel.StartBatchDownloadCommand.ExecuteAsync(null);
    await blocker.FirstMetadataRequest.WaitAsync(TimeSpan.FromSeconds(1));
    Assert.Equal(20, manager.Tasks.Count);
    blocker.Release();
    await command;
    await manager.WaitForIdleAsync(CancellationToken.None);
}

private sealed class BlockingYtDlpDownloadService : IYtDlpDownloadService
{
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task FirstMetadataRequest => _first.Task;
    public void Release() => _release.TrySetResult();

    public async Task<VideoInfo?> GetVideoInfoAsync(string url, CancellationToken cancellationToken = default)
    {
        _first.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
        return new VideoInfo { Title = url, Platform = "Twitter" };
    }

    public Task DownloadAsync(DownloadTask task, IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null, CancellationToken cancellationToken = default)
    {
        task.Status = DownloadStatus.Completed;
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Run batch tests and observe repeated/unsupported behavior**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter "FullyQualifiedName~BatchDownloadViewModelTests|FullyQualifiedName~DownloadManagerTests"`

Expected: the new test fails because only the first task is present while its metadata call is blocked.

- [ ] **Step 3: Separate queue admission from background metadata resolution**

Add all validated, de-duplicated URLs to the observable queue immediately. Use a bounded metadata-resolution channel with its own limit and keep actual downloads governed by `MaxConcurrentDownloads`. Add `Authentication` to the task phase/status text without introducing a new terminal status. Await the coordinator's shared platform request from each worker; Task 6's coalescing test proves only one WebView request is made for 20 concurrent same-platform acquisitions.

Expose `DownloadManager.WaitForIdleAsync` for deterministic tests and shutdown. Propagate cancellation to queued metadata workers. Do not perform 20 sequential 60-second `GetVideoInfoAsync` calls on the UI command.

- [ ] **Step 4: Run batch and manager tests**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --filter "FullyQualifiedName~BatchDownloadViewModelTests|FullyQualifiedName~DownloadManagerTests"`

Expected: all selected tests pass; 20 same-platform tasks trigger one managed authentication request.

- [ ] **Step 5: Commit batch authentication sharing**

```powershell
git add Services/DownloadManager.cs ViewModels/BatchDownloadViewModel.cs Models/DownloadTask.cs EasyGet.Tests/DownloadManagerTests.cs EasyGet.Tests/BatchDownloadViewModelTests.cs
git commit -m "perf: 批量任务共享平台认证结果"
```

### Task 12: Final security, UI, and release verification

**Files:**
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-07-14-smart-cookie-orchestration-design.md` only if verified implementation details differ
- Create: `docs/smart-cookie-manual-acceptance.md`
- Modify: `.github/workflows/release.yml` only if WebView2 runtime assets are not packaged by current publish steps

- [ ] **Step 1: Add a source-level secret regression test**

```csharp
[Fact]
public void CookieImplementation_DoesNotLogSecretsOrUsePermanentGlobalCookieFile()
{
    var source = Directory.GetFiles(TestRepositoryPaths.Root, "*.cs", SearchOption.AllDirectories)
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
        .Select(File.ReadAllText)
        .Aggregate(new StringBuilder(), (builder, text) => builder.AppendLine(text)).ToString();
    Assert.DoesNotContain("[yt-dlp] args: {string.Join", source, StringComparison.Ordinal);
    Assert.DoesNotContain("EasyGet\", \"cookies.txt", source, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Document manual acceptance cases with no credentials**

Create `docs/smart-cookie-manual-acceptance.md` with checkboxes and result columns for: public YouTube/Bilibili, logged-in YouTube/X/Instagram, Douyin short link, Chrome/Edge/Firefox profile discovery, browser-open database lock, expired session, canceled login, two platforms in parallel, and 20 same-platform batch URLs. Record dates and sanitized outcomes only.

- [ ] **Step 3: Run the complete automated verification**

Run: `dotnet test EasyGet.Tests/EasyGet.Tests.csproj --configuration Release --verbosity minimal`

Expected: zero failures; only tests explicitly marked as live/manual may be skipped.

Run: `dotnet build EasyGet.csproj --configuration Release --no-restore`

Expected: build succeeds with zero errors.

Run: `git diff --check`

Expected: no whitespace errors.

- [ ] **Step 4: Publish and inspect packaged WebView2 assets**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish-win-x64.ps1 -SkipZip`

Expected: `artifacts/publish/win-x64/EasyGet.exe` launches and required WebView2 loader/runtime files are present. If the machine lacks WebView2 Runtime, the UI must show an actionable installation message instead of crashing.

- [ ] **Step 5: Render and inspect settings/login UI**

Capture settings and managed-login windows at 100%, 125%, and 150% scaling. Verify no clipping, keyboard focus reaches login/cancel/clear actions, platform domains are visible, and Cookie values never appear. Store sanitized screenshots under `docs/screenshots/smart-cookie/`.

- [ ] **Step 6: Update README and commit the verified feature**

Document supported browsers, automatic fallback order, privacy boundaries, first-login behavior, and local session directories.

```powershell
git add README.md docs/smart-cookie-manual-acceptance.md docs/screenshots/smart-cookie EasyGet.Tests
git commit -m "docs: 完善智能 Cookie 验收与隐私说明"
git push origin codex/smart-cookie
```

- [ ] **Step 7: Merge the verified branch to main and push**

From the primary worktree:

```powershell
git merge --no-ff codex/smart-cookie -m "合并智能 Cookie 自动获取与登录体验优化"
git push origin main
```

## Plan self-review result

- Spec coverage: platform/domain resolution is Task 1; browser/profile discovery is Task 2; failure semantics are Task 3; scoped files, DPAPI storage, and crash cleanup are Task 4; health metadata is Task 5; ordering and same-platform request coalescing are Task 6; metadata/playlist/download integration and log redaction are Task 7; managed WebView2 login is Task 8; plaintext migration is Task 9; settings UX is Task 10; non-blocking batch admission is Task 11; complete automation, packaging, rendering, and real-site acceptance are Task 12.
- Placeholder scan: the plan contains no deferred method bodies, unspecified error-handling steps, or unresolved decision markers. Concrete site patterns, command lines, file paths, public interfaces, expected failures, and expected passing checks are named in the relevant tasks.
- Type consistency: `MediaPlatformDefinition`, `BrowserProfile`, `CookieFailure`, `BrowserCookie`, `CookieSourceKind`, `CookieAttempt`, `CookieArgumentLease`, `ICookieHealthStore`, and `IManagedLoginSessionService` are introduced before downstream use. Production implementations depend on interfaces that tests replace with local fakes.
