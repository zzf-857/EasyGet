using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyGet.Services;

namespace EasyGet.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly EnvironmentService _envService;
    private readonly DownloadManager _downloadManager;
    private bool _isInitializing;

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

    [ObservableProperty] private string _selectedThemeColor = "Indigo";
    public List<ThemePalette> ThemeOptions => ThemeManager.Palettes;

    public string[] FormatOptions { get; } = ["mp4", "mkv", "webm", "mp3", "m4a"];
    public string[] QualityOptions { get; } = ["最高画质", "2160p", "1080p", "720p", "480p"];

    public bool CanCheckEnvironment => !IsCheckingEnv && !IsInstallingTools && !IsUpdatingYtDlp;
    public bool CanInstallMissingTools => CanCheckEnvironment && (!YtDlpFound || !FfmpegFound);
    public bool CanUpdateYtDlp => CanCheckEnvironment && YtDlpFound;

    public event Action? SettingsSaved;

    public SettingsViewModel(ConfigService configService, EnvironmentService envService, DownloadManager downloadManager)
    {
        _configService = configService;
        _envService = envService;
        _downloadManager = downloadManager;
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
        }
        finally
        {
            _isInitializing = false;
        }

        RefreshEnvironmentStatus();
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
}
