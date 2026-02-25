using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyGet.Services;

namespace EasyGet.ViewModels;

/// <summary>
/// 设置页 ViewModel
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly EnvironmentService _envService;

    // 环境状态
    [ObservableProperty] private bool _ytDlpFound;
    [ObservableProperty] private string _ytDlpVersion = "";
    [ObservableProperty] private bool _ffmpegFound;
    [ObservableProperty] private string _ffmpegVersion = "";
    [ObservableProperty] private bool _isCheckingEnv;

    // 下载设置
    [ObservableProperty] private string _defaultDownloadPath = "";
    [ObservableProperty] private string _defaultFormat = "mp4";
    [ObservableProperty] private string _defaultQuality = "最高画质";
    [ObservableProperty] private int _maxConcurrentDownloads = 3;
    [ObservableProperty] private int _concurrentFragments = 8;

    // 代理设置
    [ObservableProperty] private bool _useProxy = false;
    [ObservableProperty] private string _proxyAddress = "";

    // 性能
    [ObservableProperty] private bool _useAria2c;

    // Cookie
    [ObservableProperty] private string _cookieContent = "";

    // 更新状态
    [ObservableProperty] private bool _isUpdatingYtDlp;
    [ObservableProperty] private string _updateStatusMessage = "";

    public string[] FormatOptions { get; } = ["mp4", "mkv", "webm", "mp3", "m4a"];
    public string[] QualityOptions { get; } = ["最高画质", "2160p", "1080p", "720p", "480p"];

    public SettingsViewModel(ConfigService configService, EnvironmentService envService)
    {
        _configService = configService;
        _envService = envService;
        LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        var c = _configService?.Config;
        if (c == null) return;
        
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
        await _envService.CheckEnvironmentAsync();
        RefreshEnvironmentStatus();
        IsCheckingEnv = false;
    }

    [RelayCommand]
    private async Task UpdateYtDlp()
    {
        IsUpdatingYtDlp = true;
        UpdateStatusMessage = "";
        var result = await _envService.UpdateYtDlpAsync(new Progress<string>(s => UpdateStatusMessage = s));
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
        {
            DefaultDownloadPath = dialog.SelectedPath;
        }
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

        await _configService.SaveAsync();
    }

    // 当设置值变化时自动保存
    partial void OnDefaultDownloadPathChanged(string value) => SaveSettingsCommand.Execute(null);
    partial void OnDefaultFormatChanged(string value) => SaveSettingsCommand.Execute(null);
    partial void OnDefaultQualityChanged(string value) => SaveSettingsCommand.Execute(null);
    partial void OnMaxConcurrentDownloadsChanged(int value) => SaveSettingsCommand.Execute(null);
    partial void OnConcurrentFragmentsChanged(int value) => SaveSettingsCommand.Execute(null);
    partial void OnUseProxyChanged(bool value) => SaveSettingsCommand.Execute(null);
    partial void OnProxyAddressChanged(string value) => SaveSettingsCommand.Execute(null);
    partial void OnUseAria2cChanged(bool value) => SaveSettingsCommand.Execute(null);
    partial void OnCookieContentChanged(string value) => SaveSettingsCommand.Execute(null);

    [RelayCommand]
    private void ClearCookie()
    {
        CookieContent = "";
    }
}
