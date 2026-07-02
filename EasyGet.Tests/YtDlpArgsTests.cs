using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class YtDlpArgsTests
{
    [Fact]
    public void AddAria2cArgs_SkipsExternalDownloaderWhenExecutableIsMissing()
    {
        var args = new List<string>();

        YtDlpService.AddAria2cArgs(args, useAria2c: true, aria2cPath: "");

        Assert.DoesNotContain("--external-downloader", args);
        Assert.DoesNotContain("aria2c", args);
    }

    [Fact]
    public void AddAria2cArgs_UsesResolvedExecutablePathWhenAvailable()
    {
        var args = new List<string>();

        YtDlpService.AddAria2cArgs(args, useAria2c: true, aria2cPath: @"C:\Tools\aria2c.exe");

        Assert.Contains("--external-downloader", args);
        Assert.Contains(@"C:\Tools\aria2c.exe", args);
        Assert.Contains("--external-downloader-args", args);
    }

    [Fact]
    public void AddNetworkReliabilityArgs_ConfiguresRetriesTimeoutAndBackoff()
    {
        var args = new List<string>();

        YtDlpService.AddNetworkReliabilityArgs(args);

        AssertOptionValue(args, "--retries", "20");
        AssertOptionValue(args, "--fragment-retries", "30");
        AssertOptionValue(args, "--socket-timeout", "30");
        AssertOptionValue(args, "--retry-sleep", "linear=1:5:1");
        AssertOptionValue(args, "--retry-sleep", "fragment:linear=1:5:1");
    }

    [Fact]
    public void AddDownloadThroughputArgs_IncreasesInitialDownloadBuffer()
    {
        var args = new List<string>();

        YtDlpService.AddDownloadThroughputArgs(args);

        AssertOptionValue(args, "--buffer-size", "1M");
    }

    [Fact]
    public void BuildVideoInfoBaseArgs_AddsNetworkReliabilityOptions()
    {
        var args = YtDlpService.BuildVideoInfoBaseArgs();

        Assert.Contains("--dump-json", args);
        Assert.Contains("--no-download", args);
        AssertOptionValue(args, "--retries", "20");
        AssertOptionValue(args, "--socket-timeout", "30");
        AssertOptionValue(args, "--retry-sleep", "linear=1:5:1");
    }

    [Fact]
    public void BuildPlaylistBaseArgs_AddsNetworkReliabilityOptions()
    {
        var args = YtDlpService.BuildPlaylistBaseArgs();

        Assert.Contains("--flat-playlist", args);
        Assert.Contains("--dump-json", args);
        AssertOptionValue(args, "--retries", "20");
        AssertOptionValue(args, "--socket-timeout", "30");
        AssertOptionValue(args, "--retry-sleep", "linear=1:5:1");
    }

    [Fact]
    public void AddSiteCompatibilityArgs_AddsBilibiliBrowserHeaders()
    {
        var args = new List<string>();

        YtDlpService.AddSiteCompatibilityArgs(args, "https://www.bilibili.com/video/BV1V5Eu68E5m/");

        AssertOptionValue(args, "--user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        AssertOptionValue(args, "--referer", "https://www.bilibili.com/");
        AssertOptionValue(args, "--add-header", "Origin:https://www.bilibili.com");
    }

    [Fact]
    public void AddSiteCompatibilityArgs_LeavesGenericSitesUnchanged()
    {
        var args = new List<string>();

        YtDlpService.AddSiteCompatibilityArgs(args, "https://example.com/video");

        Assert.Empty(args);
    }

    private static void AssertOptionValue(List<string> args, string option, string expectedValue)
    {
        var optionIndexes = args
            .Select((value, index) => (value, index))
            .Where(item => item.value == option)
            .Select(item => item.index)
            .ToList();

        Assert.NotEmpty(optionIndexes);
        Assert.Contains(optionIndexes, index => index + 1 < args.Count && args[index + 1] == expectedValue);
    }
}
