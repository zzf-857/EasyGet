using System.IO;
using System.Text.Json;
using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>
/// JSON 配置文件管理服务
/// </summary>
public class ConfigService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyGet");

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private AppConfig _config = new();

    /// <summary>当前配置</summary>
    public AppConfig Config => _config;

    /// <summary>
    /// 加载配置（如果配置文件不存在则使用默认值）
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var json = await File.ReadAllTextAsync(ConfigFile);
                _config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
        }
        catch
        {
            _config = new AppConfig();
        }

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
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(_config, JsonOptions);
            await File.WriteAllTextAsync(ConfigFile, json);
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
}
