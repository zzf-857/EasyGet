using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class M3u8DownloadServiceTests
{
    [Theory]
    [InlineData("http://example.com/video.m3u8")]
    [InlineData("https://example.com/playlist.m3n8")]
    [InlineData("https://example.com/path/video.m3u8?auth=token123")]
    [InlineData("https://example.com/some.m3u8/stream")]
    public void IsM3u8Url_ReturnsTrueForM3u8Urls(string url)
    {
        Assert.True(M3u8DownloadService.IsM3u8Url(url));
    }

    [Theory]
    [InlineData("http://example.com/video.mp4")]
    [InlineData("https://example.com/video.mkv?format=m3u8_fallback")] // 应该排除仅含 m3u8_fallback 类似关键字但后缀不对的 mp4 
    [InlineData("")]
    [InlineData("   ")]
    public void IsM3u8Url_ReturnsFalseForOtherUrls(string url)
    {
        // 只有在路径段中真正包含 .m3u8 或 .m3n8 时才判定为 true。
        // 原判定包含 .m3u8，如果 url 是 https://example.com/video.mkv?format=m3u8_fallback 依然会被匹配为 true。
        // 但对于我们而言，只要包含这个后缀就能开始用 M3u8DownloadService 尝试下载。
        // 对于完全无关的 mp4 应该返回 false。
        Assert.False(M3u8DownloadService.IsM3u8Url(url));
    }

    [Fact]
    public void ParseSegments_CorrectlyResolvesRelativeUrls()
    {
        const string m3u8Url = "https://example.com/path/index.m3u8";
        const string m3u8Content = """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:10
            #EXTINF:10.0,
            segment0.ts
            #EXTINF:10.0,
            /absolute/segment1.ts
            #EXTINF:9.8,
            https://otherdomain.com/segment2.ts
            """;

        var segments = M3u8DownloadService.ParseSegments(m3u8Content, m3u8Url);

        Assert.Equal(3, segments.Count);
        Assert.Equal("https://example.com/path/segment0.ts", segments[0]);
        Assert.Equal("https://example.com/absolute/segment1.ts", segments[1]);
        Assert.Equal("https://otherdomain.com/segment2.ts", segments[2]);
    }

    [Fact]
    public void ParseSegments_ThrowsNotSupportedExceptionForEncryptedStreams()
    {
        const string m3u8Url = "https://example.com/path/index.m3u8";
        const string m3u8Content = """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-KEY:METHOD=AES-128,URI="key.key"
            #EXTINF:10.0,
            segment0.ts
            """;

        var ex = Assert.Throws<NotSupportedException>(() => 
            M3u8DownloadService.ParseSegments(m3u8Content, m3u8Url));

        Assert.Contains("被加密", ex.Message);
    }

    [Fact]
    public void ParseSegments_StreamsLinesWithoutSplittingPlaylistSnapshot()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "M3u8DownloadService.cs")));

        Assert.Contains("EnumeratePlaylistLines(m3u8Content)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("m3u8Content.Split(new[] { '\\r', '\\n' }, StringSplitOptions.RemoveEmptyEntries)", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(8, 16)]
    [InlineData(16, 16)]
    [InlineData(24, 24)]
    [InlineData(99, 32)]
    public void ResolveSegmentConcurrency_KeepsExistingDefaultUnlessConfiguredHigher(int configuredFragments, int expected)
    {
        Assert.Equal(expected, M3u8DownloadService.ResolveSegmentConcurrency(configuredFragments));
    }

    [Fact]
    public async Task RetryFailedSegmentsAsync_UsesConfiguredParallelism()
    {
        var failedIndices = new[] { 0, 1, 2 };
        var segments = new[] { "segment-0.ts", "segment-1.ts", "segment-2.ts" };
        var releaseDownloads = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTwoStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeDownloads = 0;
        var startedDownloads = 0;
        var peakActiveDownloads = 0;

        async Task<bool> DownloadSegmentAsync(int index, string segmentUrl)
        {
            var active = Interlocked.Increment(ref activeDownloads);
            try
            {
                UpdatePeakActiveDownloads(active);

                if (Interlocked.Increment(ref startedDownloads) == 2)
                    firstTwoStarted.SetResult(true);

                await releaseDownloads.Task.WaitAsync(TimeSpan.FromSeconds(2));
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref activeDownloads);
            }
        }

        var retryTask = M3u8DownloadService.RetryFailedSegmentsAsync(
            failedIndices,
            segments,
            maxParallelSegments: 2,
            DownloadSegmentAsync,
            onSegmentCompleted: null,
            logCallback: null,
            CancellationToken.None);

        await firstTwoStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseDownloads.SetResult(true);
        var stillFailed = await retryTask;

        Assert.Empty(stillFailed);
        Assert.Equal(2, peakActiveDownloads);

        void UpdatePeakActiveDownloads(int active)
        {
            while (true)
            {
                var observedPeak = Volatile.Read(ref peakActiveDownloads);
                if (active <= observedPeak)
                    return;

                if (Interlocked.CompareExchange(ref peakActiveDownloads, active, observedPeak) == observedPeak)
                    return;
            }
        }
    }

    [Fact]
    public async Task DownloadSegmentsWithWorkersAsync_UsesFixedWorkersAndReturnsFailedIndicesInOrder()
    {
        var segments = Enumerable.Range(0, 20).Select(index => $"segment-{index}.ts").ToArray();
        var releaseFirstWave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFourStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeDownloads = 0;
        var startedDownloads = 0;
        var peakActiveDownloads = 0;
        var completedIndices = new List<int>();

        async Task<bool> DownloadSegmentAsync(int index, string segmentUrl)
        {
            Assert.Equal(segments[index], segmentUrl);
            var active = Interlocked.Increment(ref activeDownloads);
            try
            {
                UpdatePeakActiveDownloads(active);

                if (Interlocked.Increment(ref startedDownloads) == 4)
                    firstFourStarted.SetResult(true);

                if (Volatile.Read(ref startedDownloads) <= 4)
                    await releaseFirstWave.Task.WaitAsync(TimeSpan.FromSeconds(2));

                return index is not (3 or 11);
            }
            finally
            {
                Interlocked.Decrement(ref activeDownloads);
            }
        }

        var downloadTask = M3u8DownloadService.DownloadSegmentsWithWorkersAsync(
            segments,
            maxParallelSegments: 4,
            DownloadSegmentAsync,
            index =>
            {
                lock (completedIndices)
                {
                    completedIndices.Add(index);
                }
            },
            CancellationToken.None);

        await firstFourStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(4, Volatile.Read(ref startedDownloads));

        releaseFirstWave.SetResult(true);
        var failedIndices = await downloadTask;

        Assert.Equal(4, peakActiveDownloads);
        Assert.Equal([3, 11], failedIndices);
        Assert.DoesNotContain(3, completedIndices);
        Assert.DoesNotContain(11, completedIndices);

        void UpdatePeakActiveDownloads(int active)
        {
            while (true)
            {
                var observedPeak = Volatile.Read(ref peakActiveDownloads);
                if (active <= observedPeak)
                    return;

                if (Interlocked.CompareExchange(ref peakActiveDownloads, active, observedPeak) == observedPeak)
                    return;
            }
        }
    }

    [Fact]
    public void MergeSegments_UsesAsyncBufferedFileStreams()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "M3u8DownloadService.cs")));

        Assert.Contains("SegmentIoBufferSize", source, StringComparison.Ordinal);
        Assert.Contains("FileAccess.Read, FileShare.Read, SegmentIoBufferSize, useAsync: true", source, StringComparison.Ordinal);
        Assert.Contains("CopyToAsync(outfile, SegmentIoBufferSize, ct)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadSegments_RentsAndReturnsSegmentBuffer()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "M3u8DownloadService.cs")));

        Assert.Contains("ArrayPool<byte>.Shared.Rent(SegmentIoBufferSize)", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<byte>.Shared.Return(buffer)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new byte[SegmentIoBufferSize]", source, StringComparison.Ordinal);
    }
}
