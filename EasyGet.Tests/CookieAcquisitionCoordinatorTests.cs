using EasyGet.Services;
using EasyGet.Services.Cookies;
using Xunit;

namespace EasyGet.Tests;

public sealed class CookieAcquisitionCoordinatorTests
{
    [Fact]
    public async Task BuildAttemptsAsync_UsesAnonymousThenScopedManualBrowsersAndManagedSession()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            platformId: "twitter",
            manualCookie: "auth_token=secret",
            profiles:
            [
                new BrowserProfile(
                    "chrome",
                    "Chrome",
                    "Default",
                    @"C:\Profiles\Chrome",
                    DateTime.UtcNow),
                new BrowserProfile(
                    "firefox",
                    "Firefox",
                    "default-release",
                    @"C:\Profiles\Firefox",
                    DateTime.UtcNow.AddMinutes(-1))
            ]);

        var attempts = await fixture.Coordinator.BuildAttemptsAsync(
            "https://x.com/user/status/1",
            CancellationToken.None);

        Assert.Equal(
            [
                CookieSourceKind.Anonymous,
                CookieSourceKind.LegacyScoped,
                CookieSourceKind.Browser,
                CookieSourceKind.Browser,
                CookieSourceKind.ManagedSession
            ],
            attempts.Select(attempt => attempt.Source));
    }

    [Fact]
    public async Task BuildAttemptsAsync_WhenSmartModeIsDisabledUsesAnonymousOnly()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            profiles:
            [
                new BrowserProfile(
                    "chrome",
                    "Chrome",
                    "Default",
                    @"C:\Profiles\Chrome",
                    DateTime.UtcNow)
            ]);
        fixture.Config.Config.SmartCookieEnabled = false;

        var attempts = await fixture.Coordinator.BuildAttemptsAsync(
            "https://youtube.com/watch?v=1",
            CancellationToken.None);

        Assert.Equal(
            [CookieSourceKind.Anonymous],
            attempts.Select(attempt => attempt.Source));
    }

    [Fact]
    public async Task BuildAttemptsAsync_UsesLegacyConfigOnlyAfterItsPlatformIsExplicitlyScoped()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync();
        fixture.Config.Config.CookieContent = "auth_token=secret";

        var unscoped = await fixture.Coordinator.BuildAttemptsAsync(
            "https://x.com/user/status/1",
            CancellationToken.None);
        fixture.Config.Config.LegacyCookiePlatform = "twitter";
        var scoped = await fixture.Coordinator.BuildAttemptsAsync(
            "https://x.com/user/status/1",
            CancellationToken.None);

        Assert.DoesNotContain(
            unscoped,
            attempt => attempt.Source == CookieSourceKind.LegacyScoped);
        Assert.Contains(
            scoped,
            attempt => attempt.Source == CookieSourceKind.LegacyScoped);
    }

    [Fact]
    public async Task BuildAttemptsAsync_UsesDomainScopedLegacyFileWithoutManualPlatformGuessing()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync();
        fixture.Config.Config.CookieContent = """
            # Netscape HTTP Cookie File
            .x.com	TRUE	/	TRUE	0	auth_token	secret
            """;
        fixture.Config.Config.LegacyCookiePlatform = "";

        var twitter = await fixture.Coordinator.BuildAttemptsAsync(
            "https://x.com/user/status/1",
            CancellationToken.None);
        var youtube = await fixture.Coordinator.BuildAttemptsAsync(
            "https://youtube.com/watch?v=1",
            CancellationToken.None);

        Assert.Contains(
            twitter,
            attempt => attempt.Source == CookieSourceKind.LegacyScoped);
        Assert.DoesNotContain(
            youtube,
            attempt => attempt.Source == CookieSourceKind.LegacyScoped);
    }

    [Fact]
    public async Task BuildAttemptsAsync_PrioritizesBrowserThatPreviouslySucceededForPlatform()
    {
        var recentlyActive = new BrowserProfile(
            "chrome",
            "Chrome",
            "Default",
            @"C:\Profiles\Recent",
            DateTime.UtcNow);
        var previouslySuccessful = new BrowserProfile(
            "firefox",
            "Firefox",
            "default-release",
            @"C:\Profiles\Successful",
            DateTime.UtcNow.AddDays(-1));
        await using var fixture = await CoordinatorFixture.CreateAsync(
            profiles: [recentlyActive, previouslySuccessful],
            healthRecords:
            [
                new CookieHealthRecord(
                    "youtube",
                    CookieSourceKind.Browser,
                    previouslySuccessful.StableId,
                    DateTime.UtcNow.AddMinutes(-5),
                    null,
                    0,
                    CookieFailureCategory.None)
            ]);

        var attempts = await fixture.Coordinator.BuildAttemptsAsync(
            "https://youtube.com/watch?v=1",
            CancellationToken.None);

        var browsers = attempts
            .Where(attempt => attempt.Source == CookieSourceKind.Browser)
            .Select(attempt => attempt.BrowserProfile!)
            .ToArray();
        Assert.Equal([previouslySuccessful, recentlyActive], browsers);
    }

    [Fact]
    public async Task BuildAttemptsAsync_DoesNotPrioritizeAProfileThatFailedAfterItsLastSuccess()
    {
        var recentlyActive = new BrowserProfile(
            "chrome",
            "Chrome",
            "Default",
            @"C:\Profiles\Recent",
            DateTime.UtcNow);
        var staleFailure = new BrowserProfile(
            "firefox",
            "Firefox",
            "default-release",
            @"C:\Profiles\Failed",
            DateTime.UtcNow.AddDays(-1));
        await using var fixture = await CoordinatorFixture.CreateAsync(
            profiles: [staleFailure, recentlyActive],
            healthRecords:
            [
                new CookieHealthRecord(
                    "youtube",
                    CookieSourceKind.Browser,
                    staleFailure.StableId,
                    DateTime.UtcNow.AddHours(-1),
                    DateTime.UtcNow,
                    1,
                    CookieFailureCategory.CookieExpired)
            ]);

        var attempts = await fixture.Coordinator.BuildAttemptsAsync(
            "https://youtube.com/watch?v=1",
            CancellationToken.None);

        var firstBrowser = attempts.First(attempt => attempt.Source == CookieSourceKind.Browser);
        Assert.Equal(recentlyActive, firstBrowser.BrowserProfile);
    }

    [Fact]
    public async Task AcquireArgumentsAsync_AnonymousAttemptReturnsNoArguments()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync();
        var attempt = new CookieAttempt(
            CookieSourceKind.Anonymous,
            MediaPlatformResolver.Resolve("https://youtube.com/watch?v=1"));

        await using var lease = await fixture.Coordinator.AcquireArgumentsAsync(
            attempt,
            "https://youtube.com/watch?v=1",
            CancellationToken.None);

        Assert.Empty(lease.Arguments);
    }

    [Fact]
    public async Task AcquireArgumentsAsync_BrowserAttemptUsesSelectedProfile()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync();
        var profile = new BrowserProfile(
            "edge",
            "Edge",
            "Profile 2",
            @"C:\Profiles\Edge",
            DateTime.UtcNow);
        var attempt = new CookieAttempt(
            CookieSourceKind.Browser,
            MediaPlatformResolver.Resolve("https://youtube.com/watch?v=1"),
            profile);

        await using var lease = await fixture.Coordinator.AcquireArgumentsAsync(
            attempt,
            "https://youtube.com/watch?v=1",
            CancellationToken.None);

        Assert.Equal(["--cookies-from-browser", profile.YtDlpArgument], lease.Arguments);
        Assert.False(Directory.Exists(fixture.Root.Path("temp-cookies")));
    }

    [Fact]
    public async Task AcquireArgumentsAsync_ScopedManualAttemptPrefersVaultAndOwnsTemporaryFile()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            platformId: "twitter",
            manualCookie: "auth_token=vault-secret");
        fixture.Config.Config.CookieContent = "auth_token=legacy-secret";
        fixture.Config.Config.LegacyCookiePlatform = "twitter";
        var attempt = new CookieAttempt(
            CookieSourceKind.LegacyScoped,
            MediaPlatformResolver.Resolve("https://x.com/user/status/1"));
        string cookiePath;

        await using (var lease = await fixture.Coordinator.AcquireArgumentsAsync(
                         attempt,
                         "https://x.com/user/status/1",
                         CancellationToken.None))
        {
            Assert.Equal("--cookies", lease.Arguments[0]);
            cookiePath = lease.Arguments[1];
            var content = await File.ReadAllTextAsync(cookiePath);
            Assert.Contains(".x.com\tTRUE\t/\tTRUE\t0\tauth_token\tvault-secret", content);
            Assert.DoesNotContain("legacy-secret", content, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(cookiePath));
    }

    [Fact]
    public async Task AcquireArgumentsAsync_UsesLegacyConfigOnlyWhenPlatformMatches()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync();
        fixture.Config.Config.CookieContent = "auth_token=legacy-secret";
        fixture.Config.Config.LegacyCookiePlatform = "twitter";
        var twitter = new CookieAttempt(
            CookieSourceKind.LegacyScoped,
            MediaPlatformResolver.Resolve("https://x.com/user/status/1"));
        var youtube = new CookieAttempt(
            CookieSourceKind.LegacyScoped,
            MediaPlatformResolver.Resolve("https://youtube.com/watch?v=1"));

        await using var twitterLease = await fixture.Coordinator.AcquireArgumentsAsync(
            twitter,
            "https://x.com/user/status/1",
            CancellationToken.None);
        await using var youtubeLease = await fixture.Coordinator.AcquireArgumentsAsync(
            youtube,
            "https://youtube.com/watch?v=1",
            CancellationToken.None);

        Assert.Equal("--cookies", twitterLease.Arguments[0]);
        Assert.Contains(
            "legacy-secret",
            await File.ReadAllTextAsync(twitterLease.Arguments[1]),
            StringComparison.Ordinal);
        Assert.Empty(youtubeLease.Arguments);
    }

    [Fact]
    public async Task AcquireArgumentsAsync_CoalescesConcurrentManagedSessionRequests()
    {
        var provider = new CountingManagedLoginSessionService(
            [new BrowserCookie(".x.com", "/", "auth_token", "managed-secret", true, 0)],
            TimeSpan.FromMilliseconds(50));
        await using var fixture = await CoordinatorFixture.CreateAsync(managedLogin: provider);
        var attempt = new CookieAttempt(
            CookieSourceKind.ManagedSession,
            MediaPlatformResolver.Resolve("https://x.com/user/status/1"));

        var requests = Enumerable.Range(0, 20)
            .Select(_ => fixture.Coordinator.AcquireArgumentsAsync(
                attempt,
                "https://x.com/user/status/1",
                CancellationToken.None));
        var leases = await Task.WhenAll(requests);
        try
        {
            Assert.Equal(1, provider.CallCount);
            Assert.All(
                leases,
                lease => Assert.Equal("--cookies", lease.Arguments[0]));
            Assert.Equal(
                20,
                leases.Select(lease => lease.Arguments[1]).Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            foreach (var lease in leases)
                await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcquireArgumentsAsync_ConsumerCancellationDoesNotRestartSharedLogin()
    {
        var provider = new BlockingManagedLoginSessionService(
            [new BrowserCookie(".x.com", "/", "auth_token", "managed-secret", true, 0)]);
        await using var fixture = await CoordinatorFixture.CreateAsync(managedLogin: provider);
        var attempt = new CookieAttempt(
            CookieSourceKind.ManagedSession,
            MediaPlatformResolver.Resolve("https://x.com/user/status/1"));
        using var cancellation = new CancellationTokenSource();

        var canceledRequest = fixture.Coordinator.AcquireArgumentsAsync(
            attempt,
            "https://x.com/user/status/1",
            cancellation.Token);
        await provider.Started.WaitAsync(TimeSpan.FromSeconds(1));
        var waitingRequest = fixture.Coordinator.AcquireArgumentsAsync(
            attempt,
            "https://x.com/user/status/1",
            CancellationToken.None);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRequest);
        var laterRequest = fixture.Coordinator.AcquireArgumentsAsync(
            attempt,
            "https://x.com/user/status/1",
            CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        provider.Release();
        await using var waitingLease = await waitingRequest;
        await using var laterLease = await laterRequest;
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task ClassifyAndRecordFailureAsync_StoresNormalizedFailureMetadata()
    {
        var health = new RecordingCookieHealthStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(healthStore: health);
        var profile = new BrowserProfile(
            "chrome",
            "Chrome",
            "Default",
            @"C:\Profiles\Chrome",
            DateTime.UtcNow);
        var attempt = new CookieAttempt(
            CookieSourceKind.Browser,
            MediaPlatformResolver.Resolve("https://youtube.com/watch?v=1"),
            profile);

        var failure = await fixture.Coordinator.ClassifyAndRecordFailureAsync(
            attempt,
            ["ERROR: Sign in to confirm your age"],
            CancellationToken.None);

        Assert.Equal(CookieFailureCategory.AuthenticationRequired, failure.Category);
        var recorded = Assert.Single(health.Failures);
        Assert.Equal("youtube", recorded.PlatformId);
        Assert.Equal(CookieSourceKind.Browser, recorded.Source);
        Assert.Equal(profile, recorded.Profile);
        Assert.Equal(CookieFailureCategory.AuthenticationRequired, recorded.Category);
    }

    [Fact]
    public async Task RecordSuccessAsync_StoresSuccessfulStrategyMetadata()
    {
        var health = new RecordingCookieHealthStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(healthStore: health);
        var attempt = new CookieAttempt(
            CookieSourceKind.ManagedSession,
            MediaPlatformResolver.Resolve("https://x.com/user/status/1"));

        await fixture.Coordinator.RecordSuccessAsync(attempt, CancellationToken.None);

        var recorded = Assert.Single(health.Successes);
        Assert.Equal("twitter", recorded.PlatformId);
        Assert.Equal(CookieSourceKind.ManagedSession, recorded.Source);
        Assert.Null(recorded.Profile);
    }

    [Fact]
    public async Task ClassifyAndRecordFailureAsync_ManagedFailureAllowsFreshLoginRequest()
    {
        var provider = new CountingManagedLoginSessionService(
            [new BrowserCookie(".x.com", "/", "auth_token", "managed-secret", true, 0)],
            TimeSpan.Zero);
        await using var fixture = await CoordinatorFixture.CreateAsync(managedLogin: provider);
        var attempt = new CookieAttempt(
            CookieSourceKind.ManagedSession,
            MediaPlatformResolver.Resolve("https://x.com/user/status/1"));

        await using (var first = await fixture.Coordinator.AcquireArgumentsAsync(
                         attempt,
                         "https://x.com/user/status/1",
                         CancellationToken.None))
        {
            Assert.Equal(1, provider.CallCount);
        }
        await fixture.Coordinator.ClassifyAndRecordFailureAsync(
            attempt,
            ["ERROR: login required"],
            CancellationToken.None);
        await using var second = await fixture.Coordinator.AcquireArgumentsAsync(
            attempt,
            "https://x.com/user/status/1",
            CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task AcquireArgumentsAsync_RejectsAttemptWhosePlatformDoesNotMatchUrl()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            platformId: "twitter",
            manualCookie: "auth_token=secret");
        var twitterAttempt = new CookieAttempt(
            CookieSourceKind.LegacyScoped,
            MediaPlatformResolver.Resolve("https://x.com/user/status/1"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Coordinator.AcquireArgumentsAsync(
                twitterAttempt,
                "https://youtube.com/watch?v=1",
                CancellationToken.None));

        Assert.False(Directory.Exists(fixture.Root.Path("temp-cookies")));
    }

    [Fact]
    public async Task ClassifyAndRecordFailureAsync_HealthWriteFailureDoesNotBlockRetry()
    {
        var provider = new CountingManagedLoginSessionService(
            [new BrowserCookie(".x.com", "/", "auth_token", "managed-secret", true, 0)],
            TimeSpan.Zero);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            managedLogin: provider,
            healthStore: new ThrowingFailureHealthStore());
        var attempt = new CookieAttempt(
            CookieSourceKind.ManagedSession,
            MediaPlatformResolver.Resolve("https://x.com/user/status/1"));

        await using (var first = await fixture.Coordinator.AcquireArgumentsAsync(
                         attempt,
                         "https://x.com/user/status/1",
                         CancellationToken.None))
        {
            Assert.Equal(1, provider.CallCount);
        }
        var failure = await fixture.Coordinator.ClassifyAndRecordFailureAsync(
            attempt,
            ["ERROR: login required"],
            CancellationToken.None);
        await using var second = await fixture.Coordinator.AcquireArgumentsAsync(
            attempt,
            "https://x.com/user/status/1",
            CancellationToken.None);

        Assert.True(failure.ShouldTryNextCookieSource);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task AcquireArgumentsAsync_DoesNotCoalesceDifferentGenericWebsiteScopes()
    {
        var provider = new PerPlatformManagedLoginSessionService();
        await using var fixture = await CoordinatorFixture.CreateAsync(managedLogin: provider);
        const string firstUrl = "https://media.example.org/watch/1";
        const string secondUrl = "https://video.example.net/watch/2";
        var firstAttempt = new CookieAttempt(
            CookieSourceKind.ManagedSession,
            MediaPlatformResolver.Resolve(firstUrl));
        var secondAttempt = new CookieAttempt(
            CookieSourceKind.ManagedSession,
            MediaPlatformResolver.Resolve(secondUrl));

        var leases = await Task.WhenAll(
            fixture.Coordinator.AcquireArgumentsAsync(
                firstAttempt,
                firstUrl,
                CancellationToken.None),
            fixture.Coordinator.AcquireArgumentsAsync(
                secondAttempt,
                secondUrl,
                CancellationToken.None));
        try
        {
            Assert.Equal(2, provider.CallCount);
        }
        finally
        {
            foreach (var lease in leases)
                await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task ClearPlatformSessionAsync_InvalidatesManagedCookieCache()
    {
        var provider = new CountingManagedLoginSessionService(
            [new BrowserCookie(".x.com", "/", "auth_token", "managed-secret", true, 0)],
            TimeSpan.Zero);
        await using var fixture = await CoordinatorFixture.CreateAsync(managedLogin: provider);
        var platform = MediaPlatformResolver.Resolve("https://x.com/user/status/1");
        var attempt = new CookieAttempt(CookieSourceKind.ManagedSession, platform);
        await using (var first = await fixture.Coordinator.AcquireArgumentsAsync(
                         attempt,
                         "https://x.com/user/status/1",
                         CancellationToken.None))
        {
            Assert.Equal(1, provider.CallCount);
        }

        await fixture.Coordinator.ClearPlatformSessionAsync(
            platform,
            CancellationToken.None);
        await using var second = await fixture.Coordinator.AcquireArgumentsAsync(
            attempt,
            "https://x.com/user/status/1",
            CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
        Assert.Equal(["twitter"], provider.ClearedPlatformIds);
    }

    [Fact]
    public async Task BuildAttemptsAsync_KeepsGenericWebsiteVaultsSeparate()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync();
        const string firstUrl = "https://media.example.org/watch/1";
        const string secondUrl = "https://video.example.net/watch/2";
        var firstPlatform = MediaPlatformResolver.Resolve(firstUrl);
        await fixture.Vault.SaveAsync(
            firstPlatform.StorageKey,
            "session=first-site",
            CancellationToken.None);

        var firstAttempts = await fixture.Coordinator.BuildAttemptsAsync(
            firstUrl,
            CancellationToken.None);
        var secondAttempts = await fixture.Coordinator.BuildAttemptsAsync(
            secondUrl,
            CancellationToken.None);

        Assert.Contains(
            firstAttempts,
            attempt => attempt.Source == CookieSourceKind.LegacyScoped);
        Assert.DoesNotContain(
            secondAttempts,
            attempt => attempt.Source == CookieSourceKind.LegacyScoped);
    }

    [Fact]
    public async Task RecordSuccessAsync_UsesDomainScopedHealthKeyForGenericWebsite()
    {
        var health = new RecordingCookieHealthStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(healthStore: health);
        const string url = "https://media.example.org/watch/1";
        var platform = MediaPlatformResolver.Resolve(url);
        var attempt = new CookieAttempt(CookieSourceKind.Anonymous, platform);

        await fixture.Coordinator.RecordSuccessAsync(attempt, CancellationToken.None);

        var recorded = Assert.Single(health.Successes);
        Assert.Equal(platform.StorageKey, recorded.PlatformId);
        Assert.NotEqual(platform.Id, recorded.PlatformId);
    }

    [Fact]
    public async Task RecordSuccessAsync_HealthWriteFailureDoesNotDiscardSuccessfulResult()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            healthStore: new ThrowingSuccessHealthStore());
        var attempt = new CookieAttempt(
            CookieSourceKind.Anonymous,
            MediaPlatformResolver.Resolve("https://youtube.com/watch?v=1"));

        await fixture.Coordinator.RecordSuccessAsync(attempt, CancellationToken.None);
    }

    [Fact]
    public async Task RecordSuccessAsync_MigratesVerifiedLegacyCookieOutOfPlaintextConfig()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync();
        fixture.Config.Config.CookieContent = "auth_token=verified-secret";
        fixture.Config.Config.LegacyCookiePlatform = "twitter";
        var attempt = new CookieAttempt(
            CookieSourceKind.LegacyScoped,
            MediaPlatformResolver.Resolve("https://x.com/user/status/1"));

        await fixture.Coordinator.RecordSuccessAsync(attempt, CancellationToken.None);

        Assert.Equal("", fixture.Config.Config.CookieContent);
        Assert.Equal(
            "auth_token=verified-secret",
            await fixture.Vault.LoadAsync("twitter", CancellationToken.None));
        Assert.DoesNotContain(
            "verified-secret",
            await File.ReadAllTextAsync(fixture.Root.Path("config.json")),
            StringComparison.Ordinal);
    }

    private sealed class CoordinatorFixture : IAsyncDisposable
    {
        private CoordinatorFixture(
            TestDirectory root,
            ConfigService config,
            PlatformCookieVault vault,
            CookieAcquisitionCoordinator coordinator)
        {
            Root = root;
            Config = config;
            Vault = vault;
            Coordinator = coordinator;
        }

        public TestDirectory Root { get; }
        public ConfigService Config { get; }
        public PlatformCookieVault Vault { get; }
        public CookieAcquisitionCoordinator Coordinator { get; }

        public static async Task<CoordinatorFixture> CreateAsync(
            string? platformId = null,
            string? manualCookie = null,
            IReadOnlyList<BrowserProfile>? profiles = null,
            IReadOnlyList<CookieHealthRecord>? healthRecords = null,
            IManagedLoginSessionService? managedLogin = null,
            ICookieHealthStore? healthStore = null)
        {
            var root = new TestDirectory();
            try
            {
                var config = new ConfigService(root.DirectoryPath, new XorTestProtector());
                config.Config.SmartCookieEnabled = true;
                var vault = new PlatformCookieVault(root.DirectoryPath, new XorTestProtector());
                if (platformId is not null && manualCookie is not null)
                    await vault.SaveAsync(platformId, manualCookie, CancellationToken.None);

                var coordinator = new CookieAcquisitionCoordinator(
                    config,
                    vault,
                    new StaticBrowserProfiles(profiles ?? []),
                    healthStore ?? new InMemoryCookieHealthStore(healthRecords ?? []),
                    managedLogin ?? new EmptyManagedLoginSessionService(),
                    root.Path("temp-cookies"));
                return new CoordinatorFixture(root, config, vault, coordinator);
            }
            catch
            {
                root.Dispose();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            Root.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StaticBrowserProfiles(IReadOnlyList<BrowserProfile> profiles)
        : IBrowserProfileDiscoveryService
    {
        public IReadOnlyList<BrowserProfile> Discover() => profiles;
    }

    private sealed class InMemoryCookieHealthStore(
        IReadOnlyList<CookieHealthRecord> records) : ICookieHealthStore
    {
        public IReadOnlyList<CookieHealthRecord> Snapshot() => records;

        public Task RecordSuccessAsync(
            string platformId,
            CookieSourceKind source,
            BrowserProfile? profile,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RecordFailureAsync(
            string platformId,
            CookieSourceKind source,
            BrowserProfile? profile,
            CookieFailureCategory category,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ClearPlatformAsync(string platformId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class RecordingCookieHealthStore : ICookieHealthStore
    {
        public List<RecordedFailure> Failures { get; } = [];
        public List<RecordedSuccess> Successes { get; } = [];

        public IReadOnlyList<CookieHealthRecord> Snapshot() => [];

        public Task RecordSuccessAsync(
            string platformId,
            CookieSourceKind source,
            BrowserProfile? profile,
            CancellationToken cancellationToken)
        {
            Successes.Add(new RecordedSuccess(platformId, source, profile));
            return Task.CompletedTask;
        }

        public Task RecordFailureAsync(
            string platformId,
            CookieSourceKind source,
            BrowserProfile? profile,
            CookieFailureCategory category,
            CancellationToken cancellationToken)
        {
            Failures.Add(new RecordedFailure(platformId, source, profile, category));
            return Task.CompletedTask;
        }

        public Task ClearPlatformAsync(string platformId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class ThrowingFailureHealthStore : ICookieHealthStore
    {
        public IReadOnlyList<CookieHealthRecord> Snapshot() => [];

        public Task RecordSuccessAsync(
            string platformId,
            CookieSourceKind source,
            BrowserProfile? profile,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RecordFailureAsync(
            string platformId,
            CookieSourceKind source,
            BrowserProfile? profile,
            CookieFailureCategory category,
            CancellationToken cancellationToken)
            => Task.FromException(new IOException("health store unavailable"));

        public Task ClearPlatformAsync(string platformId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class ThrowingSuccessHealthStore : ICookieHealthStore
    {
        public IReadOnlyList<CookieHealthRecord> Snapshot() => [];

        public Task RecordSuccessAsync(
            string platformId,
            CookieSourceKind source,
            BrowserProfile? profile,
            CancellationToken cancellationToken)
            => Task.FromException(new IOException("health store unavailable"));

        public Task RecordFailureAsync(
            string platformId,
            CookieSourceKind source,
            BrowserProfile? profile,
            CookieFailureCategory category,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ClearPlatformAsync(string platformId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed record RecordedFailure(
        string PlatformId,
        CookieSourceKind Source,
        BrowserProfile? Profile,
        CookieFailureCategory Category);

    private sealed record RecordedSuccess(
        string PlatformId,
        CookieSourceKind Source,
        BrowserProfile? Profile);

    private sealed class EmptyManagedLoginSessionService : IManagedLoginSessionService
    {
        public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
            MediaPlatformDefinition platform,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<BrowserCookie>>([]);

        public Task ClearAsync(string platformId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class CountingManagedLoginSessionService(
        IReadOnlyList<BrowserCookie> cookies,
        TimeSpan delay) : IManagedLoginSessionService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public List<string> ClearedPlatformIds { get; } = [];

        public async Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
            MediaPlatformDefinition platform,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            await Task.Delay(delay, cancellationToken);
            return cookies;
        }

        public Task ClearAsync(string platformId, CancellationToken cancellationToken)
        {
            ClearedPlatformIds.Add(platformId);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingManagedLoginSessionService(
        IReadOnlyList<BrowserCookie> cookies) : IManagedLoginSessionService
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public Task Started => _started.Task;

        public void Release() => _release.TrySetResult();

        public async Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
            MediaPlatformDefinition platform,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return cookies;
        }

        public Task ClearAsync(string platformId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class PerPlatformManagedLoginSessionService : IManagedLoginSessionService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
            MediaPlatformDefinition platform,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            await Task.Delay(50, cancellationToken);
            var domain = platform.CookieDomains.Single();
            return [new BrowserCookie($".{domain}", "/", "session", "value", true, 0)];
        }

        public Task ClearAsync(string platformId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class XorTestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => Transform(plaintext);
        public byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext);

        private static byte[] Transform(byte[] input)
            => input.Select(value => (byte)(value ^ 0xA5)).ToArray();
    }
}
