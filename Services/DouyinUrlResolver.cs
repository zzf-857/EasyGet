using System.Net;
using System.Net.Http;

namespace EasyGet.Services;

/// <summary>
/// Expands Douyin share redirects to video URLs without loading landing-page bodies.
/// </summary>
internal sealed class DouyinUrlResolver
{
    private const int MaximumRedirects = 5;
    private const int MaximumCachedUrls = 128;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private readonly ConfigService? _configService;
    private readonly HttpClient? _injectedClient;
    private readonly TimeSpan _timeout;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CachedUrl> _cache = new(StringComparer.Ordinal);
    private ProxySettings? _cachedProxy;

    public DouyinUrlResolver(ConfigService configService)
    {
        _configService = configService;
        _timeout = TimeSpan.FromSeconds(20);
    }

    internal DouyinUrlResolver(HttpClient client, TimeSpan? timeout = null)
    {
        _injectedClient = client;
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    public async Task<string> ResolveAsync(string url, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (DouyinUrlParser.TryGetCanonicalVideoUrl(url, out var canonical))
            return canonical;
        if (!DouyinUrlParser.Parse(url).RequiresExpansion)
            return url;

        var current = CreateInitialUri(url);
        ValidateRedirectTarget(current);
        var cacheKey = current.GetLeftPart(UriPartial.Query);
        var config = _configService?.Config;
        var proxy = new ProxySettings(config?.UseProxy ?? false, config?.ProxyAddress?.Trim() ?? "");
        lock (_cacheGate)
        {
            if (_cachedProxy != proxy)
            {
                _cache.Clear();
                _cachedProxy = proxy;
            }
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                if (cached.ExpiresAtUtc > DateTime.UtcNow)
                    return cached.Url;
                _cache.Remove(cacheKey);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            using var ownedClient = _injectedClient is null ? CreateClient(proxy) : null;
            canonical = await ExpandAsync(_injectedClient ?? ownedClient!, current, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("抖音分享链接解析超时，请检查网络或代理后重试。", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("抖音分享链接解析失败：网络请求失败，请检查网络或代理后重试。", ex);
        }

        lock (_cacheGate)
        {
            if (_cachedProxy == proxy)
            {
                if (_cache.Count >= MaximumCachedUrls)
                    _cache.Remove(_cache.MinBy(entry => entry.Value.ExpiresAtUtc).Key);
                _cache[cacheKey] = new CachedUrl(canonical, DateTime.UtcNow.Add(CacheLifetime));
            }
        }
        return canonical;
    }

    private static async Task<string> ExpandAsync(HttpClient client, Uri current, CancellationToken ct)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var redirectCount = 0; redirectCount <= MaximumRedirects; redirectCount++)
        {
            ct.ThrowIfCancellationRequested();
            ValidateRedirectTarget(current);
            if (DouyinUrlParser.TryGetCanonicalVideoUrl(current.AbsoluteUri, out var canonical))
                return canonical;
            if (!visited.Add(current.GetLeftPart(UriPartial.Query)))
                throw ResolutionError("链接发生循环跳转，请重新复制作品分享链接。");
            if (redirectCount == MaximumRedirects)
                throw ResolutionError("链接跳转次数过多，请在浏览器中打开后复制作品链接。");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw ResolutionError("分享链接要求登录或验证，请在浏览器中打开后复制作品链接。");
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently
                or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                || response.Headers.Location is not { } location)
            {
                throw ResolutionError("未找到作品地址，链接可能已失效或需要浏览器验证，请重新复制作品链接。");
            }

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }

        throw ResolutionError("链接跳转次数过多。");
    }

    private static Uri CreateInitialUri(string url)
    {
        var value = url.Trim();
        if (value.StartsWith("//", StringComparison.Ordinal))
            value = "https:" + value;
        else if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            value = "https://" + value;
        return new Uri(value, UriKind.Absolute);
    }

    private static void ValidateRedirectTarget(Uri target)
    {
        if (target.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(target.UserInfo)
            || !target.IsDefaultPort
            || !IsDouyinHost(target.Host))
        {
            throw ResolutionError("链接跳转到了不支持的地址，请重新复制抖音作品分享链接。");
        }

        var segments = target.Host.Split('.').Concat(target.AbsolutePath.Split('/'));
        if (segments.Any(segment => Uri.UnescapeDataString(segment)
                .Replace("-", "", StringComparison.Ordinal)
                .Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() is
                "login" or "passport" or "sso" or "captcha" or "verify"
                or "verification" or "verifycenter" or "challenge"))
        {
            throw ResolutionError("分享链接要求登录或验证，请在浏览器中打开后复制作品链接。");
        }
    }

    private static bool IsDouyinHost(string host)
        => host.Equals("douyin.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("iesdouyin.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".iesdouyin.com", StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateClient(ProxySettings proxy)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = proxy.Enabled && !string.IsNullOrWhiteSpace(proxy.Address)
        };
        try
        {
            if (handler.UseProxy)
                handler.Proxy = new WebProxy(proxy.Address);
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    private static InvalidOperationException ResolutionError(string reason)
        => new($"抖音分享链接解析失败：{reason}");

    private sealed record ProxySettings(bool Enabled, string Address);
    private sealed record CachedUrl(string Url, DateTime ExpiresAtUtc);
}
