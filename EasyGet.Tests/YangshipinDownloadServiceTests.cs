using System.Net;
using System.Net.Sockets;
using System.Text;
using EasyGet.Models;
using EasyGet.Services;
using EasyGet.Services.Cookies;
using Xunit;

namespace EasyGet.Tests;

public sealed class YangshipinDownloadServiceTests
{
    private const string PageUrl =
        "https://www.yangshipin.cn/video/home?vid=b000045ctqj";
    private const string MediaUrl =
        "https://mp4playcloud-cdn.ysp.cctv.cn/b000045ctqj.Ctps10004.mp4?vkey=secret&app_id=519748109";

    [Fact]
    public void ParseDumpedDom_ExtractsSignedMediaTitleThumbnailAndDuration()
    {
        const string dom = """
            <html><body>
              <video id="myvideob000045ctqj"
                     src="https://mp4playcloud-cdn.ysp.cctv.cn/b000045ctqj.Ctps10004.mp4?vkey=secret&amp;app_id=519748109"
                     poster="https://img.yangshipin.cn/cover.jpg?x=1&amp;y=2"></video>
              <div class="video-main-l-title">
                <div class="title overflow-1">
                  2026年美加墨世界杯半决赛 法国VS西班牙
                </div>
              </div>
              <div>可试看2小时1分钟24秒</div>
            </body></html>
            """;
        var urlInfo = AssertUrlInfo();

        var capture = ChromiumYangshipinPageCapture.ParseDumpedDom(dom, urlInfo);

        Assert.Equal(MediaUrl, capture.VideoUrl);
        Assert.Equal("2026年美加墨世界杯半决赛 法国VS西班牙", capture.Title);
        Assert.Equal("https://img.yangshipin.cn/cover.jpg?x=1&y=2", capture.ThumbnailUrl);
        Assert.Equal(7_284, capture.Duration);
    }

    [Fact]
    public void ParseDumpedDom_RejectsForeignOrUnrelatedMp4Sources()
    {
        const string dom = """
            <video src="https://evil.example/b000045ctqj.mp4?vkey=secret"></video>
            <video src="https://mp4playcloud-cdn.ysp.cctv.cn/advertisement.mp4"></video>
            """;

        var exception = Assert.Throws<YangshipinMediaUnavailableException>(() =>
            ChromiumYangshipinPageCapture.ParseDumpedDom(dom, AssertUrlInfo()));

        Assert.Contains("未提供可下载", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetVideoInfoAsync_CachesOnlyStableMetadata()
    {
        var capture = new FakePageCapture(CreateCapture());
        var downloader = new FakeDirectDownloader { ContentLength = 2_455_784_251 };
        var service = new YangshipinDownloadService(capture, downloader);

        var first = await service.GetVideoInfoAsync(PageUrl);
        var second = await service.GetVideoInfoAsync(PageUrl + "&from=share");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, capture.CallCount);
        Assert.Equal(1, downloader.ContentLengthCallCount);
        Assert.Equal("央视频", first!.Platform);
        Assert.Equal(2_455_784_251, first.FileSize);
        Assert.Equal(PageUrl + "&from=share", second!.Url);
    }

    [Fact]
    public async Task GetVideoInfoAsync_CoalescesConcurrentRequestsForSameVideo()
    {
        var capture = new BlockingPageCapture(CreateCapture());
        var downloader = new FakeDirectDownloader { ContentLength = 2_455_784_251 };
        var service = new YangshipinDownloadService(capture, downloader);

        var requests = Enumerable.Range(0, 8)
            .Select(index => service.GetVideoInfoAsync($"{PageUrl}&from=batch-{index}"))
            .ToArray();
        await capture.WaitUntilCapturedAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, capture.CallCount);
        capture.Release();
        var results = await Task.WhenAll(requests);

        Assert.All(results, Assert.NotNull);
        Assert.Equal(1, capture.CallCount);
        Assert.Equal(1, downloader.ContentLengthCallCount);
    }

    [Fact]
    public async Task DownloadAsync_UsesFreshCaptureAndCompletesTask()
    {
        using var root = new TestDirectory();
        var capture = new FakePageCapture(CreateCapture());
        var downloader = new FakeDirectDownloader { DownloadedBytes = 10 };
        var service = new YangshipinDownloadService(capture, downloader);
        var logs = new List<string>();
        var task = new DownloadTask
        {
            Url = PageUrl,
            Title = "",
            Format = "mp4",
            Quality = "720",
            OutputDirectory = root.DirectoryPath
        };

        await service.DownloadAsync(task, logCallback: logs.Add);

        Assert.Equal(1, capture.CallCount);
        Assert.Equal(1, downloader.DownloadCallCount);
        Assert.Equal(MediaUrl, downloader.LastMediaUrl);
        Assert.Equal(PageUrl, downloader.LastReferer);
        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal("央视频", task.Platform);
        Assert.Equal("2026年美加墨世界杯半决赛 法国VS西班牙", task.Title);
        Assert.Equal("best", task.Quality);
        Assert.Contains(logs, line => line.Contains("默认最高画质", StringComparison.Ordinal));
        Assert.Equal(10, task.FileSize);
        Assert.True(File.Exists(task.OutputFilePath));
        Assert.EndsWith(".mp4", task.OutputFilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadAsync_RefreshesExpiredSignedUrlAndResumes()
    {
        using var root = new TestDirectory();
        var capture = new FakePageCapture(CreateCapture());
        var downloader = new FakeDirectDownloader
        {
            DownloadedBytes = 10,
            FailFirstDownloadWithForbidden = true
        };
        var service = new YangshipinDownloadService(capture, downloader);
        var task = new DownloadTask
        {
            Url = PageUrl,
            Title = "match",
            Format = "mp4",
            OutputDirectory = root.DirectoryPath
        };

        await service.DownloadAsync(task);

        Assert.Equal(2, capture.CallCount);
        Assert.Equal(2, downloader.DownloadCallCount);
        Assert.Equal(DownloadStatus.Completed, task.Status);
    }

    [Fact]
    public async Task DownloadAsync_RejectsUnsupportedOutputWithoutLaunchingBrowser()
    {
        using var root = new TestDirectory();
        var capture = new FakePageCapture(CreateCapture());
        var downloader = new FakeDirectDownloader();
        var service = new YangshipinDownloadService(capture, downloader);
        var task = new DownloadTask
        {
            Url = PageUrl,
            Format = "mp3",
            OutputDirectory = root.DirectoryPath
        };

        await service.DownloadAsync(task);

        Assert.Equal(0, capture.CallCount);
        Assert.Equal(0, downloader.DownloadCallCount);
        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.Contains("MP4", task.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateBrowserStartInfo_UsesArgumentListAndConfiguredProxy()
    {
        var config = new AppConfig
        {
            UseProxy = true,
            ProxyAddress = "http://127.0.0.1:7890"
        };

        var startInfo = ChromiumYangshipinPageCapture.CreateBrowserStartInfo(
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Temp\EasyGet Profile",
            PageUrl,
            config);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Contains("--headless=new", arguments);
        Assert.Contains("--dump-dom", arguments);
        Assert.Contains("--user-data-dir=C:\\Temp\\EasyGet Profile", arguments);
        Assert.Contains("--proxy-server=http://127.0.0.1:7890", arguments);
        Assert.Equal(PageUrl, arguments[^1]);
    }

    [Fact]
    public async Task YtDlpService_RoutesYangshipinMetadataAndDownloadToDedicatedService()
    {
        using var root = new TestDirectory();
        var config = new ConfigService(root.Path("config"));
        var coordinator = new CookieAcquisitionCoordinator(
            config,
            new PlatformCookieVault(root.Path("config")),
            new EmptyBrowserProfileDiscoveryService(),
            new CookieHealthStore(root.Path("config")),
            new EmptyManagedLoginSessionService(),
            root.Path("cookies"));
        var fake = new FakeYangshipinDownloadService();
        var service = new YtDlpService(
            config,
            new EnvironmentService(),
            coordinator,
            fake);
        var task = new DownloadTask { Url = PageUrl, Format = "mp4" };

        var info = await service.GetVideoInfoAsync(PageUrl);
        await service.DownloadAsync(task);

        Assert.NotNull(info);
        Assert.Equal(1, fake.MetadataCallCount);
        Assert.Equal(1, fake.DownloadCallCount);
        Assert.Equal(DownloadStatus.Completed, task.Status);
    }

    [Fact]
    public async Task DirectDownloader_ResumesPartFileWithRequiredRequestHeaders()
    {
        using var root = new TestDirectory();
        using var server = new ResumeHttpServer();
        var config = new ConfigService(root.Path("config"));
        var downloader = new YangshipinDirectDownloader(config);
        var temporaryPath = root.Path("match.mp4.part");
        var outputPath = root.Path("match.mp4");
        await File.WriteAllTextAsync(temporaryPath, "abcde");

        var downloaded = await downloader.DownloadAsync(
            server.Url,
            PageUrl,
            temporaryPath,
            outputPath,
            null,
            CancellationToken.None);
        var request = await server.Request.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(10, downloaded);
        Assert.Equal("abcdefghij", await File.ReadAllTextAsync(outputPath));
        Assert.False(File.Exists(temporaryPath));
        Assert.Contains("Range: bytes=5-", request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"Referer: {PageUrl}", request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Origin: https://www.yangshipin.cn", request, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirectDownloader_RejectsRedirectOutsideTrustedMediaBoundary()
    {
        using var root = new TestDirectory();
        using var server = new RedirectingHttpServer();
        var config = new ConfigService(root.Path("config"));
        var downloader = new YangshipinDirectDownloader(config);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            server.Url,
            PageUrl,
            root.Path("redirect.mp4.part"),
            root.Path("redirect.mp4"),
            null,
            CancellationToken.None));

        Assert.Equal(1, server.RequestCount);
        Assert.False(File.Exists(root.Path("redirect.mp4")));
    }

    private static YangshipinUrlInfo AssertUrlInfo()
    {
        Assert.True(YangshipinUrlParser.TryParse(PageUrl, out var info));
        return info;
    }

    private static YangshipinMediaCapture CreateCapture()
        => new(
            "b000045ctqj",
            PageUrl,
            MediaUrl,
            "2026年美加墨世界杯半决赛 法国VS西班牙",
            "https://img.yangshipin.cn/cover.jpg",
            7_284);

    private sealed class FakePageCapture(YangshipinMediaCapture capture)
        : IYangshipinPageCapture
    {
        public int CallCount { get; private set; }

        public Task<YangshipinMediaCapture> CaptureAsync(
            YangshipinUrlInfo urlInfo,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(capture);
        }
    }

    private sealed class BlockingPageCapture(YangshipinMediaCapture capture)
        : IYangshipinPageCapture
    {
        private readonly TaskCompletionSource _captured = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<YangshipinMediaCapture> CaptureAsync(
            YangshipinUrlInfo urlInfo,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _captured.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return capture;
        }

        public Task WaitUntilCapturedAsync() => _captured.Task;

        public void Release() => _release.TrySetResult();
    }

    private sealed class FakeDirectDownloader : IYangshipinDirectDownloader
    {
        public long ContentLength { get; set; }
        public long DownloadedBytes { get; set; } = 10;
        public bool FailFirstDownloadWithForbidden { get; set; }
        public int ContentLengthCallCount { get; private set; }
        public int DownloadCallCount { get; private set; }
        public string LastMediaUrl { get; private set; } = "";
        public string LastReferer { get; private set; } = "";

        public Task<long> GetContentLengthAsync(
            string mediaUrl,
            string referer,
            CancellationToken cancellationToken)
        {
            ContentLengthCallCount++;
            return Task.FromResult(ContentLength);
        }

        public async Task<long> DownloadAsync(
            string mediaUrl,
            string referer,
            string temporaryPath,
            string outputPath,
            IProgress<DownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadCallCount++;
            LastMediaUrl = mediaUrl;
            LastReferer = referer;
            if (FailFirstDownloadWithForbidden && DownloadCallCount == 1)
            {
                throw new HttpRequestException(
                    "expired",
                    null,
                    HttpStatusCode.Forbidden);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllBytesAsync(
                outputPath,
                Enumerable.Repeat((byte)1, checked((int)DownloadedBytes)).ToArray(),
                cancellationToken);
            return DownloadedBytes;
        }
    }

    private sealed class FakeYangshipinDownloadService : IYangshipinDownloadService
    {
        public int MetadataCallCount { get; private set; }
        public int DownloadCallCount { get; private set; }

        public Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            MetadataCallCount++;
            return Task.FromResult<VideoInfo?>(new VideoInfo
            {
                Title = "match",
                Platform = "央视频",
                Url = url
            });
        }

        public Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCallCount++;
            task.Status = DownloadStatus.Completed;
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyBrowserProfileDiscoveryService
        : IBrowserProfileDiscoveryService
    {
        public IReadOnlyList<BrowserProfile> Discover() => [];
    }

    private sealed class ResumeHttpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serverTask;

        public ResumeHttpServer()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/b000045ctqj.mp4";
            _serverTask = Task.Run(ServeAsync);
        }

        public string Url { get; }
        public TaskCompletionSource<string> Request { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                var stream = client.GetStream();
                var request = await ReadHeadersAsync(stream, _cts.Token);
                Request.TrySetResult(request);
                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 206 Partial Content\r\n"
                    + "Content-Type: video/mp4\r\n"
                    + "Content-Length: 5\r\n"
                    + "Content-Range: bytes 5-9/10\r\n"
                    + "Connection: close\r\n\r\n"
                    + "fghij");
                await stream.WriteAsync(response, _cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public static async Task<string> ReadHeadersAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[1024];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;
                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            return request.ToString();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _serverTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
            }
            _cts.Dispose();
        }
    }

    private sealed class RedirectingHttpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serverTask;
        private int _requestCount;

        public RedirectingHttpServer()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/b000045ctqj.mp4";
            _serverTask = Task.Run(ServeAsync);
        }

        public string Url { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);

        private async Task ServeAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    var stream = client.GetStream();
                    _ = await ResumeHttpServer.ReadHeadersAsync(stream, _cts.Token);
                    var requestNumber = Interlocked.Increment(ref _requestCount);
                    var response = requestNumber == 1
                        ? Encoding.ASCII.GetBytes(
                            "HTTP/1.1 302 Found\r\n"
                            + "Location: /redirected.mp4\r\n"
                            + "Content-Length: 0\r\n"
                            + "Connection: close\r\n\r\n")
                        : Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\n"
                            + "Content-Type: video/mp4\r\n"
                            + "Content-Length: 10\r\n"
                            + "Connection: close\r\n\r\n"
                            + "abcdefghij");
                    await stream.WriteAsync(response, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _serverTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
            }
            _cts.Dispose();
        }
    }
}
