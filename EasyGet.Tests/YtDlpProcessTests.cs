using EasyGet.Services;
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
        Assert.DoesNotContain("Directory.GetFiles(task.OutputDirectory)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderByDescending(f => f.LastWriteTime)", source, StringComparison.Ordinal);
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
}
