using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyGet.Models;
using EasyGet.Services;

namespace EasyGet.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly EnvironmentService _envService;
    private readonly DownloadManager _downloadManager;
    private readonly TelegramDownloadService _telegramDownloadService;
    private readonly IAppUpdateService _appUpdateService;
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

    [ObservableProperty] private string _defaultDownloadPath = "";
    [ObservableProperty] private string _defaultFormat = "mp4";
    [ObservableProperty] private string _defaultQuality = "最高画质";
    [ObservableProperty] private int _maxConcurrentDownloads = 3;
    [ObservableProperty] private int _concurrentFragments = 8;

    [ObservableProperty] private bool _useProxy;
    [ObservableProperty] private string _proxyAddress = "";

    [ObservableProperty] private bool _useAria2c;

    [ObservableProperty] private string _cookieContent = "";

    [ObservableProperty] private bool _autoCategorizeByPlatform = true;

    [ObservableProperty] private bool _isUpdatingYtDlp;
    [ObservableProperty] private string _updateStatusMessage = "";
    [ObservableProperty] private bool _isInstallingTools;
    [ObservableProperty] private string _installStatusStage = "";
    [ObservableProperty] private string _installStatusMessage = "";

    [ObservableProperty] private string _appVersionText = "";
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

    public bool CanCheckEnvironment => !IsCheckingEnv && !IsInstallingTools && !IsUpdatingYtDlp;
    public bool CanInstallMissingTools => CanCheckEnvironment && (!YtDlpFound || !FfmpegFound);
    public bool CanUpdateYtDlp => CanCheckEnvironment && YtDlpFound;
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
        IAppUpdateService? appUpdateService = null)
    {
        _configService = configService;
        _envService = envService;
        _downloadManager = downloadManager;
        _telegramDownloadService = telegramDownloadService;
        _appUpdateService = appUpdateService ?? new AppUpdateService();
        AppVersionText = $"v{_appUpdateService.CurrentVersion}";
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
            AutoCategorizeByPlatform = c.AutoCategorizeByPlatform;
            SelectedThemeColor = c.ThemeColor;
            TgApiId = c.TgApiId;
            TgApiHash = c.TgApiHash;
            TgPhoneNumber = c.TgPhoneNumber;
            AppVersionText = $"v{_appUpdateService.CurrentVersion}";
        }
        finally
        {
            _isInitializing = false;
        }

        RefreshEnvironmentStatus();
        _ = RefreshTgStatusAsync();
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
    private void BrowseDownloadPath()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择默认下载目录",
            InitialDirectory = DefaultDownloadPath,
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            DefaultDownloadPath = dialog.SelectedPath;
    }

    [RelayCommand]
    private async Task SaveSettings()
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
        c.CookieContent = CookieContent;
        c.AutoCategorizeByPlatform = AutoCategorizeByPlatform;
        c.ThemeColor = SelectedThemeColor;
        c.TgApiId = TgApiId;
        c.TgApiHash = TgApiHash;
        c.TgPhoneNumber = TgPhoneNumber;

        ConfigService.NormalizeRuntimeConfig(c);
        SyncNormalizedPerformanceValues(c);

        _downloadManager.UpdateConcurrencyLimit(c.MaxConcurrentDownloads);
        await _configService.SaveAsync();
        SettingsSaved?.Invoke();
    }

    partial void OnDefaultDownloadPathChanged(string value) => AutoSave();
    partial void OnDefaultFormatChanged(string value) => AutoSave();
    partial void OnDefaultQualityChanged(string value) => AutoSave();
    partial void OnMaxConcurrentDownloadsChanged(int value) => AutoSave();
    partial void OnConcurrentFragmentsChanged(int value) => AutoSave();
    partial void OnUseProxyChanged(bool value) => AutoSave();
    partial void OnProxyAddressChanged(string value) => AutoSave();
    partial void OnUseAria2cChanged(bool value) => AutoSave();
    partial void OnCookieContentChanged(string value) => AutoSave();
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

        SaveSettingsCommand.Execute(null);
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

    [RelayCommand]
    private void ClearCookie()
    {
        CookieContent = "";
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
    private void InstallAppUpdate()
    {
        if (string.IsNullOrWhiteSpace(_downloadedInstallerPath))
        {
            AppUpdateStatusMessage = "请先下载更新包。";
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
