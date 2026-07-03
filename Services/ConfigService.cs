using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>
/// JSON 配置文件管理服务
/// </summary>
public class ConfigService
{
    private static readonly string[] SupportedFormats = ["mp4", "mkv", "webm", "mp3", "m4a"];
    private static readonly string[] SupportedQualities = ["best", "2160", "1080", "720", "480"];
    private static readonly string[] SupportedSubtitles = ["none", "auto", "all"];
    private static readonly string[] SupportedDouyinModes = ["post", "like", "mix", "music", "collect", "collectmix"];

    private static readonly string DefaultConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyGet");

    private static readonly string ConfigDir = DefaultConfigDir;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private AppConfig _config = new();
    private readonly string _configDir;
    private readonly string _configFile;

    public ConfigService()
        : this(DefaultConfigDir)
    {
    }

    internal ConfigService(string configDir)
    {
        _configDir = configDir;
        _configFile = Path.Combine(_configDir, "config.json");
    }

    /// <summary>当前配置</summary>
    public AppConfig Config => _config;

    /// <summary>
    /// 加载配置（如果配置文件不存在则使用默认值）
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            if (File.Exists(_configFile))
            {
                var json = await File.ReadAllTextAsync(_configFile);
                _config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
        }
        catch
        {
            _config = new AppConfig();
        }

        NormalizeRuntimeConfig(_config);

        // 确保下载目录存在
        if (!Directory.Exists(_config.DefaultDownloadPath))
        {
            Directory.CreateDirectory(_config.DefaultDownloadPath);
        }
    }

    /// <summary>
    /// 保存配置到文件
    /// </summary>
    public async Task SaveAsync()
    {
        try
        {
            NormalizeRuntimeConfig(_config);
            Directory.CreateDirectory(_configDir);
            BackupExistingConfig(_configFile);
            var json = JsonSerializer.Serialize(_config, JsonOptions);
            await File.WriteAllTextAsync(_configFile, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfigService] Save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取 tools 目录（用于存放 yt-dlp / ffmpeg）
    /// </summary>
    public static string GetToolsDirectory()
    {
        var dir = Path.Combine(ConfigDir, "tools");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void BackupExistingConfig(string configFile)
    {
        if (!File.Exists(configFile))
            return;

        var directory = Path.GetDirectoryName(configFile);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        var backupFile = Path.Combine(directory, "config.backup.json");
        File.Copy(configFile, backupFile, overwrite: true);
    }

    internal static void NormalizeRuntimeConfig(AppConfig config)
    {
        NormalizeDownloadOptions(config);

        config.ConcurrentFragments = Math.Clamp(
            config.ConcurrentFragments,
            AppConfig.MinConcurrentFragments,
            AppConfig.MaxConcurrentFragments);

        config.MaxConcurrentDownloads = Math.Clamp(
            config.MaxConcurrentDownloads,
            AppConfig.MinConcurrentDownloadLimit,
            AppConfig.MaxConcurrentDownloadLimit);

        NormalizeWindowState(config);
    }

    private static void NormalizeDownloadOptions(AppConfig config)
    {
        var defaults = new AppConfig();

        config.DefaultDownloadPath = string.IsNullOrWhiteSpace(config.DefaultDownloadPath)
            ? defaults.DefaultDownloadPath
            : config.DefaultDownloadPath.Trim();

        config.DefaultFormat = NormalizeOption(config.DefaultFormat, SupportedFormats, defaults.DefaultFormat);
        config.DefaultQuality = NormalizeOption(config.DefaultQuality, SupportedQualities, defaults.DefaultQuality);
        config.DefaultSubtitle = NormalizeOption(config.DefaultSubtitle, SupportedSubtitles, defaults.DefaultSubtitle);
        config.ProxyAddress = config.ProxyAddress?.Trim() ?? "";
        config.CookieContent ??= "";
        config.DouyinMode = NormalizeOption(config.DouyinMode, SupportedDouyinModes, defaults.DouyinMode);
        config.DouyinLimit = Math.Max(0, config.DouyinLimit);

        config.ThemeColor = string.IsNullOrWhiteSpace(config.ThemeColor)
            ? "Indigo"
            : config.ThemeColor.Trim();

        if (!ThemeManager.Palettes.Any(p => p.Name.Equals(config.ThemeColor, StringComparison.OrdinalIgnoreCase)))
        {
            config.ThemeColor = "Indigo";
        }
    }

    private static string NormalizeOption(string? value, string[] supportedValues, string defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        var candidate = value.Trim();
        foreach (var supported in supportedValues)
        {
            if (string.Equals(candidate, supported, StringComparison.OrdinalIgnoreCase))
                return supported;
        }

        return defaultValue;
    }

    private static void NormalizeWindowState(AppConfig config)
    {
        config.Window ??= new WindowState();

        if (!double.IsFinite(config.Window.Left))
            config.Window.Left = double.NaN;

        if (!double.IsFinite(config.Window.Top))
            config.Window.Top = double.NaN;

        config.Window.Width = NormalizeWindowLength(
            config.Window.Width,
            WindowState.MinWidth,
            WindowState.DefaultWidth);

        config.Window.Height = NormalizeWindowLength(
            config.Window.Height,
            WindowState.MinHeight,
            WindowState.DefaultHeight);
    }

    private static double NormalizeWindowLength(double value, double minValue, double defaultValue)
    {
        if (!double.IsFinite(value) || value <= 0)
            return defaultValue;

        return Math.Max(minValue, value);
    }
}
