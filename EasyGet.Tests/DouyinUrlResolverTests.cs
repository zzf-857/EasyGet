using System.Net;
using System.Net.Http;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public sealed class DouyinUrlResolverTests
{
    private const string VideoId = "7320000000000000000";
    private const string CanonicalUrl = "https://www.douyin.com/video/" + VideoId;

    [Theory]
    [InlineData("https://www.douyin.com/video/7320000000000000000")]
    [InlineData("https://www.iesdouyin.com/share/video/7320000000000000000/?region=CN")]
    [InlineData("https://www.douyin.com/?modal_id=7320000000000000000")]
    public async Task ResolveAsync_KnownVideoIdsDoNotRequireNetwork(string url)
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("No request expected"));
        using var client = new HttpClient(handler);
        var resolver = new DouyinUrlResolver(client);

        Assert.Equal(CanonicalUrl, await resolver.ResolveAsync(url));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("https://example.com/video")]
    [InlineData("https://www.douyin.com/user/MS4wLjABAAAA")]
    public async Task ResolveAsync_NonShortLinksWithoutVideoIdsAreUnchanged(string url)
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("No request expected"));
        using var client = new HttpClient(handler);

        Assert.Equal(url, await new DouyinUrlResolver(client).ResolveAsync(url));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ResolveAsync_FollowsRelativeRedirectAndStopsBeforeVideoLandingPage()
    {
        using var handler = new FakeHandler((request, _) => Task.FromResult(Redirect(
            request.RequestUri!.AbsolutePath == "/first/"
                ? "/second/"
                : $"https://www.iesdouyin.com/share/video/{VideoId}/?region=CN")));
        using var client = new HttpClient(handler);

        var result = await new DouyinUrlResolver(client).ResolveAsync("https://v.douyin.com/first/");

        Assert.Equal(CanonicalUrl, result);
        Assert.Equal(["https://v.douyin.com/first/", "https://v.douyin.com/second/"], handler.Requests);
    }

    [Theory]
    [InlineData("https://example.com/video/7320000000000000000")]
    [InlineData("https://www.douyin.com.evil.example/video/7320000000000000000")]
    [InlineData("https://user@www.douyin.com/video/7320000000000000000")]
    [InlineData("https://www.douyin.com:8443/video/7320000000000000000")]
    [InlineData("ftp://www.douyin.com/video/7320000000000000000")]
    public async Task ResolveAsync_RejectsUnsafeRedirectBeforeSendingIt(string location)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Redirect(location)));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DouyinUrlResolver(client).ResolveAsync("https://v.douyin.com/first/"));

        Assert.Contains("不支持的地址", error.Message);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("passport/web/login")]
    [InlineData("l%6fgin")]
    [InlineData("verify_center")]
    public async Task ResolveAsync_RejectsLoginRedirectEvenWhenItContainsAModalId(string challengePath)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Redirect(
            $"https://www.douyin.com/{challengePath}/?modal_id={VideoId}")));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DouyinUrlResolver(client).ResolveAsync("https://v.douyin.com/first/"));

        Assert.Contains("登录或验证", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ResolveAsync_DetectsRedirectLoop()
    {
        using var handler = new FakeHandler((request, _) => Task.FromResult(Redirect(
            request.RequestUri!.AbsolutePath == "/first/" ? "/second/" : "/first/#fragment")));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DouyinUrlResolver(client).ResolveAsync("https://v.douyin.com/first/"));

        Assert.Contains("循环跳转", error.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ResolveAsync_BoundsRedirectChainToFiveRequests()
    {
        var sequence = 0;
        using var handler = new FakeHandler((_, _) => Task.FromResult(Redirect($"/hop{++sequence}/")));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DouyinUrlResolver(client).ResolveAsync("https://v.douyin.com/first/"));

        Assert.Contains("跳转次数过多", error.Message);
        Assert.Equal(5, handler.Requests.Count);
    }

    [Fact]
    public async Task ResolveAsync_ReportsMissingVideoWithoutReadingResponseBody()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnreadableContent()
        }));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DouyinUrlResolver(client).ResolveAsync("https://v.douyin.com/first/"));

        Assert.Contains("未找到作品地址", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ResolveAsync_CallerCancellationInterruptsPendingHeaders()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHandler(async (_, ct) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Redirect(CanonicalUrl);
        });
        using var client = new HttpClient(handler);
        using var source = new CancellationTokenSource();
        var pending = new DouyinUrlResolver(client).ResolveAsync("https://v.douyin.com/first/", source.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task ResolveAsync_TotalTimeoutProducesActionableError()
    {
        using var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Redirect(CanonicalUrl);
        });
        using var client = new HttpClient(handler);
        var resolver = new DouyinUrlResolver(client, TimeSpan.FromMilliseconds(30));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => resolver.ResolveAsync("https://v.douyin.com/first/"));

        Assert.Contains("解析超时", error.Message);
    }

    [Fact]
    public async Task ResolveAsync_ReusesSuccessfulExpansionWithoutDisposingInjectedClient()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Redirect(CanonicalUrl)));
        using var client = new HttpClient(handler);
        var resolver = new DouyinUrlResolver(client);

        Assert.Equal(CanonicalUrl, await resolver.ResolveAsync("https://v.douyin.com/first/"));
        Assert.Equal(CanonicalUrl, await resolver.ResolveAsync("https://v.douyin.com/first/"));
        Assert.Single(handler.Requests);
        Assert.False(handler.IsDisposed);
    }

    [Fact]
    public async Task ResolveAsync_CacheEvictsOldestEntryAfter128SuccessfulUrls()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Redirect(CanonicalUrl)));
        using var client = new HttpClient(handler);
        var resolver = new DouyinUrlResolver(client);
        for (var index = 0; index < 129; index++)
            await resolver.ResolveAsync($"https://v.douyin.com/key{index}/");

        await resolver.ResolveAsync("https://v.douyin.com/key128/");
        Assert.Equal(129, handler.Requests.Count);
        await resolver.ResolveAsync("https://v.douyin.com/key0/");
        Assert.Equal(130, handler.Requests.Count);
    }

    [Fact]
    public async Task ResolveAsync_FailuresAreNotCached()
    {
        var attempt = 0;
        using var handler = new FakeHandler((_, _) => Task.FromResult(++attempt == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Redirect(CanonicalUrl)));
        using var client = new HttpClient(handler);
        var resolver = new DouyinUrlResolver(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync("https://v.douyin.com/first/"));
        Assert.Equal(CanonicalUrl, await resolver.ResolveAsync("https://v.douyin.com/first/"));
        Assert.Equal(2, handler.Requests.Count);
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new UnreadableContent() };
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.False(request.Headers.Contains("Cookie"));
            return send(request, ct);
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class UnreadableContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new InvalidOperationException("Response bodies must not be loaded while resolving share redirects.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
