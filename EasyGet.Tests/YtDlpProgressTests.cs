using EasyGet.Services;
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

    private static DownloadProgress? ParseProgressLine(string line)
    {
        var method = typeof(YtDlpService).GetMethod(
            "ParseProgressLine",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (DownloadProgress?)method!.Invoke(null, [line]);
    }
}
