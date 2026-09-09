using System.Diagnostics;
using System.Reflection;
using EasyGet.Models;
using EasyGet.Services;
using EasyGet.Services.Cookies;
using Xunit;

namespace EasyGet.Tests;

public class YtDlpProcessTests
{
    [Fact]
    public void ProcessStartInfoSetup_IsSharedByYtDlpProcessRunners()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "YtDlpService.cs")));

        Assert.Contains("CreateProcessStartInfo", source, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(source, "PYTHONIOENCODING"));
        Assert.Equal(1, CountOccurrences(source, "PYTHONUTF8"));
        Assert.Equal(1, CountOccurrences(source, "StandardOutputEncoding = Encoding.UTF8"));
        Assert.Equal(1, CountOccurrences(source, "UseShellExecute = false"));
        Assert.DoesNotContain("DrainProcessOutputAsync(Task<string>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveOutputFile_EnumeratesOutputDirectoryWithoutSortingSnapshot()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "YtDlpService.cs")));

        Assert.Contains("Directory.EnumerateFiles(task.OutputDirectory)", source, StringComparison.Ordinal);
        Assert.Contains("OutputFileNameOverride", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.GetFiles(task.OutputDirectory)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderByDescending(f => f.LastWriteTime)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadAsync_ExitCodeZeroRequiresExistingOutputFile()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "YtDlpService.cs")));

        var exitZero = source.IndexOf("if (processOutput.ExitCode == 0)", StringComparison.Ordinal);
        Assert.True(exitZero >= 0);
        var missingOutput = source.IndexOf("未找到输出文件", exitZero, StringComparison.Ordinal);
        var recordSuccess = source.IndexOf(
            "RecordSuccessAsync",
            exitZero,
            StringComparison.Ordinal);
        var completed = source.IndexOf(
            "task.Status = DownloadStatus.Completed;",
            exitZero,
            StringComparison.Ordinal);
        var existsCheck = source.IndexOf("File.Exists(outputFile)", exitZero, StringComparison.Ordinal);

        Assert.True(missingOutput > exitZero);
        Assert.True(existsCheck > exitZero && existsCheck < recordSuccess);
        Assert.True(recordSuccess > missingOutput && recordSuccess < completed);
        Assert.True(completed > recordSuccess);
    }

    [Fact]
    public void ResolveOutputFile_PrefersReservedOverrideOverAlreadyDownloadedFile()
    {
        using var root = new TestDirectory();
        var oldFile = root.Path("共享标题.mp4");
        var reservedFile = root.Path("共享标题 (2).mkv");
        File.WriteAllText(oldFile, "old");
        File.WriteAllText(reservedFile, "reserved");
        var task = new DownloadTask
        {
            OutputDirectory = root.DirectoryPath,
            Format = "mp4",
            Title = "共享标题",
            OutputFileNameOverride = "共享标题 (2)"
        };

        var resolved = InvokeResolveOutputFile(oldFile, task, DateTime.Now);

        Assert.Equal(Path.GetFullPath(reservedFile), resolved);
    }

    [Fact]
    public void ResolveOutputFile_IgnoresNewestUnrelatedFileWhenOverrideIsSet()
    {
        using var root = new TestDirectory();
        var unrelated = root.Path("other.mp4");
        File.WriteAllText(unrelated, "unrelated");
        File.SetLastWriteTime(unrelated, DateTime.Now);
        var task = new DownloadTask
        {
            OutputDirectory = root.DirectoryPath,
            Format = "mp4",
            Title = "共享标题",
            OutputFileNameOverride = "共享标题 (2)"
        };

        var resolved = InvokeResolveOutputFile(unrelated, task, DateTime.Now);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveOutputFile_KeepsCapturedPathWhenOverrideIsAbsent()
    {
        using var root = new TestDirectory();
        var existing = root.Path("共享标题.mp4");
        File.WriteAllText(existing, "existing");
        var task = new DownloadTask
        {
            OutputDirectory = root.DirectoryPath,
            Format = "mp4",
            Title = "共享标题"
        };

        var resolved = InvokeResolveOutputFile(existing, task, DateTime.Now);

        Assert.Equal(existing, resolved);
    }

    [Fact]
    public async Task DownloadAsync_ExitCodeZeroWithoutOutputFileMarksFailed()
    {
        using var root = new TestDirectory();
        var outputDirectory = root.Path("downloads");
        Directory.CreateDirectory(outputDirectory);
        var oldFile = Path.Combine(outputDirectory, "共享标题.mp4");
        File.WriteAllText(oldFile, "already-downloaded");
        var health = new RecordingCookieHealthStore();
        var service = CreateYtDlpService(
            root,
            health,
            CompileFakeYtDlp(
                root.DirectoryPath,
                $"[download] {oldFile} has already been downloaded"));
        var task = new DownloadTask
        {
            Url = "https://example.test/video",
            Title = "共享标题",
            OutputDirectory = outputDirectory,
            Format = "mp4",
            OutputFileNameOverride = "共享标题 (2)"
        };

        await service.DownloadAsync(task);

        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.Contains("未找到输出文件", task.ErrorMessage, StringComparison.Ordinal);
        Assert.NotEqual(oldFile, task.OutputFilePath);
        Assert.Equal(0, health.SuccessCount);
    }

    [Fact]
    public async Task DownloadAsync_ExitCodeZeroWithReservedOutputFileCompletes()
    {
        using var root = new TestDirectory();
        var outputDirectory = root.Path("downloads");
        Directory.CreateDirectory(outputDirectory);
        var reservedFile = Path.Combine(outputDirectory, "共享标题 (2).mp4");
        File.WriteAllText(reservedFile, "reserved-output");
        var oldFile = Path.Combine(outputDirectory, "共享标题.mp4");
        File.WriteAllText(oldFile, "already-downloaded");
        var health = new RecordingCookieHealthStore();
        var service = CreateYtDlpService(
            root,
            health,
            CompileFakeYtDlp(
                root.DirectoryPath,
                $"[download] {oldFile} has already been downloaded"));
        var task = new DownloadTask
        {
            Url = "https://example.test/video",
            Title = "共享标题",
            OutputDirectory = outputDirectory,
            Format = "mp4",
            OutputFileNameOverride = "共享标题 (2)"
        };

        await service.DownloadAsync(task);

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(Path.GetFullPath(reservedFile), task.OutputFilePath);
        Assert.Equal(1, health.SuccessCount);
    }

    [Fact]
    public async Task RunProcessAsync_CapturesStderrWithoutBlocking()
    {
        var result = await YtDlpService.RunProcessAsync(
            "powershell",
            [
                "-NoProfile",
                "-Command",
                "[Console]::Error.WriteLine('easyget-ytdlp-stderr-marker')"
            ],
            TimeSpan.FromSeconds(5));

        Assert.Contains("easyget-ytdlp-stderr-marker", result.StandardError);
    }

    [Fact]
    public async Task RunProcessAsync_ThrowsTimeoutExceptionWhenProcessHangs()
    {
        await Assert.ThrowsAsync<TimeoutException>(() =>
            YtDlpService.RunProcessAsync(
                "powershell",
                [
                    "-NoProfile",
                    "-Command",
                    "Start-Sleep -Seconds 5"
                ],
                TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task RunProcessAsync_KillsProcessWhenCancellationIsRequested()
    {
        var startedPath = Path.Combine(Path.GetTempPath(), $"easyget-ytdlp-started-{Guid.NewGuid():N}.txt");
        var markerPath = Path.Combine(Path.GetTempPath(), $"easyget-ytdlp-cancel-{Guid.NewGuid():N}.txt");
        using var cts = new CancellationTokenSource();

        try
        {
            var runTask = YtDlpService.RunProcessAsync(
                "powershell",
                [
                    "-NoProfile",
                    "-Command",
                    $"Set-Content -LiteralPath '{startedPath}' -Value started; "
                    + $"Start-Sleep -Seconds 30; Set-Content -LiteralPath '{markerPath}' -Value completed"
                ],
                TimeSpan.FromSeconds(35),
                cts.Token);
            await WaitForFileAsync(startedPath, TimeSpan.FromSeconds(10));
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

            await Task.Delay(1200);

            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            if (File.Exists(startedPath))
                File.Delete(startedPath);
            if (File.Exists(markerPath))
                File.Delete(markerPath);
        }
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.True(File.Exists(path), $"Process did not create its start marker: {path}");
    }

    [Fact]
    public async Task RunDownloadProcessAsync_KillsProcessWhenOutputStalls()
    {
        var markerPath = Path.Combine(Path.GetTempPath(), $"easyget-ytdlp-idle-{Guid.NewGuid():N}.txt");

        try
        {
            var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
                YtDlpService.RunDownloadProcessAsync(
                    "cmd",
                    [
                        "/c",
                        $"echo download started & ping -n 3 127.0.0.1 > nul & echo completed>{markerPath}"
                    ],
                    TimeSpan.FromMilliseconds(200)));

            Assert.Contains("没有输出", ex.Message);

            await Task.Delay(1200);

            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            if (File.Exists(markerPath))
                File.Delete(markerPath);
        }
    }

    [Fact]
    public async Task RunDownloadProcessAsync_StreamsLinesWithoutRetainingFullOutput()
    {
        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();

        var result = await YtDlpService.RunDownloadProcessAsync(
            "powershell",
            [
                "-NoProfile",
                "-Command",
                "[Console]::Out.WriteLine('download line'); [Console]::Error.WriteLine('error line')"
            ],
            TimeSpan.FromSeconds(5),
            stdoutLines.Add,
            stderrLines.Add);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("download line", stdoutLines);
        Assert.Contains("error line", stderrLines);
        Assert.Equal("", result.StandardOutput);
        Assert.Equal("", result.StandardError);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RunDownloadProcessAsync_KeepsDrainingPipeWhenOutputSubscriberThrows(
        bool standardError, bool unexpectedException)
    {
        var receivedCount = 0;
        var sawFinalOutput = false;
        void ReceiveLine(string line)
        {
            if (++receivedCount == 1)
            {
                if (unexpectedException)
                    throw new ArgumentException("observer failed");
                throw new InvalidOperationException("observer failed");
            }
            sawFinalOutput |= line == "finished";
        }

        var streamName = standardError ? "Error" : "Out";
        var result = await YtDlpService.RunDownloadProcessAsync(
            "powershell",
            ["-NoProfile", "-Command",
                $"[Console]::{streamName}.WriteLine('start'); "
                + $"for ($i = 0; $i -lt 2000; $i++) {{ [Console]::{streamName}.WriteLine(('x' * 256)) }}; "
                + $"[Console]::{streamName}.WriteLine('finished')"],
            TimeSpan.FromSeconds(2),
            standardError ? null : ReceiveLine,
            standardError ? ReceiveLine : null);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2_002, receivedCount);
        Assert.True(sawFinalOutput);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string? InvokeResolveOutputFile(
        string? capturedPath,
        DownloadTask task,
        DateTime downloadStartTime)
    {
        var method = typeof(YtDlpService).GetMethod(
            "ResolveOutputFile",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string?)method!.Invoke(null, [capturedPath, task, downloadStartTime]);
    }

    private static YtDlpService CreateYtDlpService(
        TestDirectory root,
        ICookieHealthStore health,
        string ytDlpPath)
    {
        var config = new ConfigService(root.Path("config"));
        config.Config.SmartCookieEnabled = false;
        var environment = new EnvironmentService();
        environment.Status.YtDlpPath = ytDlpPath;
        var coordinator = new CookieAcquisitionCoordinator(
            config,
            new PlatformCookieVault(root.Path("config")),
            new EmptyBrowserProfileDiscoveryService(),
            health,
            new EmptyManagedLoginSessionService(),
            root.Path("cookies"));
        return new YtDlpService(config, environment, coordinator);
    }

    private static string CompileFakeYtDlp(string directory, string stdoutLine, int exitCode = 0)
    {
        var csPath = Path.Combine(directory, $"fake-ytdlp-{Guid.NewGuid():N}.cs");
        var exePath = Path.ChangeExtension(csPath, ".exe");
        File.WriteAllText(
            csPath,
            $$"""
            using System;
            public class FakeYtDlp
            {
                public static int Main(string[] args)
                {
                    Console.WriteLine({{ToCSharpStringLiteral(stdoutLine)}});
                    return {{exitCode}};
                }
            }
            """);

        var csc = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET",
            "Framework64",
            "v4.0.30319",
            "csc.exe");
        if (File.Exists(csc))
        {
            var compiled = RunCompiler(
                csc,
                ["/nologo", $"/out:{exePath}", csPath]);
            if (compiled && File.Exists(exePath))
                return exePath;
        }

        var compiledWithPowerShell = RunCompiler(
            "powershell",
            [
                "-NoProfile",
                "-Command",
                $"Add-Type -Path '{csPath}' -OutputAssembly '{exePath}' -OutputType ConsoleApplication"
            ]);
        Assert.True(compiledWithPowerShell && File.Exists(exePath), $"Failed to compile fake yt-dlp: {exePath}");
        return exePath;
    }

    private static bool RunCompiler(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null)
            return false;

        process.WaitForExit(15_000);
        return process.ExitCode == 0;
    }

    private static string ToCSharpStringLiteral(string value)
        => "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            + "\"";

    private sealed class RecordingCookieHealthStore : ICookieHealthStore
    {
        private int _successCount;

        public int SuccessCount => Volatile.Read(ref _successCount);

        public IReadOnlyList<CookieHealthRecord> Snapshot() => [];

        public Task RecordSuccessAsync(
            string platformId,
            CookieSourceKind source,
            BrowserProfile? profile,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _successCount);
            return Task.CompletedTask;
        }

        public Task RecordFailureAsync(
            string platformId,
            CookieSourceKind source,
            BrowserProfile? profile,
            CookieFailureCategory category,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ClearPlatformAsync(string platformId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class EmptyBrowserProfileDiscoveryService : IBrowserProfileDiscoveryService
    {
        public IReadOnlyList<BrowserProfile> Discover() => [];
    }
}
