using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class YtDlpProcessTests
{
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
        var markerPath = Path.Combine(Path.GetTempPath(), $"easyget-ytdlp-cancel-{Guid.NewGuid():N}.txt");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                YtDlpService.RunProcessAsync(
                    "powershell",
                    [
                        "-NoProfile",
                        "-Command",
                        $"Start-Sleep -Milliseconds 800; Set-Content -Path '{markerPath}' -Value completed"
                    ],
                    TimeSpan.FromSeconds(5),
                    cts.Token));

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
}
