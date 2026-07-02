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
        var logPath = Path.Combine(tempDir, "update.log");
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var service = new AppUpdateService(
            new HttpClient(new StubHttpMessageHandler(payload)),
            tempDir,
            logPath);
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
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
            }

            var log = await File.ReadAllTextAsync(logPath);
            Assert.Contains("Download streams disposed before move", log, StringComparison.Ordinal);
            Assert.Contains("File.Move completed", log, StringComparison.Ordinal);
            Assert.Contains($"{path}.download", log, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(path, log, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_ReplacesExistingTargetAfterClosingAllStreams()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"easyget-update-tests-{Guid.NewGuid():N}");
        var logPath = Path.Combine(tempDir, "update.log");
        var payload = new byte[] { 9, 8, 7, 6 };
        var service = new AppUpdateService(
            new HttpClient(new StubHttpMessageHandler(payload)),
            tempDir,
            logPath);
        var targetPath = Path.Combine(tempDir, "EasyGet-Setup-v1.1.2.exe");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllBytesAsync(targetPath, [1, 1, 1]);

        var info = new AppUpdateInfo
        {
            LatestVersion = "1.1.2",
            InstallerFileName = "EasyGet-Setup-v1.1.2.exe",
            InstallerDownloadUrl = new Uri("https://example.com/EasyGet-Setup-v1.1.2.exe"),
            InstallerSize = payload.Length
        };

        try
        {
            var path = await service.DownloadInstallerAsync(info);

            Assert.Equal(targetPath, path);
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
            Assert.False(File.Exists($"{path}.download"));
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\repo\EasyGet\artifacts\publish\Release\win-x64\", "发布目录运行")]
    [InlineData(@"C:\repo\EasyGet\bin\Release\net8.0-windows\", "开发构建运行")]
    [InlineData(@"F:\AI\AIMadeupTools\01_DesktopApps\EasyGet\EXE\", "项目 EXE 目录运行")]
    [InlineData(@"C:\Program Files\EasyGet\", "自定义目录运行")]
    public void DescribeRuntime_ClassifiesCommonExecutionLocations(string baseDirectory, string expected)
    {
        var executablePath = Path.Combine(baseDirectory, "EasyGet.exe");

        var actual = AppUpdateService.DescribeRuntime(executablePath, baseDirectory);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DescribeRuntime_UsesInnoRegisteredInstallLocationWhenAvailable()
    {
        const string baseDirectory = @"F:\AI\AIMadeupTools\01_DesktopApps\EasyGet\EXE\";

        var actual = AppUpdateService.DescribeRuntime(
            Path.Combine(baseDirectory, "EasyGet.exe"),
            baseDirectory,
            baseDirectory);

        Assert.Equal("安装版运行", actual);
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
