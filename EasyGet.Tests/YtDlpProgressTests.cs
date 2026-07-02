using EasyGet.Services;
using System.Globalization;
using System.Reflection;
using Xunit;

namespace EasyGet.Tests;

public class YtDlpProgressTests
{
    [Fact]
    public void ParseProgressLine_LeavesEtaAtZeroWhenEtaTokenIsMalformed()
    {
        var progress = ParseProgressLine("download:50.0% 1.00MiB/s ETA abc");

        Assert.NotNull(progress);
        Assert.Equal(50, progress!.Percent);
        Assert.Equal(0, progress.Eta);
    }

    [Fact]
    public void ParseProgressLine_UsesInvariantCultureForDecimalValues()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

            var progress = ParseProgressLine("download:12.5% 1.50MiB/s ETA 00:01");

            Assert.NotNull(progress);
            Assert.Equal(12.5, progress!.Percent, precision: 3);
            Assert.Equal(1.5 * 1024 * 1024, progress.Speed, precision: 3);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void ParseProgressLine_KeepsPercentWhenSpeedIsUnknown()
    {
        var progress = ParseProgressLine("download:33.3% Unknown B/s ETA 00:02");

        Assert.NotNull(progress);
        Assert.Equal(33.3, progress!.Percent, precision: 3);
        Assert.Equal(0, progress.Speed);
        Assert.Equal(2, progress.Eta);
    }

    [Fact]
    public void ParseProgressLine_KeepsPercentWhenEtaIsPlaceholder()
    {
        var progress = ParseProgressLine("download:45.0% 1.00MiB/s ETA --:--");

        Assert.NotNull(progress);
        Assert.Equal(45, progress!.Percent);
        Assert.Equal(1.0 * 1024 * 1024, progress.Speed, precision: 3);
        Assert.Equal(0, progress.Eta);
    }

    [Fact]
    public void ParseProgressLine_AvoidsPerLineSpeedRegexAndEtaSplit()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "YtDlpService.cs")));

        Assert.DoesNotContain("SpeedRegex().Match(speedStr)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("etaStr.Split(':')", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("01:02:03", 3723)]
    [InlineData("02:03", 123)]
    [InlineData("42", 42)]
    public void ParseProgressLine_ParsesEtaFormatsWithoutCulture(string eta, double expected)
    {
        var progress = ParseProgressLine($"download:25.0% 1.00MiB/s ETA {eta}");

        Assert.NotNull(progress);
        Assert.Equal(expected, progress!.Eta);
    }

    [Theory]
    [InlineData("512.00B/s", 512)]
    [InlineData("1.00KiB/s", 1024)]
    [InlineData("1.50MiB/s", 1.5 * 1024 * 1024)]
    [InlineData("2.00GiB/s", 2.0 * 1024 * 1024 * 1024)]
    public void ParseProgressLine_ParsesBinarySpeedUnits(string speed, double expected)
    {
        var progress = ParseProgressLine($"download:25.0% {speed} ETA 00:01");

        Assert.NotNull(progress);
        Assert.Equal(expected, progress!.Speed, precision: 3);
    }

    [Theory]
    [InlineData("download:45.0% 1.00MiB/s ETA 00:01", false)]
    [InlineData("[download] Destination: C:\\Videos\\sample.mp4", true)]
    [InlineData("[Merger] Merging formats into \"C:\\Videos\\sample.mp4\"", true)]
    [InlineData("[yt-dlp] completed: sample", true)]
    public void ShouldLogDownloadOutputLine_SkipsProgressTemplateLines(string line, bool expected)
    {
        Assert.Equal(expected, YtDlpService.ShouldLogDownloadOutputLine(line));
    }

    [Fact]
    public void ClassifyDownloadOutputLine_FastPathsTemplateProgressLines()
    {
        var result = YtDlpService.ClassifyDownloadOutputLine("download:45.0% 1.00MiB/s ETA 00:01");

        Assert.NotNull(result.Progress);
        Assert.Equal(45, result.Progress!.Percent);
        Assert.Null(result.OutputPath);
        Assert.False(result.ShouldLog);
    }

    [Fact]
    public void ClassifyDownloadOutputLine_CapturesDestinationLines()
    {
        var result = YtDlpService.ClassifyDownloadOutputLine("[download] Destination: C:\\Videos\\sample.mp4");

        Assert.Null(result.Progress);
        Assert.Equal("C:\\Videos\\sample.mp4", result.OutputPath);
        Assert.True(result.ShouldLog);
    }

    [Fact]
    public void ClassifyDownloadOutputLine_CapturesMergerLinesAndProgress()
    {
        var result = YtDlpService.ClassifyDownloadOutputLine("[Merger] Merging formats into \"C:\\Videos\\sample.mp4\"");

        Assert.NotNull(result.Progress);
        Assert.Equal(99, result.Progress!.Percent);
        Assert.Equal("C:\\Videos\\sample.mp4", result.OutputPath);
        Assert.True(result.ShouldLog);
    }

    private static DownloadProgress? ParseProgressLine(string line)
    {
        var method = typeof(YtDlpService).GetMethod(
            "ParseProgressLine",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (DownloadProgress?)method!.Invoke(null, [line]);
    }
}
