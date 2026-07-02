using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class ConfigServiceTests
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"easyget-config-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveAsync_CreatesBackupBeforeOverwritingExistingConfig()
    {
        Directory.CreateDirectory(_tempDir);
        var configPath = Path.Combine(_tempDir, "config.json");
        var originalJson = """
        {
          "defaultDownloadPath": "D:\\Old",
          "cookieContent": "old-cookie",
          "tgApiId": "12345",
          "tgApiHash": "old-hash",
          "tgPhoneNumber": "+8613800000000"
        }
        """;
        await File.WriteAllTextAsync(configPath, originalJson);

        var service = new ConfigService(_tempDir);
        await service.LoadAsync();
        service.Config.CookieContent = "new-cookie";

        await service.SaveAsync();

        var backupPath = Path.Combine(_tempDir, "config.backup.json");
        Assert.True(File.Exists(backupPath));
        var backupJson = await File.ReadAllTextAsync(backupPath);
        Assert.Contains("old-cookie", backupJson);
        Assert.Contains("old-hash", backupJson);
    }

    [Fact]
    public void NormalizeRuntimeConfig_ClampsPerformanceValuesToSafeRange()
    {
        var config = new AppConfig
        {
            ConcurrentFragments = 0,
            MaxConcurrentDownloads = 99
        };

        ConfigService.NormalizeRuntimeConfig(config);

        Assert.Equal(AppConfig.MinConcurrentFragments, config.ConcurrentFragments);
        Assert.Equal(AppConfig.MaxConcurrentDownloadLimit, config.MaxConcurrentDownloads);
    }

    [Fact]
    public void NormalizeRuntimeConfig_SanitizesInvalidWindowBounds()
    {
        var config = new AppConfig
        {
            Window = new WindowState
            {
                Left = double.PositiveInfinity,
                Top = double.NegativeInfinity,
                Width = 320,
                Height = double.NaN
            }
        };

        ConfigService.NormalizeRuntimeConfig(config);

        Assert.True(double.IsNaN(config.Window.Left));
        Assert.True(double.IsNaN(config.Window.Top));
        Assert.Equal(WindowState.MinWidth, config.Window.Width);
        Assert.Equal(WindowState.DefaultHeight, config.Window.Height);
    }

    [Fact]
    public void NormalizeRuntimeConfig_SanitizesInvalidDownloadOptionText()
    {
        var defaultConfig = new AppConfig();
        var config = new AppConfig
        {
            DefaultDownloadPath = "   ",
            DefaultFormat = "avi",
            DefaultQuality = "144",
            DefaultSubtitle = "manual",
            ProxyAddress = null!,
            CookieContent = null!
        };

        ConfigService.NormalizeRuntimeConfig(config);

        Assert.Equal(defaultConfig.DefaultDownloadPath, config.DefaultDownloadPath);
        Assert.Equal(defaultConfig.DefaultFormat, config.DefaultFormat);
        Assert.Equal(defaultConfig.DefaultQuality, config.DefaultQuality);
        Assert.Equal(defaultConfig.DefaultSubtitle, config.DefaultSubtitle);
        Assert.Equal("", config.ProxyAddress);
        Assert.Equal("", config.CookieContent);
    }
}
