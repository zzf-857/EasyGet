using EasyGet.Services;
using EasyGet.Models;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace EasyGet.Tests;

public class DouyinBrowserDownloadServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"easyget-douyin-{Guid.NewGuid():N}");

    [Fact]
    public void TryExtractVideoUrlFromCdpMessage_ReturnsDouyinMp4ResponseUrl()
    {
        const string message = """
            {
              "method": "Network.responseReceived",
              "params": {
                "response": {
                  "mimeType": "video/mp4",
                  "url": "https://v26-web.douyinvod.com/path/video.mp4?a=6383"
                }
              }
            }
            """;

        var found = DouyinBrowserDownloadService.TryExtractVideoUrlFromCdpMessage(message, out var url);

        Assert.True(found);
        Assert.Equal("https://v26-web.douyinvod.com/path/video.mp4?a=6383", url);
    }

    [Fact]
    public void TryExtractThumbnailUrlFromCdpMessage_ReturnsDouyinImageResponseUrl()
    {
        const string message = """
            {
              "method": "Network.responseReceived",
              "params": {
                "response": {
                  "mimeType": "image/jpeg",
                  "url": "https://p3-pc-sign.douyinpic.com/image-cut-tos/cover.jpeg"
                }
              }
            }
            """;

        var found = DouyinBrowserDownloadService.TryExtractThumbnailUrlFromCdpMessage(message, out var url);

        Assert.True(found);
        Assert.Equal("https://p3-pc-sign.douyinpic.com/image-cut-tos/cover.jpeg", url);
    }

    [Fact]
    public void TryExtractUrlsFromCdpMessage_IgnoresNonStringResponseFields()
    {
        const string message = """
            {
              "method": "Network.responseReceived",
              "params": {
                "response": {
                  "mimeType": 42,
                  "url": { "href": "https://v26-web.douyinvod.com/path/video.mp4" }
                }
              }
            }
            """;

        var foundVideo = DouyinBrowserDownloadService.TryExtractVideoUrlFromCdpMessage(message, out var videoUrl);
        var foundThumbnail = DouyinBrowserDownloadService.TryExtractThumbnailUrlFromCdpMessage(message, out var thumbnailUrl);

        Assert.False(foundVideo);
        Assert.Equal("", videoUrl);
        Assert.False(foundThumbnail);
        Assert.Equal("", thumbnailUrl);
    }

    [Fact]
    public void BuildOutputPath_AppendsCounterWhenFileExists()
    {
        Directory.CreateDirectory(_tempDir);
        var existing = Path.Combine(_tempDir, "title.mp4");
        File.WriteAllText(existing, "existing");

        var outputPath = DouyinBrowserDownloadService.BuildOutputPath(_tempDir, "title");

        Assert.Equal(Path.Combine(_tempDir, "title (1).mp4"), outputPath);
    }

    [Fact]
    public void NormalizeDouyinTitle_RemovesPlatformSuffixAndQuotes()
    {
        var title = DouyinBrowserDownloadService.NormalizeDouyinTitle(
            "《队友祭天，法力无边》@哈哈电竞俱乐部（上分点我）#抖音游戏趣讲作者团 - 抖音");

        Assert.Equal("队友祭天，法力无边@哈哈电竞俱乐部（上分点我）#抖音游戏趣讲作者团", title);
    }

    [Fact]
    public void ApplyCapturedMetadata_UpdatesTaskTitlePlatformAndThumbnail()
    {
        var task = new DownloadTask
        {
            Title = "",
            Platform = "",
            ThumbnailUrl = ""
        };

        DouyinBrowserDownloadService.ApplyCapturedMetadata(
            task,
            new DouyinBrowserCaptureResult(
                "https://v26-web.douyinvod.com/video.mp4",
                "《测试视频》 - 抖音",
                "https://p3-pc-sign.douyinpic.com/cover.jpeg"));

        Assert.Equal("测试视频", task.Title);
        Assert.Equal("Douyin", task.Platform);
        Assert.Equal("https://p3-pc-sign.douyinpic.com/cover.jpeg", task.ThumbnailUrl);
    }

    [Fact]
    public async Task DownloadFileAsync_RestartsWhenServerIgnoresRangeAfterPartialResponse()
    {
        var tempPath = Path.Combine(_tempDir, "video.mp4.part");
        var outputPath = Path.Combine(_tempDir, "video.mp4");
        Directory.CreateDirectory(_tempDir);

        using var server = new ScriptedHttpServer(
            """
            HTTP/1.1 206 Partial Content
            Content-Type: video/mp4
            Content-Length: 5
            Content-Range: bytes 0-4/10
            Connection: close

            abcde
            """,
            """
            HTTP/1.1 200 OK
            Content-Type: video/mp4
            Content-Length: 10
            Connection: close

            abcdefghij
            """);

        await DouyinBrowserDownloadService.DownloadFileAsync(
            server.Url,
            tempPath,
            outputPath,
            "https://www.douyin.com/",
            null,
            CancellationToken.None);

        Assert.Equal("abcdefghij", File.ReadAllText(outputPath));
        Assert.Equal(10, new FileInfo(outputPath).Length);
    }

    [Fact]
    public async Task DownloadFileAsync_RemovesTempFileWhenCancelled()
    {
        var tempPath = Path.Combine(_tempDir, "cancelled.mp4.part");
        var outputPath = Path.Combine(_tempDir, "cancelled.mp4");
        Directory.CreateDirectory(_tempDir);

        using var server = new SlowBodyHttpServer(totalBytes: 64 * 1024, chunkBytes: 1024, delayMs: 20);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DouyinBrowserDownloadService.DownloadFileAsync(
                server.Url,
                tempPath,
                outputPath,
                "https://www.douyin.com/",
                null,
                cts.Token));

        Assert.False(File.Exists(tempPath));
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task DownloadFileAsync_ThrowsIOExceptionAndCleansTempWhenResponseIsInterrupted()
    {
        var tempPath = Path.Combine(_tempDir, "no-progress.mp4.part");
        var outputPath = Path.Combine(_tempDir, "no-progress.mp4");
        Directory.CreateDirectory(_tempDir);

        using var server = new ScriptedHttpServer(
            """
            HTTP/1.1 200 OK
            Content-Type: video/mp4
            Content-Length: 0
            Connection: close

            """);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));

        await Assert.ThrowsAsync<IOException>(() =>
            DouyinBrowserDownloadService.DownloadFileAsync(
                server.Url,
                tempPath,
                outputPath,
                "https://www.douyin.com/",
                null,
                cts.Token));

        Assert.False(File.Exists(tempPath));
        Assert.False(File.Exists(outputPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private sealed class ScriptedHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly IReadOnlyList<byte[]> _responses;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serverTask;

        public string Url { get; }

        public ScriptedHttpServer(params string[] responses)
        {
            _responses = responses.Select(ToHttpBytes).ToList();
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();

            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/video.mp4";
            _serverTask = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            try
            {
                foreach (var response in _responses)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    var stream = client.GetStream();
                    await ReadRequestAsync(stream, _cts.Token);
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

        public static async Task ReadRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[1024];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                    break;

                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
        }

        private static byte[] ToHttpBytes(string response)
        {
            var normalized = response
                .Trim()
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\n", "\r\n", StringComparison.Ordinal);

            return Encoding.ASCII.GetBytes(normalized);
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

    private sealed class SlowBodyHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _totalBytes;
        private readonly int _chunkBytes;
        private readonly int _delayMs;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serverTask;

        public string Url { get; }

        public SlowBodyHttpServer(int totalBytes, int chunkBytes, int delayMs)
        {
            _totalBytes = totalBytes;
            _chunkBytes = chunkBytes;
            _delayMs = delayMs;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();

            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/slow-video.mp4";
            _serverTask = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                var stream = client.GetStream();
                await ScriptedHttpServer.ReadRequestAsync(stream, _cts.Token);

                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: video/mp4\r\nContent-Length: {_totalBytes}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, _cts.Token);

                var chunk = Enumerable.Repeat((byte)'x', _chunkBytes).ToArray();
                var remaining = _totalBytes;
                while (remaining > 0 && !_cts.IsCancellationRequested)
                {
                    var length = Math.Min(chunk.Length, remaining);
                    await stream.WriteAsync(chunk.AsMemory(0, length), _cts.Token);
                    remaining -= length;
                    await Task.Delay(_delayMs, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
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
