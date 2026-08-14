using System.IO;
using System.IO.Pipes;
using System.Text;

namespace EasyGet.Services;

/// <summary>
/// 保证 EasyGet 只运行一个进程：次实例通过命名管道把命令行交给首实例后退出。
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    public const string DefaultMutexName = @"Local\EasyGet.SingleInstance";
    public const string DefaultPipeName = "EasyGet.SingleInstance.Pipe";

    private const int DefaultNotifyTimeoutMilliseconds = 2000;
    private const int MaxMessageBytes = 64 * 1024;

    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly object _gate = new();

    private Mutex? _mutex;
    private NamedPipeServerStream? _server;
    private CancellationTokenSource? _listenCts;
    private Task? _listenTask;
    private bool _ownsPrimary;
    private bool _disposed;

    public SingleInstanceService()
        : this(DefaultMutexName, DefaultPipeName)
    {
    }

    internal SingleInstanceService(string mutexName, string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        _mutexName = mutexName;
        _pipeName = pipeName;
    }

    internal string MutexName => _mutexName;

    internal string PipeName => _pipeName;

    /// <summary>
    /// 尝试成为首实例。失败（已被占用或无法创建互斥量）时返回 false，调用方应通知后退出。
    /// </summary>
    public bool TryClaimPrimary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_ownsPrimary)
                return true;

            Mutex? mutex = null;
            try
            {
                // createdNew 判断的是内核对象是否新建，避免同进程同线程上 Mutex.WaitOne 可重入
                // 而误把次实例当成首实例。
                mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
                if (!createdNew)
                {
                    mutex.Dispose();
                    return false;
                }

                _mutex = mutex;
                _ownsPrimary = true;
                return true;
            }
            catch (Exception)
            {
                mutex?.Dispose();
                return false;
            }
        }
    }

    /// <summary>
    /// 启动命名管道监听。失败时返回 false，首实例仍应继续正常启动。
    /// 回调在后台线程触发：<c>null</c> 表示仅激活窗口。
    /// </summary>
    public bool TryStartListening(Action<string?> onUrlReceived)
    {
        ArgumentNullException.ThrowIfNull(onUrlReceived);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_listenTask is not null)
                return true;

            try
            {
                _listenCts = new CancellationTokenSource();
                _server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                _listenTask = ListenLoopAsync(onUrlReceived, _listenCts.Token);
                return true;
            }
            catch (Exception)
            {
                StopListener_NoLock();
                return false;
            }
        }
    }

    public bool TryNotifyPrimary(IEnumerable<string>? arguments)
        => TryNotifyPrimary(arguments, TimeSpan.FromMilliseconds(DefaultNotifyTimeoutMilliseconds));

    internal bool TryNotifyPrimary(IEnumerable<string>? arguments, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var payload = SerializePipeMessage(arguments);
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out);
            client.Connect(timeout);
            using var writer = new StreamWriter(
                client,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true
            };
            writer.Write(payload);
            writer.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string? ExtractUrlFromArguments(IEnumerable<string>? arguments)
    {
        if (arguments is null)
            return null;

        foreach (var argument in arguments)
        {
            var extracted = ShareUrlExtractor.Extract(argument);
            if (extracted is null)
                continue;

            if (Uri.TryCreate(extracted, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return extracted;
            }
        }

        return null;
    }

    internal static string SerializePipeMessage(IEnumerable<string>? arguments)
    {
        if (arguments is null)
            return string.Empty;

        var parts = arguments
            .Where(static argument => !string.IsNullOrWhiteSpace(argument))
            .Select(static argument => argument.Trim())
            .ToArray();
        return parts.Length == 0 ? string.Empty : string.Join('\n', parts);
    }

    internal static SingleInstanceActivation DeserializePipeMessage(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return SingleInstanceActivation.ActivateOnly;

        var arguments = payload.Split('\n', StringSplitOptions.None);
        return new SingleInstanceActivation(ExtractUrlFromArguments(arguments));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            StopListener_NoLock();

            if (_mutex is not null)
            {
                if (_ownsPrimary)
                {
                    try
                    {
                        _mutex.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                    }
                }

                _mutex.Dispose();
                _mutex = null;
                _ownsPrimary = false;
            }
        }
    }

    private async Task ListenLoopAsync(Action<string?> onUrlReceived, CancellationToken cancellationToken)
    {
        var server = _server;
        if (server is null)
            return;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var payload = await ReadPayloadAsync(server, cancellationToken).ConfigureAwait(false);
                var activation = DeserializePipeMessage(payload);

                try
                {
                    onUrlReceived(activation.Url);
                }
                catch (Exception)
                {
                    // Listener must survive callback failures.
                }

                if (server.IsConnected)
                    server.Disconnect();
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

    private static async Task<string> ReadPayloadAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxMessageBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await server.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
        }

        return total == 0
            ? string.Empty
            : Encoding.UTF8.GetString(buffer, 0, total);
    }

    private void StopListener_NoLock()
    {
        try
        {
            _listenCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _server?.Dispose();
        }
        catch (Exception)
        {
        }

        _server = null;

        var listenTask = _listenTask;
        _listenTask = null;
        if (listenTask is not null)
        {
            try
            {
                listenTask.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception)
            {
            }
        }

        _listenCts?.Dispose();
        _listenCts = null;
    }
}

internal readonly record struct SingleInstanceActivation(string? Url)
{
    public static SingleInstanceActivation ActivateOnly { get; } = new(null);

    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
}
