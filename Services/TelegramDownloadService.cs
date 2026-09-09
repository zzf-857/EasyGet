using Message = TL.Message;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyGet.Models;
using TL;

namespace EasyGet.Services;

public class TelegramDownloadService : IDisposable
{
    private readonly ConfigService _configService;
    private WTelegram.Client? _client;
    private ITelegramDownloadClient? _downloadClient;
    private string? _loginRequirement; // 缓存登录步骤，如 "verification_code" 或 "password"
    private readonly SemaphoreSlim _clientSemaphore = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    // Keep server cooldowns when a cancelled connection is recreated or the next
    // queued video starts. Reconnecting must not reset a known Telegram wait.
    private readonly TelegramDownloadThrottle _downloadThrottle = new();
    private int _disposed;

    public TelegramDownloadService(ConfigService configService)
    {
        _configService = configService;
    }

    internal TelegramDownloadService(ConfigService configService, ITelegramDownloadClient client)
        : this(configService)
    {
        _downloadClient = client;
    }

    public static bool IsTelegramUrl(string? url) => ParseTelegramLink(url) is not null;

    public static (string chatTarget, int startId, int? endId)? ParseTelegramLink(string? link)
        => TelegramLinkParser.Parse(link);

    /// <summary>
    /// WTelegram 对话字典以频道原始正数 ID 为键；解析得到的 <c>-100{channelId}</c> 仅作展示标记。
    /// </summary>
    internal static long GetPrivateChatLookupId(string chatTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatTarget);

        const string markedPrefix = "-100";
        if (chatTarget.StartsWith(markedPrefix, StringComparison.Ordinal)
            && TryParsePositiveLong(chatTarget[markedPrefix.Length..], out var channelId))
        {
            return channelId;
        }

        if (TryParsePositiveLong(chatTarget, out channelId))
            return channelId;

        throw new ArgumentException("不是有效的 Telegram 私有频道标识。", nameof(chatTarget));
    }

    private static bool TryParsePositiveLong(string value, out long result)
    {
        result = 0;
        return ContainsOnlyAsciiDigits(value)
            && long.TryParse(value, out result)
            && result > 0;
    }

    private static bool ContainsOnlyAsciiDigits(string value)
    {
        if (value.Length == 0)
            return false;

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
                return false;
        }

        return true;
    }

    /// <summary>
    /// 初始化并连接 Telegram，检查登录状态
    /// </summary>
    public async Task<string?> CheckLoginStatusAsync()
    {
        await _clientSemaphore.WaitAsync(_shutdown.Token);
        try
        {
            if (_client == null && _downloadClient == null)
            {
                if (!HasCredentials() || !File.Exists(GetSessionPath()))
                    return "phone_number";
                InitClient();
            }

            if (_downloadClient!.IsLoggedIn)
                return null;

            if (_loginRequirement is not null)
                return _loginRequirement;
            if (!_downloadClient.HasSavedSession)
                return "phone_number";
            await _downloadClient.RestoreSessionAsync(_shutdown.Token);
            return _downloadClient.IsLoggedIn ? null : "phone_number";
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            await AbortDownloadClientAsync();
            throw;
        }
        finally
        {
            ClearAbortedClient();
            _clientSemaphore.Release();
        }
    }

    /// <summary>
    /// 发送验证码（用于登录的第一步）
    /// </summary>
    public async Task<string?> SendCodeAsync(string phone, string apiId, string apiHash)
    {
        await _clientSemaphore.WaitAsync(_shutdown.Token);
        try
        {
            // 保存凭证
            _configService.Config.TgPhoneNumber = phone;
            _configService.Config.TgApiId = apiId;
            _configService.Config.TgApiHash = apiHash;
            await _configService.SaveAsync();

            if (_client != null)
            {
                _client.Dispose();
                _client = null;
            }

            _downloadClient = null;
            _loginRequirement = null;
            InitClient();
            var loginResult = await _client!.Login(phone);
            _loginRequirement = loginResult;
            return loginResult;
        }
        finally
        {
            _clientSemaphore.Release();
        }
    }

    /// <summary>
    /// 提交接收到的验证码
    /// </summary>
    public async Task<string?> SubmitCodeAsync(string code)
    {
        await _clientSemaphore.WaitAsync(_shutdown.Token);
        try
        {
            if (_client == null)
                throw new InvalidOperationException("客户端未初始化，请先发送验证码");

            var loginResult = await _client.Login(code);
            _loginRequirement = loginResult;
            return loginResult;
        }
        finally
        {
            _clientSemaphore.Release();
        }
    }

    /// <summary>
    /// 提交两步验证密码
    /// </summary>
    public async Task<string?> SubmitPasswordAsync(string password)
    {
        await _clientSemaphore.WaitAsync(_shutdown.Token);
        try
        {
            if (_client == null)
                throw new InvalidOperationException("客户端未初始化");

            var loginResult = await _client.Login(password);
            _loginRequirement = loginResult;
            return loginResult;
        }
        finally
        {
            _clientSemaphore.Release();
        }
    }

    /// <summary>
    /// 退出登录，并清理本地缓存的 session 文件
    /// </summary>
    public async Task LogOutAsync()
    {
        await _clientSemaphore.WaitAsync(_shutdown.Token);
        try
        {
            if (_client != null)
            {
                await _client.Auth_LogOut();
                _client.Dispose();
                _client = null;
            }

            _downloadClient = null;
            _loginRequirement = null;
            var sessionPath = GetSessionPath();
            if (File.Exists(sessionPath))
            {
                File.Delete(sessionPath);
            }
        }
        finally
        {
            _clientSemaphore.Release();
        }
    }

    private string GetSessionPath()
    {
        return Path.Combine(ConfigService.GetToolsDirectory(), "telegram.session");
    }

    private bool HasCredentials() => !string.IsNullOrWhiteSpace(_configService.Config.TgApiId)
        && !string.IsNullOrWhiteSpace(_configService.Config.TgApiHash);

    private void InitClient()
    {
        var sessionPath = GetSessionPath();
        var apiId = _configService.Config.TgApiId;
        var apiHash = _configService.Config.TgApiHash;

        if (string.IsNullOrWhiteSpace(apiId) || string.IsNullOrWhiteSpace(apiHash))
        {
            throw new InvalidOperationException("未配置 Telegram API ID 或 API Hash，请先在设置中配置。");
        }

        _client = new WTelegram.Client(configKey =>
        {
            return configKey switch
            {
                "api_id" => apiId,
                "api_hash" => apiHash,
                "phone_number" => _configService.Config.TgPhoneNumber,
                "session_pathname" => sessionPath,
                _ => null
            };
        });

        _client.TcpHandler = (host, port) => TelegramConnectionFactory.ConnectAsync(
            host, port, _configService.Config.UseProxy ? _configService.Config.ProxyAddress : null);
        _client.FloodRetryThreshold = 0;
        _downloadClient = new TelegramDownloadClient(_client, _downloadThrottle);

        // 屏蔽 WTelegramClient 的内部日志以防刷屏
        WTelegram.Helpers.Log = (level, message) => Debug.WriteLine($"[WTelegram] {level}: {message}");
    }

    /// <summary>
    /// Telegram 下载接口
    /// </summary>
    public async Task DownloadAsync(
        DownloadTask task,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null,
        CancellationToken ct = default)
    {
        var gateHeld = false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        try
        {
            ct.ThrowIfCancellationRequested();
            ct = lifetime.Token;
            ct.ThrowIfCancellationRequested();
            var parsed = ParseTelegramLink(task.Url)
                ?? throw new ArgumentException("无法识别的 Telegram 消息链接，请复制具体消息链接。");
            var (chatTarget, startId, endId) = parsed;

            // Do not allow login/logout to dispose a client while it is downloading.
            await _clientSemaphore.WaitAsync(ct);
            gateHeld = true;
            task.Status = DownloadStatus.Downloading;
            task.ErrorMessage = "";
            task.Progress = 0;
            if (_downloadClient is null && HasCredentials() && File.Exists(GetSessionPath()))
                InitClient();
            var client = _downloadClient
                ?? throw new InvalidOperationException("请先在设置中完成 Telegram 授权");
            if (!client.IsLoggedIn)
            {
                if (!client.HasSavedSession)
                    throw new InvalidOperationException("请先在设置中完成 Telegram 授权");
                logCallback?.Invoke("[Telegram] 正在恢复本机已保存的登录会话...");
                await client.RestoreSessionAsync(ct);
                if (!client.IsLoggedIn)
                    throw new InvalidOperationException("Telegram 会话已失效，请在设置中重新登录。");
            }

            logCallback?.Invoke($"[Telegram] 正在查找会话: {chatTarget}（私有会话包含全部分页和归档）...");
            var peerInfo = await ExecuteTelegramRequestAsync(
                async () => chatTarget.StartsWith("-100", StringComparison.Ordinal)
                    ? (IPeerInfo)await TelegramPeerResolver.ResolvePrivateChannelAsync(client, GetPrivateChatLookupId(chatTarget), ct)
                    : await client.ResolveUsernameAsync(chatTarget, ct),
                logCallback, ct);
            var peer = peerInfo.ToInputPeer();
            var title = peerInfo is ChatBase chat ? chat.Title : chatTarget;
            var safeTitle = DownloadFileNameBuilder.SanitizeResolvedTitle(title);
            if (safeTitle.Length > 100)
                safeTitle = safeTitle[..100];
            var folderName = endId is { } end
                ? $"{safeTitle}_{startId}-{end}"
                : $"{safeTitle}_{startId}";
            var savePath = BuildSafeMediaFilePath(task.OutputDirectory, folderName, $"TG_{startId}");
            Directory.CreateDirectory(savePath);
            task.Title = folderName;
            task.OutputFilePath = savePath;
            task.OutputFilePaths = [];

            var total = (long)(endId ?? startId) - startId + 1;
            var successes = 0L;
            var skipped = 0L;
            var failureCount = 0L;
            var failedMessageIds = new List<int>();
            string? firstError = null;
            for (long index = 0; index < total; index++)
            {
                ct.ThrowIfCancellationRequested();
                var messageId = checked((int)(startId + index));
                try
                {
                    var message = await ExecuteTelegramRequestAsync(
                        () => client.GetMessageAsync(peer, messageId, ct), logCallback, ct);
                    if (message is not Message regularMessage)
                    {
                        skipped++;
                        logCallback?.Invoke($"[Telegram] 消息 {messageId} 不存在、已删除或为话题/服务消息，跳过。");
                    }
                    else
                    {
                        await SaveMessageAsync(client, peer, regularMessage, task, savePath,
                            endId.HasValue ? $"{messageId}_" : "", index, total, progress, logCallback, ct);
                        successes++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (TimeoutException) { throw; }
                catch (Exception ex)
                {
                    if (client is TelegramDownloadClient { IsAborted: true })
                        throw;
                    if (!endId.HasValue)
                        throw new IOException($"下载消息 {messageId} 失败: {DescribeTelegramError(ex)}", ex);
                    failureCount++;
                    if (failedMessageIds.Count < 20)
                        failedMessageIds.Add(messageId);
                    firstError ??= DescribeTelegramError(ex);
                    logCallback?.Invoke($"[Telegram] 消息 {messageId} 下载失败: {DescribeTelegramError(ex)}");
                }
                progress?.Report(new DownloadProgress { Percent = Math.Min(99.9, (index + 1) * 100d / total) });
            }

            ct.ThrowIfCancellationRequested();
            UpdateTaskFileSizeFromDirectory(task, savePath, logCallback);
            if (failureCount > 0)
                throw new IOException($"Telegram 范围下载未全部完成：成功 {successes}，跳过 {skipped}，失败 {failureCount}。失败消息 ID：{string.Join(", ", failedMessageIds)}。{firstError} 已下载文件保留在：{savePath}");
            if (successes == 0)
                throw new IOException("没有下载到可保存的消息。消息可能已删除、无访问权限，或链接指向话题入口；请复制话题内具体消息的链接。");
            task.DownloadedSize = task.FileSize;
            task.Progress = 100;
            task.Status = DownloadStatus.Completed;
            logCallback?.Invoke($"[Telegram] 下载完成：成功 {successes}，跳过 {skipped}。已保存至：{savePath}");
        }
        catch (OperationCanceledException)
        {
            if (gateHeld)
                await AbortDownloadClientAsync();
            task.MarkCancelledUnlessPaused();
            logCallback?.Invoke("[Telegram] 下载已取消。");
            throw;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            if (gateHeld)
                await AbortDownloadClientAsync();
            task.MarkCancelledUnlessPaused();
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex)
        {
            if (gateHeld && ex is TimeoutException)
                await AbortDownloadClientAsync();
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = DescribeTelegramError(ex);
            logCallback?.Invoke($"[Telegram] 下载失败: {task.ErrorMessage}");
            throw;
        }
        finally
        {
            if (gateHeld)
            {
                ClearAbortedClient();
                _clientSemaphore.Release();
            }
        }
    }

    private async Task AbortDownloadClientAsync()
    {
        if (_downloadClient is not TelegramDownloadClient client || client.IsAborted)
            return;
        try { await client.AbortAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (Exception ex) { Debug.WriteLine($"[Telegram] 中止连接: {ex.GetType().Name}"); }
    }

    private void ClearAbortedClient()
    {
        if (_downloadClient is TelegramDownloadClient { IsAborted: true })
        {
            _client = null;
            _downloadClient = null;
            _loginRequirement = null;
        }
    }

    private static async Task SaveMessageAsync(
        ITelegramDownloadClient client, InputPeer peer, Message message, DownloadTask task,
        string savePath, string prefix, long batchIndex, long batchCount,
        IProgress<DownloadProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        var hasText = !string.IsNullOrWhiteSpace(message.message);
        if (hasText)
        {
            var textPath = Path.Combine(savePath, $"{prefix}message_text.txt");
            await File.WriteAllTextAsync(textPath, message.message, Encoding.UTF8, ct);
            task.OutputFilePaths.Add(textPath);
        }
        if (message.media is not (MessageMediaDocument or MessageMediaPhoto))
        {
            if (!hasText)
                throw new NotSupportedException("该消息没有可保存的文本、文件或照片。");
            return;
        }

        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var (filename, expectedSize) = GetMediaFileInfo(message);
            var finalPath = BuildSafeMediaFilePath(savePath, filename, $"media_{message.id}.bin", prefix);
            if (hasText && Path.GetFileName(finalPath).Equals($"{prefix}message_text.txt", StringComparison.OrdinalIgnoreCase))
                finalPath = BuildSafeMediaFilePath(savePath, $"media_{message.id}_{filename}", $"media_{message.id}.bin", prefix);
            var temporaryPath = Path.Combine(savePath, $".telegram-{Guid.NewGuid():N}.part");
            try
            {
                log?.Invoke($"[Telegram] 下载 {Path.GetFileName(finalPath)}，大小 {ByteSizeFormatter.FormatOrUnknown(expectedSize)}");
                var timer = Stopwatch.StartNew();
                var lastBytes = 0L;
                var lastTime = TimeSpan.Zero;
                using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var callback = CreateCancellableProgressCallback(ct, (downloaded, size) =>
                    {
                        var now = timer.Elapsed;
                        var elapsed = (now - lastTime).TotalSeconds;
                        if (elapsed < 0.1 && downloaded != size)
                            return;
                        var speed = elapsed > 0 ? Math.Max(0, downloaded - lastBytes) / elapsed : 0;
                        lastBytes = downloaded;
                        lastTime = now;
                        progress?.Report(new DownloadProgress
                        {
                            Percent = Math.Min(99.9, (batchIndex + (size > 0 ? Math.Clamp(downloaded / (double)size, 0, 1) : 0)) * 100 / batchCount),
                            Speed = speed,
                            Eta = speed > 0 ? Math.Max(0, size - downloaded) / speed : 0,
                            Downloaded = downloaded,
                            Total = size
                        });
                    });
                    await client.DownloadMediaAsync(message.media, output, callback, ct, log);
                    await output.FlushAsync(ct);
                    if (output.Length == 0 || (expectedSize > 0 && output.Length != expectedSize))
                        throw new IOException($"Telegram 文件不完整：预期 {expectedSize} 字节，实际 {output.Length} 字节。");
                }
                ct.ThrowIfCancellationRequested();
                File.Move(temporaryPath, finalPath, overwrite: true);
                task.Format = Path.GetExtension(finalPath).TrimStart('.').ToLowerInvariant();
                task.OutputFilePaths.Add(finalPath);
                var savedBytes = new FileInfo(finalPath).Length;
                var averageSpeed = (long)(savedBytes / Math.Max(0.001, timer.Elapsed.TotalSeconds));
                log?.Invoke($"[Telegram] 媒体已保存: {finalPath}，耗时 {timer.Elapsed.TotalSeconds:F1} 秒，平均 {ByteSizeFormatter.FormatOrUnknown(averageSpeed)}/s（包含服务器等待）。");
                return;
            }
            catch (RpcException ex) when (attempt < 2 && ex.Message.StartsWith("FILE_REFERENCE_", StringComparison.Ordinal))
            {
                log?.Invoke("[Telegram] 文件引用已过期，重新获取消息后重试...");
                message = await ExecuteTelegramRequestAsync(
                    () => client.GetMessageAsync(peer, message.id, ct), log, ct) as Message
                    ?? throw new IOException("重新获取消息失败，消息可能已删除。", ex);
            }
            catch (RpcException ex) when (attempt < 2 && ex.Code == 420 && ex.X > 0 && ex.X <= 60
                && !(client is TelegramDownloadClient && TelegramDownloadClient.UsesChunkDownloader(message.media)))
            {
                log?.Invoke($"[Telegram] 请求受限，按服务器要求等待 {ex.X} 秒后重试...");
                await Task.Delay(TimeSpan.FromSeconds(ex.X), ct);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }

    internal static (string FileName, long Size) GetMediaFileInfo(Message message)
    {
        if (message.media is MessageMediaDocument { document: Document document })
        {
            var filename = document.attributes?.OfType<DocumentAttributeFilename>().FirstOrDefault()?.file_name;
            if (string.IsNullOrWhiteSpace(filename))
            {
                var extension = document.mime_type?.ToLowerInvariant() switch
                {
                    "video/mp4" => ".mp4", "video/webm" => ".webm", "video/quicktime" => ".mov",
                    "audio/mpeg" => ".mp3", "audio/mp4" => ".m4a", "audio/ogg" => ".ogg",
                    "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp",
                    "image/gif" => ".gif", "application/pdf" => ".pdf", "application/zip" => ".zip",
                    _ => ".bin"
                };
                filename = $"media_{message.id}{extension}";
            }
            return (filename, document.size);
        }
        if (message.media is MessageMediaPhoto { photo: Photo photo } && photo.LargestPhotoSize is { } size)
            return ($"media_{message.id}.jpg", size.FileSize);
        throw new IOException("这条消息的媒体已失效或没有可下载的原始文件。");
    }

    private static async Task<T> ExecuteTelegramRequestAsync<T>(
        Func<Task<T>> request, Action<string>? log, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await request(); }
            catch (RpcException ex) when (attempt < 2 && ex.Code == 420 && ex.X > 0 && ex.X <= 60)
            {
                log?.Invoke($"[Telegram] 请求受限，按服务器要求等待 {ex.X} 秒后重试...");
                await Task.Delay(TimeSpan.FromSeconds(ex.X), ct);
            }
        }
    }

    private static string DescribeTelegramError(Exception ex) => ex switch
    {
        RpcException rpc when rpc.Code == 420 && rpc.Message == "FLOOD_PREMIUM_WAIT_X"
            => $"Telegram 对免费账号的下载请求限速，请等待 {rpc.X} 秒后重试。",
        RpcException { Code: 420 } rpc => $"Telegram 请求过于频繁，请等待 {rpc.X} 秒后重试。",
        RpcException rpc when rpc.Message is "CHANNEL_PRIVATE" or "CHAT_FORBIDDEN" or "CHANNEL_INVALID"
            => "当前 Telegram 账号无法访问该私有频道或群组，请确认绑定账号和成员权限。",
        RpcException { Code: 401 } => "Telegram 登录会话已失效，请在设置中重新登录。",
        TimeoutException => "Telegram 请求超时，请检查代理和网络连接后重试。",
        _ => ex.Message
    };
    internal static WTelegram.Client.ProgressCallback CreateCancellableProgressCallback(
        CancellationToken ct,
        Action<long, long> reportProgress)
    {
        ArgumentNullException.ThrowIfNull(reportProgress);

        return (bytesDownloaded, totalSize) =>
        {
            ct.ThrowIfCancellationRequested();
            reportProgress(bytesDownloaded, totalSize);
        };
    }

    private static void UpdateTaskFileSizeFromDirectory(DownloadTask task, string directoryPath, Action<string>? logCallback)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                long totalSize = 0;
                var dirInfo = new DirectoryInfo(directoryPath);
                foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
                {
                    totalSize += file.Length;
                }
                if (totalSize > 0)
                {
                    task.FileSize = totalSize;
                }
            }
        }
        catch (Exception ex)
        {
            logCallback?.Invoke($"[Telegram] 计算目录大小失败: {ex.Message}");
        }
    }

    internal static string BuildSafeMediaFilePath(
        string savePath,
        string? remoteFileName,
        string fallbackFileName,
        string prefix = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackFileName);

        string? leafName;
        try
        {
            leafName = Path.GetFileName(remoteFileName?.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            leafName = null;
        }

        var fileName = DownloadFileNameBuilder.SanitizeResolvedTitle(
            string.IsNullOrWhiteSpace(leafName) ? fallbackFileName : leafName);
        var safePrefix = string.IsNullOrWhiteSpace(prefix)
            ? ""
            : DownloadFileNameBuilder.SanitizeResolvedTitle(prefix);
        var rootPath = Path.GetFullPath(savePath);
        var outputPath = Path.GetFullPath(Path.Combine(rootPath, $"{safePrefix}{fileName}"));
        var rootWithSeparator = rootPath.EndsWith(Path.DirectorySeparatorChar)
            || rootPath.EndsWith(Path.AltDirectorySeparatorChar)
                ? rootPath
                : rootPath + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!outputPath.StartsWith(rootWithSeparator, comparison))
            throw new IOException("Telegram 媒体文件路径超出任务下载目录。");

        return outputPath;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _shutdown.Cancel();
        if (_downloadClient is TelegramDownloadClient client)
            client.AbortAsync().GetAwaiter().GetResult();
        else
            _client?.Dispose();
        // In-flight operations and queued callers still use the gate/token while
        // unwinding. Leave these managed objects for GC instead of disposing them
        // underneath WaitAsync/Release (no WaitHandle is allocated by this service).
    }
}
