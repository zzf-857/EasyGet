using EasyGet.Services;
using System.Collections.Concurrent;
using System.Net;
using Xunit;

namespace EasyGet.Tests;

public class EnvironmentServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"easyget-tests-{Guid.NewGuid():N}");

    [Fact]
    public void GetMissingToolNames_ReturnsOnlyToolsNotFound()
    {
        var status = new EnvironmentStatus
        {
            YtDlpFound = false,
            FfmpegFound = true
        };

        var missing = EnvironmentService.GetMissingToolNames(status);

        Assert.Equal(["yt-dlp"], missing);
    }

    [Fact]
    public void GetToolDownloadUri_UsesStableOfficialReleaseEndpoints()
    {
        Assert.Equal(
            "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe",
            EnvironmentService.GetToolDownloadUri("yt-dlp").ToString());

        Assert.Equal(
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
            EnvironmentService.GetToolDownloadUri("ffmpeg").ToString());
    }

    [Fact]
    public void FindExecutableInDirectoryTree_FindsFfmpegInsideExtractedZipLayout()
    {
        var binDir = Path.Combine(_tempDir, "ffmpeg-release-essentials", "bin");
        Directory.CreateDirectory(binDir);
        var expectedPath = Path.Combine(binDir, "ffmpeg.exe");
        File.WriteAllText(expectedPath, "fake exe");

        var foundPath = EnvironmentService.FindExecutableInDirectoryTree(_tempDir, "ffmpeg.exe");

        Assert.Equal(expectedPath, foundPath);
    }

    [Fact]
    public void FindExecutableInDirectoryTree_PrefersBinExecutableWithoutSortingSnapshot()
    {
        var rootCandidateDir = Path.Combine(_tempDir, "tools");
        var binDir = Path.Combine(_tempDir, "ffmpeg-release-essentials", "bin");
        Directory.CreateDirectory(rootCandidateDir);
        Directory.CreateDirectory(binDir);

        File.WriteAllText(Path.Combine(rootCandidateDir, "ffmpeg.exe"), "root exe");
        var expectedPath = Path.Combine(binDir, "ffmpeg.exe");
        File.WriteAllText(expectedPath, "bin exe");

        var foundPath = EnvironmentService.FindExecutableInDirectoryTree(_tempDir, "ffmpeg.exe");

        Assert.Equal(expectedPath, foundPath);

        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "EnvironmentService.cs")));
        Assert.DoesNotContain(".OrderBy(path =>", source, StringComparison.Ordinal);
        Assert.Contains("foreach (var path in Directory.EnumerateFiles(rootDirectory, executableName, SearchOption.AllDirectories))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FindExecutableOnPath_ReturnsExecutableFromSearchPath()
    {
        Directory.CreateDirectory(_tempDir);
        var expectedPath = Path.Combine(_tempDir, "aria2c.exe");
        File.WriteAllText(expectedPath, "fake exe");

        var foundPath = EnvironmentService.FindExecutableOnPath("aria2c", _tempDir);

        Assert.Equal(expectedPath, foundPath);
    }

    [Fact]
    public async Task CheckEnvironmentAsync_StartsToolChecksConcurrently()
    {
        var startedTools = new ConcurrentQueue<string>();
        var bothToolsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseToolChecks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new EnvironmentService(async (tool, _) =>
        {
            startedTools.Enqueue(tool);
            if (startedTools.Count == 2)
                bothToolsStarted.TrySetResult();

            await releaseToolChecks.Task;

            return tool == "yt-dlp"
                ? (true, "2026.01.01", @"C:\Tools\yt-dlp.exe")
                : (true, "7.0", @"C:\Tools\ffmpeg.exe");
        });

        var checkTask = service.CheckEnvironmentAsync();

        try
        {
            var completed = await Task.WhenAny(
                bothToolsStarted.Task,
                Task.Delay(TimeSpan.FromMilliseconds(200)));

            Assert.Same(bothToolsStarted.Task, completed);
        }
        finally
        {
            releaseToolChecks.TrySetResult();
        }

        var status = await checkTask;
        Assert.True(status.IsReady);
        Assert.Equal("2026.01.01", status.YtDlpVersion);
        Assert.Equal("7.0", status.FfmpegVersion);
    }

    [Fact]
    public async Task RunCommandAsync_IncludesStderrWhenStdoutIsEmpty()
    {
        var output = await EnvironmentService.RunCommandAsync(
            "powershell",
            "-NoProfile -Command \"[Console]::Error.WriteLine('easyget-stderr-marker')\"",
            TimeSpan.FromSeconds(5));

        Assert.Contains("easyget-stderr-marker", output);
    }

    [Fact]
    public async Task RunCommandAsync_ThrowsTimeoutExceptionWhenProcessHangs()
    {
        await Assert.ThrowsAsync<TimeoutException>(() =>
            EnvironmentService.RunCommandAsync(
                "powershell",
                "-NoProfile -Command \"Start-Sleep -Seconds 5\"",
                TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task RunCommandAsync_KillsProcessWhenCallerCancels()
    {
        var markerPath = Path.Combine(
            Path.GetTempPath(),
            $"easyget-cancelled-process-{Guid.NewGuid():N}.txt");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                EnvironmentService.RunCommandAsync(
                    "powershell",
                    $"-NoProfile -Command \"Start-Sleep -Seconds 2; Set-Content -LiteralPath '{markerPath}' -Value 'not-killed'\"",
                    TimeSpan.FromSeconds(10),
                    cancellation.Token));

            await Task.Delay(TimeSpan.FromMilliseconds(2300));
            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            if (File.Exists(markerPath))
                File.Delete(markerPath);
        }
    }

    [Fact]
    public async Task DownloadFileAsync_RetriesTransientServerFailure()
    {
        Directory.CreateDirectory(_tempDir);
        var targetPath = Path.Combine(_tempDir, "tool.exe");
        var attempts = 0;
        var handler = new SequenceHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });
        using var httpClient = new HttpClient(handler);
        var progress = new ListProgress();

        await EnvironmentService.DownloadFileAsync(
            new Uri("https://example.test/tool.exe"),
            targetPath,
            "yt-dlp",
            progress,
            CancellationToken.None,
            httpClient,
            (_, _) => Task.CompletedTask);

        Assert.Equal(2, attempts);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(targetPath));
        Assert.Contains(progress.Messages, message => message.Contains("准备重试", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadFileAsync_ThrowsIOExceptionAndCleansTargetWhenContentLengthIsShort()
    {
        Directory.CreateDirectory(_tempDir);
        var targetPath = Path.Combine(_tempDir, "tool.exe");
        var attempts = 0;
        var handler = new SequenceHandler(_ =>
        {
            attempts++;
            var content = new ByteArrayContent([1, 2]);
            content.Headers.ContentLength = 3;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            };
        });
        using var httpClient = new HttpClient(handler);

        await Assert.ThrowsAsync<IOException>(() =>
            EnvironmentService.DownloadFileAsync(
                new Uri("https://example.test/tool.exe"),
                targetPath,
                "yt-dlp",
                log: null,
                CancellationToken.None,
                httpClient,
                (_, _) => Task.CompletedTask));

        Assert.Equal(3, attempts);
        Assert.False(File.Exists(targetPath));
    }

    [Fact]
    public void ToolApply_DownloadsOfficialReleaseAndReplacesWithoutSelfUpdateFlag()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "EnvironmentService.cs")));

        Assert.Contains("ReplaceExecutable(", source, StringComparison.Ordinal);
        Assert.Contains("EasyGet-ToolUpdater", source, StringComparison.Ordinal);
        Assert.Contains("YtDlpLatestReleaseApiUrl", source, StringComparison.Ordinal);
        Assert.Contains("FfmpegReleaseVersionUrl", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RunCommandAsync(Status.YtDlpPath, \"-U\"",
            source,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026.03.17", "2026.08.11", true)]
    [InlineData("2026.08.11", "2026.08.11", false)]
    [InlineData("8.0-essentials_build", "8.0", false)]
    [InlineData("7.1.1", "8.0", true)]
    [InlineData("", "2026.08.11", true)]
    [InlineData("2026.08.11", "", false)]
    public void IsToolUpdateAvailable_ComparesNormalizedVersions(
        string current,
        string latest,
        bool expected)
    {
        Assert.Equal(expected, EnvironmentService.IsToolUpdateAvailable(current, latest));
    }

    [Fact]
    public void ParseLatestVersions_ReadsGithubTagAndGyanPlainText()
    {
        Assert.Equal(
            "2026.08.11",
            EnvironmentService.ParseYtDlpLatestVersion("""{"tag_name":"2026.08.11","name":"yt-dlp 2026.08.11"}"""));
        Assert.Equal("8.0", EnvironmentService.ParseFfmpegLatestVersion("8.0\n"));
        Assert.Equal("7.1.1", EnvironmentService.NormalizeToolVersion("n7.1.1-essentials_build"));
    }

    [Fact]
    public void ReplaceExecutable_ReplacesTargetAndRemovesBackup()
    {
        Directory.CreateDirectory(_tempDir);
        var source = Path.Combine(_tempDir, "new.exe");
        var target = Path.Combine(_tempDir, "yt-dlp.exe");
        File.WriteAllText(source, "new-bytes");
        File.WriteAllText(target, "old-bytes");

        EnvironmentService.ReplaceExecutable(source, target);

        Assert.Equal("new-bytes", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".old"));
    }

    [Fact]
    public void ReplaceExecutable_ThrowsFriendlyErrorWhenTargetIsLocked()
    {
        Directory.CreateDirectory(_tempDir);
        var source = Path.Combine(_tempDir, "new.exe");
        var target = Path.Combine(_tempDir, "ffmpeg.exe");
        File.WriteAllText(source, "new-bytes");
        File.WriteAllText(target, "old-bytes");

        using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Assert.Throws<IOException>(() => EnvironmentService.ReplaceExecutable(source, target));
            Assert.Contains("正在使用", error.Message, StringComparison.Ordinal);
        }

        Assert.Equal("old-bytes", File.ReadAllText(target));
    }

    [Fact]
    public async Task CheckToolUpdatesAsync_ReadsLatestVersionsFromOfficialEndpoints()
    {
        var handler = new SequenceHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? "";
            if (url.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"tag_name":"2026.08.11"}""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("8.0")
            };
        });
        using var httpClient = new HttpClient(handler);
        var service = new EnvironmentService(
            configService: null,
            checkToolAsync: (tool, _) => Task.FromResult(
                tool == "yt-dlp"
                    ? (true, "2026.03.17", @"C:\Tools\yt-dlp.exe")
                    : (true, "7.1", @"C:\Tools\ffmpeg.exe")),
            httpClientFactory: () => httpClient);

        await service.CheckEnvironmentAsync();
        var results = await service.CheckToolUpdatesAsync();

        var ytDlp = Assert.Single(results, result => result.ToolName == "yt-dlp");
        var ffmpeg = Assert.Single(results, result => result.ToolName == "ffmpeg");
        Assert.True(ytDlp.IsUpdateAvailable);
        Assert.Equal("2026.08.11", ytDlp.LatestVersion);
        Assert.True(ffmpeg.IsUpdateAvailable);
        Assert.Equal("8.0", ffmpeg.LatestVersion);
    }

    [Fact]
    public void ToolDownload_UsesAsyncBufferedFileStreamAndRentedBuffer()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "EnvironmentService.cs")));

        Assert.Contains("ToolDownloadBufferSize", source, StringComparison.Ordinal);
        Assert.Contains("new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, ToolDownloadBufferSize, useAsync: true)", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<byte>.Shared.Rent(ToolDownloadBufferSize)", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<byte>.Shared.Return(buffer)", source, StringComparison.Ordinal);
        Assert.Contains("HttpIdleRead.ReadAsync(", source, StringComparison.Ordinal);
        Assert.Contains("catch (TimeoutException ex)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new byte[ToolDownloadBufferSize]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Create(targetPath)", source, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private sealed class ListProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value) => Messages.Add(value);
    }

    private sealed class SequenceHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
