using EasyGet.Models;
using Xunit;

namespace EasyGet.Tests;

public class DownloadQueueSummaryTests
{
    [Fact]
    public void Capture_EnumeratesOnceAndPreservesQueueStateSemantics()
    {
        var enumerations = 0;
        IEnumerable<DownloadTask> Tasks()
        {
            Assert.Equal(1, ++enumerations);
            foreach (var status in Enum.GetValues<DownloadStatus>())
                yield return new DownloadTask { Status = status, Progress = 50, Speed = 100 };
        }

        var summary = DownloadQueueSummary.Capture(Tasks());

        Assert.Equal(9, summary.Total);
        Assert.Equal(3, summary.Running);
        Assert.Equal(3, summary.Finished);
        Assert.Equal(6, summary.Remaining);
        Assert.Equal(1, summary.Paused);
        Assert.Equal(1, summary.Scheduled);
        Assert.Equal(100, summary.AggregateSpeed);
        Assert.Equal(500d / 9, summary.OverallProgress, 8);
        Assert.True(summary.CanStop);
    }

    [Fact]
    public void Capture_InvalidProgressCannotPoisonTheWholeQueue()
    {
        var summary = DownloadQueueSummary.Capture([
            new DownloadTask { Status = DownloadStatus.Downloading, Progress = double.NaN, Speed = double.PositiveInfinity },
            new DownloadTask { Status = DownloadStatus.Completed, Progress = -1 },
            new DownloadTask { Status = DownloadStatus.Downloading, Progress = 200, Speed = -1 }
        ]);

        Assert.Equal(200d / 3, summary.OverallProgress, 8);
        Assert.Equal(0, summary.AggregateSpeed);
    }
}
