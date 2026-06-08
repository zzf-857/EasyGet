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
}
