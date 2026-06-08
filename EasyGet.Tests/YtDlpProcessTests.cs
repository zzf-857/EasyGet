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
}
