using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyGet.Models;
using EasyGet.Services;
using EasyGet.Services.Cookies;

namespace EasyGet.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsCategoryTitle))]
    [NotifyPropertyChangedFor(nameof(SettingsCategoryDescription))]
    private string _selectedCategory = "常规";

    public string SettingsCategoryTitle => SelectedCategory;
    public string SettingsCategoryDescription => SelectedCategory switch
    {
        "常规" => "外观与基础行为,更改即时生效并自动保存",
        "下载" => "下载目录、媒体参数与并发性能",
        "网络" => "代理连接与网络访问策略",
        "账号与 Cookie" => "平台登录状态与 Cookie 获取策略",
        "集成" => "Telegram 账号绑定与外部服务",
        "更新与环境" => "EasyGet、yt-dlp、ffmpeg 与运行环境",
        "数据管理" => "备份、恢复、诊断与清理本地数据",
        _ => "EasyGet 设置"
    };

    private const int AutoSaveDebounceMilliseconds = 150;
    private static readonly int[] GlobalDownloadRateLimitPresets =
    [
        0,
        512,
        1024,
        2048,
        4096,
        8192,
        16384,
        32768,
        65536,
        131072,
        262144,
        524288,
        AppConfig.MaxGlobalDownloadRateLimitKilobytesPerSecond
    ];
    private static readonly TimeSpan BrowserLoginDetectionTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan BrowserLoginDetectionInterval = TimeSpan.FromSeconds(2);

    private enum SettingsSaveIntent
    {
        Automatic,
        Explicit
    }

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
    private readonly IBrowserCookieLoginDetector _browserLoginDetector;
    private readonly CookieAcquisitionCoordinator? _cookieCoordinator;
    private readonly PlatformCookieVault _cookieVault;
    private readonly SupportBundleService _supportBundleService;
    private readonly UserDataBackupService _userDataBackupService;
    private readonly DownloadPerformanceRecommendation _downloadPerformanceRecommendation =
        DownloadPerformanceAdvisor.GetCurrentRecommendation();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _browserLoginCancellations =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
    private readonly object _autoSaveGate = new();
    private CancellationTokenSource? _autoSaveDebounce;
    private Task _pendingAutoSaveTask = Task.CompletedTask;
    private long _autoSaveRequestedVersion;
    private long _autoSavePersistedVersion;
    private int _lastDiscoveredBrowserProfileCount;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConcurrentDownloadsRiskLevel))]
    [NotifyPropertyChangedFor(nameof(ConcurrentFragmentsDescriptionText))]
    [NotifyPropertyChangedFor(nameof(PerformanceEvaluationRows))]
    [NotifyPropertyChangedFor(nameof(PerformanceStatusText))]
    [NotifyPropertyChangedFor(nameof(PerformanceRiskLevel))]
    private int _maxConcurrentDownloads = AppConfig.GetDefaultConcurrentDownloadLimit();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConcurrentFragmentsRiskLevel))]
    [NotifyPropertyChangedFor(nameof(ConcurrentFragmentsDescriptionText))]
    [NotifyPropertyChangedFor(nameof(PerformanceEvaluationRows))]
    [NotifyPropertyChangedFor(nameof(PerformanceStatusText))]
    [NotifyPropertyChangedFor(nameof(PerformanceRiskLevel))]
    private int _concurrentFragments = AppConfig.GetDefaultConcurrentFragments();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalDownloadRateLimitDisplayText))]
    [NotifyPropertyChangedFor(nameof(GlobalDownloadRateLimitSliderStep))]
    private int _globalDownloadRateLimitKilobytesPerSecond;
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

    [ObservableProperty] private bool _clipboardMonitoringEnabled = true;
    [ObservableProperty] private bool _preventSleepDuringDownloads = true;
    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private bool _systemNotificationsEnabled = true;
    [ObservableProperty] private bool _automaticUpdateChecksEnabled = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageUserData))]
    private bool _isDataManagementOperating;
    [ObservableProperty] private string _dataManagementStatusMessage = "";

    public bool CanManageUserData => !IsDataManagementOperating;

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
    public string PerformanceRecommendationText
    {
        get
        {
            var memoryText = GetPerformanceMemoryText();
            return $"本机 {_downloadPerformanceRecommendation.LogicalProcessorCount} 个逻辑处理器 · {memoryText}；"
                   + $"建议 {_downloadPerformanceRecommendation.RecommendedFragments} 分片 / "
                   + $"{_downloadPerformanceRecommendation.RecommendedConcurrentDownloads} 个同时任务";
        }
    }

    public string ConcurrentFragmentsRiskLevel
        => _downloadPerformanceRecommendation
            .EvaluateConfiguredFragments(ConcurrentFragments)
            .ToString();

    public string ConcurrentDownloadsRiskLevel
        => _downloadPerformanceRecommendation
            .EvaluateConcurrentDownloads(MaxConcurrentDownloads)
            .ToString();

    public string ConcurrentFragmentsDescriptionText
    {
        get
        {
            var assessment = GetDownloadPerformanceAssessment();
            return assessment.IsSmartLimited
                ? $"设置范围 1–32；当前会按 {assessment.EffectiveFragments} 分片运行（智能限流）"
                : "单个任务的并行下载分片数（1–32）";
        }
    }

    public string PerformanceRiskLevel
        => GetDownloadPerformanceAssessment().Risk.ToString();

    public string PerformanceStatusText
    {
        get
        {
            var assessment = GetDownloadPerformanceAssessment();
            var statusText = assessment.Risk switch
            {
                DownloadPerformanceRisk.Normal => "当前配置合理",
                DownloadPerformanceRisk.Warning => "当前配置偏高",
                _ => "当前配置过高"
            };
            var smartLimitText = assessment.IsSmartLimited ? "（已智能限流）" : "";
            var impactText = assessment.Risk switch
            {
                DownloadPerformanceRisk.Normal => "",
                DownloadPerformanceRisk.Warning => "，可能增加磁盘与网络波动",
                _ => "，可能导致卡顿、下载失败或平台限流"
            };

            return $"{statusText} · {assessment.ConcurrentDownloads} 个任务 × "
                   + $"实际 {assessment.EffectiveFragments} 分片{smartLimitText}{impactText}";
        }
    }

    public IReadOnlyList<PerformanceEvaluationRow> PerformanceEvaluationRows
    {
        get
        {
            var recommendation = _downloadPerformanceRecommendation;
            var assessment = GetDownloadPerformanceAssessment();
            var configuredFragmentsText = assessment.IsSmartLimited
                ? $"{assessment.ConfiguredFragments} 个（实际 {assessment.EffectiveFragments}）"
                : $"{assessment.ConfiguredFragments} 个";
            var fragmentRationale = assessment.IsSmartLimited
                ? "同时任务数达到 8 后，程序会把实际单任务分片限制为 4"
                : "影响每个下载任务建立的连接数量";
            var memoryReference = recommendation.MemoryBudgetBytes > 0
                ? $"内存预算：{recommendation.MemoryFragmentBudget} 分片 / "
                  + $"{recommendation.MemoryConcurrentDownloadsBudget} 个任务"
                : "未获取，当前仅按处理器预算估算";

            return
            [
                new PerformanceEvaluationRow(
                    "处理器",
                    $"{recommendation.LogicalProcessorCount} 个逻辑处理器",
                    $"CPU 预算：{recommendation.ProcessorFragmentBudget} 分片 / "
                    + $"{recommendation.ProcessorConcurrentDownloadsBudget} 个任务",
                    "解析、合并和后处理会共享处理器资源"),
                new PerformanceEvaluationRow(
                    "运行时内存预算",
                    GetPerformanceMemoryText(),
                    memoryReference,
                    "并发任务会同时占用下载器和媒体合并进程内存"),
                new PerformanceEvaluationRow(
                    "综合建议",
                    $"{recommendation.RecommendedFragments} 分片 / "
                    + $"{recommendation.RecommendedConcurrentDownloads} 个任务",
                    $"建议峰值连接约 {recommendation.RecommendedPeakConnections}",
                    "取 CPU、内存和共享连接策略中更保守的结果"),
                new PerformanceEvaluationRow(
                    "同时任务数",
                    $"{assessment.ConcurrentDownloads} 个",
                    DescribePerformanceRiskRange(recommendation.RecommendedConcurrentDownloads),
                    "直接决定同时运行的下载进程数量",
                    ConcurrentDownloadsRiskLevel),
                new PerformanceEvaluationRow(
                    "单任务分片",
                    configuredFragmentsText,
                    DescribePerformanceRiskRange(recommendation.RecommendedFragments),
                    fragmentRationale,
                    ConcurrentFragmentsRiskLevel),
                new PerformanceEvaluationRow(
                    "总连接预算",
                    $"{assessment.ConcurrentDownloads} × {assessment.EffectiveFragments} = {assessment.CurrentPeakConnections}",
                    DescribePerformanceRiskRange(recommendation.RecommendedPeakConnections),
                    "用于估算网络、磁盘和平台请求压力",
                    assessment.ConnectionRisk.ToString()),
                new PerformanceEvaluationRow(
                    "未自动测速",
                    "磁盘 / 网络 / 前台负载",
                    "结合任务管理器与实际下载稳定性判断",
                    "若磁盘满载、速度剧烈波动或失败率升高，优先降低同时任务数")
            ];
        }
    }

    private string GetPerformanceMemoryText()
        => _downloadPerformanceRecommendation.MemoryBudgetBytes > 0
            ? $"运行时可用内存预算约 {_downloadPerformanceRecommendation.MemoryBudgetBytes / (1024d * 1024d * 1024d):0.#} GB"
            : "内存信息未获取";

    private static string DescribePerformanceRiskRange(int recommendedValue)
    {
        var warningUpperBound = DownloadPerformanceAdvisor.GetWarningUpperBound(recommendedValue);
        return $"正常 ≤ {recommendedValue}；黄色 ≤ {warningUpperBound}；红色 > {warningUpperBound}";
    }

    public string GlobalDownloadRateLimitDisplayText
        => GlobalDownloadRateLimitKilobytesPerSecond <= 0
            ? "不限速"
            : GlobalDownloadRateLimitKilobytesPerSecond % 1024 == 0
                ? $"{GlobalDownloadRateLimitKilobytesPerSecond / 1024:N0} MB/s"
                : $"{GlobalDownloadRateLimitKilobytesPerSecond:N0} KB/s";
    public int GlobalDownloadRateLimitSliderMaximum => GlobalDownloadRateLimitPresets.Length - 1;
    public int GlobalDownloadRateLimitSliderStep
    {
        get => ResolveGlobalDownloadRateLimitSliderStep(
            GlobalDownloadRateLimitKilobytesPerSecond);
        set => GlobalDownloadRateLimitKilobytesPerSecond =
            GlobalDownloadRateLimitPresets[Math.Clamp(
                value,
                0,
                GlobalDownloadRateLimitSliderMaximum)];
    }
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
    public Func<string, string, bool>? ConfirmFunc { get; set; } = ConfirmationDialogService.Show;

    private static int ResolveGlobalDownloadRateLimitSliderStep(int rateLimit)
    {
        var normalizedRateLimit = Math.Clamp(
            rateLimit,
            AppConfig.MinGlobalDownloadRateLimitKilobytesPerSecond,
            AppConfig.MaxGlobalDownloadRateLimitKilobytesPerSecond);
        var closestIndex = 0;
        var closestDistance = long.MaxValue;
        for (var index = 0; index < GlobalDownloadRateLimitPresets.Length; index++)
        {
            var distance = Math.Abs(
                (long)GlobalDownloadRateLimitPresets[index] - normalizedRateLimit);
            if (distance >= closestDistance)
                continue;

            closestIndex = index;
            closestDistance = distance;
        }

        return closestIndex;
    }

    private DownloadPerformanceAssessment GetDownloadPerformanceAssessment()
        => _downloadPerformanceRecommendation.Assess(
            ConcurrentFragments,
            MaxConcurrentDownloads);

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
        IDefaultBrowserLauncher? defaultBrowserLauncher = null,
        IBrowserCookieLoginDetector? browserLoginDetector = null,
        SupportBundleService? supportBundleService = null,
        UserDataBackupService? userDataBackupService = null)
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
        _browserLoginDetector = browserLoginDetector ?? new BrowserCookieLoginDetector();
        _cookieCoordinator = cookieCoordinator;
        _cookieVault = cookieVault ?? new PlatformCookieVault(configService.ConfigDirectory);
        _supportBundleService = supportBundleService ?? new SupportBundleService(
            configService.ConfigDirectory,
            [Path.Combine(AppContext.BaseDirectory, "logs")]);
        _userDataBackupService = userDataBackupService ?? new UserDataBackupService(
            UserDataBackupPaths.FromConfigDirectory(configService.ConfigDirectory));
        _configService.DefaultDownloadPathChanged += OnSharedDefaultDownloadPathChanged;
        AppVersionText = $"v{_appUpdateService.CurrentVersion}";
        AppRuntimeText = _appUpdateService.RuntimeDescription;
    }

    [RelayCommand]
    private void SelectCategory(string? category)
    {
        if (!string.IsNullOrWhiteSpace(category))
            SelectedCategory = category;
    }

    [RelayCommand(CanExecute = nameof(CanApplyRecommendedPerformanceSettings))]
    private void ApplyRecommendedPerformanceSettings()
    {
        ConcurrentFragments = _downloadPerformanceRecommendation.RecommendedFragments;
        MaxConcurrentDownloads = _downloadPerformanceRecommendation.RecommendedConcurrentDownloads;
    }

    private bool CanApplyRecommendedPerformanceSettings()
        => ConcurrentFragments != _downloadPerformanceRecommendation.RecommendedFragments
           || MaxConcurrentDownloads != _downloadPerformanceRecommendation.RecommendedConcurrentDownloads;

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
            GlobalDownloadRateLimitKilobytesPerSecond = c.GlobalDownloadRateLimitKilobytesPerSecond;
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
            ClipboardMonitoringEnabled = c.ClipboardMonitoringEnabled;
            PreventSleepDuringDownloads = c.PreventSleepDuringDownloads;
            MinimizeToTray = c.MinimizeToTray;
            SystemNotificationsEnabled = c.SystemNotificationsEnabled;
            AutomaticUpdateChecksEnabled = c.AutomaticUpdateChecksEnabled;
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
        _ = RefreshCookieStatus(CancellationToken.None);
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
    private async Task RefreshCookieStatus(CancellationToken cancellationToken)
    {
        if (IsRefreshingCookieStatus)
            return;

        IsRefreshingCookieStatus = true;
        try
        {
            var profiles = await Task.Run(_cookieProfiles.Discover, cancellationToken);
            var platforms = MediaPlatformResolver.KnownPlatforms;
            var detection = await _browserLoginDetector.DetectAsync(
                profiles,
                platforms,
                cancellationToken);
            var health = _cookieHealthStore.Snapshot();
            _lastDiscoveredBrowserProfileCount = profiles.Count;
            var verifiedPlatforms = 0;

            foreach (var platform in platforms)
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

                var item = CookiePlatformStatuses.FirstOrDefault(status =>
                    string.Equals(status.StorageKey, platform.StorageKey, StringComparison.Ordinal));
                if (item is null)
                {
                    item = new CookiePlatformStatusItem
                    {
                        PlatformId = platform.Id,
                        StorageKey = platform.StorageKey,
                        DisplayName = platform.DisplayName
                    };
                    CookiePlatformStatuses.Add(item);
                }

                var browserLoginDetected = detection.TryGetProfile(
                    platform.StorageKey,
                    out var detectedProfile);
                if (item.IsOperating)
                {
                    if (successful is not null)
                        verifiedPlatforms++;
                    continue;
                }

                item.IsDetected = browserLoginDetected;
                if (successful is not null)
                {
                    verifiedPlatforms++;
                    item.IsAvailable = true;
                    item.NeedsLogin = false;
                    item.StatusText = $"最近验证可用 · {DescribeCookieSource(successful.Source)}";
                }
                else if (browserLoginDetected)
                {
                    item.IsAvailable = false;
                    item.NeedsLogin = false;
                    item.StatusText = $"已检测到 {detectedProfile.BrowserName} 登录状态 · 下载时自动读取 Cookie";
                }
                else if (profiles.Count > 0)
                {
                    item.IsAvailable = false;
                    item.NeedsLogin = detection.ReadableProfileCount > 0;
                    item.StatusText = detection.ReadableProfileCount > 0
                        ? "未检测到该平台登录 Cookie · 可点击浏览器登录"
                        : "浏览器配置已发现，但登录状态暂时无法读取 · 下载时仍会自动尝试";
                }
                else
                {
                    item.IsAvailable = false;
                    item.NeedsLogin = true;
                    item.StatusText = "未发现可复用浏览器配置，首次使用时需要登录";
                }
            }

            UpdateCookieStatusSummary(
                profiles.Count,
                detection.AuthenticatedProfiles.Count,
                verifiedPlatforms);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CookieStatusSummary = "登录状态检测已取消";
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

    private void UpdateCookieStatusSummary(
        int profileCount,
        int detectedPlatformCount,
        int verifiedPlatformCount)
    {
        CookieStatusSummary = profileCount == 0
            ? $"未发现受支持浏览器配置 · 检测到 {detectedPlatformCount} 个平台登录 · {verifiedPlatformCount} 个平台近期下载验证"
            : $"发现 {profileCount} 个浏览器配置 · 检测到 {detectedPlatformCount} 个平台登录 · {verifiedPlatformCount} 个平台近期下载验证";
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

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task LoginPlatform(CookiePlatformStatusItem? item)
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

        using var loginCancellation = new CancellationTokenSource();
        if (!_browserLoginCancellations.TryAdd(item.StorageKey, loginCancellation))
            return;

        using var timeoutCancellation = new CancellationTokenSource(BrowserLoginDetectionTimeout);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            loginCancellation.Token,
            timeoutCancellation.Token);
        var cancellationToken = operationCancellation.Token;
        item.IsOperating = true;
        item.StatusText = "正在打开系统默认浏览器...";
        var browserOpened = false;
        try
        {
            await _defaultBrowserLauncher.OpenAsync(platform.LoginUri, cancellationToken);
            browserOpened = true;
            item.IsAvailable = false;
            item.IsDetected = false;
            item.NeedsLogin = false;
            item.StatusText = "已打开系统默认浏览器 · 请完成登录，EasyGet 正在自动检测（最多 3 分钟）";

            var waitResult = await WaitForBrowserLoginAsync(platform, item, cancellationToken);
            _lastDiscoveredBrowserProfileCount = Math.Max(
                _lastDiscoveredBrowserProfileCount,
                waitResult.DiscoveredProfileCount);
            if (waitResult.Profile is not null)
            {
                item.IsDetected = true;
                item.NeedsLogin = false;
                item.StatusText = $"已检测到 {waitResult.Profile.BrowserName} 登录状态 · 下载时将优先读取此配置";
                UpdateCookieStatusSummary(
                    _lastDiscoveredBrowserProfileCount,
                    CookiePlatformStatuses.Count(status => status.IsDetected),
                    CookiePlatformStatuses.Count(status => status.IsAvailable));
                return;
            }

            item.IsDetected = false;
            item.NeedsLogin = waitResult.AnyReadableProfile;
            item.StatusText = waitResult.AnyUnreadableProfile
                ? "未能读取正在使用的浏览器 Cookie · 请关闭浏览器后重新扫描，或使用兼容登录"
                : waitResult.AnyReadableProfile
                    ? "暂未检测到该平台登录 Cookie · 完成登录后点击上方“重新扫描”"
                    : "未发现可读取的浏览器 Cookie · 可尝试兼容登录";
        }
        catch (OperationCanceledException) when (loginCancellation.IsCancellationRequested)
        {
            item.StatusText = browserOpened
                ? "已停止自动检测 · 浏览器中已完成的登录不受影响"
                : "打开浏览器操作已取消";
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            item.StatusText = browserOpened
                ? "自动检测已结束 · 请关闭浏览器后重新扫描，或使用兼容登录"
                : "打开系统默认浏览器超时，请重试";
        }
        catch (Exception)
        {
            item.StatusText = browserOpened
                ? "系统浏览器已打开，但自动检测失败 · 完成登录后点击上方“刷新”"
                : "无法打开系统默认浏览器，请检查 Windows 默认应用设置";
        }
        finally
        {
            _browserLoginCancellations.TryRemove(item.StorageKey, out _);
            item.IsOperating = false;
        }
    }

    [RelayCommand]
    private void CancelPlatformLogin(CookiePlatformStatusItem? item)
    {
        if (item is null
            || !_browserLoginCancellations.TryGetValue(item.StorageKey, out var cancellation))
        {
            return;
        }

        item.StatusText = "正在停止自动检测...";
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The detection completed between the dictionary lookup and cancellation.
        }
    }

    private async Task<BrowserLoginWaitResult> WaitForBrowserLoginAsync(
        MediaPlatformDefinition platform,
        CookiePlatformStatusItem item,
        CancellationToken cancellationToken)
    {
        var deadlineUtc = DateTime.UtcNow + BrowserLoginDetectionTimeout;
        var discoveredProfileCount = 0;
        var anyReadableProfile = false;
        var anyUnreadableProfile = false;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profiles = await Task.Run(_cookieProfiles.Discover, cancellationToken);
            discoveredProfileCount = Math.Max(discoveredProfileCount, profiles.Count);
            var detection = await _browserLoginDetector.DetectAsync(
                profiles,
                [platform],
                cancellationToken);
            anyReadableProfile |= detection.ReadableProfileCount > 0;
            anyUnreadableProfile |= detection.UnreadableProfileCount > 0;
            if (detection.TryGetProfile(platform.StorageKey, out var profile))
            {
                return new BrowserLoginWaitResult(
                    profile,
                    discoveredProfileCount,
                    anyReadableProfile,
                    anyUnreadableProfile);
            }

            item.StatusText = detection.UnreadableProfileCount > 0
                ? "浏览器 Cookie 正被占用 · 完成登录后请关闭浏览器，EasyGet 将继续检测"
                : "正在等待浏览器登录 · EasyGet 每 2 秒自动检测一次";

            var remaining = deadlineUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;
            await Task.Delay(
                remaining < BrowserLoginDetectionInterval
                    ? remaining
                    : BrowserLoginDetectionInterval,
                cancellationToken);
        }
        while (DateTime.UtcNow < deadlineUtc);

        return new BrowserLoginWaitResult(
            null,
            discoveredProfileCount,
            anyReadableProfile,
            anyUnreadableProfile);
    }

    private sealed record BrowserLoginWaitResult(
        BrowserProfile? Profile,
        int DiscoveredProfileCount,
        bool AnyReadableProfile,
        bool AnyUnreadableProfile);

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
    private async Task ConfirmClearAllManagedSessions()
    {
        if (ConfirmFunc?.Invoke(
                "确定要清除 EasyGet 保存的全部平台登录数据吗？系统浏览器中的登录不会受影响。",
                "清除全部登录数据") != true)
        {
            return;
        }

        await ClearAllManagedSessions(CancellationToken.None);
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
        var targetVersion = CancelPendingAutoSave();
        if (await PersistSettingsAsync(SettingsSaveIntent.Explicit))
            MarkAutoSaveVersionPersisted(targetVersion);
    }

    private async Task<bool> PersistSettingsAsync(SettingsSaveIntent saveIntent)
    {
        await _settingsSaveGate.WaitAsync();
        try
        {
            var c = _configService.Config;
            _configService.UpdateDefaultDownloadPath(
                string.IsNullOrWhiteSpace(DefaultDownloadPath)
                    ? c.DefaultDownloadPath
                    : DefaultDownloadPath);
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
            c.GlobalDownloadRateLimitKilobytesPerSecond = GlobalDownloadRateLimitKilobytesPerSecond;
            c.UseProxy = UseProxy;
            c.ProxyAddress = ProxyAddress;
            c.UseAria2c = UseAria2c;
            c.SmartCookieEnabled = SmartCookieEnabled;

            var manualCookieContent = CookieContent;
            var manualCookiePlatform = LegacyCookiePlatform;
            var selectedCookiePlatform = "";
            var canPersistManualCookie = false;
            if (saveIntent == SettingsSaveIntent.Explicit)
            {
                selectedCookiePlatform = MediaPlatformResolver.KnownPlatforms
                    .FirstOrDefault(platform => string.Equals(
                        platform.StorageKey,
                        manualCookiePlatform?.Trim(),
                        StringComparison.Ordinal))
                    ?.StorageKey ?? "";
                var cookieContentRequiresPlatform = !string.IsNullOrWhiteSpace(manualCookieContent)
                                                    && !CookieFileSerializer.HasExplicitDomainRows(manualCookieContent);
                canPersistManualCookie = !cookieContentRequiresPlatform
                                         || selectedCookiePlatform.Length > 0;
                if (!canPersistManualCookie)
                {
                    ManualCookieValidationMessage = "请先选择所属平台，再保存 Header 格式 Cookie。";
                    IsManualCookieMessageSuccess = false;
                }
                else if (!string.IsNullOrWhiteSpace(manualCookieContent))
                {
                    ManualCookieValidationMessage = "";
                    IsManualCookieMessageSuccess = false;
                }

                if (canPersistManualCookie)
                {
                    c.CookieContent = manualCookieContent;
                    c.LegacyCookiePlatform = string.IsNullOrWhiteSpace(c.CookieContent)
                        ? ""
                        : selectedCookiePlatform;
                }
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
            c.ClipboardMonitoringEnabled = ClipboardMonitoringEnabled;
            c.PreventSleepDuringDownloads = PreventSleepDuringDownloads;
            c.MinimizeToTray = MinimizeToTray;
            c.SystemNotificationsEnabled = SystemNotificationsEnabled;
            c.AutomaticUpdateChecksEnabled = AutomaticUpdateChecksEnabled;
            c.ThemeColor = SelectedThemeColor;
            c.TgApiId = TgApiId;
            c.TgApiHash = TgApiHash;
            c.TgPhoneNumber = TgPhoneNumber;

            ConfigService.NormalizeRuntimeConfig(c);
            SyncNormalizedPerformanceValues(c);
            SyncNormalizedDouyinValues(c);

            _downloadManager.UpdateConcurrencyLimit(c.MaxConcurrentDownloads);
            if (saveIntent == SettingsSaveIntent.Explicit
                && canPersistManualCookie
                && !string.IsNullOrWhiteSpace(c.CookieContent))
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
                var cookieDraftUnchanged = string.Equals(
                                               CookieContent,
                                               manualCookieContent,
                                               StringComparison.Ordinal)
                                           && string.Equals(
                                               LegacyCookiePlatform,
                                               manualCookiePlatform,
                                               StringComparison.Ordinal);
                if (cookieDraftUnchanged)
                {
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
                else
                {
                    ManualCookieValidationMessage = "先前的手动 Cookie 已加密保存，当前修改仍待保存。";
                    ManualCookieStatusText = string.IsNullOrWhiteSpace(CookieContent)
                        ? "未配置 Cookie"
                        : "待加密保存";
                    IsManualCookieMessageSuccess = false;
                }
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

    private void OnSharedDefaultDownloadPathChanged(string path)
    {
        void Apply()
        {
            var wasInitializing = _isInitializing;
            _isInitializing = true;
            try
            {
                DefaultDownloadPath = path;
            }
            finally
            {
                _isInitializing = wasInitializing;
            }
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Apply();
        else
            dispatcher.Invoke(Apply);
    }

    partial void OnDefaultFormatChanged(string value) => AutoSave();
    partial void OnDefaultQualityChanged(string value) => AutoSave();
    partial void OnMaxConcurrentDownloadsChanged(int value)
    {
        ApplyRecommendedPerformanceSettingsCommand.NotifyCanExecuteChanged();
        AutoSave();
    }

    partial void OnConcurrentFragmentsChanged(int value)
    {
        ApplyRecommendedPerformanceSettingsCommand.NotifyCanExecuteChanged();
        AutoSave();
    }
    partial void OnGlobalDownloadRateLimitKilobytesPerSecondChanged(int value) => AutoSave();
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
    partial void OnClipboardMonitoringEnabledChanged(bool value) => AutoSave();
    partial void OnPreventSleepDuringDownloadsChanged(bool value) => AutoSave();
    partial void OnMinimizeToTrayChanged(bool value) => AutoSave();
    partial void OnSystemNotificationsEnabledChanged(bool value) => AutoSave();
    partial void OnAutomaticUpdateChecksEnabledChanged(bool value) => AutoSave();
    partial void OnTgApiIdChanged(string value) => AutoSave();
    partial void OnTgApiHashChanged(string value) => AutoSave();
    partial void OnTgPhoneNumberChanged(string value) => AutoSave();
    partial void OnSelectedThemeColorChanged(string value)
    {
        if (_isInitializing)
            return;

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
    partial void OnIsDataManagementOperatingChanged(bool value)
    {
        CreateDataBackupCommand.NotifyCanExecuteChanged();
        RestoreDataBackupCommand.NotifyCanExecuteChanged();
        CreateSupportBundleCommand.NotifyCanExecuteChanged();
    }

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
            if (await PersistSettingsAsync(SettingsSaveIntent.Automatic))
                MarkAutoSaveVersionPersisted(version);
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

            if (!await PersistSettingsAsync(SettingsSaveIntent.Automatic))
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

    private long CancelPendingAutoSave()
    {
        CancellationTokenSource? debounce;
        long targetVersion;
        lock (_autoSaveGate)
        {
            debounce = _autoSaveDebounce;
            targetVersion = _autoSaveRequestedVersion;
        }
        TryCancelDebounce(debounce);
        return targetVersion;
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

    private void MarkAutoSaveVersionPersisted(long version)
    {
        lock (_autoSaveGate)
            _autoSavePersistedVersion = Math.Max(_autoSavePersistedVersion, version);
    }

    private void SyncNormalizedPerformanceValues(EasyGet.Models.AppConfig config)
    {
        if (MaxConcurrentDownloads == config.MaxConcurrentDownloads
            && ConcurrentFragments == config.ConcurrentFragments
            && GlobalDownloadRateLimitKilobytesPerSecond == config.GlobalDownloadRateLimitKilobytesPerSecond)
        {
            return;
        }

        _isInitializing = true;
        try
        {
            MaxConcurrentDownloads = config.MaxConcurrentDownloads;
            ConcurrentFragments = config.ConcurrentFragments;
            GlobalDownloadRateLimitKilobytesPerSecond = config.GlobalDownloadRateLimitKilobytesPerSecond;
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
    private async Task ConfirmClearCookie()
    {
        if (ConfirmFunc?.Invoke(
                "确定要清除手动导入并加密保存的 Cookie 吗？",
                "清除手动 Cookie") != true)
        {
            return;
        }

        await ClearCookie(CancellationToken.None);
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

    [RelayCommand(CanExecute = nameof(CanManageUserData))]
    private async Task CreateDataBackup()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "备份 EasyGet 用户数据",
            Filter = "EasyGet 备份 (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"EasyGet-backup-{DateTime.Now:yyyyMMdd-HHmm}.zip"
        };
        if (dialog.ShowDialog() != true)
            return;

        IsDataManagementOperating = true;
        DataManagementStatusMessage = "正在创建脱敏备份...";
        try
        {
            var preview = await _userDataBackupService.CreateBackupAsync(dialog.FileName);
            DataManagementStatusMessage = $"备份完成：{preview.HistoryRecordCount} 条历史记录。";
        }
        catch (Exception ex)
        {
            DataManagementStatusMessage = $"备份失败：{ex.Message}";
        }
        finally
        {
            IsDataManagementOperating = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageUserData))]
    private async Task RestoreDataBackup()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "恢复 EasyGet 用户数据",
            Filter = "EasyGet 备份 (*.zip)|*.zip",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
            return;

        var validation = await _userDataBackupService.ValidateBackupAsync(dialog.FileName);
        if (!validation.IsValid || validation.Preview is null)
        {
            DataManagementStatusMessage = "备份校验失败：" + string.Join("；", validation.Errors);
            return;
        }

        if (ConfirmFunc?.Invoke(
                $"将恢复 {validation.Preview.HistoryRecordCount} 条历史记录和非敏感设置。\n当前 Cookie、登录会话和 Telegram 凭据不会被覆盖。是否继续？",
                "恢复用户数据") != true)
        {
            return;
        }

        IsDataManagementOperating = true;
        DataManagementStatusMessage = "正在校验并恢复备份...";
        try
        {
            await _userDataBackupService.RestoreBackupAsync(dialog.FileName);
            DataManagementStatusMessage = "恢复完成，请重启 EasyGet 载入数据。";
        }
        catch (Exception ex)
        {
            DataManagementStatusMessage = $"恢复失败：{ex.Message}";
        }
        finally
        {
            IsDataManagementOperating = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageUserData))]
    private async Task CreateSupportBundle()
    {
        IsDataManagementOperating = true;
        DataManagementStatusMessage = "正在收集并脱敏诊断信息...";
        try
        {
            var bundlePath = await _supportBundleService.CreateAsync(
                _appUpdateService.CurrentVersion,
                new Dictionary<string, string>
                {
                    ["runtime"] = _appUpdateService.RuntimeDescription,
                    ["yt-dlp"] = YtDlpVersion,
                    ["ffmpeg"] = FfmpegVersion
                });
            DataManagementStatusMessage = $"诊断包已生成：{bundlePath}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{bundlePath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            DataManagementStatusMessage = $"诊断包生成失败：{ex.Message}";
        }
        finally
        {
            IsDataManagementOperating = false;
        }
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
    private async Task ConfirmTgLogOut()
    {
        if (ConfirmFunc?.Invoke(
                "确定要移除本机保存的 Telegram 登录会话吗？",
                "移除 Telegram 会话") != true)
        {
            return;
        }

        await TgLogOut();
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
