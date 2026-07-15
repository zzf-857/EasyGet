using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyGet.Models;
using EasyGet.Services;
using EasyGet.Services.Cookies;

namespace EasyGet.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private const int AutoSaveDebounceMilliseconds = 150;

    private static readonly IReadOnlyDictionary<string, string> DouyinTemplatePreviewValues =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "7412345678901234567",
            ["title"] = "今天去爬山啦",
            ["author"] = "示例作者",
            ["author_id"] = "MS4wLjABAAAAexample",
            ["date"] = "2026-04-10",
            ["year"] = "2026",
            ["month"] = "04",
            ["day"] = "10",
            ["time"] = "221530",
            ["hour"] = "22",
            ["minute"] = "15",
            ["second"] = "30",
            ["timestamp"] = "1775830530",
            ["type"] = "video",
            ["mode"] = "post"
        };

    private readonly ConfigService _configService;
    private readonly EnvironmentService _envService;
    private readonly DownloadManager _downloadManager;
    private readonly TelegramDownloadService _telegramDownloadService;
    private readonly IAppUpdateService _appUpdateService;
    private readonly IDouyinSidecarHealthService _douyinSidecarHealthService;
    private readonly IBrowserProfileDiscoveryService _cookieProfiles;
    private readonly ICookieHealthStore _cookieHealthStore;
    private readonly IManagedLoginSessionService _managedLogin;
    private readonly IDefaultBrowserLauncher _defaultBrowserLauncher;
    private readonly CookieAcquisitionCoordinator? _cookieCoordinator;
    private readonly PlatformCookieVault _cookieVault;
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
    private readonly object _autoSaveGate = new();
    private CancellationTokenSource? _autoSaveDebounce;
    private Task _pendingAutoSaveTask = Task.CompletedTask;
    private long _autoSaveRequestedVersion;
    private long _autoSavePersistedVersion;
    private AppUpdateInfo? _availableAppUpdate;
    private string? _downloadedInstallerPath;
    private bool _isInitializing;

    [ObservableProperty] private string _tgApiId = "";
    [ObservableProperty] private string _tgApiHash = "";
    [ObservableProperty] private string _tgPhoneNumber = "";
    [ObservableProperty] private string _tgLoginStatusText = "未登录";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTgSendCodeButton))]
    [NotifyPropertyChangedFor(nameof(ShowTgSubmitCodeButton))]
    private bool _showTgCodeInput;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTgSubmitCodeButton))]
    private bool _showTgPasswordInput;

    [ObservableProperty] private string _tgVerificationCode = "";
    [ObservableProperty] private string _tgTwoFactorPassword = "";
    [ObservableProperty] private string _tgStatusMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperateTg))]
    private bool _isTgOperating;

    public bool ShowTgSendCodeButton => !ShowTgCodeInput;
    public bool ShowTgSubmitCodeButton => ShowTgCodeInput && !ShowTgPasswordInput;
    public bool CanOperateTg => !IsTgOperating;

    [ObservableProperty] private bool _ytDlpFound;
    [ObservableProperty] private string _ytDlpVersion = "";
    [ObservableProperty] private bool _ffmpegFound;
    [ObservableProperty] private string _ffmpegVersion = "";
    [ObservableProperty] private bool _isCheckingEnv;
    [ObservableProperty] private string _douyinSidecarHealthText = "抖音 sidecar 未检测";
    [ObservableProperty] private bool _isDouyinSidecarAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckDouyinSidecarHealth))]
    private bool _isCheckingDouyinSidecar;

    [ObservableProperty] private string _defaultDownloadPath = "";
    [ObservableProperty] private string _defaultFormat = "mp4";
    [ObservableProperty] private string _defaultQuality = "最高画质";
    [ObservableProperty] private int _maxConcurrentDownloads = 3;
    [ObservableProperty] private int _concurrentFragments = 8;
    [ObservableProperty] private string _settingsSaveStatusMessage = "";

    [ObservableProperty] private bool _useProxy;
    [ObservableProperty] private string _proxyAddress = "";

    [ObservableProperty] private bool _useAria2c;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DouyinCookieHealthText))]
    private string _cookieContent = "";

    [ObservableProperty] private bool _smartCookieEnabled = true;
    [ObservableProperty] private string _legacyCookiePlatform = "";
    [ObservableProperty] private string _manualCookieValidationMessage = "";
    [ObservableProperty] private string _manualCookieStatusText = "未配置 Cookie";
    [ObservableProperty] private bool _isManualCookieMessageSuccess;
    [ObservableProperty] private string _cookieStatusSummary = "尚未检测本机登录状态";
    [ObservableProperty] private bool _isRefreshingCookieStatus;

    public ObservableCollection<CookiePlatformStatusItem> CookiePlatformStatuses { get; } = [];
    public IReadOnlyList<MediaPlatformDefinition> CookiePlatformOptions =>
        MediaPlatformResolver.KnownPlatforms;

    [ObservableProperty] private bool _enableDouyinSpecialEngine;
    [ObservableProperty] private string _douyinMode = "post";
    [ObservableProperty] private int _douyinLimit;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DouyinFilenameTemplatePreviewText))]
    private string _douyinFilenameTemplate = AppConfig.DefaultDouyinTemplate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DouyinFolderTemplatePreviewText))]
    private string _douyinFolderTemplate = AppConfig.DefaultDouyinTemplate;

    [ObservableProperty] private string _douyinAuthorDirectoryMode = "nickname";
    [ObservableProperty] private bool _douyinGroupByMode = true;
    [ObservableProperty] private string _douyinStartTime = "";
    [ObservableProperty] private string _douyinEndTime = "";
    [ObservableProperty] private bool _douyinDownloadPinned;
    [ObservableProperty] private bool _douyinDownloadCover;
    [ObservableProperty] private bool _douyinDownloadAvatar;
    [ObservableProperty] private bool _douyinDownloadMusic;
    [ObservableProperty] private bool _douyinDownloadComments;
    [ObservableProperty] private bool _douyinCommentIncludeReplies;
    [ObservableProperty] private int _douyinMaxComments;
    [ObservableProperty] private int _douyinCommentPageSize = AppConfig.MaxDouyinCommentPageSize;
    [ObservableProperty] private bool _douyinDownloadJson;
    [ObservableProperty] private bool _douyinEnableDatabase;
    [ObservableProperty] private bool _douyinIncrementalDownload;
    [ObservableProperty] private bool _douyinEnableBrowserFallback;
    [ObservableProperty] private int _douyinLiveMaxDurationSeconds;
    [ObservableProperty] private int _douyinLiveChunkSize = AppConfig.DefaultDouyinLiveChunkSize;
    [ObservableProperty] private int _douyinLiveIdleTimeoutSeconds = AppConfig.DefaultDouyinLiveIdleTimeoutSeconds;

    [ObservableProperty] private bool _autoCategorizeByPlatform = true;

    [ObservableProperty] private bool _isUpdatingYtDlp;
    [ObservableProperty] private string _updateStatusMessage = "";
    [ObservableProperty] private bool _isInstallingTools;
    [ObservableProperty] private string _installStatusStage = "";
    [ObservableProperty] private string _installStatusMessage = "";

    [ObservableProperty] private string _appVersionText = "";
    [ObservableProperty] private string _appRuntimeText = "";
    [ObservableProperty] private string _latestAppVersion = "";
    [ObservableProperty] private string _appUpdateStatusMessage = "";
    [ObservableProperty] private int _appUpdateProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckAppUpdate))]
    [NotifyPropertyChangedFor(nameof(CanDownloadAppUpdate))]
    [NotifyPropertyChangedFor(nameof(CanInstallAppUpdate))]
    private bool _isCheckingAppUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckAppUpdate))]
    [NotifyPropertyChangedFor(nameof(CanDownloadAppUpdate))]
    [NotifyPropertyChangedFor(nameof(CanInstallAppUpdate))]
    private bool _isDownloadingAppUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadAppUpdate))]
    private bool _isAppUpdateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadAppUpdate))]
    [NotifyPropertyChangedFor(nameof(CanInstallAppUpdate))]
    private bool _isAppUpdateDownloaded;

    [ObservableProperty] private string _selectedThemeColor = "Indigo";
    public List<ThemePalette> ThemeOptions => ThemeManager.Palettes;

    public string[] FormatOptions { get; } = ["mp4", "mkv", "webm", "mp3", "m4a"];
    public string[] QualityOptions { get; } = ["最高画质", "2160p", "1080p", "720p", "480p"];
    public string[] DouyinModeOptions { get; } =
    [
        "post",
        "like",
        "mix",
        "music",
        "post,like,mix,music",
        "collect",
        "collectmix"
    ];
    public string[] DouyinAuthorDirectoryModeOptions { get; } =
        ["nickname", "sec_uid", "nickname_uid", "user_sec_uid"];
    public string DouyinFilenameTemplatePreviewText => BuildDouyinTemplatePreview(DouyinFilenameTemplate);
    public string DouyinFolderTemplatePreviewText => BuildDouyinTemplatePreview(DouyinFolderTemplate);
    public string DouyinCookieHealthText => DouyinCookieHealthReporter.Describe(CookieContent);
    public string DouyinTemplateVariablesText { get; } = string.Join(
        "  ",
        ConfigService.SupportedDouyinTemplateVariableNames.Select(variable => $"{{{variable}}}"));

    public bool CanCheckEnvironment => !IsCheckingEnv && !IsInstallingTools && !IsUpdatingYtDlp;
    public bool CanInstallMissingTools => CanCheckEnvironment && (!YtDlpFound || !FfmpegFound);
    public bool CanUpdateYtDlp => CanCheckEnvironment && YtDlpFound;
    public bool CanCheckDouyinSidecarHealth => !IsCheckingDouyinSidecar;
    public bool CanCheckAppUpdate => !IsCheckingAppUpdate && !IsDownloadingAppUpdate;
    public bool CanDownloadAppUpdate => CanCheckAppUpdate
        && IsAppUpdateAvailable
        && !IsAppUpdateDownloaded
        && _availableAppUpdate?.InstallerDownloadUrl is not null;
    public bool CanInstallAppUpdate => !IsCheckingAppUpdate
        && !IsDownloadingAppUpdate
        && IsAppUpdateDownloaded
        && !string.IsNullOrWhiteSpace(_downloadedInstallerPath);

    public event Action? SettingsSaved;

    public SettingsViewModel(
        ConfigService configService,
        EnvironmentService envService,
        DownloadManager downloadManager,
        TelegramDownloadService telegramDownloadService,
        IAppUpdateService? appUpdateService = null,
        IDouyinSidecarHealthService? douyinSidecarHealthService = null,
        IBrowserProfileDiscoveryService? cookieProfiles = null,
        ICookieHealthStore? cookieHealthStore = null,
        IManagedLoginSessionService? managedLogin = null,
        CookieAcquisitionCoordinator? cookieCoordinator = null,
        PlatformCookieVault? cookieVault = null,
        IDefaultBrowserLauncher? defaultBrowserLauncher = null)
    {
        _configService = configService;
        _envService = envService;
        _downloadManager = downloadManager;
        _telegramDownloadService = telegramDownloadService;
        _appUpdateService = appUpdateService ?? new AppUpdateService();
        _douyinSidecarHealthService = douyinSidecarHealthService ?? new DouyinSpecialDownloadService();
        _cookieProfiles = cookieProfiles ?? new BrowserProfileDiscoveryService();
        _cookieHealthStore = cookieHealthStore ?? new CookieHealthStore(configService.ConfigDirectory);
        _managedLogin = managedLogin ?? new EmptyManagedLoginSessionService();
        _defaultBrowserLauncher = defaultBrowserLauncher ?? new DefaultBrowserLauncher();
        _cookieCoordinator = cookieCoordinator;
        _cookieVault = cookieVault ?? new PlatformCookieVault(configService.ConfigDirectory);
        AppVersionText = $"v{_appUpdateService.CurrentVersion}";
        AppRuntimeText = _appUpdateService.RuntimeDescription;
    }

    public void Initialize()
    {
        var c = _configService.Config;

        _isInitializing = true;
        try
        {
            DefaultDownloadPath = c.DefaultDownloadPath;
            DefaultFormat = c.DefaultFormat;
            DefaultQuality = c.DefaultQuality switch
            {
                "best" => "最高画质",
                "2160" => "2160p",
                "1080" => "1080p",
                "720" => "720p",
                "480" => "480p",
                _ => "最高画质"
            };
            MaxConcurrentDownloads = c.MaxConcurrentDownloads;
            ConcurrentFragments = c.ConcurrentFragments;
            UseProxy = c.UseProxy;
            ProxyAddress = c.ProxyAddress;
            UseAria2c = c.UseAria2c;
            CookieContent = c.CookieContent;
            SmartCookieEnabled = c.SmartCookieEnabled;
            LegacyCookiePlatform = c.LegacyCookiePlatform;
            ManualCookieStatusText = string.IsNullOrWhiteSpace(c.CookieContent)
                ? "未配置 Cookie"
                : "待选择平台并加密保存";
            IsManualCookieMessageSuccess = false;
            EnableDouyinSpecialEngine = c.EnableDouyinSpecialEngine;
            DouyinMode = c.DouyinMode;
            DouyinLimit = c.DouyinLimit;
            DouyinFilenameTemplate = c.DouyinFilenameTemplate;
            DouyinFolderTemplate = c.DouyinFolderTemplate;
            DouyinAuthorDirectoryMode = c.DouyinAuthorDirectoryMode;
            DouyinGroupByMode = c.DouyinGroupByMode;
            DouyinStartTime = c.DouyinStartTime;
            DouyinEndTime = c.DouyinEndTime;
            DouyinDownloadPinned = c.DouyinDownloadPinned;
            DouyinDownloadCover = c.DouyinDownloadCover;
            DouyinDownloadAvatar = c.DouyinDownloadAvatar;
            DouyinDownloadMusic = c.DouyinDownloadMusic;
            DouyinDownloadComments = c.DouyinDownloadComments;
            DouyinCommentIncludeReplies = c.DouyinCommentIncludeReplies;
            DouyinMaxComments = c.DouyinMaxComments;
            DouyinCommentPageSize = c.DouyinCommentPageSize;
            DouyinDownloadJson = c.DouyinDownloadJson;
            DouyinEnableDatabase = c.DouyinEnableDatabase;
            DouyinIncrementalDownload = c.DouyinIncrementalDownload;
            DouyinEnableBrowserFallback = c.DouyinEnableBrowserFallback;
            DouyinLiveMaxDurationSeconds = c.DouyinLiveMaxDurationSeconds;
            DouyinLiveChunkSize = c.DouyinLiveChunkSize;
            DouyinLiveIdleTimeoutSeconds = c.DouyinLiveIdleTimeoutSeconds;
            AutoCategorizeByPlatform = c.AutoCategorizeByPlatform;
            SelectedThemeColor = c.ThemeColor;
            TgApiId = c.TgApiId;
            TgApiHash = c.TgApiHash;
            TgPhoneNumber = c.TgPhoneNumber;
            AppVersionText = $"v{_appUpdateService.CurrentVersion}";
            AppRuntimeText = _appUpdateService.RuntimeDescription;
        }
        finally
        {
            _isInitializing = false;
        }

        RefreshEnvironmentStatus();
        _ = RefreshTgStatusAsync();
        _ = RefreshCookieStatus();
    }

    public void RefreshEnvironmentStatus()
    {
        var status = _envService.Status;
        YtDlpFound = status.YtDlpFound;
        YtDlpVersion = status.YtDlpVersion;
        FfmpegFound = status.FfmpegFound;
        FfmpegVersion = status.FfmpegVersion;
    }

    [RelayCommand]
    private async Task RefreshCookieStatus()
    {
        if (IsRefreshingCookieStatus)
            return;

        IsRefreshingCookieStatus = true;
        try
        {
            var profiles = await Task.Run(_cookieProfiles.Discover);
            var health = _cookieHealthStore.Snapshot();
            CookiePlatformStatuses.Clear();
            var verifiedPlatforms = 0;

            foreach (var platform in MediaPlatformResolver.KnownPlatforms)
            {
                var successful = health
                    .Where(record => string.Equals(
                                         record.PlatformId,
                                         platform.StorageKey,
                                         StringComparison.Ordinal)
                                     && record.LastSuccessUtc.HasValue
                                     && record.ConsecutiveFailures == 0
                                     && (!record.LastFailureUtc.HasValue
                                         || record.LastSuccessUtc.Value >= record.LastFailureUtc.Value))
                    .OrderByDescending(record => record.LastSuccessUtc)
                    .FirstOrDefault();

                var item = new CookiePlatformStatusItem
                {
                    PlatformId = platform.Id,
                    StorageKey = platform.StorageKey,
                    DisplayName = platform.DisplayName
                };
                if (successful is not null)
                {
                    verifiedPlatforms++;
                    item.IsAvailable = true;
                    item.NeedsLogin = false;
                    item.StatusText = $"最近验证可用 · {DescribeCookieSource(successful.Source)}";
                }
                else if (profiles.Count > 0)
                {
                    item.IsAvailable = false;
                    item.NeedsLogin = false;
                    item.StatusText = $"已发现 {profiles.Count} 个浏览器配置，下载时自动尝试";
                }
                else
                {
                    item.IsAvailable = false;
                    item.NeedsLogin = true;
                    item.StatusText = "未发现可复用浏览器配置，首次使用时需要登录";
                }

                CookiePlatformStatuses.Add(item);
            }

            CookieStatusSummary = profiles.Count == 0
                ? $"未发现受支持浏览器配置 · {verifiedPlatforms} 个平台近期验证可用"
                : $"已发现 {profiles.Count} 个浏览器配置 · {verifiedPlatforms} 个平台近期验证可用";
        }
        catch (Exception)
        {
            CookieStatusSummary = "登录状态检测失败，请稍后重试";
        }
        finally
        {
            IsRefreshingCookieStatus = false;
        }
    }

    private static string DescribeCookieSource(CookieSourceKind source)
        => source switch
        {
            CookieSourceKind.Anonymous => "公开访问",
            CookieSourceKind.LegacyScoped => "平台手动 Cookie",
            CookieSourceKind.Browser => "本机浏览器",
            CookieSourceKind.ManagedSession => "EasyGet 托管登录",
            _ => "本地登录状态"
        };

    [RelayCommand]
    private async Task LoginPlatform(
        CookiePlatformStatusItem? item,
        CancellationToken cancellationToken)
    {
        if (item is null || item.IsOperating)
            return;

        var platform = MediaPlatformResolver.KnownPlatforms.FirstOrDefault(definition =>
            string.Equals(definition.StorageKey, item.StorageKey, StringComparison.Ordinal));
        if (platform is null)
        {
            item.StatusText = "平台定义不可用，请更新 EasyGet";
            return;
        }

        item.IsOperating = true;
        item.StatusText = "正在打开系统默认浏览器...";
        try
        {
            await _defaultBrowserLauncher.OpenAsync(platform.LoginUri, cancellationToken);
            item.StatusText = "已打开系统默认浏览器；完成登录后直接重试下载";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            item.StatusText = "打开浏览器操作已取消";
        }
        catch (Exception)
        {
            item.StatusText = "无法打开系统默认浏览器，请检查 Windows 默认应用设置";
        }
        finally
        {
            item.IsOperating = false;
        }
    }

    [RelayCommand]
    private async Task CompatibleLoginPlatform(
        CookiePlatformStatusItem? item,
        CancellationToken cancellationToken)
    {
        if (item is null || item.IsOperating)
            return;

        var platform = MediaPlatformResolver.KnownPlatforms.FirstOrDefault(definition =>
            string.Equals(definition.StorageKey, item.StorageKey, StringComparison.Ordinal));
        if (platform is null)
        {
            item.StatusText = "平台定义不可用，请更新 EasyGet";
            return;
        }

        item.IsOperating = true;
        item.StatusText = "正在打开 EasyGet 兼容登录窗口...";
        try
        {
            var cookies = await _managedLogin.GetCookiesAsync(platform, cancellationToken);
            var scopedLines = CookieFileSerializer.BuildScopedLines(cookies, platform);
            if (!scopedLines.Skip(3).Any())
            {
                await _cookieHealthStore.RecordFailureAsync(
                    platform.StorageKey,
                    CookieSourceKind.ManagedSession,
                    profile: null,
                    CookieFailureCategory.AuthenticationRequired,
                    cancellationToken);
                item.IsAvailable = false;
                item.NeedsLogin = true;
                item.StatusText = "未完成兼容登录；系统浏览器登录状态不受影响";
                return;
            }

            await _cookieVault.SaveAsync(
                platform.StorageKey,
                string.Join(Environment.NewLine, scopedLines),
                cancellationToken);
            await _cookieHealthStore.RecordSuccessAsync(
                platform.StorageKey,
                CookieSourceKind.ManagedSession,
                profile: null,
                cancellationToken);
            item.IsAvailable = true;
            item.NeedsLogin = false;
            item.StatusText = "兼容登录成功 · Cookie 已加密保存";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            item.StatusText = "兼容登录已取消";
        }
        catch (Exception)
        {
            item.IsAvailable = false;
            item.NeedsLogin = true;
            item.StatusText = "兼容登录失败，请重试或检查 WebView2 运行环境";
        }
        finally
        {
            item.IsOperating = false;
        }
    }

    [RelayCommand]
    private async Task ClearPlatformSession(
        CookiePlatformStatusItem? item,
        CancellationToken cancellationToken)
        => _ = await ClearPlatformSessionCore(item, cancellationToken);

    private async Task<bool> ClearPlatformSessionCore(
        CookiePlatformStatusItem? item,
        CancellationToken cancellationToken)
    {
        if (item is null || item.IsOperating)
            return false;

        var platform = MediaPlatformResolver.KnownPlatforms.FirstOrDefault(definition =>
            string.Equals(definition.StorageKey, item.StorageKey, StringComparison.Ordinal));
        if (platform is null)
            return false;

        item.IsOperating = true;
        item.StatusText = "正在清除 EasyGet 登录数据...";
        try
        {
            if (_cookieCoordinator is not null)
            {
                await _cookieCoordinator.ClearPlatformSessionAsync(
                    platform,
                    cancellationToken);
            }
            else
            {
                await _managedLogin.ClearAsync(platform.StorageKey, cancellationToken);
                await _cookieVault.DeleteAsync(platform.StorageKey, cancellationToken);
                await _cookieHealthStore.ClearPlatformAsync(
                    platform.StorageKey,
                    cancellationToken);
            }

            item.IsAvailable = false;
            item.NeedsLogin = true;
            item.StatusText = "EasyGet 登录数据已清除；系统浏览器登录不受影响";
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            item.StatusText = "清除操作已取消";
            return false;
        }
        catch (Exception)
        {
            item.StatusText = "清除失败，请关闭相关登录窗口后重试";
            return false;
        }
        finally
        {
            item.IsOperating = false;
        }
    }

    [RelayCommand]
    private async Task ClearAllManagedSessions(CancellationToken cancellationToken)
    {
        if (CookiePlatformStatuses.Count == 0)
        {
            foreach (var platform in MediaPlatformResolver.KnownPlatforms)
            {
                CookiePlatformStatuses.Add(new CookiePlatformStatusItem
                {
                    PlatformId = platform.Id,
                    StorageKey = platform.StorageKey,
                    DisplayName = platform.DisplayName
                });
            }
        }

        var failureCount = 0;
        foreach (var item in CookiePlatformStatuses.ToArray())
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            if (!await ClearPlatformSessionCore(item, cancellationToken))
                failureCount++;
        }

        CookieStatusSummary = cancellationToken.IsCancellationRequested
            ? "批量清除登录状态已取消"
            : failureCount > 0
                ? $"{failureCount} 个平台清除失败，请逐项重试"
                : "所有平台的 EasyGet 登录数据已清除；系统浏览器不受影响";
    }

    [RelayCommand]
    private async Task CheckEnvironment()
    {
        IsCheckingEnv = true;
        try
        {
            await _envService.CheckEnvironmentAsync();
            RefreshEnvironmentStatus();
        }
        finally
        {
            IsCheckingEnv = false;
        }
    }

    [RelayCommand]
    private async Task CheckDouyinSidecarHealth()
    {
        IsCheckingDouyinSidecar = true;
        DouyinSidecarHealthText = "正在检测抖音 sidecar...";
        try
        {
            var result = await _douyinSidecarHealthService.CheckHealthAsync();
            IsDouyinSidecarAvailable = result.IsAvailable;
            DouyinSidecarHealthText = result.StatusText;
        }
        catch (Exception ex)
        {
            IsDouyinSidecarAvailable = false;
            DouyinSidecarHealthText = $"抖音 sidecar 异常 · {ex.Message}";
        }
        finally
        {
            IsCheckingDouyinSidecar = false;
        }
    }

    [RelayCommand]
    private async Task InstallMissingTools()
    {
        IsInstallingTools = true;
        InstallStatusMessage = "";
        try
        {
            await _envService.InstallMissingToolsAsync(new Progress<string>(s => InstallStatusMessage = s));
            RefreshEnvironmentStatus();
        }
        catch (Exception ex)
        {
            InstallStatusMessage = $"安装失败: {ex.Message}";
        }
        finally
        {
            IsInstallingTools = false;
        }
    }

    [RelayCommand]
    private async Task UpdateYtDlp()
    {
        IsUpdatingYtDlp = true;
        UpdateStatusMessage = "";
        await _envService.UpdateYtDlpAsync(new Progress<string>(s => UpdateStatusMessage = s));
        RefreshEnvironmentStatus();
        IsUpdatingYtDlp = false;
    }

    [RelayCommand]
    private async Task BrowseDownloadPath()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择默认下载目录",
            InitialDirectory = DefaultDownloadPath
        };

        if (dialog.ShowDialog() == true)
        {
            DefaultDownloadPath = dialog.FolderName;
            SettingsSaveStatusMessage = "正在保存下载目录...";
            await FlushPendingSaveAsync();
        }
    }

    [RelayCommand]
    private async Task SaveSettings()
    {
        CancelPendingAutoSave();
        if (await PersistSettingsAsync())
            MarkLatestAutoSaveVersionPersisted();
    }

    private async Task<bool> PersistSettingsAsync()
    {
        await _settingsSaveGate.WaitAsync();
        try
        {
            var c = _configService.Config;
            c.DefaultDownloadPath = DefaultDownloadPath;
            c.DefaultFormat = DefaultFormat;
            c.DefaultQuality = DefaultQuality switch
            {
                "最高画质" => "best",
                "2160p" => "2160",
                "1080p" => "1080",
                "720p" => "720",
                "480p" => "480",
                _ => "best"
            };
            c.MaxConcurrentDownloads = MaxConcurrentDownloads;
            c.ConcurrentFragments = ConcurrentFragments;
            c.UseProxy = UseProxy;
            c.ProxyAddress = ProxyAddress;
            c.UseAria2c = UseAria2c;
            var selectedCookiePlatform = MediaPlatformResolver.KnownPlatforms
                .FirstOrDefault(platform => string.Equals(
                    platform.StorageKey,
                    LegacyCookiePlatform?.Trim(),
                    StringComparison.Ordinal))
                ?.StorageKey ?? "";
            var cookieContentRequiresPlatform = !string.IsNullOrWhiteSpace(CookieContent)
                                                && !CookieFileSerializer.HasExplicitDomainRows(CookieContent);
            var canPersistManualCookie = !cookieContentRequiresPlatform
                                         || selectedCookiePlatform.Length > 0;
            if (!canPersistManualCookie)
            {
                ManualCookieValidationMessage = "请先选择所属平台，再保存 Header 格式 Cookie。";
                IsManualCookieMessageSuccess = false;
            }
            else if (!string.IsNullOrWhiteSpace(CookieContent))
            {
                ManualCookieValidationMessage = "";
                IsManualCookieMessageSuccess = false;
            }
            if (canPersistManualCookie)
                c.CookieContent = CookieContent;
            c.SmartCookieEnabled = SmartCookieEnabled;
            if (canPersistManualCookie)
            {
                c.LegacyCookiePlatform = string.IsNullOrWhiteSpace(c.CookieContent)
                    ? ""
                    : selectedCookiePlatform;
            }
            c.EnableDouyinSpecialEngine = EnableDouyinSpecialEngine;
            c.DouyinMode = DouyinMode;
            c.DouyinLimit = DouyinLimit;
            c.DouyinFilenameTemplate = DouyinFilenameTemplate;
            c.DouyinFolderTemplate = DouyinFolderTemplate;
            c.DouyinAuthorDirectoryMode = DouyinAuthorDirectoryMode;
            c.DouyinGroupByMode = DouyinGroupByMode;
            c.DouyinStartTime = DouyinStartTime;
            c.DouyinEndTime = DouyinEndTime;
            c.DouyinDownloadPinned = DouyinDownloadPinned;
            c.DouyinDownloadCover = DouyinDownloadCover;
            c.DouyinDownloadAvatar = DouyinDownloadAvatar;
            c.DouyinDownloadMusic = DouyinDownloadMusic;
            c.DouyinDownloadComments = DouyinDownloadComments;
            c.DouyinCommentIncludeReplies = DouyinCommentIncludeReplies;
            c.DouyinMaxComments = DouyinMaxComments;
            c.DouyinCommentPageSize = DouyinCommentPageSize;
            c.DouyinDownloadJson = DouyinDownloadJson;
            c.DouyinEnableDatabase = DouyinEnableDatabase;
            c.DouyinIncrementalDownload = DouyinIncrementalDownload;
            c.DouyinEnableBrowserFallback = DouyinEnableBrowserFallback;
            c.DouyinLiveMaxDurationSeconds = DouyinLiveMaxDurationSeconds;
            c.DouyinLiveChunkSize = DouyinLiveChunkSize;
            c.DouyinLiveIdleTimeoutSeconds = DouyinLiveIdleTimeoutSeconds;
            c.AutoCategorizeByPlatform = AutoCategorizeByPlatform;
            c.ThemeColor = SelectedThemeColor;
            c.TgApiId = TgApiId;
            c.TgApiHash = TgApiHash;
            c.TgPhoneNumber = TgPhoneNumber;

            ConfigService.NormalizeRuntimeConfig(c);
            SyncNormalizedPerformanceValues(c);
            SyncNormalizedDouyinValues(c);

            _downloadManager.UpdateConcurrencyLimit(c.MaxConcurrentDownloads);
            if (canPersistManualCookie && !string.IsNullOrWhiteSpace(c.CookieContent))
            {
                var savedPlatform = MediaPlatformResolver.KnownPlatforms.FirstOrDefault(platform =>
                    string.Equals(
                        platform.StorageKey,
                        selectedCookiePlatform,
                        StringComparison.Ordinal));
                await _configService.CompleteLegacyCookieMigrationAsync(
                    selectedCookiePlatform,
                    _cookieVault,
                    CancellationToken.None);
                _isInitializing = true;
                try
                {
                    CookieContent = "";
                    LegacyCookiePlatform = "";
                }
                finally
                {
                    _isInitializing = false;
                }

                ManualCookieValidationMessage = "手动 Cookie 已加密保存并按平台隔离。";
                ManualCookieStatusText = savedPlatform is null
                    ? "已加密保存 · 已按域名拆分"
                    : $"已加密保存 · {savedPlatform.DisplayName}";
                IsManualCookieMessageSuccess = true;
            }

            if (!await _configService.SaveAsync())
            {
                SettingsSaveStatusMessage = "设置保存失败，请稍后重试";
                return false;
            }

            SettingsSaveStatusMessage = "设置已保存";
            SettingsSaved?.Invoke();
            return true;
        }
        catch (Exception)
        {
            SettingsSaveStatusMessage = "设置保存失败，请检查目录权限后重试";
            return false;
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    partial void OnDefaultDownloadPathChanged(string value) => AutoSave();
    partial void OnDefaultFormatChanged(string value) => AutoSave();
    partial void OnDefaultQualityChanged(string value) => AutoSave();
    partial void OnMaxConcurrentDownloadsChanged(int value) => AutoSave();
    partial void OnConcurrentFragmentsChanged(int value) => AutoSave();
    partial void OnUseProxyChanged(bool value) => AutoSave();
    partial void OnProxyAddressChanged(string value) => AutoSave();
    partial void OnUseAria2cChanged(bool value) => AutoSave();
    partial void OnCookieContentChanged(string value)
    {
        if (_isInitializing)
            return;

        ManualCookieStatusText = string.IsNullOrWhiteSpace(value)
            ? "未配置 Cookie"
            : "待加密保存";
        IsManualCookieMessageSuccess = false;
    }
    partial void OnSmartCookieEnabledChanged(bool value) => AutoSave();
    partial void OnEnableDouyinSpecialEngineChanged(bool value) => AutoSave();
    partial void OnDouyinModeChanged(string value) => AutoSave();
    partial void OnDouyinLimitChanged(int value) => AutoSave();
    partial void OnDouyinFilenameTemplateChanged(string value) => AutoSave();
    partial void OnDouyinFolderTemplateChanged(string value) => AutoSave();
    partial void OnDouyinAuthorDirectoryModeChanged(string value) => AutoSave();
    partial void OnDouyinGroupByModeChanged(bool value) => AutoSave();
    partial void OnDouyinStartTimeChanged(string value) => AutoSave();
    partial void OnDouyinEndTimeChanged(string value) => AutoSave();
    partial void OnDouyinDownloadPinnedChanged(bool value) => AutoSave();
    partial void OnDouyinDownloadCoverChanged(bool value) => AutoSave();
    partial void OnDouyinDownloadAvatarChanged(bool value) => AutoSave();
    partial void OnDouyinDownloadMusicChanged(bool value) => AutoSave();
    partial void OnDouyinDownloadCommentsChanged(bool value) => AutoSave();
    partial void OnDouyinCommentIncludeRepliesChanged(bool value) => AutoSave();
    partial void OnDouyinMaxCommentsChanged(int value) => AutoSave();
    partial void OnDouyinCommentPageSizeChanged(int value) => AutoSave();
    partial void OnDouyinDownloadJsonChanged(bool value) => AutoSave();
    partial void OnDouyinEnableDatabaseChanged(bool value) => AutoSave();
    partial void OnDouyinIncrementalDownloadChanged(bool value) => AutoSave();
    partial void OnDouyinEnableBrowserFallbackChanged(bool value) => AutoSave();
    partial void OnDouyinLiveMaxDurationSecondsChanged(int value) => AutoSave();
    partial void OnDouyinLiveChunkSizeChanged(int value) => AutoSave();
    partial void OnDouyinLiveIdleTimeoutSecondsChanged(int value) => AutoSave();
    partial void OnAutoCategorizeByPlatformChanged(bool value) => AutoSave();
    partial void OnTgApiIdChanged(string value) => AutoSave();
    partial void OnTgApiHashChanged(string value) => AutoSave();
    partial void OnTgPhoneNumberChanged(string value) => AutoSave();
    partial void OnSelectedThemeColorChanged(string value)
    {
        ThemeManager.ApplyTheme(value);
        AutoSave();
    }
    partial void OnYtDlpFoundChanged(bool value) => NotifyEnvironmentActionStateChanged();
    partial void OnFfmpegFoundChanged(bool value) => NotifyEnvironmentActionStateChanged();
    partial void OnIsCheckingEnvChanged(bool value) => NotifyEnvironmentActionStateChanged();
    partial void OnIsInstallingToolsChanged(bool value)
    {
        RefreshInstallStatusStage();
        NotifyEnvironmentActionStateChanged();
    }
    partial void OnIsUpdatingYtDlpChanged(bool value) => NotifyEnvironmentActionStateChanged();
    partial void OnInstallStatusMessageChanged(string value) => RefreshInstallStatusStage();
    partial void OnIsAppUpdateAvailableChanged(bool value) => NotifyAppUpdateActionStateChanged();
    partial void OnIsAppUpdateDownloadedChanged(bool value) => NotifyAppUpdateActionStateChanged();
    partial void OnIsCheckingAppUpdateChanged(bool value) => NotifyAppUpdateActionStateChanged();
    partial void OnIsDownloadingAppUpdateChanged(bool value) => NotifyAppUpdateActionStateChanged();

    private void AutoSave()
    {
        if (_isInitializing)
            return;

        CancellationTokenSource debounce;
        CancellationTokenSource? previousDebounce;
        long version;
        lock (_autoSaveGate)
        {
            version = ++_autoSaveRequestedVersion;
            previousDebounce = _autoSaveDebounce;
            debounce = new CancellationTokenSource();
            _autoSaveDebounce = debounce;
            _pendingAutoSaveTask = RunAutoSaveAsync(version, debounce);
        }
        TryCancelDebounce(previousDebounce);
    }

    private async Task RunAutoSaveAsync(
        long version,
        CancellationTokenSource debounce)
    {
        try
        {
            await Task.Delay(AutoSaveDebounceMilliseconds, debounce.Token);
            if (await PersistSettingsAsync())
            {
                lock (_autoSaveGate)
                    _autoSavePersistedVersion = Math.Max(_autoSavePersistedVersion, version);
            }
        }
        catch (OperationCanceledException) when (debounce.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_autoSaveGate)
            {
                if (ReferenceEquals(_autoSaveDebounce, debounce))
                    _autoSaveDebounce = null;
            }

            debounce.Dispose();
        }
    }

    public async Task<bool> FlushPendingSaveAsync()
    {
        while (true)
        {
            Task pendingSave;
            CancellationTokenSource? debounce;
            long targetVersion;
            lock (_autoSaveGate)
            {
                targetVersion = _autoSaveRequestedVersion;
                debounce = _autoSaveDebounce;
                pendingSave = _pendingAutoSaveTask;
            }
            TryCancelDebounce(debounce);

            await pendingSave;

            lock (_autoSaveGate)
            {
                if (_autoSavePersistedVersion >= targetVersion
                    && _autoSaveRequestedVersion == targetVersion)
                {
                    return true;
                }
            }

            if (!await PersistSettingsAsync())
                return false;

            lock (_autoSaveGate)
            {
                _autoSavePersistedVersion = Math.Max(
                    _autoSavePersistedVersion,
                    targetVersion);
                if (_autoSaveRequestedVersion == targetVersion)
                    return true;
            }
        }
    }

    private void CancelPendingAutoSave()
    {
        CancellationTokenSource? debounce;
        lock (_autoSaveGate)
            debounce = _autoSaveDebounce;
        TryCancelDebounce(debounce);
    }

    private static void TryCancelDebounce(CancellationTokenSource? debounce)
    {
        try
        {
            debounce?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void MarkLatestAutoSaveVersionPersisted()
    {
        lock (_autoSaveGate)
            _autoSavePersistedVersion = _autoSaveRequestedVersion;
    }

    private void SyncNormalizedPerformanceValues(EasyGet.Models.AppConfig config)
    {
        if (MaxConcurrentDownloads == config.MaxConcurrentDownloads
            && ConcurrentFragments == config.ConcurrentFragments)
        {
            return;
        }

        _isInitializing = true;
        try
        {
            MaxConcurrentDownloads = config.MaxConcurrentDownloads;
            ConcurrentFragments = config.ConcurrentFragments;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void SyncNormalizedDouyinValues(EasyGet.Models.AppConfig config)
    {
        if (DouyinMode == config.DouyinMode
            && DouyinLimit == config.DouyinLimit
            && DouyinFilenameTemplate == config.DouyinFilenameTemplate
            && DouyinFolderTemplate == config.DouyinFolderTemplate
            && DouyinAuthorDirectoryMode == config.DouyinAuthorDirectoryMode
            && DouyinGroupByMode == config.DouyinGroupByMode
            && DouyinMaxComments == config.DouyinMaxComments
            && DouyinCommentPageSize == config.DouyinCommentPageSize
            && DouyinLiveMaxDurationSeconds == config.DouyinLiveMaxDurationSeconds
            && DouyinLiveChunkSize == config.DouyinLiveChunkSize
            && DouyinLiveIdleTimeoutSeconds == config.DouyinLiveIdleTimeoutSeconds
            && DouyinStartTime == config.DouyinStartTime
            && DouyinEndTime == config.DouyinEndTime)
        {
            return;
        }

        _isInitializing = true;
        try
        {
            DouyinMode = config.DouyinMode;
            DouyinLimit = config.DouyinLimit;
            DouyinFilenameTemplate = config.DouyinFilenameTemplate;
            DouyinFolderTemplate = config.DouyinFolderTemplate;
            DouyinAuthorDirectoryMode = config.DouyinAuthorDirectoryMode;
            DouyinGroupByMode = config.DouyinGroupByMode;
            DouyinMaxComments = config.DouyinMaxComments;
            DouyinCommentPageSize = config.DouyinCommentPageSize;
            DouyinLiveMaxDurationSeconds = config.DouyinLiveMaxDurationSeconds;
            DouyinLiveChunkSize = config.DouyinLiveChunkSize;
            DouyinLiveIdleTimeoutSeconds = config.DouyinLiveIdleTimeoutSeconds;
            DouyinStartTime = config.DouyinStartTime;
            DouyinEndTime = config.DouyinEndTime;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void NotifyEnvironmentActionStateChanged()
    {
        OnPropertyChanged(nameof(CanCheckEnvironment));
        OnPropertyChanged(nameof(CanInstallMissingTools));
        OnPropertyChanged(nameof(CanUpdateYtDlp));
    }

    private void NotifyAppUpdateActionStateChanged()
    {
        OnPropertyChanged(nameof(CanCheckAppUpdate));
        OnPropertyChanged(nameof(CanDownloadAppUpdate));
        OnPropertyChanged(nameof(CanInstallAppUpdate));
    }

    private void RefreshInstallStatusStage()
    {
        InstallStatusStage = DescribeInstallStatusStage(InstallStatusMessage, IsInstallingTools);
    }

    internal static string DescribeInstallStatusStage(string message, bool isInstalling)
    {
        if (string.IsNullOrWhiteSpace(message))
            return isInstalling ? "检测中" : "";

        if (message.Contains("失败", StringComparison.OrdinalIgnoreCase)
            || message.Contains("未完成", StringComparison.OrdinalIgnoreCase))
            return "失败";

        if (message.Contains("下载中", StringComparison.OrdinalIgnoreCase))
            return "下载中";

        if (message.Contains("解压", StringComparison.OrdinalIgnoreCase))
            return "解压中";

        if (message.Contains("安装完成", StringComparison.OrdinalIgnoreCase)
            || message.Contains("环境已就绪", StringComparison.OrdinalIgnoreCase))
            return "完成";

        if (message.Contains("正在安装", StringComparison.OrdinalIgnoreCase))
            return "准备安装";

        return isInstalling ? "处理中" : "";
    }

    internal static string BuildDouyinTemplatePreview(string? template)
    {
        var preview = ConfigService.NormalizeDouyinTemplate(template);
        foreach (var (variable, value) in DouyinTemplatePreviewValues)
        {
            preview = preview.Replace($"{{{variable}}}", value, StringComparison.Ordinal);
        }

        return $"示例：{preview}";
    }

    [RelayCommand]
    private async Task ClearCookie(CancellationToken cancellationToken)
    {
        var platform = MediaPlatformResolver.KnownPlatforms.FirstOrDefault(definition =>
            string.Equals(
                definition.StorageKey,
                LegacyCookiePlatform?.Trim(),
                StringComparison.Ordinal));
        if (platform is null && string.IsNullOrWhiteSpace(CookieContent))
        {
            ManualCookieValidationMessage = "请先选择要清除手动 Cookie 的平台。";
            IsManualCookieMessageSuccess = false;
            return;
        }

        if (platform is not null)
            await _cookieVault.DeleteAsync(platform.StorageKey, cancellationToken);
        await _cookieVault.DeleteAsync(
            ConfigService.LegacyUnscopedCookieStorageKey,
            cancellationToken);

        _isInitializing = true;
        try
        {
            CookieContent = "";
            LegacyCookiePlatform = "";
        }
        finally
        {
            _isInitializing = false;
        }

        _configService.Config.CookieContent = "";
        _configService.Config.LegacyCookiePlatform = "";
        await _configService.SaveAsync();
        ManualCookieValidationMessage = platform is null
            ? "未保存的手动 Cookie 内容已清空。"
            : $"{platform.DisplayName} 的加密手动 Cookie 已清除。";
        ManualCookieStatusText = "未配置 Cookie";
        IsManualCookieMessageSuccess = true;
    }

    [RelayCommand]
    private async Task CheckAppUpdate()
    {
        IsCheckingAppUpdate = true;
        AppUpdateStatusMessage = "正在连接 GitHub 检查更新...";
        AppUpdateProgress = 0;
        IsAppUpdateDownloaded = false;
        _downloadedInstallerPath = null;

        try
        {
            var result = await _appUpdateService.CheckLatestAsync();
            _availableAppUpdate = result;
            LatestAppVersion = result.LatestVersion;
            IsAppUpdateAvailable = result.IsUpdateAvailable && result.InstallerDownloadUrl is not null;

            AppUpdateStatusMessage = IsAppUpdateAvailable
                ? $"发现新版本 v{result.LatestVersion}，可下载更新包。"
                : $"当前已是最新版本 v{result.CurrentVersion}。";
        }
        catch (Exception ex)
        {
            IsAppUpdateAvailable = false;
            LatestAppVersion = "";
            AppUpdateStatusMessage = $"检查更新失败: {ex.Message}";
        }
        finally
        {
            IsCheckingAppUpdate = false;
            NotifyAppUpdateActionStateChanged();
        }
    }

    [RelayCommand]
    private async Task DownloadAppUpdate()
    {
        if (_availableAppUpdate is null || _availableAppUpdate.InstallerDownloadUrl is null)
        {
            AppUpdateStatusMessage = "请先检查更新。";
            return;
        }

        IsDownloadingAppUpdate = true;
        AppUpdateProgress = 0;
        AppUpdateStatusMessage = "正在下载更新包...";

        try
        {
            var progress = new Progress<double>(value =>
            {
                AppUpdateProgress = (int)Math.Clamp(Math.Round(value), 0, 100);
            });
            _downloadedInstallerPath = await _appUpdateService.DownloadInstallerAsync(_availableAppUpdate, progress);
            AppUpdateProgress = 100;
            IsAppUpdateDownloaded = true;
            AppUpdateStatusMessage = $"更新包已下载: {_availableAppUpdate.InstallerFileName}";
        }
        catch (Exception ex)
        {
            IsAppUpdateDownloaded = false;
            _downloadedInstallerPath = null;
            AppUpdateStatusMessage = $"下载更新失败: {ex.Message}";
        }
        finally
        {
            IsDownloadingAppUpdate = false;
            NotifyAppUpdateActionStateChanged();
        }
    }

    [RelayCommand]
    private async Task InstallAppUpdate()
    {
        if (string.IsNullOrWhiteSpace(_downloadedInstallerPath))
        {
            AppUpdateStatusMessage = "请先下载更新包。";
            return;
        }

        AppUpdateStatusMessage = "正在保存设置并准备安装...";
        if (!await FlushPendingSaveAsync() || !await _configService.SaveAsync())
        {
            AppUpdateStatusMessage = "设置保存失败，已取消启动安装程序，请重试。";
            return;
        }

        if (!_appUpdateService.LaunchInstaller(_downloadedInstallerPath))
        {
            AppUpdateStatusMessage = "安装程序启动失败，请重新下载更新包。";
            return;
        }

        AppUpdateStatusMessage = "安装程序已启动，EasyGet 即将退出。";
        System.Windows.Application.Current?.Shutdown();
    }

    [RelayCommand]
    private async Task SendTgCode()
    {
        if (string.IsNullOrWhiteSpace(TgApiId) || string.IsNullOrWhiteSpace(TgApiHash) || string.IsNullOrWhiteSpace(TgPhoneNumber))
        {
            TgStatusMessage = "请填写完整的 API ID、API Hash 和手机号";
            return;
        }

        IsTgOperating = true;
        TgStatusMessage = "正在发送验证码...";
        ShowTgCodeInput = false;
        ShowTgPasswordInput = false;

        try
        {
            var result = await _telegramDownloadService.SendCodeAsync(TgPhoneNumber.Trim(), TgApiId.Trim(), TgApiHash.Trim());
            if (result == "verification_code")
            {
                ShowTgCodeInput = true;
                TgStatusMessage = "验证码发送成功，请输入收到的验证码";
            }
            else if (result == "password")
            {
                ShowTgCodeInput = true;
                ShowTgPasswordInput = true;
                TgStatusMessage = "请输入验证码及两步验证密码";
            }
            else if (result == null)
            {
                TgStatusMessage = "登录成功！";
                await RefreshTgStatusAsync();
            }
            else
            {
                TgStatusMessage = $"未知的登录状态响应: {result}";
            }
        }
        catch (Exception ex)
        {
            TgStatusMessage = $"发送验证码失败: {ex.Message}";
        }
        finally
        {
            IsTgOperating = false;
        }
    }

    [RelayCommand]
    private async Task SubmitTgCode()
    {
        if (string.IsNullOrWhiteSpace(TgVerificationCode))
        {
            TgStatusMessage = "请输入验证码";
            return;
        }

        IsTgOperating = true;
        TgStatusMessage = "正在提交验证码...";

        try
        {
            var result = await _telegramDownloadService.SubmitCodeAsync(TgVerificationCode.Trim());
            if (result == "password")
            {
                ShowTgPasswordInput = true;
                TgStatusMessage = "该账号开启了两步验证，请输入两步验证密码";
            }
            else if (result == null)
            {
                TgStatusMessage = "登录绑定成功！";
                ShowTgCodeInput = false;
                ShowTgPasswordInput = false;
                TgVerificationCode = "";
                TgTwoFactorPassword = "";
                await RefreshTgStatusAsync();
            }
            else
            {
                TgStatusMessage = $"登录遇到后续要求: {result}";
            }
        }
        catch (Exception ex)
        {
            TgStatusMessage = $"提交验证码失败: {ex.Message}";
        }
        finally
        {
            IsTgOperating = false;
        }
    }

    [RelayCommand]
    private async Task SubmitTgPassword()
    {
        if (string.IsNullOrWhiteSpace(TgTwoFactorPassword))
        {
            TgStatusMessage = "请输入两步验证密码";
            return;
        }

        IsTgOperating = true;
        TgStatusMessage = "正在提交密码...";

        try
        {
            var result = await _telegramDownloadService.SubmitPasswordAsync(TgTwoFactorPassword.Trim());
            if (result == null)
            {
                TgStatusMessage = "两步验证成功，已完成登录绑定！";
                ShowTgCodeInput = false;
                ShowTgPasswordInput = false;
                TgVerificationCode = "";
                TgTwoFactorPassword = "";
                await RefreshTgStatusAsync();
            }
            else
            {
                TgStatusMessage = $"登录失败，继续提示: {result}";
            }
        }
        catch (Exception ex)
        {
            TgStatusMessage = $"提交密码失败: {ex.Message}";
        }
        finally
        {
            IsTgOperating = false;
        }
    }

    [RelayCommand]
    private async Task TgLogOut()
    {
        IsTgOperating = true;
        TgStatusMessage = "正在退出登录...";
        try
        {
            await _telegramDownloadService.LogOutAsync();
            TgStatusMessage = "已成功注销绑定。";
            ShowTgCodeInput = false;
            ShowTgPasswordInput = false;
            TgVerificationCode = "";
            TgTwoFactorPassword = "";
            await RefreshTgStatusAsync();
        }
        catch (Exception ex)
        {
            TgStatusMessage = $"退出登录失败: {ex.Message}";
        }
        finally
        {
            IsTgOperating = false;
        }
    }

    public async Task RefreshTgStatusAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(TgApiId) || string.IsNullOrWhiteSpace(TgApiHash) || string.IsNullOrWhiteSpace(TgPhoneNumber))
            {
                TgLoginStatusText = "未配置凭证";
                return;
            }

            var result = await _telegramDownloadService.CheckLoginStatusAsync();
            if (result == null)
            {
                TgLoginStatusText = "已登录已绑定";
            }
            else
            {
                TgLoginStatusText = $"未登录 (待输入: {result})";
            }
        }
        catch (Exception ex)
        {
            TgLoginStatusText = "未登录";
            System.Diagnostics.Debug.WriteLine($"[SettingsViewModel] RefreshTgStatusAsync failed: {ex.Message}");
        }
    }
}
