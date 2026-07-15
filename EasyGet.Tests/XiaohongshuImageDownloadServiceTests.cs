using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class XiaohongshuImageDownloadServiceTests
{
    [Theory]
    [InlineData("https://www.xiaohongshu.com/discovery/item/6a1d4bd30000000008024b72?source=webshare", "6a1d4bd30000000008024b72")]
    [InlineData("https://www.xiaohongshu.com/explore/6a1d4bd30000000008024b72", "6a1d4bd30000000008024b72")]
    [InlineData("https://www.xiaohongshu.com/explore/abc123XYZ", "abc123XYZ")]
    [InlineData("https://www.xiaohongshu.com/user/profile/123", "")]
    public void ExtractNoteId_ExtractsCorrectId(string url, string expectedId)
    {
        var noteId = XiaohongshuImageDownloadService.ExtractNoteId(url);
        Assert.Equal(expectedId, noteId);
    }

    [Fact]
    public void ExtractNoteDataFromJson_ExtractsExpectedProperties_And_ReplacesUndefined()
    {
        const string noteId = "6a1d4bd30000000008024b72";
        const string html = """
            <html>
            <body>
            <script>
            window.__INITIAL_STATE__ = {
              "note": {
                "noteDetailMap": {
                  "6a1d4bd30000000008024b72": {
                    "note": {
                      "title": "测试图文标题",
                      "desc": "测试图文描述",
                      "imageList": [
                        {
                          "urlDefault": "http://sns-webpic-qc.xhscdn.com/img1.jpg",
                          "urlPre": undefined
                        }
                      ]
                    }
                  }
                }
              }
            };
            </script>
            </body>
            </html>
            """;

        var noteData = XiaohongshuImageDownloadService.ExtractNoteDataFromJson(html, noteId);
        
        Assert.NotNull(noteData);
        Assert.True(noteData.Value.TryGetProperty("title", out var titleProp));
        Assert.Equal("测试图文标题", titleProp.GetString());

        Assert.True(noteData.Value.TryGetProperty("desc", out var descProp));
        Assert.Equal("测试图文描述", descProp.GetString());

        Assert.True(noteData.Value.TryGetProperty("imageList", out var imageListProp));
        Assert.Equal(JsonValueKind.Array, imageListProp.ValueKind);
        Assert.Equal(1, imageListProp.GetArrayLength());

        var firstImage = imageListProp[0];
        Assert.True(firstImage.TryGetProperty("urlDefault", out var urlDefaultProp));
        Assert.Equal("http://sns-webpic-qc.xhscdn.com/img1.jpg", urlDefaultProp.GetString());

        Assert.True(firstImage.TryGetProperty("urlPre", out var urlPreProp));
        Assert.Equal(JsonValueKind.Null, urlPreProp.ValueKind); // undefined was replaced with null
    }

    [Fact]
    public void ExtractNoteDataFromJson_DisposesJsonDocumentAfterCloningResult()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "XiaohongshuImageDownloadService.cs")));
        var sourceLines = source.Split(Environment.NewLine);

        Assert.Contains("using var doc = JsonDocument.Parse(jsonStr);", source, StringComparison.Ordinal);
        Assert.DoesNotContain(sourceLines, line =>
            line.TrimStart().StartsWith("var doc = JsonDocument.Parse(jsonStr);", StringComparison.Ordinal));
    }

    [Fact]
    public void DownloadFileAsync_RentsBufferAndUsesAsyncFileStream()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("Services", "XiaohongshuImageDownloadService.cs")));

        Assert.Contains("ImageDownloadBufferSize", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<byte>.Shared.Rent(ImageDownloadBufferSize)", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<byte>.Shared.Return(buffer)", source, StringComparison.Ordinal);
        Assert.Contains("new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, ImageDownloadBufferSize, useAsync: true)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new byte[64 * 1024]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IsXiaohongshuUrl_IdentifiesCorrectDomains()
    {
        Assert.True(YtDlpService.IsXiaohongshuUrl("https://www.xiaohongshu.com/explore/abc"));
        Assert.True(YtDlpService.IsXiaohongshuUrl("http://xiaohongshu.com/discovery/item/123"));
        Assert.True(YtDlpService.IsXiaohongshuUrl("https://xhslink.com/aBc123XYZ"));
        Assert.False(YtDlpService.IsXiaohongshuUrl("https://www.bilibili.com/video/BV123"));
        Assert.False(YtDlpService.IsXiaohongshuUrl(""));
        Assert.False(YtDlpService.IsXiaohongshuUrl(null!));
    }

    [Fact]
    public async Task TryDownloadAsync_DownloadsImagesWithBoundedConcurrencyAndStableOutputOrder()
    {
        using var server = new ConcurrentImageNoteServer(imageCount: 4);
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"easyget-xhs-concurrent-{Guid.NewGuid():N}");
        var task = new DownloadTask
        {
            Url = server.NoteUrl,
            OutputDirectory = outputDirectory
        };
        var service = new XiaohongshuImageDownloadService(new TestConfigService());

        try
        {
            var downloadTask = service.TryDownloadAsync(task, ct: CancellationToken.None);
            var completed = await Task.WhenAny(
                server.AllImageRequestsStarted,
                Task.Delay(TimeSpan.FromMilliseconds(300)));

            server.ReleaseImages();
            var success = await downloadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Same(server.AllImageRequestsStarted, completed);
            Assert.True(success);
            Assert.Equal(DownloadStatus.Completed, task.Status);
            Assert.EndsWith($"{Path.DirectorySeparatorChar}1.jpg", task.OutputFilePath);

            var subfolder = Path.Combine(outputDirectory, "并发下载测试");
            Assert.Equal(
                ["1.jpg", "2.jpg", "3.jpg", "4.jpg"],
                Directory.GetFiles(subfolder)
                    .Select(path => Path.GetFileName(path) ?? "")
                    .OrderBy(fileName => fileName, StringComparer.Ordinal)
                    .ToArray());
        }
        finally
        {
            server.ReleaseImages();
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact(Skip = "Live external-site test. Run manually when validating Xiaohongshu network behavior.")]
    public async Task LiveDownloadTest()
    {
        var configService = new TestConfigService();
        await configService.LoadAsync();
        
        var service = new XiaohongshuImageDownloadService(configService);
        var url = "https://www.xiaohongshu.com/discovery/item/6a1d4bd30000000008024b72?source=webshare&xhsshare=pc_web&xsec_token=ABIb_Pvhngcei7NkLQkQ95_-UO3w49ylotO02t3FD-J-U=&xsec_source=pc_share";
        
        var info = await service.GetImageNoteInfoAsync(url);
        Assert.NotNull(info);
        Assert.Contains("AI", info.Title);
        Assert.Equal("XiaoHongShu", info.Platform);
        
        var tempDir = Path.Combine(Path.GetTempPath(), $"easyget-xhs-live-test-{Guid.NewGuid():N}");
        var task = new DownloadTask
        {
            Url = url,
            Title = info.Title,
            OutputDirectory = tempDir
        };
        
        try
        {
            var success = await service.TryDownloadAsync(task);
            Assert.True(success);
            Assert.Equal(DownloadStatus.Completed, task.Status);
            Assert.True(task.Format == "jpg" || task.Format == "png" || task.Format == "webp");
            Assert.True(Directory.Exists(task.OutputDirectory));
            
            var subfolder = Path.Combine(task.OutputDirectory, DownloadFileNameBuilder.SanitizeResolvedTitle(info.Title));
            Assert.True(Directory.Exists(subfolder));
            var files = Directory.GetFiles(subfolder);
            Assert.NotEmpty(files);
            foreach (var file in files)
            {
                Assert.True(new FileInfo(file).Length > 0);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    private sealed class ConcurrentImageNoteServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _allImageRequestsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseImages = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serverTask;
        private readonly int _imageCount;
        private int _startedImageRequests;

        public string BaseUrl { get; }
        public string NoteUrl => $"{BaseUrl}/explore/abc123";
        public Task AllImageRequestsStarted => _allImageRequestsStarted.Task;

        public ConcurrentImageNoteServer(int imageCount)
        {
            _imageCount = imageCount;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();

            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _serverTask = Task.Run(ServeAsync);
        }

        public void ReleaseImages() => _releaseImages.TrySetResult();

        private async Task ServeAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => ServeClientAsync(client), _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task ServeClientAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var path = await ReadRequestPathAsync(stream, _cts.Token);

                if (path.StartsWith("/explore/", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTextResponseAsync(stream, BuildNoteHtml(), "text/html; charset=utf-8", _cts.Token);
                    return;
                }

                if (path.StartsWith("/image/", StringComparison.OrdinalIgnoreCase))
                {
                    if (Interlocked.Increment(ref _startedImageRequests) == _imageCount)
                        _allImageRequestsStarted.TrySetResult();

                    await _releaseImages.Task.WaitAsync(_cts.Token);
                    await WriteTextResponseAsync(stream, "image-bytes", "image/jpeg", _cts.Token);
                    return;
                }

                await WriteTextResponseAsync(stream, "not found", "text/plain", _cts.Token, "404 Not Found");
            }
        }

        private string BuildNoteHtml()
        {
            var images = string.Join(
                ",",
                Enumerable.Range(1, _imageCount).Select(index => $$"""{"urlDefault":"{{BaseUrl}}/image/{{index}}.jpg"}"""));

            return $$"""
                <html>
                <body>
                <script>
                window.__INITIAL_STATE__ = {
                  "note": {
                    "noteDetailMap": {
                      "abc123": {
                        "note": {
                          "title": "并发下载测试",
                          "desc": "并发下载测试",
                          "imageList": [{{images}}]
                        }
                      }
                    }
                  }
                };
                </script>
                </body>
                </html>
                """;
        }

        private static async Task<string> ReadRequestPathAsync(NetworkStream stream, CancellationToken ct)
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

            var firstLine = request.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[1] : "/";
        }

        private static async Task WriteTextResponseAsync(
            NetworkStream stream,
            string body,
            string contentType,
            CancellationToken ct,
            string status = "200 OK")
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");

            await stream.WriteAsync(headers, ct);
            await stream.WriteAsync(bodyBytes, ct);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _releaseImages.TrySetResult();
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
