using EasyGet.Models;
using EasyGet.Services;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace EasyGet.Tests;

public class AppUpdateServiceTests
{
    private const string ValidSha256 = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

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
    public void ParseUpdateManifest_SelectsSetupAssetAndMarksUpdateAvailable()
    {
        var info = AppUpdateService.ParseUpdateManifestJson(CreateManifest(), "1.0.0");

        Assert.True(info.IsUpdateAvailable);
        Assert.Equal("1.0.0", info.CurrentVersion);
        Assert.Equal("1.1.0", info.LatestVersion);
        Assert.Equal("EasyGet-Setup-v1.1.0.exe", info.InstallerFileName);
        Assert.Equal("https://github.com/zzf-857/EasyGet/releases/download/v1.1.0/EasyGet-Setup-v1.1.0.exe", info.InstallerDownloadUrl?.ToString());
        Assert.Equal("https://github.com/zzf-857/EasyGet/releases/tag/v1.1.0", info.ReleasePageUrl?.ToString());
        Assert.Equal(85_000_000, info.InstallerSize);
        Assert.Equal(ValidSha256, info.InstallerSha256);
    }

    [Fact]
    public void ParseUpdateManifest_ReturnsNoUpdateWhenLatestIsNotNewer()
    {
        var info = AppUpdateService.ParseUpdateManifestJson(CreateManifest(), "1.1.0");

        Assert.False(info.IsUpdateAvailable);
        Assert.Equal("1.1.0", info.LatestVersion);
        Assert.Equal("EasyGet-Setup-v1.1.0.exe", info.InstallerFileName);
    }

    [Fact]
    public void ParseUpdateManifest_RejectsTagThatDoesNotMatchVersion()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            AppUpdateService.ParseUpdateManifestJson(
                CreateManifest(tag: "v1.1.1"),
                "1.0.0"));

        Assert.Contains("tag", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("EasyGet-Setup-v1.1.1.exe")]
    [InlineData("../EasyGet-Setup-v1.1.0.exe")]
    [InlineData("easyget-setup-v1.1.0.exe")]
    public void ParseUpdateManifest_RejectsUnexpectedInstallerAsset(string setupAsset)
    {
        Assert.Throws<InvalidDataException>(() =>
            AppUpdateService.ParseUpdateManifestJson(
                CreateManifest(setupAsset: setupAsset),
                "1.0.0"));
    }

    [Theory]
    [InlineData(0, ValidSha256)]
    [InlineData(1_073_741_825, ValidSha256)]
    [InlineData(85_000_000, "")]
    [InlineData(85_000_000, "0123456789ABCDEF")]
    [InlineData(85_000_000, "G123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    public void ParseUpdateManifest_RejectsInvalidInstallerIntegrityMetadata(long setupSize, string setupSha256)
    {
        Assert.Throws<InvalidDataException>(() =>
            AppUpdateService.ParseUpdateManifestJson(
                CreateManifest(setupSize: setupSize, setupSha256: setupSha256),
                "1.0.0"));
    }

    [Fact]
    public async Task CheckLatestAsync_UsesStaticReleaseManifestWithoutGitHubApi()
    {
        var handler = new StubHttpMessageHandler(Encoding.UTF8.GetBytes(CreateManifest(version: "9.9.9")));
        var service = new AppUpdateService(new HttpClient(handler));

        var info = await service.CheckLatestAsync();

        Assert.True(info.IsUpdateAvailable);
        Assert.Equal(
            "https://github.com/zzf-857/EasyGet/releases/latest/download/easyget-update.json",
            handler.LastRequestUri?.ToString());

        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "AppUpdateService.cs")));
        Assert.DoesNotContain("api.github.com", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckLatestAsync_RejectsOversizedManifestWithoutContentLength()
    {
        var handler = new StubHttpMessageHandler(new byte[65_537], omitContentLength: true);
        var service = new AppUpdateService(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.CheckLatestAsync());

        Assert.Contains("清单体积异常", exception.Message, StringComparison.Ordinal);
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
            InstallerSize = payload.Length,
            InstallerSha256 = ComputeSha256(payload)
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
            InstallerSize = payload.Length,
            InstallerSha256 = ComputeSha256(payload)
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

    [Fact]
    public async Task DownloadInstallerAsync_RejectsTruncatedPayloadAndPreservesExistingInstaller()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"easyget-update-tests-{Guid.NewGuid():N}");
        var logPath = Path.Combine(tempDir, "update.log");
        var payload = new byte[] { 1, 2, 3 };
        var service = new AppUpdateService(
            new HttpClient(new StubHttpMessageHandler(payload, contentLength: 5)),
            tempDir,
            logPath);
        var targetPath = Path.Combine(tempDir, "EasyGet-Setup-v1.1.3.exe");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllBytesAsync(targetPath, [9, 9, 9, 9]);
        var info = new AppUpdateInfo
        {
            LatestVersion = "1.1.3",
            InstallerFileName = Path.GetFileName(targetPath),
            InstallerDownloadUrl = new Uri("https://example.test/EasyGet-Setup-v1.1.3.exe"),
            InstallerSize = 5,
            InstallerSha256 = ComputeSha256(payload)
        };

        try
        {
            var exception = await Assert.ThrowsAsync<IOException>(() =>
                service.DownloadInstallerAsync(info));

            Assert.Contains("下载不完整", exception.Message, StringComparison.Ordinal);
            Assert.Equal([9, 9, 9, 9], await File.ReadAllBytesAsync(targetPath));
            Assert.False(File.Exists($"{targetPath}.download"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_RejectsHashMismatchAndPreservesExistingInstaller()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"easyget-update-tests-{Guid.NewGuid():N}");
        var payload = new byte[] { 1, 2, 3, 4 };
        var service = new AppUpdateService(
            new HttpClient(new StubHttpMessageHandler(payload)),
            tempDir);
        var targetPath = Path.Combine(tempDir, "EasyGet-Setup-v1.1.4.exe");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllBytesAsync(targetPath, [9, 9, 9]);
        var info = new AppUpdateInfo
        {
            LatestVersion = "1.1.4",
            InstallerFileName = Path.GetFileName(targetPath),
            InstallerDownloadUrl = new Uri("https://example.test/EasyGet-Setup-v1.1.4.exe"),
            InstallerSize = payload.Length,
            InstallerSha256 = ComputeSha256([4, 3, 2, 1])
        };

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.DownloadInstallerAsync(info));

            Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
            Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(targetPath));
            Assert.False(File.Exists($"{targetPath}.download"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_RejectsContentLengthThatDiffersFromManifest()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"easyget-update-tests-{Guid.NewGuid():N}");
        var payload = new byte[] { 1, 2, 3, 4 };
        var service = new AppUpdateService(
            new HttpClient(new StubHttpMessageHandler(payload, contentLength: 5)),
            tempDir);
        var info = new AppUpdateInfo
        {
            LatestVersion = "1.1.5",
            InstallerFileName = "EasyGet-Setup-v1.1.5.exe",
            InstallerDownloadUrl = new Uri("https://example.test/EasyGet-Setup-v1.1.5.exe"),
            InstallerSize = payload.Length,
            InstallerSha256 = ComputeSha256(payload)
        };

        try
        {
            var exception = await Assert.ThrowsAsync<IOException>(() =>
                service.DownloadInstallerAsync(info));

            Assert.Contains("大小与清单不一致", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_StopsUnknownLengthResponseAtManifestLimit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"easyget-update-tests-{Guid.NewGuid():N}");
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var service = new AppUpdateService(
            new HttpClient(new StubHttpMessageHandler(payload, omitContentLength: true)),
            tempDir);
        var info = new AppUpdateInfo
        {
            LatestVersion = "1.1.6",
            InstallerFileName = "EasyGet-Setup-v1.1.6.exe",
            InstallerDownloadUrl = new Uri("https://example.test/EasyGet-Setup-v1.1.6.exe"),
            InstallerSize = 4,
            InstallerSha256 = ComputeSha256(payload[..4])
        };

        try
        {
            var exception = await Assert.ThrowsAsync<IOException>(() =>
                service.DownloadInstallerAsync(info));

            Assert.Contains("超过清单声明", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(tempDir, "EasyGet-Setup-v1.1.6.exe.download")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("..\\EasyGet-Setup-v1.1.3.exe")]
    [InlineData("C:\\Temp\\EasyGet-Setup-v1.1.3.exe")]
    [InlineData("EasyGet-v1.1.3.exe")]
    public async Task DownloadInstallerAsync_RejectsUnsafeInstallerFileName(string fileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"easyget-update-tests-{Guid.NewGuid():N}");
        var service = new AppUpdateService(
            new HttpClient(new StubHttpMessageHandler([1])),
            tempDir);
        var info = new AppUpdateInfo
        {
            LatestVersion = "1.1.3",
            InstallerFileName = fileName,
            InstallerDownloadUrl = new Uri("https://example.test/installer.exe")
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadInstallerAsync(info));

        Assert.False(Directory.Exists(tempDir));
    }

    [Fact]
    public void DownloadInstallerAsync_UsesAsyncBufferedFileStreamAndRentedBuffer()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "AppUpdateService.cs")));

        Assert.Contains("InstallerDownloadBufferSize", source, StringComparison.Ordinal);
        Assert.Contains("new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, InstallerDownloadBufferSize, useAsync: true)", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<byte>.Shared.Rent(InstallerDownloadBufferSize)", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<byte>.Shared.Return(buffer)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new byte[InstallerDownloadBufferSize]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Create(tempPath)", source, StringComparison.Ordinal);
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

    [Fact]
    public void RegisteredInstallDirectory_EnumeratesBothRegistryViews()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "AppUpdateService.cs")));

        Assert.Contains("RegistryView.Registry64", source, StringComparison.Ordinal);
        Assert.Contains("RegistryView.Registry32", source, StringComparison.Ordinal);
        Assert.Contains("RegistryKey.OpenBaseKey(hive, view)", source, StringComparison.Ordinal);
    }

    private static string CreateManifest(
        string version = "1.1.0",
        string? tag = null,
        string? setupAsset = null,
        long setupSize = 85_000_000,
        string setupSha256 = ValidSha256)
        => JsonSerializer.Serialize(new
        {
            version,
            tag = tag ?? $"v{version}",
            setupAsset = setupAsset ?? $"EasyGet-Setup-v{version}.exe",
            setupSize,
            setupSha256,
            releaseUrl = $"https://github.com/zzf-857/EasyGet/releases/tag/v{version}"
        });

    private static string ComputeSha256(byte[] payload)
        => Convert.ToHexString(SHA256.HashData(payload));

    private sealed class StubHttpMessageHandler(
        byte[] payload,
        long? contentLength = null,
        bool omitContentLength = false) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            HttpContent content = omitContentLength
                ? new UnknownLengthContent(payload)
                : new ByteArrayContent(payload);
            if (!omitContentLength)
                content.Headers.ContentLength = contentLength ?? payload.Length;

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            return Task.FromResult(response);
        }
    }

    private sealed class UnknownLengthContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(payload).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
