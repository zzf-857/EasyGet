using Message = TL.Message;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EasyGet.Models;
using TL;

namespace EasyGet.Services;

public class TelegramDownloadService : IDisposable
{
    private readonly ConfigService _configService;
    private WTelegram.Client? _client;
    private string? _loginRequirement; // 缓存登录步骤，如 "verification_code" 或 "password"
    private readonly SemaphoreSlim _clientSemaphore = new(1, 1);

    public TelegramDownloadService(ConfigService configService)
    {
        _configService = configService;
    }

    public static bool IsTelegramUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.Contains("t.me/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("tg://", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex PrivateLinkRe = new(@"(?:t\.me/c/|tg://private\?channel=)(\d+)(?:/|&post=)(\d+)(?:[\s_\-]+(\d+))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PublicLinkRe = new(@"(?:t\.me/|tg://resolve\?domain=)([a-zA-Z0-9_]+)(?:/|&post=)(\d+)(?:[\s_\-]+(\d+))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static (string chatTarget, int startId, int? endId)? ParseTelegramLink(string link)
    {
        link = link.Trim();
        if (link.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            link = link.Split('?')[0];
        }
        
        var privateMatch = PrivateLinkRe.Match(link);
        if (privateMatch.Success)
        {
            var chatId = $"-100{privateMatch.Groups[1].Value}";
            var startId = int.Parse(privateMatch.Groups[2].Value);
            int? endId = privateMatch.Groups[3].Success ? int.Parse(privateMatch.Groups[3].Value) : null;
            return (chatId, startId, endId);
        }

        var publicMatch = PublicLinkRe.Match(link);
        if (publicMatch.Success)
        {
            var chatUsername = publicMatch.Groups[1].Value;
            if (string.Equals(chatUsername, "c", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var startId = int.Parse(publicMatch.Groups[2].Value);
            int? endId = publicMatch.Groups[3].Success ? int.Parse(publicMatch.Groups[3].Value) : null;
            return (chatUsername, startId, endId);
        }

        return null;
    }

    /// <summary>
    /// 初始化并连接 Telegram，检查登录状态
    /// </summary>
    public async Task<string?> CheckLoginStatusAsync()
    {
        await _clientSemaphore.WaitAsync();
        try
        {
            if (_client == null)
            {
                InitClient();
            }

            if (_client!.User != null)
                return null; // 已登录

            var loginResult = await _client.Login(_configService.Config.TgPhoneNumber);
            _loginRequirement = loginResult;
            return loginResult; // 返回 "verification_code", "password" 或 null
        }
        finally
        {
            _clientSemaphore.Release();
        }
    }

    /// <summary>
    /// 发送验证码（用于登录的第一步）
    /// </summary>
    public async Task<string?> SendCodeAsync(string phone, string apiId, string apiHash)
    {
        await _clientSemaphore.WaitAsync();
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
        await _clientSemaphore.WaitAsync();
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
        await _clientSemaphore.WaitAsync();
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
        await _clientSemaphore.WaitAsync();
        try
        {
            if (_client != null)
            {
                await _client.Auth_LogOut();
                _client.Dispose();
                _client = null;
            }

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

        // 接管 TCP 连接并处理 Socks5/HTTP 代理
        _client.TcpHandler = async (host, port) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                if (_configService.Config.UseProxy && !string.IsNullOrWhiteSpace(_configService.Config.ProxyAddress))
                {
                    var proxyUri = new Uri(_configService.Config.ProxyAddress);
                    var proxyHost = proxyUri.Host;
                    var proxyPort = proxyUri.Port;

                    await socket.ConnectAsync(proxyHost, proxyPort);

                    if (proxyUri.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase))
                    {
                        // Socks5 握手
                        socket.Send(new byte[] { 5, 1, 0 });
                        var resp = new byte[2];
                        socket.Receive(resp);
                        if (resp[0] != 5 || resp[1] != 0)
                        {
                            throw new Exception("Socks5 代理建立握手协议失败。");
                        }

                        var request = new List<byte> { 5, 1, 0 };
                        if (IPAddress.TryParse(host, out var ipAddress))
                        {
                            if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
                            {
                                request.Add(1);
                                request.AddRange(ipAddress.GetAddressBytes());
                            }
                            else
                            {
                                request.Add(4);
                                request.AddRange(ipAddress.GetAddressBytes());
                            }
                        }
                        else
                        {
                            request.Add(3);
                            var domainBytes = Encoding.ASCII.GetBytes(host);
                            request.Add((byte)domainBytes.Length);
                            request.AddRange(domainBytes);
                        }

                        request.Add((byte)(port >> 8));
                        request.Add((byte)(port & 0xFF));

                        socket.Send(request.ToArray());

                        var connResp = new byte[256];
                        int len = socket.Receive(connResp);
                        if (len < 4 || connResp[1] != 0)
                        {
                            throw new Exception($"Socks5 代理连接目标失败，Reply Code: {connResp[1]}");
                        }
                    }
                    else if (proxyUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                    {
                        // HTTP CONNECT 隧道
                        var connectCmd = $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n";
                        socket.Send(Encoding.ASCII.GetBytes(connectCmd));

                        var buffer = new byte[2048];
                        int len = socket.Receive(buffer);
                        var responseText = Encoding.ASCII.GetString(buffer, 0, len);
                        if (!responseText.Contains("200 Connection Established", StringComparison.OrdinalIgnoreCase)
                            && !responseText.Contains("200 OK", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new Exception($"HTTP 代理建立通道失败:\n{responseText}");
                        }
                    }
                    else
                    {
                        throw new NotSupportedException($"暂不支持的代理协议: {proxyUri.Scheme}");
                    }
                }
                else
                {
                    await socket.ConnectAsync(host, port);
                }
                
                var tcpClient = new TcpClient();
                tcpClient.Client = socket;
                return tcpClient;
            }
            catch
            {
                socket.Close();
                throw;
            }
        };

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
        task.Status = DownloadStatus.Downloading;
        logCallback?.Invoke($"[Telegram] 开始提取任务: {task.Url}");

        var parsed = ParseTelegramLink(task.Url);
        if (parsed == null)
        {
            throw new ArgumentException("无法识别的 Telegram 直链格式");
        }

        var (chatTarget, startId, endId) = parsed.Value;

        // 确保客户端登录成功
        await _clientSemaphore.WaitAsync(ct);
        try
        {
            if (_client == null)
            {
                InitClient();
            }

            if (_client!.User == null)
            {
                var loginStatus = await _client.Login(_configService.Config.TgPhoneNumber);
                if (loginStatus != null)
                {
                    throw new InvalidOperationException("Telegram 账号未在设置中完成授权绑定登录，无法开始下载任务。");
                }
            }
        }
        finally
        {
            _clientSemaphore.Release();
        }

        try
        {
            logCallback?.Invoke($"[Telegram] 正在获取会话: {chatTarget}...");
            IPeerInfo peerInfo;
            try
            {
                // 获取会话实体
                if (long.TryParse(chatTarget, out var chatId))
                {
                    // 私有群组：为了避免 access_hash 缺失，先获取所有对话列表填充本地缓存
                    var dialogs = await _client!.Messages_GetDialogs();
                    Dictionary<long, ChatBase>? chatsDict = null;
                    if (dialogs is Messages_Dialogs md) chatsDict = md.chats;
                    else if (dialogs is Messages_DialogsSlice mds) chatsDict = mds.chats;

                    if (chatsDict != null && chatsDict.TryGetValue(chatId, out var chat))
                    {
                        peerInfo = chat;
                    }
                    else
                    {
                        throw new Exception($"未能在对话列表中查找到频道 ID {chatId}，请确保您已加入该私有群聊并且该群在对话列表中。");
                    }
                }
                else
                {
                    // 公开群组/频道
                    var resolved = await _client!.Contacts_ResolveUsername(chatTarget);
                    peerInfo = resolved.UserOrChat;
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"获取会话失败，请确保您已加入此群组且网络正常: {ex.Message}", ex);
            }

            string chatTitle = "";
            InputPeer? inputPeer = null;
            if (peerInfo is User u)
            {
                chatTitle = u.MainUsername ?? u.ID.ToString();
                inputPeer = u.ToInputPeer();
            }
            else if (peerInfo is ChatBase cb)
            {
                chatTitle = cb.Title;
                inputPeer = cb.ToInputPeer();
            }
            else
            {
                throw new Exception("无法识别的 Telegram 会话类型");
            }

            var safeTitle = string.Concat(chatTitle.Split(Path.GetInvalidFileNameChars())).Trim();
            if (string.IsNullOrWhiteSpace(safeTitle))
            {
                safeTitle = chatTarget;
            }

            if (endId != null)
            {
                // 范围消息下载
                var start = startId;
                var end = endId.Value;
                if (end < start)
                {
                    // 自动纠错顺序
                    (start, end) = (end, start);
                }

                var msgIds = new List<int>();
                for (int i = start; i <= end; i++)
                {
                    msgIds.Add(i);
                }

                logCallback?.Invoke($"[Telegram] 启动范围提取任务: {chatTitle} [ID {start} - {end}]，共 {msgIds.Count} 个消息。");
                var folderName = $"{safeTitle}_{start}-{end}";
                task.Title = folderName;
                var savePath = Path.Combine(task.OutputDirectory, folderName);
                Directory.CreateDirectory(savePath);

                int successCount = 0;
                int failCount = 0;

                for (int idx = 0; idx < msgIds.Count; idx++)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    var msgId = msgIds[idx];
                    logCallback?.Invoke($"[Telegram] [{idx + 1}/{msgIds.Count}] 正在拉取消息 ID: {msgId}...");
                    
                    var success = await DownloadSingleMessageAsync(
                        task, inputPeer, msgId, savePath, logCallback, progress, 
                        totalInBatch: msgIds.Count, currentBatchIndex: idx, 
                        prefix: $"{msgId}_", ct: ct);

                    if (success) successCount++;
                    else failCount++;

                    // 智能避让风控
                    if (idx < msgIds.Count - 1)
                    {
                        var delayMs = new Random().Next(2000, 5000);
                        logCallback?.Invoke($"[Telegram] 模拟真人呼吸，等待 {delayMs / 1000.0:F1} 秒...");
                        await Task.Delay(delayMs, ct);
                    }
                }

                if (successCount == 0)
                {
                    throw new Exception("范围提取中所有的消息均下载失败。");
                }

                task.Status = DownloadStatus.Completed;
                task.Progress = 100;
                task.OutputFilePath = savePath;
                UpdateTaskFileSizeFromDirectory(task, savePath, logCallback);
                logCallback?.Invoke($"[Telegram] 范围提取完成。成功: {successCount} | 失败: {failCount}。已保存至目录: {savePath}");
            }
            else
            {
                // 单条消息下载
                logCallback?.Invoke($"[Telegram] 正在拉取单条消息 ID: {startId}...");
                var folderName = $"{safeTitle}_{startId}";
                task.Title = folderName;
                var savePath = Path.Combine(task.OutputDirectory, folderName);
                Directory.CreateDirectory(savePath);

                var success = await DownloadSingleMessageAsync(task, inputPeer, startId, savePath, logCallback, progress, 1, 0, "", ct);
                if (!success)
                {
                    throw new Exception($"下载消息 ID {startId} 失败。");
                }

                task.Status = DownloadStatus.Completed;
                task.Progress = 100;
                task.OutputFilePath = savePath;
                UpdateTaskFileSizeFromDirectory(task, savePath, logCallback);
                logCallback?.Invoke($"[Telegram] 单条提取完成。已保存至目录: {savePath}");
            }
        }
        catch (OperationCanceledException)
        {
            task.Status = DownloadStatus.Cancelled;
            logCallback?.Invoke("[Telegram] 提取任务已被用户取消。");
            throw;
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = ex.Message;
            logCallback?.Invoke($"[Telegram] 任务失败: {ex.Message}");
            throw;
        }
    }

    private async Task<bool> DownloadSingleMessageAsync(
        DownloadTask task,
        InputPeer peer,
        int messageId,
        string savePath,
        Action<string>? logCallback,
        IProgress<DownloadProgress>? progress,
        int totalInBatch,
        int currentBatchIndex,
        string prefix,
        CancellationToken ct)
    {
        try
        {
            // 通过 WTelegramClient 抓取单条消息，根据 Peer 类型分流调用
            Messages_MessagesBase res;
            if (peer is InputPeerChannel ipc)
            {
                var inputChannel = new InputChannel(ipc.channel_id, ipc.access_hash);
                res = await _client!.Channels_GetMessages(inputChannel, new InputMessageID { id = messageId });
            }
            else
            {
                res = await _client!.Messages_GetMessages(new InputMessageID { id = messageId });
            }
            if (res.Messages.Length == 0 || res.Messages[0] is MessageEmpty)
            {
                logCallback?.Invoke($"[Telegram] 消息 ID {messageId} 未找到或已被删除。");
                return false;
            }

            var message = res.Messages[0] as Message;
            if (message == null)
            {
                logCallback?.Invoke($"[Telegram] 消息 ID {messageId} 格式无效。");
                return false;
            }

            // 1. 保存文本内容
            if (!string.IsNullOrWhiteSpace(message.message))
            {
                logCallback?.Invoke($"[Telegram] 消息文本: {message.message}");
                var textPath = Path.Combine(savePath, $"{prefix}message_text.txt");
                await File.WriteAllTextAsync(textPath, message.message, Encoding.UTF8, ct);
                logCallback?.Invoke($"[Telegram] 文本已写入: {textPath}");
            }

            // 2. 下载媒体内容
            if (message.media != null)
            {
                string filename = $"media_{messageId}";
                long totalSize = 0;

                if (message.media is MessageMediaDocument md && md.document is Document doc)
                {
                    totalSize = doc.size;
                    // 从属性中寻找文件名
                    foreach (var attr in doc.attributes)
                    {
                        if (attr is DocumentAttributeFilename daf)
                        {
                            filename = daf.file_name;
                            break;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(filename))
                    {
                        var ext = !string.IsNullOrWhiteSpace(doc.mime_type) && doc.mime_type.Contains('/') 
                            ? $".{doc.mime_type.Split('/')[1]}" 
                            : ".bin";
                        filename = $"media_{messageId}{ext}";
                    }
                }
                else if (message.media is MessageMediaPhoto mp && mp.photo is Photo photo)
                {
                    // 照片通常取最大尺寸
                    filename = $"media_{messageId}.jpg";
                    totalSize = photo.sizes.Length > 0 ? photo.sizes[^1].FileSize : 0;
                }

                if (totalSize > 0)
                {
                    task.FileSize = totalSize;
                }
                var fileExt = Path.GetExtension(filename).TrimStart('.').ToLowerInvariant();
                if (!string.IsNullOrEmpty(fileExt))
                {
                    task.Format = fileExt;
                }

                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    filename = $"{prefix}{filename}";
                }

                var mediaFilePath = Path.Combine(savePath, filename);
                logCallback?.Invoke($"[Telegram] 准备下载媒体文件: {filename} (大小: {ByteSizeFormatter.FormatOrUnknown(totalSize)})");

                // 流式分块下载，并提供精确进度上报
                using (var fileStream = new FileStream(mediaFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var lastReportedTime = DateTime.UtcNow;
                    var lastReportedBytes = 0L;

                    Action<long, long> reportProgress = (bytesDownloaded, totalSize) =>
                    {
                        if (ct.IsCancellationRequested) return;

                        var now = DateTime.UtcNow;
                        var elapsed = (now - lastReportedTime).TotalSeconds;

                        if (elapsed >= 0.1 || bytesDownloaded == totalSize)
                        {
                            double speed = 0;
                            if (elapsed > 0)
                            {
                                speed = (bytesDownloaded - lastReportedBytes) / elapsed;
                            }
                            lastReportedTime = now;
                            lastReportedBytes = bytesDownloaded;

                            double eta = 0;
                            if (speed > 0 && totalSize > bytesDownloaded)
                            {
                                eta = (totalSize - bytesDownloaded) / speed;
                            }

                            double batchBasePercent = (double)currentBatchIndex / totalInBatch * 100;
                            double currentMediaPercent = totalSize > 0 ? (double)bytesDownloaded / totalSize * 100 : 0;
                            double totalPercent = batchBasePercent + (currentMediaPercent / totalInBatch);

                            progress?.Report(new DownloadProgress
                            {
                                Percent = Math.Min(99.9, totalPercent),
                                Speed = speed,
                                Eta = eta,
                                Downloaded = bytesDownloaded,
                                Total = totalSize
                            });
                        }
                    };

                    if (message.media is MessageMediaDocument mDoc && mDoc.document is Document d)
                    {
                        await _client!.DownloadFileAsync(d, fileStream, (PhotoSizeBase)null!, (bytes, total) => reportProgress(bytes, total));
                    }
                    else if (message.media is MessageMediaPhoto mPhoto && mPhoto.photo is Photo p)
                    {
                        var largestSize = p.sizes[^1];
                        await _client!.DownloadFileAsync(p, fileStream, largestSize, (bytes, total) => reportProgress(bytes, total));
                    }
                }

                logCallback?.Invoke($"[Telegram] 媒体保存成功: {mediaFilePath}");
            }

            return true;
        }
        catch (Exception ex)
        {
            logCallback?.Invoke($"[Telegram] 下载消息 ID {messageId} 出错: {ex.Message}");
            return false;
        }
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

    public void Dispose()
    {
        _client?.Dispose();
        _clientSemaphore.Dispose();
    }
}
