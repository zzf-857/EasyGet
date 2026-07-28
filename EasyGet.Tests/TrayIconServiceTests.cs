using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class TrayIconServiceTests
{
    [Fact]
    public void TrimBalloonText_UsesFallbackAndBoundsLongText()
    {
        Assert.Equal("EasyGet", TrayIconService.TrimBalloonText("  ", 12));
        Assert.Equal("123456789…", TrayIconService.TrimBalloonText("123456789012", 10));
    }

    [Fact]
    public void TrimBalloonText_RejectsInvalidLimit()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            TrayIconService.TrimBalloonText("text", 0));
}
