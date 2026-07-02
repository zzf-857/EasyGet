using EasyGet.Models;
using EasyGet.Services;
using System.Net;
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

    [Fact]
    public async Task DownloadInstallerAsync_ClosesTempFileBeforeMovingToFinalInstaller()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"easyget-update-tests-{Guid.NewGuid():N}");
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var service = new AppUpdateService(
            new HttpClient(new StubHttpMessageHandler(payload)),
            tempDir);
        var info = new AppUpdateInfo
        {
            LatestVersion = "1.1.1",
            InstallerFileName = "EasyGet-Setup-v1.1.1.exe",
            InstallerDownloadUrl = new Uri("https://example.com/EasyGet-Setup-v1.1.1.exe"),
            InstallerSize = payload.Length
        };

        try
        {
            var path = await service.DownloadInstallerAsync(info);

            Assert.Equal(Path.Combine(tempDir, "EasyGet-Setup-v1.1.1.exe"), path);
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
            Assert.False(File.Exists($"{path}.download"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class StubHttpMessageHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            response.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(response);
        }
    }
}
