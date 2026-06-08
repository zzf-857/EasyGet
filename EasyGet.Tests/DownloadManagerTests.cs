using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class DownloadManagerTests
{
    [Theory]
    [InlineData(0, AppConfig.MinConcurrentDownloadLimit)]
    [InlineData(-5, AppConfig.MinConcurrentDownloadLimit)]
    [InlineData(3, 3)]
    [InlineData(99, AppConfig.MaxConcurrentDownloadLimit)]
    public void NormalizeConcurrencyLimit_ClampsToSupportedRange(int value, int expected)
    {
        Assert.Equal(expected, DownloadManager.NormalizeConcurrencyLimit(value));
    }
}
