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

    private static DownloadProgress? ParseProgressLine(string line)
    {
        var method = typeof(YtDlpService).GetMethod(
            "ParseProgressLine",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (DownloadProgress?)method!.Invoke(null, [line]);
    }
}
