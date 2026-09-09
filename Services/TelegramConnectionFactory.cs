using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EasyGet.Services;

/// <summary>建立完整的代理隧道，再将 TCP 连接交给 Telegram 协议处理。</summary>
internal static class TelegramConnectionFactory
{
    internal static readonly TimeSpan DefaultConnectionTimeout = TimeSpan.FromSeconds(30);
    private const int MaximumHttpHeaderBytes = 32 * 1024;

    internal static Task<TcpClient> ConnectAsync(
        string host,
        int port,
        string? proxyAddress,
        CancellationToken cancellationToken = default)
        => ConnectAsync(host, port, proxyAddress, DefaultConnectionTimeout, cancellationToken);

    internal static async Task<TcpClient> ConnectAsync(
        string host,
        int port,
        string? proxyAddress,
        TimeSpan connectionTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (connectionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(connectionTimeout));
        cancellationToken.ThrowIfCancellationRequested();

        host = host.Trim('[', ']');
        if (host.Any(char.IsWhiteSpace) || host.Any(char.IsControl))
            throw new ArgumentException("Telegram 服务器地址无效。", nameof(host));

        Uri? proxy = null;
        var proxyPort = 0;
        if (!string.IsNullOrWhiteSpace(proxyAddress))
        {
            if (!Uri.TryCreate(proxyAddress.Trim(), UriKind.Absolute, out proxy)
                || string.IsNullOrEmpty(proxy.Host))
                throw new ArgumentException("代理地址无效，请使用 http:// 或 socks5:// 地址。", nameof(proxyAddress));

            if (proxy.Scheme is not ("socks5" or "socks5h" or "http"))
                throw new NotSupportedException($"暂不支持的代理协议: {proxy.Scheme}");
            if (proxy.Port == 0)
                throw new ArgumentException("代理端口必须在 1 至 65535 之间。", nameof(proxyAddress));

            proxyPort = proxy.Port > 0 ? proxy.Port : proxy.Scheme == "http" ? 80 : 1080;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connectionTimeout);
        // 双栈 socket 同时支持 IPv4 / IPv6 代理和 Telegram 服务器。
        var client = new TcpClient(Socket.OSSupportsIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);

        try
        {
            if (Socket.OSSupportsIPv6)
                client.Client.DualMode = true;
            client.NoDelay = true;
            await client.ConnectAsync(proxy?.DnsSafeHost ?? host, proxy is null ? port : proxyPort, timeout.Token)
                .ConfigureAwait(false);

            if (proxy is not null)
            {
                var stream = client.GetStream();
                if (proxy.Scheme == "http")
                    await ConnectHttpAsync(stream, host, port, proxy, timeout.Token).ConfigureAwait(false);
                else
                    await ConnectSocksAsync(stream, host, port, proxy, timeout.Token).ConfigureAwait(false);
            }

            timeout.Token.ThrowIfCancellationRequested();
            return client;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException("连接 Telegram 服务器或代理握手超时，请检查代理设置和网络连接。", ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static (string UserName, string Password)? GetCredentials(Uri proxy)
    {
        if (string.IsNullOrEmpty(proxy.UserInfo))
            return null;

        var separator = proxy.UserInfo.IndexOf(':');
        return (
            Uri.UnescapeDataString(separator < 0 ? proxy.UserInfo : proxy.UserInfo[..separator]),
            separator < 0 ? "" : Uri.UnescapeDataString(proxy.UserInfo[(separator + 1)..]));
    }

    private static async Task ConnectSocksAsync(NetworkStream stream, string host, int port, Uri proxy, CancellationToken token)
    {
        var credentials = GetCredentials(proxy);
        byte[] greeting = credentials.HasValue ? [5, 2, 0, 2] : [5, 1, 0];
        await stream.WriteAsync(greeting, token).ConfigureAwait(false);
        var selection = new byte[2];
        await stream.ReadExactlyAsync(selection, token).ConfigureAwait(false);
        if (selection[0] != 5 || selection[1] is not (0 or 2))
            throw new IOException("SOCKS5 代理未接受支持的认证方式。请检查代理配置。");

        if (selection[1] == 2)
        {
            if (!credentials.HasValue)
                throw new IOException("SOCKS5 代理需要用户名和密码，请在代理地址中配置认证信息。");

            var userName = Encoding.UTF8.GetBytes(credentials.Value.UserName);
            var password = Encoding.UTF8.GetBytes(credentials.Value.Password);
            if (userName.Length is < 1 or > 255 || password.Length is < 1 or > 255)
                throw new ArgumentException("SOCKS5 代理用户名和密码必须各为 1 至 255 字节。");

            byte[] authentication = [1, (byte)userName.Length, .. userName, (byte)password.Length, .. password];
            await stream.WriteAsync(authentication, token).ConfigureAwait(false);
            var authenticationReply = new byte[2];
            await stream.ReadExactlyAsync(authenticationReply, token).ConfigureAwait(false);
            if (authenticationReply[0] != 1 || authenticationReply[1] != 0)
                throw new IOException("SOCKS5 代理用户名或密码认证失败。");
        }

        byte[] address;
        if (IPAddress.TryParse(host, out var ip))
        {
            address = [(byte)(ip.AddressFamily == AddressFamily.InterNetwork ? 1 : 4), .. ip.GetAddressBytes()];
        }
        else
        {
            var domain = Encoding.ASCII.GetBytes(new IdnMapping().GetAscii(host));
            if (domain.Length is < 1 or > 255)
                throw new ArgumentException("SOCKS5 目标域名长度无效。", nameof(host));
            address = [3, (byte)domain.Length, .. domain];
        }

        byte[] request = [5, 1, 0, .. address, (byte)(port >> 8), (byte)port];
        await stream.WriteAsync(request, token).ConfigureAwait(false);
        var response = new byte[4];
        await stream.ReadExactlyAsync(response, token).ConfigureAwait(false);
        if (response[0] != 5 || response[2] != 0)
            throw new IOException("SOCKS5 代理返回了无效的连接响应。");
        if (response[1] != 0)
            throw new IOException($"SOCKS5 代理连接 Telegram 失败，返回码: {response[1]}。");

        var addressLength = response[3] switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadByteAsync(stream, token).ConfigureAwait(false),
            _ => throw new IOException("SOCKS5 代理返回了不支持的地址类型。")
        };
        if (addressLength == 0)
            throw new IOException("SOCKS5 代理返回了空的绑定地址。");

        // 必须精确读完 BND.ADDR 和 BND.PORT；既不能遗漏，也不能读走隧道首包。
        await stream.ReadExactlyAsync(new byte[addressLength + 2], token).ConfigureAwait(false);
    }

    private static async Task<byte> ReadByteAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[1];
        await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
        return buffer[0];
    }

    private static async Task ConnectHttpAsync(NetworkStream stream, string host, int port, Uri proxy, CancellationToken token)
    {
        var authority = host.Contains(':') ? $"[{host}]:{port}" : $"{new IdnMapping().GetAscii(host)}:{port}";
        var request = new StringBuilder($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n");
        if (GetCredentials(proxy) is { } credentials)
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.UserName}:{credentials.Password}"));
            request.Append("Proxy-Authorization: Basic ").Append(encoded).Append("\r\n");
        }
        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), token).ConfigureAwait(false);

        var response = new List<byte>();
        var buffer = new byte[1];
        // NetworkStream 没有可退回的缓冲区；只读取头部，保留随响应到达的隧道数据。
        for (var totalBytes = 0; totalBytes < MaximumHttpHeaderBytes; totalBytes++)
        {
            await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
            response.Add(buffer[0]);
            var count = response.Count;
            if (count < 4 || response[count - 4] != '\r' || response[count - 3] != '\n'
                || response[count - 2] != '\r' || response[count - 1] != '\n')
                continue;

            var header = Encoding.ASCII.GetString(response.ToArray());
            var firstLine = header[..header.IndexOf("\r\n", StringComparison.Ordinal)];
            var fields = firstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[0] is not ("HTTP/1.0" or "HTTP/1.1")
                || fields[1].Length != 3
                || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status))
                throw new IOException("HTTP 代理返回了无效的状态行。");

            if (status is >= 200 and < 300)
                return;
            if (status is >= 100 and < 200 && status != 101)
            {
                response.Clear();
                continue;
            }
            if (status == 407)
                throw new IOException("HTTP 代理认证失败，请检查代理用户名和密码。");
            throw new IOException($"HTTP 代理建立 Telegram 隧道失败，状态码: {status}。");
        }

        throw new IOException("HTTP 代理响应头过大，无法建立 Telegram 隧道。");
    }
}
