using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class YtDlpConcurrencyTests
{
    [Theory]
    [InlineData(8, 3, 8)]
    [InlineData(8, 6, 8)]
    [InlineData(8, 8, 4)]
    [InlineData(8, 10, 4)]
    [InlineData(8, 12, 4)]
    [InlineData(2, 12, 2)]
    [InlineData(99, 12, 4)]
    public void ResolveConcurrentFragments_UsesSmartHighConcurrencyBudget(
        int configuredFragments,
        int maxConcurrentDownloads,
        int expected)
    {
        Assert.Equal(
            expected,
            YtDlpService.ResolveConcurrentFragments(
                configuredFragments,
                maxConcurrentDownloads));
    }
}
