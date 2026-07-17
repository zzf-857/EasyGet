using EasyGet.Models;
using EasyGet.Services;
using EasyGet.Services.Cookies;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace EasyGet.Tests;

public class ConfigServiceTests
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"easyget-config-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ConfigDirectory_ReturnsTheConfiguredApplicationDataRoot()
    {
        var service = new ConfigService(_tempDir);

        Assert.Equal(_tempDir, service.ConfigDirectory);
    }

    [Fact]
    public async Task SaveAndReload_PreservesCustomDownloadPath()
    {
        var expectedPath = Path.Combine(_tempDir, "用户选择的下载目录");
        var service = new ConfigService(_tempDir);
        service.Config.DefaultDownloadPath = expectedPath;

        Assert.True(await service.SaveAsync());

        var reloaded = new ConfigService(_tempDir);
        await reloaded.LoadAsync();
        Assert.Equal(expectedPath, reloaded.Config.DefaultDownloadPath);
    }

    [Fact]
    public async Task LoadAsync_PrefersVersionedBackupWhenLegacyProcessOverwritesPrimary()
    {
        Directory.CreateDirectory(_tempDir);
        var stalePath = Path.Combine(_tempDir, "stale-default");
        var expectedPath = Path.Combine(_tempDir, "preserved-user-path");
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "config.json"),
            $$"""
              {
                "defaultDownloadPath": "{{stalePath.Replace("\\", "\\\\")}}",
                "defaultFormat": "mp4"
              }
              """);
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "config.backup.json"),
            JsonSerializer.Serialize(new AppConfig
            {
                ConfigVersion = AppConfig.CurrentConfigVersion,
                DefaultDownloadPath = expectedPath
            }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
            }));

        var service = new ConfigService(_tempDir);
        await service.LoadAsync();

        Assert.Equal(expectedPath, service.Config.DefaultDownloadPath);
        var restoredPrimary = await File.ReadAllTextAsync(Path.Combine(_tempDir, "config.json"));
        Assert.Contains(
            $"\"configVersion\": {AppConfig.CurrentConfigVersion}",
            restoredPrimary,
            StringComparison.Ordinal);
        Assert.Contains("preserved-user-path", restoredPrimary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_RecoversCorruptPrimaryWithoutOverwritingGoodBackup()
    {
        Directory.CreateDirectory(_tempDir);
        var expectedPath = Path.Combine(_tempDir, "backup-downloads");
        var backupPath = Path.Combine(_tempDir, "config.backup.json");
        var backupJson = JsonSerializer.Serialize(new AppConfig
        {
            DefaultDownloadPath = expectedPath
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        });
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "config.json"), "{ broken json");
        await File.WriteAllTextAsync(backupPath, backupJson);

        var service = new ConfigService(_tempDir);
        await service.LoadAsync();

        Assert.Equal(expectedPath, service.Config.DefaultDownloadPath);
        Assert.Equal(backupJson, await File.ReadAllTextAsync(backupPath));
        using var restored = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_tempDir, "config.json")));
        Assert.Equal(JsonValueKind.Object, restored.RootElement.ValueKind);
    }

    [Fact]
    public async Task SaveAsync_DoesNotReplaceNewerBackupWithLegacyPrimary()
    {
        Directory.CreateDirectory(_tempDir);
        var expectedPath = Path.Combine(_tempDir, "preserved-downloads");
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "config.json"),
            """
            {
              "defaultDownloadPath": "C:\\stale-default"
            }
            """);
        var backupPath = Path.Combine(_tempDir, "config.backup.json");
        var backupJson = JsonSerializer.Serialize(new AppConfig
        {
            ConfigVersion = AppConfig.CurrentConfigVersion,
            DefaultDownloadPath = expectedPath
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        });
        await File.WriteAllTextAsync(backupPath, backupJson);
        var service = new ConfigService(_tempDir);
        service.Config.DefaultDownloadPath = expectedPath;

        Assert.True(await service.SaveAsync());

        var preservedBackup = JsonSerializer.Deserialize<AppConfig>(
            await File.ReadAllTextAsync(backupPath),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
            });
        Assert.NotNull(preservedBackup);
        Assert.Equal(expectedPath, preservedBackup!.DefaultDownloadPath);
    }

    [Fact]
    public async Task ConcurrentSaveAsync_LeavesValidJsonAndNoTemporaryFiles()
    {
        var services = Enumerable.Range(0, 12)
            .Select(index =>
            {
                var service = new ConfigService(_tempDir);
                service.Config.DefaultDownloadPath = Path.Combine(_tempDir, $"downloads-{index}");
                service.Config.MaxConcurrentDownloads = 1 + index % AppConfig.MaxConcurrentDownloadLimit;
                return service;
            })
            .ToArray();

        var results = await Task.WhenAll(services.Select(service => service.SaveAsync()));

        Assert.All(results, Assert.True);
        using var config = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_tempDir, "config.json")));
        Assert.Equal(JsonValueKind.Object, config.RootElement.ValueKind);
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task SaveAsync_SanitizesCookieBeforeBackingUpOrOverwritingConfig()
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
        Assert.DoesNotContain("old-cookie", backupJson);
        Assert.DoesNotContain("new-cookie", backupJson);
        Assert.Contains("old-hash", backupJson);
        Assert.DoesNotContain(
            "new-cookie",
            await File.ReadAllTextAsync(configPath),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_MigratesScopedCookieIntoEncryptedVault()
    {
        var protector = new XorTestProtector();
        var service = new ConfigService(_tempDir, protector);
        service.Config.CookieContent = "auth_token=scoped-secret";
        service.Config.LegacyCookiePlatform = "twitter";

        await service.SaveAsync();

        var vault = new PlatformCookieVault(_tempDir, protector);
        Assert.Equal(
            "auth_token=scoped-secret",
            await vault.LoadAsync("twitter", CancellationToken.None));
        Assert.Equal("", service.Config.CookieContent);
        foreach (var fileName in new[] { "config.json", "config.backup.json" })
        {
            var json = await File.ReadAllTextAsync(Path.Combine(_tempDir, fileName));
            Assert.DoesNotContain("scoped-secret", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SaveAsync_QuarantinesUnscopedLegacyCookieAndRestoresItOnlyInMemory()
    {
        var protector = new XorTestProtector();
        var service = new ConfigService(_tempDir, protector);
        service.Config.CookieContent = "Cookie: legacy_unscoped=secret-value";
        service.Config.LegacyCookiePlatform = "";

        await service.SaveAsync();

        var vault = new PlatformCookieVault(_tempDir, protector);
        Assert.Equal(
            "Cookie: legacy_unscoped=secret-value",
            await vault.LoadAsync(
                ConfigService.LegacyUnscopedCookieStorageKey,
                CancellationToken.None));
        Assert.Equal("", service.Config.CookieContent);
        Assert.DoesNotContain(
            "secret-value",
            await File.ReadAllTextAsync(Path.Combine(_tempDir, "config.json")),
            StringComparison.Ordinal);

        var reloaded = new ConfigService(_tempDir, protector);
        await reloaded.LoadAsync();

        Assert.Equal("Cookie: legacy_unscoped=secret-value", reloaded.Config.CookieContent);
        Assert.Equal("", reloaded.Config.LegacyCookiePlatform);
        Assert.DoesNotContain(
            "secret-value",
            await File.ReadAllTextAsync(Path.Combine(_tempDir, "config.json")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_WhenCookieEncryptionFails_PreservesExistingPersistentCopy()
    {
        Directory.CreateDirectory(_tempDir);
        var configPath = Path.Combine(_tempDir, "config.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "cookieContent": "auth_token=only-persistent-copy",
              "legacyCookiePlatform": "twitter"
            }
            """);
        var service = new ConfigService(_tempDir, new ThrowingTestProtector());
        await service.LoadAsync();

        await service.SaveAsync();

        Assert.Contains(
            "only-persistent-copy",
            await File.ReadAllTextAsync(configPath),
            StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_tempDir, "config.backup.json")));
    }

    [Fact]
    public async Task LoadAndSave_PreservesDouyinDownloadCommentsToggle()
    {
        var service = new ConfigService(_tempDir);

        await service.LoadAsync();

        AssertAppConfigBool(service.Config, "DouyinDownloadComments", expected: false);
        SetAppConfigBool(service.Config, "DouyinDownloadComments", value: true);

        await service.SaveAsync();

        var reloaded = new ConfigService(_tempDir);
        await reloaded.LoadAsync();

        AssertAppConfigBool(reloaded.Config, "DouyinDownloadComments", expected: true);
    }

    [Fact]
    public async Task LoadAndSave_PreservesDouyinDownloadAvatarToggle()
    {
        var service = new ConfigService(_tempDir);

        await service.LoadAsync();

        AssertAppConfigBool(service.Config, "DouyinDownloadAvatar", expected: false);
        SetAppConfigBool(service.Config, "DouyinDownloadAvatar", value: true);

        await service.SaveAsync();

        var reloaded = new ConfigService(_tempDir);
        await reloaded.LoadAsync();

        AssertAppConfigBool(reloaded.Config, "DouyinDownloadAvatar", expected: true);
    }

    [Fact]
    public async Task LoadAndSave_PreservesDouyinDatabaseAndIncrementalToggles()
    {
        var service = new ConfigService(_tempDir);

        await service.LoadAsync();

        AssertAppConfigBool(service.Config, "DouyinEnableDatabase", expected: false);
        AssertAppConfigBool(service.Config, "DouyinIncrementalDownload", expected: false);
        SetAppConfigBool(service.Config, "DouyinEnableDatabase", value: true);
        SetAppConfigBool(service.Config, "DouyinIncrementalDownload", value: true);

        await service.SaveAsync();

        var reloaded = new ConfigService(_tempDir);
        await reloaded.LoadAsync();

        AssertAppConfigBool(reloaded.Config, "DouyinEnableDatabase", expected: true);
        AssertAppConfigBool(reloaded.Config, "DouyinIncrementalDownload", expected: true);
    }

    [Fact]
    public async Task LoadAndSave_PreservesDouyinBrowserFallbackToggle()
    {
        var service = new ConfigService(_tempDir);

        await service.LoadAsync();

        AssertAppConfigBool(service.Config, "DouyinEnableBrowserFallback", expected: false);
        SetAppConfigBool(service.Config, "DouyinEnableBrowserFallback", value: true);

        await service.SaveAsync();

        var reloaded = new ConfigService(_tempDir);
        await reloaded.LoadAsync();

        AssertAppConfigBool(reloaded.Config, "DouyinEnableBrowserFallback", expected: true);
    }

    [Fact]
    public async Task LoadAndSave_PreservesDouyinLiveRecordingOptions()
    {
        var service = new ConfigService(_tempDir);

        await service.LoadAsync();

        AssertAppConfigInt(service.Config, "DouyinLiveMaxDurationSeconds", 0);
        AssertAppConfigInt(service.Config, "DouyinLiveChunkSize", 65536);
        AssertAppConfigInt(service.Config, "DouyinLiveIdleTimeoutSeconds", 30);
        SetAppConfigInt(service.Config, "DouyinLiveMaxDurationSeconds", 3600);
        SetAppConfigInt(service.Config, "DouyinLiveChunkSize", 131072);
        SetAppConfigInt(service.Config, "DouyinLiveIdleTimeoutSeconds", 45);

        await service.SaveAsync();

        var reloaded = new ConfigService(_tempDir);
        await reloaded.LoadAsync();

        AssertAppConfigInt(reloaded.Config, "DouyinLiveMaxDurationSeconds", 3600);
        AssertAppConfigInt(reloaded.Config, "DouyinLiveChunkSize", 131072);
        AssertAppConfigInt(reloaded.Config, "DouyinLiveIdleTimeoutSeconds", 45);
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
    public async Task LoadAsync_MigratesUntouchedVersion2ConcurrencyDefaults()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "config.json"),
            """
            {
              "configVersion": 2,
              "maxConcurrentDownloads": 3,
              "concurrentFragments": 8
            }
            """);

        var service = new ConfigService(_tempDir);
        await service.LoadAsync();

        Assert.Equal(AppConfig.CurrentConfigVersion, service.Config.ConfigVersion);
        Assert.Equal(AppConfig.GetDefaultConcurrentDownloadLimit(), service.Config.MaxConcurrentDownloads);
        Assert.Equal(AppConfig.GetDefaultConcurrentFragments(), service.Config.ConcurrentFragments);
    }

    [Fact]
    public async Task LoadAsync_PreservesCustomizedVersion2ConcurrencyValues()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "config.json"),
            """
            {
              "configVersion": 2,
              "maxConcurrentDownloads": 4,
              "concurrentFragments": 7
            }
            """);

        var service = new ConfigService(_tempDir);
        await service.LoadAsync();

        Assert.Equal(AppConfig.CurrentConfigVersion, service.Config.ConfigVersion);
        Assert.Equal(4, service.Config.MaxConcurrentDownloads);
        Assert.Equal(7, service.Config.ConcurrentFragments);
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

    [Theory]
    [InlineData("post")]
    [InlineData("like")]
    [InlineData("mix")]
    [InlineData("music")]
    [InlineData("collect")]
    [InlineData("collectmix")]
    public void NormalizeRuntimeConfig_AllowsSupportedDouyinUserModes(string mode)
    {
        var config = new AppConfig
        {
            DouyinMode = mode,
            DouyinLimit = 5
        };

        ConfigService.NormalizeRuntimeConfig(config);

        Assert.Equal(mode, config.DouyinMode);
        Assert.Equal(5, config.DouyinLimit);
    }

    [Theory]
    [InlineData("post,like,mix,music", "post,like,mix,music")]
    [InlineData(" like, mix , music ", "like,mix,music")]
    [InlineData("POST,Like,MIX", "post,like,mix")]
    [InlineData("post,post,like", "post,like")]
    public void NormalizeRuntimeConfig_AllowsSupportedDouyinMultiModes(
        string mode,
        string expected)
    {
        var config = new AppConfig
        {
            DouyinMode = mode
        };

        ConfigService.NormalizeRuntimeConfig(config);

        Assert.Equal(expected, config.DouyinMode);
    }

    [Theory]
    [InlineData("collect,post")]
    [InlineData("collectmix,like")]
    [InlineData("post,unknown")]
    [InlineData(",")]
    public void NormalizeRuntimeConfig_DefaultsInvalidDouyinMultiModes(string mode)
    {
        var config = new AppConfig
        {
            DouyinMode = mode
        };

        ConfigService.NormalizeRuntimeConfig(config);

        Assert.Equal("post", config.DouyinMode);
    }

    [Fact]
    public void NormalizeRuntimeConfig_SanitizesDouyinSpecialOptions()
    {
        var config = new AppConfig
        {
            DouyinMode = "favorites",
            DouyinLimit = -5,
            DouyinDownloadCover = true,
            DouyinDownloadMusic = true,
            DouyinDownloadJson = true
        };
        SetAppConfigBool(config, "DouyinDownloadComments", value: true);
        SetAppConfigBool(config, "DouyinDownloadAvatar", value: true);
        SetAppConfigBool(config, "DouyinEnableDatabase", value: true);
        SetAppConfigBool(config, "DouyinIncrementalDownload", value: true);
        SetAppConfigInt(config, "DouyinMaxComments", -5);
        SetAppConfigInt(config, "DouyinCommentPageSize", 99);
        SetAppConfigInt(config, "DouyinLiveMaxDurationSeconds", -1);
        SetAppConfigInt(config, "DouyinLiveChunkSize", -1);
        SetAppConfigInt(config, "DouyinLiveIdleTimeoutSeconds", 0);

        ConfigService.NormalizeRuntimeConfig(config);

        Assert.Equal("post", config.DouyinMode);
        Assert.Equal(0, config.DouyinLimit);
        Assert.True(config.DouyinDownloadCover);
        Assert.True(config.DouyinDownloadMusic);
        Assert.True(config.DouyinDownloadJson);
        AssertAppConfigBool(config, "DouyinDownloadComments", expected: true);
        AssertAppConfigBool(config, "DouyinDownloadAvatar", expected: true);
        AssertAppConfigBool(config, "DouyinEnableDatabase", expected: true);
        AssertAppConfigBool(config, "DouyinIncrementalDownload", expected: true);
        AssertAppConfigInt(config, "DouyinMaxComments", 0);
        AssertAppConfigInt(config, "DouyinCommentPageSize", 20);
        AssertAppConfigInt(config, "DouyinLiveMaxDurationSeconds", 0);
        AssertAppConfigInt(config, "DouyinLiveChunkSize", 65536);
        AssertAppConfigInt(config, "DouyinLiveIdleTimeoutSeconds", 30);
    }

    [Fact]
    public void NormalizeRuntimeConfig_ClampsDouyinCommentPageSizeToLowerBound()
    {
        var config = new AppConfig();
        SetAppConfigInt(config, "DouyinCommentPageSize", 0);

        ConfigService.NormalizeRuntimeConfig(config);

        AssertAppConfigInt(config, "DouyinCommentPageSize", 1);
    }

    [Theory]
    [InlineData("nickname", "nickname")]
    [InlineData("sec_uid", "sec_uid")]
    [InlineData("nickname_uid", "nickname_uid")]
    [InlineData("user_sec_uid", "user_sec_uid")]
    [InlineData(" SEC_UID ", "sec_uid")]
    [InlineData("unknown", "nickname")]
    public void NormalizeRuntimeConfig_NormalizesDouyinAuthorDirectoryMode(
        string value,
        string expected)
    {
        var config = new AppConfig();
        SetAppConfigString(config, "DouyinAuthorDirectoryMode", value);

        ConfigService.NormalizeRuntimeConfig(config);

        AssertAppConfigString(config, "DouyinAuthorDirectoryMode", expected);
    }

    [Fact]
    public void NormalizeRuntimeConfig_SanitizesDouyinTimeRangeOptions()
    {
        var config = new AppConfig();
        SetAppConfigString(config, "DouyinStartTime", " 2024-01-01 ");
        SetAppConfigString(config, "DouyinEndTime", null);

        ConfigService.NormalizeRuntimeConfig(config);

        AssertAppConfigString(config, "DouyinStartTime", "2024-01-01");
        AssertAppConfigString(config, "DouyinEndTime", "");
        AssertAppConfigBool(config, "DouyinDownloadPinned", expected: false);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void NormalizeRuntimeConfig_DefaultsBlankDouyinTemplates(string? template)
    {
        var config = new AppConfig();
        SetAppConfigString(config, "DouyinFilenameTemplate", template);
        SetAppConfigString(config, "DouyinFolderTemplate", template);

        ConfigService.NormalizeRuntimeConfig(config);

        AssertAppConfigString(config, "DouyinFilenameTemplate", "{date}_{title}_{id}");
        AssertAppConfigString(config, "DouyinFolderTemplate", "{date}_{title}_{id}");
    }

    [Theory]
    [InlineData("{date}_{title}")]
    [InlineData("{date}_{unknown}_{id}")]
    [InlineData("{date}_{title}_{id}/part")]
    [InlineData("{date}_{title}_{id}\\part")]
    [InlineData("{date}_{title}_{id}:part")]
    [InlineData("{date}_{title}_{id}#part")]
    [InlineData("{date}_{title}_{id}_{timestamp:yyyy}")]
    [InlineData("{date}_{title}_{id}_{bad-var}")]
    [InlineData("{date}_{title}_{id}_{author")]
    [InlineData("{date}_{title}_{id}_}")]
    [InlineData(".._{id}")]
    public void NormalizeRuntimeConfig_DefaultsUnsafeUnknownOrMissingIdDouyinTemplates(string template)
    {
        var config = new AppConfig();
        SetAppConfigString(config, "DouyinFilenameTemplate", template);
        SetAppConfigString(config, "DouyinFolderTemplate", template);

        ConfigService.NormalizeRuntimeConfig(config);

        AssertAppConfigString(config, "DouyinFilenameTemplate", "{date}_{title}_{id}");
        AssertAppConfigString(config, "DouyinFolderTemplate", "{date}_{title}_{id}");
    }

    [Fact]
    public void NormalizeRuntimeConfig_DefaultsOverlongDouyinTemplates()
    {
        var config = new AppConfig();
        var template = "{id}_" + new string('x', 201);
        SetAppConfigString(config, "DouyinFilenameTemplate", template);
        SetAppConfigString(config, "DouyinFolderTemplate", template);

        ConfigService.NormalizeRuntimeConfig(config);

        AssertAppConfigString(config, "DouyinFilenameTemplate", "{date}_{title}_{id}");
        AssertAppConfigString(config, "DouyinFolderTemplate", "{date}_{title}_{id}");
    }

    [Fact]
    public void NormalizeRuntimeConfig_AllowsKnownDouyinTemplateVariables()
    {
        var template = " {year}-{month}-{day}_{time}_{hour}{minute}{second}_{author}_{author_id}_{type}_{mode}_{timestamp}_{id} ";
        var expected = "{year}-{month}-{day}_{time}_{hour}{minute}{second}_{author}_{author_id}_{type}_{mode}_{timestamp}_{id}";
        var config = new AppConfig();
        SetAppConfigString(config, "DouyinFilenameTemplate", template);
        SetAppConfigString(config, "DouyinFolderTemplate", template);

        ConfigService.NormalizeRuntimeConfig(config);

        AssertAppConfigString(config, "DouyinFilenameTemplate", expected);
        AssertAppConfigString(config, "DouyinFolderTemplate", expected);
    }

    private static void AssertAppConfigBool(AppConfig config, string propertyName, bool expected)
    {
        var property = typeof(AppConfig).GetProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(expected, Assert.IsType<bool>(property!.GetValue(config)));
    }

    private static void AssertAppConfigString(AppConfig config, string propertyName, string expected)
    {
        var property = typeof(AppConfig).GetProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(expected, Assert.IsType<string>(property!.GetValue(config)));
    }

    private static void AssertAppConfigInt(AppConfig config, string propertyName, int expected)
    {
        var property = typeof(AppConfig).GetProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(expected, Assert.IsType<int>(property!.GetValue(config)));
    }

    private static void SetAppConfigBool(AppConfig config, string propertyName, bool value)
    {
        var property = typeof(AppConfig).GetProperty(propertyName);
        Assert.NotNull(property);
        property!.SetValue(config, value);
    }

    private static void SetAppConfigInt(AppConfig config, string propertyName, int value)
    {
        var property = typeof(AppConfig).GetProperty(propertyName);
        Assert.NotNull(property);
        property!.SetValue(config, value);
    }

    private static void SetAppConfigString(AppConfig config, string propertyName, string? value)
    {
        var property = typeof(AppConfig).GetProperty(propertyName);
        Assert.NotNull(property);
        property!.SetValue(config, value);
    }

    private sealed class XorTestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => Transform(plaintext);
        public byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext);

        private static byte[] Transform(byte[] input)
            => input.Select(value => (byte)(value ^ 0xA5)).ToArray();
    }

    private sealed class ThrowingTestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext)
            => throw new System.Security.Cryptography.CryptographicException(
                "test encryption failed");

        public byte[] Unprotect(byte[] ciphertext)
            => ciphertext.ToArray();
    }
}
