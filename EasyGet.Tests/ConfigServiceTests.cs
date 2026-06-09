using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class ConfigServiceTests
{
    [Fact]
    public void NormalizeRuntimeConfig_ClampsPerformanceValuesToSafeRange()
    {
        var config = new AppConfig
        {
            ConcurrentFragments = 0,
            MaxConcurrentDownloads = 99
        };

        ConfigService.NormalizeRuntimeConfig(config);

        Assert.Equal(AppConfig.MinConcurrentFragments, config.ConcurrentFragments);
        Assert.Equal(AppConfig.MaxConcurrentDownloadLimit, config.MaxConcurrentDownloads);
    }

    [Fact]
    public void NormalizeRuntimeConfig_SanitizesInvalidWindowBounds()
    {
        var config = new AppConfig
        {
            Window = new WindowState
            {
                Left = double.PositiveInfinity,
                Top = double.NegativeInfinity,
                Width = 320,
                Height = double.NaN
            }
        };

        ConfigService.NormalizeRuntimeConfig(config);

        Assert.True(double.IsNaN(config.Window.Left));
        Assert.True(double.IsNaN(config.Window.Top));
        Assert.Equal(WindowState.MinWidth, config.Window.Width);
        Assert.Equal(WindowState.DefaultHeight, config.Window.Height);
    }
}
