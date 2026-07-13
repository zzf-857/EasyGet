using System.Text;
using EasyGet.Models;
using EasyGet.Services;
using EasyGet.Services.Cookies;
using Xunit;

namespace EasyGet.Tests;

public sealed class CookieMigrationTests
{
    [Fact]
    public void AppConfig_DefaultsToCurrentSmartCookieSchemaVersion()
    {
        var config = new AppConfig();

        Assert.Equal(AppConfig.CurrentConfigVersion, config.ConfigVersion);
        Assert.True(config.SmartCookieEnabled);
        Assert.Equal("", config.LegacyCookiePlatform);
    }

    [Fact]
    public void NormalizeRuntimeConfig_RejectsUnsafeLegacyPlatformScopeAndFutureVersion()
    {
        var config = new AppConfig
        {
            ConfigVersion = int.MaxValue,
            LegacyCookiePlatform = " ../twitter "
        };

        ConfigService.NormalizeRuntimeConfig(config);

        Assert.Equal(AppConfig.CurrentConfigVersion, config.ConfigVersion);
        Assert.Equal("", config.LegacyCookiePlatform);
    }

    [Fact]
    public async Task LoadAsync_KeepsUnscopedLegacyHeaderDisabledUntilUserChoosesPlatform()
    {
        using var root = new TestDirectory();
        await root.WriteAsync(
            "config.json",
            $$"""
            {
              "defaultDownloadPath": {{System.Text.Json.JsonSerializer.Serialize(root.DirectoryPath)}},
              "cookieContent": "auth_token=secret",
              "legacyCookiePlatform": ""
            }
            """);
        var service = new ConfigService(root.DirectoryPath, new XorTestProtector());

        await service.LoadAsync();

        Assert.Equal("auth_token=secret", service.Config.CookieContent);
        Assert.Equal("", service.Config.LegacyCookiePlatform);
    }

    [Fact]
    public async Task CompleteLegacyCookieMigrationAsync_EncryptsBackupAndClearsAllPlaintextCopies()
    {
        using var root = new TestDirectory();
        var protector = new XorTestProtector();
        var service = new ConfigService(root.DirectoryPath, protector);
        service.Config.CookieContent = "auth_token=secret";
        service.Config.LegacyCookiePlatform = "twitter";
        await service.SaveAsync();
        var vault = new PlatformCookieVault(root.DirectoryPath, protector);

        await service.CompleteLegacyCookieMigrationAsync(
            "twitter",
            vault,
            CancellationToken.None);

        Assert.Equal("", service.Config.CookieContent);
        Assert.Equal("auth_token=secret", await vault.LoadAsync("twitter", CancellationToken.None));
        var migrationBackup = await File.ReadAllBytesAsync(
            root.Path("config.cookie-migration.backup.bin"));
        Assert.DoesNotContain(
            "auth_token=secret",
            Encoding.UTF8.GetString(migrationBackup),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "auth_token=secret",
            await File.ReadAllTextAsync(root.Path("config.json")),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "auth_token=secret",
            await File.ReadAllTextAsync(root.Path("config.backup.json")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteLegacyCookieMigrationAsync_SplitsDomainScopedFileAcrossKnownPlatforms()
    {
        using var root = new TestDirectory();
        var protector = new XorTestProtector();
        var service = new ConfigService(root.DirectoryPath, protector);
        service.Config.CookieContent = """
            # Netscape HTTP Cookie File
            .youtube.com	TRUE	/	TRUE	0	SID	youtube-secret
            .x.com	TRUE	/	TRUE	0	auth_token	twitter-secret
            .unknown.example	TRUE	/	TRUE	0	session	unknown-secret
            """;
        service.Config.LegacyCookiePlatform = "";
        await service.SaveAsync();
        var vault = new PlatformCookieVault(root.DirectoryPath, protector);

        await service.CompleteLegacyCookieMigrationAsync(
            "",
            vault,
            CancellationToken.None);

        var youtube = await vault.LoadAsync("youtube", CancellationToken.None);
        var twitter = await vault.LoadAsync("twitter", CancellationToken.None);
        Assert.Contains("youtube-secret", youtube, StringComparison.Ordinal);
        Assert.DoesNotContain("twitter-secret", youtube, StringComparison.Ordinal);
        Assert.Contains("twitter-secret", twitter, StringComparison.Ordinal);
        Assert.DoesNotContain("youtube-secret", twitter, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "unknown-secret",
            string.Concat(youtube, twitter),
            StringComparison.Ordinal);
        Assert.Equal("", service.Config.CookieContent);
    }

    private sealed class XorTestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => Transform(plaintext);
        public byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext);

        private static byte[] Transform(byte[] input)
            => input.Select(value => (byte)(value ^ 0xA5)).ToArray();
    }
}
