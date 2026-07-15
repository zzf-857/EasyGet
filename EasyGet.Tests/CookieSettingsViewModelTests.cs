using EasyGet.Services;
using EasyGet.Services.Cookies;
using EasyGet.ViewModels;
using Xunit;

namespace EasyGet.Tests;

public sealed class CookieSettingsViewModelTests
{
    [Fact]
    public async Task RefreshCookieStatusAsync_ShowsHealthWithoutSecretsOrProfilePaths()
    {
        using var root = new TestDirectory();
        var config = new ConfigService(root.Path("config"));
        using var history = new HistoryService(root.Path("history.db"));
        var environment = new EnvironmentService();
        var ytDlp = new YtDlpService(config, environment);
        var manager = new DownloadManager(ytDlp, history, config);
        var profile = new BrowserProfile(
            "chrome",
            "Chrome",
            "Default",
            @"C:\Users\me\Secret Profile",
            DateTime.UtcNow);
        var health = new CookieHealthRecord(
            "youtube",
            CookieSourceKind.Browser,
            profile.StableId,
            DateTime.UtcNow,
            null,
            0,
            CookieFailureCategory.None);
        var viewModel = new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            cookieProfiles: new StaticBrowserProfiles([profile]),
            cookieHealthStore: new StaticCookieHealthStore([health]),
            managedLogin: new FakeManagedLoginSessionService());

        await viewModel.RefreshCookieStatusCommand.ExecuteAsync(null);

        Assert.Contains(
            viewModel.CookiePlatformStatuses,
            item => item.PlatformId == "youtube" && item.IsAvailable);
        Assert.Contains("1 个浏览器配置", viewModel.CookieStatusSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Profile", viewModel.CookieStatusSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", viewModel.CookieStatusSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            viewModel.CookiePlatformStatuses,
            item => item.StatusText.Contains("Secret Profile", StringComparison.Ordinal));
        var unverified = viewModel.CookiePlatformStatuses.Single(item => item.PlatformId == "twitter");
        Assert.False(unverified.IsAvailable);
        Assert.False(unverified.NeedsLogin);
        Assert.Contains("自动尝试", unverified.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshCookieStatusAsync_DoesNotExposeDiscoveryExceptionDetails()
    {
        using var root = new TestDirectory();
        var config = new ConfigService(root.Path("config"));
        using var history = new HistoryService(root.Path("history.db"));
        var environment = new EnvironmentService();
        var manager = new DownloadManager(
            new YtDlpService(config, environment),
            history,
            config);
        var viewModel = new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            cookieProfiles: new ThrowingBrowserProfiles(
                @"无法读取 C:\Users\me\Secret Profile\Cookies，SID=secret-value"),
            cookieHealthStore: new StaticCookieHealthStore([]),
            managedLogin: new FakeManagedLoginSessionService());

        await viewModel.RefreshCookieStatusCommand.ExecuteAsync(null);

        Assert.Contains("检测失败", viewModel.CookieStatusSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Profile", viewModel.CookieStatusSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", viewModel.CookieStatusSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginPlatformAsync_RecordsManagedSessionSuccessAndUpdatesStatus()
    {
        using var root = new TestDirectory();
        var config = new ConfigService(root.Path("config"));
        using var history = new HistoryService(root.Path("history.db"));
        var environment = new EnvironmentService();
        var manager = new DownloadManager(
            new YtDlpService(config, environment),
            history,
            config);
        var health = new RecordingCookieHealthStore();
        var managed = new RecordingManagedLoginSessionService(
            [new BrowserCookie(".youtube.com", "/", "SID", "value", true, 0)]);
        var viewModel = new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            cookieProfiles: new StaticBrowserProfiles([]),
            cookieHealthStore: health,
            managedLogin: managed);
        await viewModel.RefreshCookieStatusCommand.ExecuteAsync(null);
        var item = viewModel.CookiePlatformStatuses.Single(status => status.PlatformId == "youtube");

        await viewModel.LoginPlatformCommand.ExecuteAsync(item);

        Assert.Equal(1, managed.GetCookiesCallCount);
        var success = Assert.Single(health.Successes);
        Assert.Equal("youtube", success.PlatformId);
        Assert.Equal(CookieSourceKind.ManagedSession, success.Source);
        Assert.True(item.IsAvailable);
        Assert.Contains("登录成功", item.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginPlatformAsync_EmptyManagedSessionInvalidatesStaleSuccess()
    {
        using var root = new TestDirectory();
        var config = new ConfigService(root.Path("config"));
        using var history = new HistoryService(root.Path("history.db"));
        var environment = new EnvironmentService();
        var manager = new DownloadManager(
            new YtDlpService(config, environment),
            history,
            config);
        var health = new RecordingCookieHealthStore();
        var viewModel = new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            cookieProfiles: new StaticBrowserProfiles([]),
            cookieHealthStore: health,
            managedLogin: new RecordingManagedLoginSessionService([]));
        await viewModel.RefreshCookieStatusCommand.ExecuteAsync(null);
        var item = viewModel.CookiePlatformStatuses.Single(status => status.PlatformId == "youtube");

        await viewModel.LoginPlatformCommand.ExecuteAsync(item);

        Assert.Empty(health.Successes);
        var failure = Assert.Single(health.Failures);
        Assert.Equal("youtube", failure.PlatformId);
        Assert.Equal(CookieSourceKind.ManagedSession, failure.Source);
        Assert.Equal(CookieFailureCategory.AuthenticationRequired, failure.Category);
        Assert.False(item.IsAvailable);
        Assert.True(item.NeedsLogin);
    }

    [Fact]
    public async Task LoginPlatformAsync_DoesNotExposeManagedLoginExceptionDetails()
    {
        using var root = new TestDirectory();
        var config = new ConfigService(root.Path("config"));
        using var history = new HistoryService(root.Path("history.db"));
        var environment = new EnvironmentService();
        var manager = new DownloadManager(
            new YtDlpService(config, environment),
            history,
            config);
        var viewModel = new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            cookieProfiles: new StaticBrowserProfiles([]),
            cookieHealthStore: new StaticCookieHealthStore([]),
            managedLogin: new ThrowingManagedLoginSessionService(
                @"WebView2 profile C:\Users\me\Secret Session contains SID=secret-value"));
        await viewModel.RefreshCookieStatusCommand.ExecuteAsync(null);
        var item = viewModel.CookiePlatformStatuses.Single(status => status.PlatformId == "youtube");

        await viewModel.LoginPlatformCommand.ExecuteAsync(item);

        Assert.Contains("登录失败", item.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Session", item.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", item.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginPlatformAsync_AllowsDifferentPlatformsToOperateConcurrently()
    {
        using var root = new TestDirectory();
        var config = new ConfigService(root.Path("config"));
        using var history = new HistoryService(root.Path("history.db"));
        var environment = new EnvironmentService();
        var manager = new DownloadManager(
            new YtDlpService(config, environment),
            history,
            config);
        var managed = new ConcurrentManagedLoginSessionService();
        var viewModel = new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            cookieProfiles: new StaticBrowserProfiles([]),
            cookieHealthStore: new StaticCookieHealthStore([]),
            managedLogin: managed);
        await viewModel.RefreshCookieStatusCommand.ExecuteAsync(null);
        var youtube = viewModel.CookiePlatformStatuses.Single(status => status.PlatformId == "youtube");
        var twitter = viewModel.CookiePlatformStatuses.Single(status => status.PlatformId == "twitter");

        var first = viewModel.LoginPlatformCommand.ExecuteAsync(youtube);
        await managed.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = viewModel.LoginPlatformCommand.ExecuteAsync(twitter);
        var concurrentResult = await Task.WhenAny(
            managed.TwoCallsStarted.Task,
            Task.Delay(TimeSpan.FromSeconds(1)));
        managed.Release();
        await Task.WhenAll(first, second);

        Assert.Same(managed.TwoCallsStarted.Task, concurrentResult);
        Assert.Equal(2, managed.GetCookiesCallCount);
    }

    [Fact]
    public async Task ClearPlatformSessionAsync_ClearsCoordinatorCacheAndUpdatesStatus()
    {
        using var root = new TestDirectory();
        var config = new ConfigService(root.Path("config"));
        using var history = new HistoryService(root.Path("history.db"));
        var environment = new EnvironmentService();
        var manager = new DownloadManager(
            new YtDlpService(config, environment),
            history,
            config);
        var profiles = new StaticBrowserProfiles([]);
        var health = new RecordingCookieHealthStore();
        var managed = new RecordingManagedLoginSessionService([]);
        var coordinator = new CookieAcquisitionCoordinator(
            config,
            new PlatformCookieVault(root.Path("config")),
            profiles,
            health,
            managed,
            root.Path("temp"));
        var viewModel = new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            cookieProfiles: profiles,
            cookieHealthStore: health,
            managedLogin: managed,
            cookieCoordinator: coordinator);
        await viewModel.RefreshCookieStatusCommand.ExecuteAsync(null);
        var item = viewModel.CookiePlatformStatuses.Single(status => status.PlatformId == "youtube");

        await viewModel.ClearPlatformSessionCommand.ExecuteAsync(item);

        Assert.Equal(["youtube"], managed.ClearedPlatformIds);
        Assert.Equal(["youtube"], health.ClearedPlatformIds);
        Assert.True(item.NeedsLogin);
        Assert.Contains("已清除", item.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearPlatformSessionAsync_DoesNotExposeStorageExceptionDetails()
    {
        using var root = new TestDirectory();
        var config = new ConfigService(root.Path("config"));
        using var history = new HistoryService(root.Path("history.db"));
        var environment = new EnvironmentService();
        var manager = new DownloadManager(
            new YtDlpService(config, environment),
            history,
            config);
        var viewModel = new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            cookieProfiles: new StaticBrowserProfiles([]),
            cookieHealthStore: new StaticCookieHealthStore([]),
            managedLogin: new ThrowingManagedLoginSessionService(
                @"Cannot delete C:\Users\me\Secret Session containing SID=secret-value"));
        await viewModel.RefreshCookieStatusCommand.ExecuteAsync(null);
        var item = viewModel.CookiePlatformStatuses.Single(status => status.PlatformId == "youtube");

        await viewModel.ClearPlatformSessionCommand.ExecuteAsync(item);

        Assert.Contains("清除失败", item.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Session", item.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", item.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveSettingsAsync_DoesNotPersistHeaderWithoutPlatformButSavesOtherSettings()
    {
        using var root = new TestDirectory();
        var config = new ConfigService(root.Path("config"));
        using var history = new HistoryService(root.Path("history.db"));
        var environment = new EnvironmentService();
        var manager = new DownloadManager(
            new YtDlpService(config, environment),
            history,
            config);
        var viewModel = new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            cookieProfiles: new StaticBrowserProfiles([]),
            cookieHealthStore: new StaticCookieHealthStore([]),
            managedLogin: new FakeManagedLoginSessionService())
        {
            CookieContent = "Cookie: auth_token=secret-value",
            LegacyCookiePlatform = "",
            UseProxy = true,
            ProxyAddress = "http://127.0.0.1:7890"
        };

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);
        if (viewModel.SaveSettingsCommand.ExecutionTask is { } pendingSave)
            await pendingSave;

        Assert.Equal("", config.Config.CookieContent);
        Assert.Equal("", config.Config.LegacyCookiePlatform);
        Assert.True(config.Config.UseProxy);
        Assert.Equal("http://127.0.0.1:7890", config.Config.ProxyAddress);
        Assert.Contains("选择所属平台", viewModel.ManualCookieValidationMessage, StringComparison.Ordinal);
        var configJson = await File.ReadAllTextAsync(root.Path("config", "config.json"));
        Assert.DoesNotContain("secret-value", configJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveSettingsAsync_EncryptsManualCookieAndClearsPlaintextState()
    {
        using var root = new TestDirectory();
        var protector = new XorTestProtector();
        var config = new ConfigService(root.Path("config"), protector);
        var vault = new PlatformCookieVault(root.Path("config"), protector);
        using var history = new HistoryService(root.Path("history.db"));
        var environment = new EnvironmentService();
        var manager = new DownloadManager(
            new YtDlpService(config, environment),
            history,
            config);
        var viewModel = new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            cookieProfiles: new StaticBrowserProfiles([]),
            cookieHealthStore: new StaticCookieHealthStore([]),
            managedLogin: new FakeManagedLoginSessionService(),
            cookieVault: vault)
        {
            CookieContent = "Cookie: auth_token=manual-secret",
            LegacyCookiePlatform = "twitter"
        };

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal("", config.Config.CookieContent);
        Assert.Equal("", config.Config.LegacyCookiePlatform);
        Assert.Equal("", viewModel.CookieContent);
        Assert.Equal("", viewModel.LegacyCookiePlatform);
        Assert.Equal(
            "Cookie: auth_token=manual-secret",
            await vault.LoadAsync("twitter", CancellationToken.None));
        Assert.Contains("已加密保存", viewModel.ManualCookieValidationMessage, StringComparison.Ordinal);
        Assert.Contains("已加密保存", viewModel.ManualCookieStatusText, StringComparison.Ordinal);
        Assert.True(viewModel.IsManualCookieMessageSuccess);
        foreach (var fileName in new[] { "config.json", "config.backup.json" })
        {
            var json = await File.ReadAllTextAsync(root.Path("config", fileName));
            Assert.DoesNotContain("manual-secret", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SaveSettingsAsync_SplitsDomainScopedCookieWithoutPlatformSelection()
    {
        using var root = new TestDirectory();
        var protector = new XorTestProtector();
        var config = new ConfigService(root.Path("config"), protector);
        var vault = new PlatformCookieVault(root.Path("config"), protector);
        using var history = new HistoryService(root.Path("history.db"));
        var environment = new EnvironmentService();
        var manager = new DownloadManager(
            new YtDlpService(config, environment),
            history,
            config);
        var viewModel = new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            cookieProfiles: new StaticBrowserProfiles([]),
            cookieHealthStore: new StaticCookieHealthStore([]),
            managedLogin: new FakeManagedLoginSessionService(),
            cookieVault: vault)
        {
            CookieContent = """
                # Netscape HTTP Cookie File
                .youtube.com	TRUE	/	TRUE	0	SID	youtube-secret
                .x.com	TRUE	/	TRUE	0	auth_token	twitter-secret
                """,
            LegacyCookiePlatform = ""
        };

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.True(await vault.ExistsAsync("youtube", CancellationToken.None));
        Assert.True(await vault.ExistsAsync("twitter", CancellationToken.None));
        Assert.Equal("", viewModel.CookieContent);
        Assert.Contains("按域名拆分", viewModel.ManualCookieStatusText, StringComparison.Ordinal);
        Assert.True(viewModel.IsManualCookieMessageSuccess);
    }

    [Fact]
    public async Task SaveSettingsAsync_QuarantinesLoadedUnscopedLegacyCookieWithoutLosingIt()
    {
        using var root = new TestDirectory();
        var protector = new XorTestProtector();
        var config = new ConfigService(root.Path("config"), protector);
        config.Config.CookieContent = "Cookie: legacy_unscoped=secret-value";
        config.Config.LegacyCookiePlatform = "";
        var vault = new PlatformCookieVault(root.Path("config"), protector);
        using var history = new HistoryService(root.Path("history.db"));
        var environment = new EnvironmentService();
        var manager = new DownloadManager(
            new YtDlpService(config, environment),
            history,
            config);
        var viewModel = new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            cookieProfiles: new StaticBrowserProfiles([]),
            cookieHealthStore: new StaticCookieHealthStore([]),
            managedLogin: new FakeManagedLoginSessionService(),
            cookieVault: vault);
        viewModel.Initialize();

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(
            "Cookie: legacy_unscoped=secret-value",
            await vault.LoadAsync(
                ConfigService.LegacyUnscopedCookieStorageKey,
                CancellationToken.None));
        Assert.Equal("", config.Config.CookieContent);
        Assert.Equal("Cookie: legacy_unscoped=secret-value", viewModel.CookieContent);
        Assert.Contains("选择所属平台", viewModel.ManualCookieValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearCookieAsync_DeletesOnlySelectedPlatformEncryptedCookie()
    {
        using var root = new TestDirectory();
        var protector = new XorTestProtector();
        var config = new ConfigService(root.Path("config"), protector);
        var vault = new PlatformCookieVault(root.Path("config"), protector);
        await vault.SaveAsync("twitter", "auth_token=twitter-secret", CancellationToken.None);
        await vault.SaveAsync("youtube", "SID=youtube-secret", CancellationToken.None);
        using var history = new HistoryService(root.Path("history.db"));
        var environment = new EnvironmentService();
        var manager = new DownloadManager(
            new YtDlpService(config, environment),
            history,
            config);
        var viewModel = new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            cookieProfiles: new StaticBrowserProfiles([]),
            cookieHealthStore: new StaticCookieHealthStore([]),
            managedLogin: new FakeManagedLoginSessionService(),
            cookieVault: vault)
        {
            LegacyCookiePlatform = "twitter"
        };

        await viewModel.ClearCookieCommand.ExecuteAsync(null);

        Assert.False(await vault.ExistsAsync("twitter", CancellationToken.None));
        Assert.True(await vault.ExistsAsync("youtube", CancellationToken.None));
        Assert.Equal("", viewModel.LegacyCookiePlatform);
        Assert.Contains("X / Twitter", viewModel.ManualCookieValidationMessage, StringComparison.Ordinal);
        Assert.Contains("已清除", viewModel.ManualCookieValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearAllManagedSessionsAsync_ClearsEveryPlatformBeforeStatusRefreshCompletes()
    {
        using var root = new TestDirectory();
        var config = new ConfigService(root.Path("config"));
        using var history = new HistoryService(root.Path("history.db"));
        var environment = new EnvironmentService();
        var manager = new DownloadManager(
            new YtDlpService(config, environment),
            history,
            config);
        var health = new RecordingCookieHealthStore();
        var managed = new RecordingManagedLoginSessionService([]);
        var viewModel = new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            cookieProfiles: new StaticBrowserProfiles([]),
            cookieHealthStore: health,
            managedLogin: managed);

        await viewModel.ClearAllManagedSessionsCommand.ExecuteAsync(null);

        var expectedPlatformIds = MediaPlatformResolver.KnownPlatforms
            .Select(platform => platform.StorageKey)
            .ToArray();
        Assert.Equal(expectedPlatformIds, managed.ClearedPlatformIds);
        Assert.Equal(expectedPlatformIds, health.ClearedPlatformIds);
        Assert.All(viewModel.CookiePlatformStatuses, item => Assert.True(item.NeedsLogin));
        Assert.Contains("所有平台", viewModel.CookieStatusSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearAllManagedSessionsAsync_ReportsFailuresTruthfully()
    {
        using var root = new TestDirectory();
        var config = new ConfigService(root.Path("config"));
        using var history = new HistoryService(root.Path("history.db"));
        var environment = new EnvironmentService();
        var manager = new DownloadManager(
            new YtDlpService(config, environment),
            history,
            config);
        var viewModel = new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            cookieProfiles: new StaticBrowserProfiles([]),
            cookieHealthStore: new StaticCookieHealthStore([]),
            managedLogin: new ThrowingManagedLoginSessionService("SID=secret-value"));

        await viewModel.ClearAllManagedSessionsCommand.ExecuteAsync(null);

        Assert.Contains("清除失败", viewModel.CookieStatusSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", viewModel.CookieStatusSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("所有平台的 EasyGet 托管登录状态已清除", viewModel.CookieStatusSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAndSaveSettings_PreserveSmartModeAndPlatformScopedHeader()
    {
        using var root = new TestDirectory();
        var protector = new XorTestProtector();
        var config = new ConfigService(root.Path("config"), protector);
        var vault = new PlatformCookieVault(root.Path("config"), protector);
        config.Config.SmartCookieEnabled = false;
        config.Config.CookieContent = "Cookie: SID=initial";
        config.Config.LegacyCookiePlatform = "youtube";
        using var history = new HistoryService(root.Path("history.db"));
        var environment = new EnvironmentService();
        var manager = new DownloadManager(
            new YtDlpService(config, environment),
            history,
            config);
        var viewModel = new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            cookieProfiles: new StaticBrowserProfiles([]),
            cookieHealthStore: new StaticCookieHealthStore([]),
            managedLogin: new FakeManagedLoginSessionService(),
            cookieVault: vault);

        viewModel.Initialize();

        Assert.False(viewModel.SmartCookieEnabled);
        Assert.Equal("youtube", viewModel.LegacyCookiePlatform);
        viewModel.CookieContent = "Cookie: auth_token=updated";
        viewModel.LegacyCookiePlatform = "twitter";
        await viewModel.SaveSettingsCommand.ExecuteAsync(null);
        viewModel.SmartCookieEnabled = true;
        if (viewModel.SaveSettingsCommand.ExecutionTask is { } autoSave)
            await autoSave;

        Assert.True(config.Config.SmartCookieEnabled);
        Assert.Equal("", config.Config.CookieContent);
        Assert.Equal("", config.Config.LegacyCookiePlatform);
        Assert.Equal(
            "Cookie: auth_token=updated",
            await vault.LoadAsync("twitter", CancellationToken.None));
        Assert.Contains("已加密保存", viewModel.ManualCookieValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsXaml_UsesSmartCookieCommandsAndKeepsManualImportAdvanced()
    {
        var xaml = File.ReadAllText(TestRepositoryPaths.GetViewPath("SettingsView.xaml"));

        Assert.Contains("智能登录与 Cookie", xaml, StringComparison.Ordinal);
        Assert.Contains("SmartCookieEnabled", xaml, StringComparison.Ordinal);
        Assert.Contains("RefreshCookieStatusCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("LoginPlatformCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ClearPlatformSessionCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ClearAllManagedSessionsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("LegacyCookiePlatform", xaml, StringComparison.Ordinal);
        Assert.Contains("ManualCookieValidationMessage", xaml, StringComparison.Ordinal);
        Assert.Contains("加密保存手动 Cookie", xaml, StringComparison.Ordinal);
        Assert.Contains("Expander", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name", xaml, StringComparison.Ordinal);
    }

    private sealed class StaticBrowserProfiles(IReadOnlyList<BrowserProfile> profiles)
        : IBrowserProfileDiscoveryService
    {
        public IReadOnlyList<BrowserProfile> Discover() => profiles;
    }

    private sealed class ThrowingBrowserProfiles(string message)
        : IBrowserProfileDiscoveryService
    {
        public IReadOnlyList<BrowserProfile> Discover() => throw new IOException(message);
    }

    private sealed class StaticCookieHealthStore(IReadOnlyList<CookieHealthRecord> records)
        : ICookieHealthStore
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

    private sealed class FakeManagedLoginSessionService : IManagedLoginSessionService
    {
        public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
            MediaPlatformDefinition platform,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<BrowserCookie>>([]);

        public Task ClearAsync(string platformId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class ThrowingManagedLoginSessionService(string message)
        : IManagedLoginSessionService
    {
        public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
            MediaPlatformDefinition platform,
            CancellationToken cancellationToken)
            => Task.FromException<IReadOnlyList<BrowserCookie>>(new IOException(message));

        public Task ClearAsync(string platformId, CancellationToken cancellationToken)
            => Task.FromException(new IOException(message));
    }

    private sealed class RecordingCookieHealthStore : ICookieHealthStore
    {
        public List<(string PlatformId, CookieSourceKind Source)> Successes { get; } = [];
        public List<(
            string PlatformId,
            CookieSourceKind Source,
            CookieFailureCategory Category)> Failures { get; } = [];
        public List<string> ClearedPlatformIds { get; } = [];

        public IReadOnlyList<CookieHealthRecord> Snapshot() => [];

        public Task RecordSuccessAsync(
            string platformId,
            CookieSourceKind source,
            BrowserProfile? profile,
            CancellationToken cancellationToken)
        {
            Successes.Add((platformId, source));
            return Task.CompletedTask;
        }

        public Task RecordFailureAsync(
            string platformId,
            CookieSourceKind source,
            BrowserProfile? profile,
            CookieFailureCategory category,
            CancellationToken cancellationToken)
        {
            Failures.Add((platformId, source, category));
            return Task.CompletedTask;
        }

        public Task ClearPlatformAsync(string platformId, CancellationToken cancellationToken)
        {
            ClearedPlatformIds.Add(platformId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingManagedLoginSessionService(
        IReadOnlyList<BrowserCookie> cookies) : IManagedLoginSessionService
    {
        public int GetCookiesCallCount { get; private set; }
        public List<string> ClearedPlatformIds { get; } = [];

        public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
            MediaPlatformDefinition platform,
            CancellationToken cancellationToken)
        {
            GetCookiesCallCount++;
            return Task.FromResult(cookies);
        }

        public Task ClearAsync(string platformId, CancellationToken cancellationToken)
        {
            ClearedPlatformIds.Add(platformId);
            return Task.CompletedTask;
        }
    }

    private sealed class ConcurrentManagedLoginSessionService : IManagedLoginSessionService
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _getCookiesCallCount;

        public int GetCookiesCallCount => Volatile.Read(ref _getCookiesCallCount);
        public TaskCompletionSource FirstCallStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TwoCallsStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
            MediaPlatformDefinition platform,
            CancellationToken cancellationToken)
        {
            var callCount = Interlocked.Increment(ref _getCookiesCallCount);
            FirstCallStarted.TrySetResult();
            if (callCount >= 2)
                TwoCallsStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return [new BrowserCookie(".example.com", "/", "session", "value", true, 0)];
        }

        public Task ClearAsync(string platformId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Release() => _release.TrySetResult();
    }

    private sealed class XorTestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext)
            => plaintext.Select(value => (byte)(value ^ 0xA5)).ToArray();

        public byte[] Unprotect(byte[] ciphertext)
            => Protect(ciphertext);
    }
}
