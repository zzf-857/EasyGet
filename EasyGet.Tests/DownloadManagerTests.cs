using EasyGet.Models;
using EasyGet.Services;
using System.Reflection;
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

    [Fact]
    public void ApplyProgress_ClampsOutOfRangeValuesForUiState()
    {
        var task = new DownloadTask();
        var progress = new DownloadProgress
        {
            Percent = 135,
            Speed = -42,
            Eta = -8,
            Downloaded = -256
        };

        ApplyProgress(task, progress);

        Assert.Equal(100, task.Progress);
        Assert.Equal(0, task.Speed);
        Assert.Equal(0, task.Eta);
        Assert.Equal(0, task.DownloadedSize);
    }

    private static void ApplyProgress(DownloadTask task, DownloadProgress progress)
    {
        var method = typeof(DownloadManager).GetMethod(
            "ApplyProgress",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, [task, progress]);
    }
}
