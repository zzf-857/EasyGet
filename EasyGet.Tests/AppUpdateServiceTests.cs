using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class AppUpdateServiceTests
{
    [Theory]
    [InlineData("1.1.0", "1.0.9", 1)]
    [InlineData("v1.1.0", "1.1.0+206b077", 0)]
    [InlineData("1.1.0-beta.1", "1.1.0", 0)]
    [InlineData("1.0.9", "1.1.0", -1)]
    public void CompareVersions_NormalizesTagsMetadataAndPrereleaseLabels(string left, string right, int expected)
    {
        var actual = AppUpdateService.CompareVersions(left, right);

        Assert.Equal(expected, Math.Sign(actual));
    }

    [Fact]
    public void ParseLatestRelease_SelectsSetupAssetAndMarksUpdateAvailable()
    {
        const string json = """
        {
          "tag_name": "v1.1.0",
          "html_url": "https://github.com/zzf-857/EasyGet/releases/tag/v1.1.0",
          "assets": [
            {
              "name": "EasyGet-win-x64-Release.zip",
              "size": 72000000,
              "browser_download_url": "https://github.com/zzf-857/EasyGet/releases/download/v1.1.0/EasyGet-win-x64-Release.zip"
            },
            {
              "name": "EasyGet-Setup-v1.1.0.exe",
              "size": 85000000,
              "browser_download_url": "https://github.com/zzf-857/EasyGet/releases/download/v1.1.0/EasyGet-Setup-v1.1.0.exe"
            }
          ]
        }
        """;

        var info = AppUpdateService.ParseLatestReleaseJson(json, "1.0.0");

        Assert.True(info.IsUpdateAvailable);
        Assert.Equal("1.0.0", info.CurrentVersion);
        Assert.Equal("1.1.0", info.LatestVersion);
        Assert.Equal("EasyGet-Setup-v1.1.0.exe", info.InstallerFileName);
        Assert.Equal("https://github.com/zzf-857/EasyGet/releases/download/v1.1.0/EasyGet-Setup-v1.1.0.exe", info.InstallerDownloadUrl?.ToString());
    }

    [Fact]
    public void ParseLatestRelease_ReturnsNoUpdateWhenLatestIsNotNewer()
    {
        const string json = """
        {
          "tag_name": "v1.1.0",
          "html_url": "https://github.com/zzf-857/EasyGet/releases/tag/v1.1.0",
          "assets": [
            {
              "name": "EasyGet-Setup-v1.1.0.exe",
              "size": 85000000,
              "browser_download_url": "https://github.com/zzf-857/EasyGet/releases/download/v1.1.0/EasyGet-Setup-v1.1.0.exe"
            }
          ]
        }
        """;

        var info = AppUpdateService.ParseLatestReleaseJson(json, "1.1.0");

        Assert.False(info.IsUpdateAvailable);
        Assert.Equal("1.1.0", info.LatestVersion);
        Assert.Equal("EasyGet-Setup-v1.1.0.exe", info.InstallerFileName);
    }
}
