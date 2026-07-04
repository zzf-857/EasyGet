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
    private static readonly string[] SupportedDouyinAuthorDirectoryModes =
        ["nickname", "sec_uid", "nickname_uid", "user_sec_uid"];
    internal static readonly string[] SupportedDouyinTemplateVariableNames =
    [
        "id",
        "title",
        "author",
        "author_id",
        "date",
        "year",
        "month",
        "day",
        "time",
        "hour",
        "minute",
        "second",
        "timestamp",
        "type",
        "mode"
    ];
    private static readonly HashSet<string> SupportedDouyinTemplateVariables = new(
        SupportedDouyinTemplateVariableNames,
        StringComparer.Ordinal);
    private static readonly char[] UnsafeDouyinTemplateCharacters =
    [
        '/',
        '\\',
        ':',
        '*',
        '?',
        '"',
        '<',
        '>',
        '|',
        '#'
    ];

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
        config.DouyinMode = NormalizeDouyinMode(config.DouyinMode, defaults.DouyinMode);
        config.DouyinLimit = Math.Max(0, config.DouyinLimit);
        config.DouyinMaxComments = Math.Max(0, config.DouyinMaxComments);
        config.DouyinCommentPageSize = Math.Clamp(
            config.DouyinCommentPageSize,
            1,
            AppConfig.MaxDouyinCommentPageSize);
        config.DouyinLiveMaxDurationSeconds = Math.Max(0, config.DouyinLiveMaxDurationSeconds);
        config.DouyinLiveChunkSize = config.DouyinLiveChunkSize > 0
            ? config.DouyinLiveChunkSize
            : AppConfig.DefaultDouyinLiveChunkSize;
        config.DouyinLiveIdleTimeoutSeconds = config.DouyinLiveIdleTimeoutSeconds > 0
            ? config.DouyinLiveIdleTimeoutSeconds
            : AppConfig.DefaultDouyinLiveIdleTimeoutSeconds;
        config.DouyinFilenameTemplate = NormalizeDouyinTemplate(config.DouyinFilenameTemplate);
        config.DouyinFolderTemplate = NormalizeDouyinTemplate(config.DouyinFolderTemplate);
        config.DouyinAuthorDirectoryMode = NormalizeDouyinAuthorDirectoryMode(
            config.DouyinAuthorDirectoryMode,
            defaults.DouyinAuthorDirectoryMode);
        config.DouyinStartTime = config.DouyinStartTime?.Trim() ?? "";
        config.DouyinEndTime = config.DouyinEndTime?.Trim() ?? "";

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

    internal static string NormalizeDouyinMode(string? value, string defaultValue = "post")
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        var normalizedModes = new List<string>();
        foreach (var rawMode in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var mode = SupportedDouyinModes.FirstOrDefault(
                supported => string.Equals(rawMode, supported, StringComparison.OrdinalIgnoreCase));
            if (mode is null)
                return defaultValue;
            if (!normalizedModes.Contains(mode, StringComparer.Ordinal))
                normalizedModes.Add(mode);
        }

        if (normalizedModes.Count == 0)
            return defaultValue;

        var containsCollectionMode = normalizedModes.Any(mode => mode is "collect" or "collectmix");
        if (containsCollectionMode && normalizedModes.Count > 1)
            return defaultValue;

        return string.Join(",", normalizedModes);
    }

    internal static string NormalizeDouyinAuthorDirectoryMode(string? value, string defaultValue = "nickname")
        => NormalizeOption(value, SupportedDouyinAuthorDirectoryModes, defaultValue);

    internal static string NormalizeDouyinTemplate(string? value)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return AppConfig.DefaultDouyinTemplate;

        if (candidate.Length > 200
            || candidate.IndexOfAny(UnsafeDouyinTemplateCharacters) >= 0
            || candidate.Any(char.IsControl)
            || candidate.Contains("..", StringComparison.Ordinal))
        {
            return AppConfig.DefaultDouyinTemplate;
        }

        var hasId = false;
        for (var index = 0; index < candidate.Length;)
        {
            var current = candidate[index];
            if (current == '}')
                return AppConfig.DefaultDouyinTemplate;

            if (current != '{')
            {
                index++;
                continue;
            }

            var closeIndex = candidate.IndexOf('}', index + 1);
            if (closeIndex < 0)
                return AppConfig.DefaultDouyinTemplate;

            var variable = candidate.Substring(index + 1, closeIndex - index - 1);
            if (variable.Length == 0
                || variable.Contains('{', StringComparison.Ordinal)
                || variable.Contains('}', StringComparison.Ordinal)
                || !SupportedDouyinTemplateVariables.Contains(variable))
            {
                return AppConfig.DefaultDouyinTemplate;
            }

            hasId |= string.Equals(variable, "id", StringComparison.Ordinal);
            index = closeIndex + 1;
        }

        return hasId ? candidate : AppConfig.DefaultDouyinTemplate;
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
