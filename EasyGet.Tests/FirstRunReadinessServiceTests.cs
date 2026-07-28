using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class FirstRunReadinessServiceTests
{
    [Fact]
    public async Task CheckAsync_ReportsMissingToolsAndDirectoryProblem()
    {
        var environment = new EnvironmentService((tool, _) => Task.FromResult(
            tool == "yt-dlp"
                ? (true, "2026.07.01", @"C:\Tools\yt-dlp.exe")
                : (false, "", "")));
        var preflight = new DownloadPreflightService(
            _ => true,
            _ => { },
            _ => false,
            _ => 10L * 1024 * 1024 * 1024);
        var service = new FirstRunReadinessService(environment, preflight);

        var report = await service.CheckAsync(@"C:\Downloads");

        Assert.False(report.IsReady);
        Assert.Equal(["ffmpeg"], report.MissingTools);
        Assert.Contains("ffmpeg", report.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("不可写", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_IsReadyWhenToolsAndDirectoryAreReady()
    {
        var environment = new EnvironmentService((tool, _) => Task.FromResult(
            (true, "1.0", $@"C:\Tools\{tool}.exe")));
        var preflight = new DownloadPreflightService(
            _ => true,
            _ => { },
            _ => true,
            _ => 10L * 1024 * 1024 * 1024);
        var service = new FirstRunReadinessService(environment, preflight);

        var report = await service.CheckAsync(@"C:\Downloads");

        Assert.True(report.IsReady);
        Assert.Equal("运行环境和下载目录已就绪。", report.Summary);
    }
}
