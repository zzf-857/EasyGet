using System.Net;
using System.Net.Sockets;
using System.Text;
using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class HttpResourceDownloadServiceTests
{
    [Theory]
    [InlineData("https://example.test/course/lesson.pdf?token=1", true)]
    [InlineData("https://example.test/course/slides.pptx", true)]
    [InlineData("https://example.test/course/photo.webp", true)]
    [InlineData("https://example.test/course/page", false)]
    [InlineData("https://example.test/course/video.mp4", false)]
    public void IsResourceUrl_RecognizesNonVideoExtensions(string url, bool expected)
    {
        Assert.Equal(expected, HttpResourceDownloadService.IsResourceUrl(url));
    }

    [Fact]
    public void DownloadRouteResolver_UsesResourceHintForExtensionlessAttachmentUrl()
    {
        Assert.Equal(
            DownloadEngine.Resource,
            DownloadRouteResolver.Resolve(
                "https://example.test/download?id=lesson-notes",
                resourceHint: true));
    }

    [Fact]
    public void DownloadRouteResolver_PreservesResourceExtensionHintForExtensionlessUrl()
    {
        Assert.True(DownloadRouteResolver.TryCreateLocalVideoInfo(
            "https://example.test/download?id=lesson-notes",
            out var info,
            resourceHint: true,
            resourceExtensionHint: "pdf",
            resourceMimeType: "application/pdf"));

        Assert.True(info.IsResource);
        Assert.Equal("pdf", info.Extension);
        Assert.Equal("application/pdf", info.MimeType);
    }

    [Theory]
    [InlineData("第一章讲义", "第一章讲义.pdf")]
    [InlineData("Chapter 1. Notes", "Chapter 1. Notes.pdf")]
    public async Task DownloadAsync_WritesResourceWithRetrySafeOutputAndProgress(
        string title,
        string expectedFileName)
    {
        using var root = new TestDirectory();
        using var server = new ResourceServer(
            "application/pdf",
            Encoding.UTF8.GetBytes("course notes"));
        var config = new ConfigService(root.Path("config"));
        config.Config.DefaultDownloadPath = root.Path("downloads");
        var service = new HttpResourceDownloadService(config);
        var task = new DownloadTask
        {
            Url = server.Url,
            Title = title,
            OutputDirectory = root.Path("downloads"),
            ResourceExtension = "pdf",
            ResourceMimeType = "application/pdf",
            IsNonVideoResource = true
        };
        var reports = new List<DownloadProgress>();

        await service.DownloadAsync(task, new RecordingProgress(reports));

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(root.Path("downloads", expectedFileName), task.OutputFilePath);
        Assert.Equal("course notes", File.ReadAllText(task.OutputFilePath));
        Assert.Equal(task.OutputFilePath, Assert.Single(task.OutputFilePaths));
        Assert.Equal(task.FileSize, new FileInfo(task.OutputFilePath).Length);
        Assert.Contains(reports, report => report.Total == "course notes".Length);
    }

    [Fact]
    public async Task DownloadAsync_RejectsHtmlResponseInsteadOfSavingPageAsDocument()
    {
        using var root = new TestDirectory();
        using var server = new ResourceServer("text/html", Encoding.UTF8.GetBytes("<html>login</html>"));
        var config = new ConfigService(root.Path("config"));
        var service = new HttpResourceDownloadService(config);
        var task = new DownloadTask
        {
            Url = server.Url,
            Title = "课程资料",
            OutputDirectory = root.Path("downloads"),
            ResourceExtension = "pdf",
            IsNonVideoResource = true
        };

        await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(task));

        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.Empty(Directory.Exists(task.OutputDirectory)
            ? Directory.GetFiles(task.OutputDirectory)
            : []);
    }

    private sealed class ResourceServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly byte[] _body;
        private readonly string _contentType;
        private readonly Task _serverTask;

        public ResourceServer(string contentType, byte[] body)
        {
            _contentType = contentType;
            _body = body;
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/course.pdf";
            _serverTask = Task.Run(ServeAsync);
        }

        public string Url { get; }

        private async Task ServeAsync()
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                    using var stream = client.GetStream();
                    var requestBuffer = new byte[2048];
                    var request = new StringBuilder();
                    while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(requestBuffer, _cancellation.Token);
                        if (read == 0)
                            return;
                        request.Append(Encoding.ASCII.GetString(requestBuffer, 0, read));
                    }

                    var headers = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n"
                        + $"Content-Type: {_contentType}\r\n"
                        + $"Content-Length: {_body.Length}\r\n"
                        + "Connection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _cancellation.Token);
                    await stream.WriteAsync(_body, _cancellation.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try
            {
                _serverTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
            }
            _cancellation.Dispose();
        }
    }

    private sealed class RecordingProgress(List<DownloadProgress> reports) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => reports.Add(value);
    }
}
