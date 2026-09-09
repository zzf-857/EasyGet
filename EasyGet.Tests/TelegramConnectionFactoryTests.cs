using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class TelegramConnectionFactoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DirectConnection_PreservesServerData(string? proxy)
    {
        await WithServerAsync(
            async (stream, token) => await stream.WriteAsync(new byte[] { 11, 22, 33 }, token),
            async (port, token) =>
            {
                using var client = await TelegramConnectionFactory.ConnectAsync("127.0.0.1", port, proxy, token);
                Assert.Equal(new byte[] { 11, 22, 33 }, await ReadAsync(client.GetStream(), 3, token));
            });
    }

    [Theory]
    [InlineData("socks5", 1)]
    [InlineData("socks5h", 3)]
    [InlineData("socks5", 4)]
    public async Task SocksConnection_ReadsFragmentedHandshakeAndPreservesTunnelData(string scheme, int addressType)
    {
        byte[] boundAddress = addressType switch
        {
            1 => [127, 0, 0, 1],
            3 => [4, (byte)'h', (byte)'o', (byte)'s', (byte)'t'],
            _ => IPAddress.IPv6Loopback.GetAddressBytes()
        };
        await WithServerAsync(
            async (stream, token) =>
            {
                Assert.Equal(new byte[] { 5, 1, 0 }, await ReadAsync(stream, 3, token));
                await WriteFragmentsAsync(stream, [5, 0], token);
                Assert.Equal(new byte[] { 5, 1, 0, 1, 149, 154, 167, 50, 1, 187 }, await ReadAsync(stream, 10, token));
                await WriteFragmentsAsync(stream, [5, 0, 0, (byte)addressType, .. boundAddress], token);
                // 代理响应尾部与隧道首包一次写入，不能被握手逻辑吞掉。
                await stream.WriteAsync(new byte[] { 0, 80, 99, 100, 101 }, token);
            },
            async (port, token) =>
            {
                using var client = await TelegramConnectionFactory.ConnectAsync("149.154.167.50", 443, $"{scheme}://127.0.0.1:{port}", token);
                Assert.Equal(new byte[] { 99, 100, 101 }, await ReadAsync(client.GetStream(), 3, token));
            });
    }

    [Fact]
    public async Task SocksConnection_AuthenticatesWithDecodedCredentialsAndResolvesDomainAtProxy()
    {
        await WithServerAsync(
            async (stream, token) =>
            {
                Assert.Equal(new byte[] { 5, 2, 0, 2 }, await ReadAsync(stream, 4, token));
                await WriteFragmentsAsync(stream, [5, 2], token);
                var expectedAuth = new byte[] { 1, 4, (byte)'u', (byte)'@', (byte)'s', (byte)'r', 3, (byte)'p', (byte)':', (byte)'w' };
                Assert.Equal(expectedAuth, await ReadAsync(stream, expectedAuth.Length, token));
                await WriteFragmentsAsync(stream, [1, 0], token);
                byte[] expectedRequest = [5, 1, 0, 3, 12, .. Encoding.ASCII.GetBytes("test.invalid"), 1, 187];
                Assert.Equal(expectedRequest, await ReadAsync(stream, expectedRequest.Length, token));
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 80, 42 }, token);
            },
            async (port, token) =>
            {
                using var client = await TelegramConnectionFactory.ConnectAsync("test.invalid", 443, $"socks5h://u%40sr:p%3Aw@127.0.0.1:{port}", token);
                Assert.Equal(new byte[] { 42 }, await ReadAsync(client.GetStream(), 1, token));
            });
    }

    [Fact]
    public async Task SocksConnection_EncodesIpv6Target()
    {
        await WithServerAsync(
            async (stream, token) =>
            {
                await ReadAsync(stream, 3, token);
                await stream.WriteAsync(new byte[] { 5, 0 }, token);
                byte[] request = [5, 1, 0, 4, .. IPAddress.IPv6Loopback.GetAddressBytes(), 1, 187];
                Assert.Equal(request, await ReadAsync(stream, request.Length, token));
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 80 }, token);
            },
            async (port, token) =>
            {
                using var client = await TelegramConnectionFactory.ConnectAsync("::1", 443, $"socks5://127.0.0.1:{port}", token);
                Assert.True(client.Connected);
            });
    }

    [Theory]
    [InlineData(5, 255)]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    [InlineData(5, 2)]
    public async Task SocksConnection_RejectsInvalidOrUnsupportedAuthenticationAndClosesSocket(int version, int method)
    {
        await WithServerAsync(
            async (stream, token) =>
            {
                await ReadAsync(stream, 3, token);
                await stream.WriteAsync(new byte[] { (byte)version, (byte)method }, token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
            },
            async (port, token) =>
            {
                await Assert.ThrowsAsync<IOException>(() => TelegramConnectionFactory.ConnectAsync("test.invalid", 443, $"socks5://127.0.0.1:{port}", token));
            });
    }

    [Fact]
    public async Task SocksConnection_ReportsAuthenticationFailureWithoutLeakingCredentials()
    {
        await WithServerAsync(
            async (stream, token) =>
            {
                await ReadAsync(stream, 4, token);
                await stream.WriteAsync(new byte[] { 5, 2 }, token);
                await ReadAsync(stream, 3 + "username".Length + "secret".Length, token);
                await stream.WriteAsync(new byte[] { 1, 1 }, token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
            },
            async (port, token) =>
            {
                var error = await Assert.ThrowsAsync<IOException>(() => TelegramConnectionFactory.ConnectAsync("test.invalid", 443, $"socks5://username:secret@127.0.0.1:{port}", token));
                Assert.Contains("认证失败", error.Message);
                Assert.DoesNotContain("secret", error.Message);
            });
    }

    [Fact]
    public async Task SocksConnection_RejectsIncompleteReply()
    {
        await WithServerAsync(
            async (stream, token) =>
            {
                await ReadAsync(stream, 3, token);
                await stream.WriteAsync(new byte[] { 5 }, token);
            },
            async (port, token) =>
            {
                await Assert.ThrowsAsync<EndOfStreamException>(() => TelegramConnectionFactory.ConnectAsync("test.invalid", 443, $"socks5://127.0.0.1:{port}", token));
            });
    }

    [Fact]
    public async Task SocksConnection_ReportsDestinationFailure()
    {
        await WithServerAsync(
            async (stream, token) =>
            {
                await ReadAsync(stream, 3, token);
                await stream.WriteAsync(new byte[] { 5, 0 }, token);
                await ReadAsync(stream, 10, token);
                await stream.WriteAsync(new byte[] { 5, 5, 0, 1 }, token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
            },
            async (port, token) =>
            {
                var error = await Assert.ThrowsAsync<IOException>(() => TelegramConnectionFactory.ConnectAsync("127.0.0.1", 443, $"socks5://127.0.0.1:{port}", token));
                Assert.Contains("返回码: 5", error.Message);
            });
    }

    [Fact]
    public async Task HttpConnection_ParsesStatusCodeAndCompleteFragmentedHeadersWithoutConsumingTunnelData()
    {
        await WithServerAsync(
            async (stream, token) =>
            {
                Assert.Equal("CONNECT test.invalid:443 HTTP/1.1\r\nHost: test.invalid:443\r\n\r\n", await ReadHeaderAsync(stream, token));
                await WriteFragmentsAsync(stream, Encoding.ASCII.GetBytes("HTTP/1.1 200 Tunnel Ready\r\n"), token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("X-Proxy: " + new string('x', 4096) + "\r\n"), token);
                await Task.Delay(10, token);
                byte[] tail = [.. Encoding.ASCII.GetBytes("\r\n"), 42, 43, 44];
                await stream.WriteAsync(tail, token);
            },
            async (port, token) =>
            {
                using var client = await TelegramConnectionFactory.ConnectAsync("test.invalid", 443, $"http://127.0.0.1:{port}", token);
                Assert.Equal(new byte[] { 42, 43, 44 }, await ReadAsync(client.GetStream(), 3, token));
            });
    }

    [Fact]
    public async Task HttpConnection_SendsBasicAuthenticationAndBracketsIpv6Authority()
    {
        await WithServerAsync(
            async (stream, token) =>
            {
                var expected = "CONNECT [::1]:443 HTTP/1.1\r\nHost: [::1]:443\r\nProxy-Authorization: Basic "
                    + Convert.ToBase64String(Encoding.UTF8.GetBytes("u@sr:p:w")) + "\r\n\r\n";
                Assert.Equal(expected, await ReadHeaderAsync(stream, token));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.0 200 arbitrary text\r\n\r\n"), token);
            },
            async (port, token) =>
            {
                using var client = await TelegramConnectionFactory.ConnectAsync("::1", 443, $"http://u%40sr:p%3Aw@127.0.0.1:{port}", token);
                Assert.True(client.Connected);
            });
    }

    [Fact]
    public async Task HttpConnection_WaitsForFinalSuccessAfterInformationalResponse()
    {
        await WithServerAsync(
            async (stream, token) =>
            {
                await ReadHeaderAsync(stream, token);
                byte[] response = [.. Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 200 Ready\r\n\r\n"), 66];
                await stream.WriteAsync(response, token);
            },
            async (port, token) =>
            {
                using var client = await TelegramConnectionFactory.ConnectAsync("test.invalid", 443, $"http://127.0.0.1:{port}", token);
                Assert.Equal(new byte[] { 66 }, await ReadAsync(client.GetStream(), 1, token));
            });
    }

    [Theory]
    [InlineData(407, "认证失败")]
    [InlineData(502, "状态码: 502")]
    public async Task HttpConnection_ReportsStatusFailureAndClosesSocket(int status, string message)
    {
        await WithServerAsync(
            async (stream, token) =>
            {
                await ReadHeaderAsync(stream, token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} error\r\nX-Note: 200 OK\r\n\r\n"), token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
            },
            async (port, token) =>
            {
                var error = await Assert.ThrowsAsync<IOException>(() => TelegramConnectionFactory.ConnectAsync("test.invalid", 443, $"http://127.0.0.1:{port}", token));
                Assert.Contains(message, error.Message);
            });
    }

    [Fact]
    public async Task HttpConnection_RejectsUnboundedHeaders()
    {
        await WithServerAsync(
            async (stream, token) =>
            {
                await ReadHeaderAsync(stream, token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(new string('x', 32768)), token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
            },
            async (port, token) =>
            {
                var error = await Assert.ThrowsAsync<IOException>(() => TelegramConnectionFactory.ConnectAsync("test.invalid", 443, $"http://127.0.0.1:{port}", token));
                Assert.Contains("响应头过大", error.Message);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connection_StopsStalledHandshakeAndClosesSocket(bool callerCancels)
    {
        await WithServerAsync(
            async (stream, token) =>
            {
                await ReadAsync(stream, 3, token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
            },
            async (port, token) =>
            {
                using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (callerCancels)
                    caller.CancelAfter(TimeSpan.FromMilliseconds(150));
                var connect = TelegramConnectionFactory.ConnectAsync("test.invalid", 443, $"socks5://127.0.0.1:{port}",
                    callerCancels ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(150), caller.Token);
                if (callerCancels)
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
                else
                    await Assert.ThrowsAsync<TimeoutException>(() => connect);
            });
    }

    [Fact]
    public async Task Connection_RejectsAlreadyCancelledRequest()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TelegramConnectionFactory.ConnectAsync("test.invalid", 443, null, new CancellationToken(true)));
    }

    [Fact]
    public async Task Connection_RejectsUnsupportedProxyBeforeConnecting()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            TelegramConnectionFactory.ConnectAsync("test.invalid", 443, "https://127.0.0.1:1"));
    }

    private static async Task WithServerAsync(
        Func<NetworkStream, CancellationToken, Task> server,
        Func<int, CancellationToken, Task> client)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = ServeAsync();
        try
        {
            await Task.WhenAll(serving, client(port, deadline.Token));
        }
        finally
        {
            deadline.Cancel();
            listener.Stop();
        }

        async Task ServeAsync()
        {
            using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
            accepted.NoDelay = true;
            await server(accepted.GetStream(), deadline.Token);
        }
    }

    private static async Task<byte[]> ReadAsync(NetworkStream stream, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, token);
        return buffer;
    }

    private static async Task WriteFragmentsAsync(NetworkStream stream, byte[] bytes, CancellationToken token)
    {
        foreach (var value in bytes)
        {
            await stream.WriteAsync(new byte[] { value }, token);
            await Task.Delay(5, token);
        }
    }

    private static async Task<string> ReadHeaderAsync(NetworkStream stream, CancellationToken token)
    {
        var result = new StringBuilder();
        while (result.Length < 32768)
        {
            var value = await ReadAsync(stream, 1, token);
            result.Append((char)value[0]);
            if (result.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                return result.ToString();
        }
        throw new InvalidDataException("Request header exceeded the test limit.");
    }
}
