using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;

namespace EasyGet.Services;

public enum FailureCategory
{
    Authentication,
    Network,
    Proxy,
    Tooling,
    Storage,
    Access,
    RateLimit,
    Unsupported,
    Unknown
}

public static class FailureRecoveryActionKeys
{
    public const string OpenAccountSettings = "open_account_settings";
    public const string Retry = "retry";
    public const string OpenProxySettings = "open_proxy_settings";
    public const string RepairTools = "repair_tools";
    public const string ChooseOutputFolder = "choose_output_folder";
    public const string OpenAccessHelp = "open_access_help";
    public const string RetryLater = "retry_later";
    public const string OpenSupport = "open_support";
    public const string OpenDiagnostics = "open_diagnostics";
}

public sealed record FailureRecoveryAdvice(
    FailureCategory Category,
    string UserMessage,
    string SuggestedActionKey);

/// <summary>
/// Converts raw failures into stable, non-sensitive recovery guidance for the UI.
/// </summary>
public sealed class FailureRecoveryAdvisor
{
    public FailureRecoveryAdvice Advise(Exception? exception)
    {
        if (exception is null)
            return CreateAdvice(FailureCategory.Unknown);

        var exceptions = EnumerateExceptions(exception).ToList();
        var combinedMessage = string.Join(
            Environment.NewLine,
            exceptions.Select(candidate => candidate.Message));
        var category = ClassifyByText(combinedMessage);
        if (category != FailureCategory.Unknown)
            return CreateAdvice(category);

        category = exceptions.Select(ClassifyByType)
            .FirstOrDefault(candidate => candidate != FailureCategory.Unknown);
        return CreateAdvice(category);
    }

    public FailureRecoveryAdvice Advise(string? errorMessage)
        => CreateAdvice(ClassifyByText(errorMessage));

    public FailureCategory Classify(string? errorMessage)
        => ClassifyByText(errorMessage);

    private static FailureCategory ClassifyByText(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return FailureCategory.Unknown;

        var text = errorMessage.ToLowerInvariant();

        if (ContainsAny(text,
                "http 429", "status code 429", "too many requests", "rate limit",
                "rate-limit", "请求过于频繁", "请求太频繁", "访问频率", "限流"))
        {
            return FailureCategory.RateLimit;
        }

        if (ContainsAny(text,
                "proxy", "socks", "代理服务器", "代理连接", "代理认证", "代理配置"))
        {
            return FailureCategory.Proxy;
        }

        if (ContainsAny(text,
                "login required", "sign in", "not logged", "authentication required",
                "authentication failed", "invalid credential", "cookie", "http 401",
                "http 403", "status code 401", "status code 403", "unauthorized request",
                "forbidden", "需要登录", "请登录", "登录状态", "身份验证", "认证失败"))
        {
            return FailureCategory.Authentication;
        }

        if (ContainsAny(text,
                "yt-dlp", "youtube-dl", "ffmpeg", "ffprobe", "aria2", "executable not found",
                "tool was not found", "missing executable", "找不到可执行", "工具未安装",
                "组件未安装", "运行环境缺失"))
        {
            return FailureCategory.Tooling;
        }

        if (ContainsAny(text,
                "disk full", "no space left", "insufficient disk", "not enough space",
                "enospc", "drive not found", "directory not found", "path not found",
                "磁盘空间不足", "磁盘已满", "存储空间不足", "找不到目录", "路径不存在"))
        {
            return FailureCategory.Storage;
        }

        if (ContainsAny(text,
                "access denied", "permission denied", "unauthorized access", "sharing violation",
                "file is being used", "file is in use", "拒绝访问", "没有权限", "权限不足",
                "文件被占用", "正在被另一进程使用"))
        {
            return FailureCategory.Access;
        }

        if (ContainsAny(text,
                "unsupported url", "unsupported site", "unsupported format", "not supported",
                "no video formats found", "drm protected", "encrypted media", "不支持的链接",
                "暂不支持", "不受支持", "受 drm 保护"))
        {
            return FailureCategory.Unsupported;
        }

        if (ContainsAny(text,
                "timed out", "timeout", "name or service not known", "dns", "socket",
                "connection refused", "connection reset", "connection aborted", "network is unreachable",
                "tls", "ssl", "certificate", "http request", "无法连接", "连接超时",
                "网络不可用", "网络错误", "域名解析", "连接被重置"))
        {
            return FailureCategory.Network;
        }

        return FailureCategory.Unknown;
    }

    private static FailureCategory ClassifyByType(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => FailureCategory.Access,
            DirectoryNotFoundException or DriveNotFoundException => FailureCategory.Storage,
            FileNotFoundException fileNotFound when ContainsAny(
                fileNotFound.FileName?.ToLowerInvariant() ?? "",
                "yt-dlp", "youtube-dl", "ffmpeg", "ffprobe", "aria2") => FailureCategory.Tooling,
            TimeoutException or HttpRequestException or SocketException => FailureCategory.Network,
            NotSupportedException => FailureCategory.Unsupported,
            Win32Exception win32 when win32.NativeErrorCode is 2 or 3 => FailureCategory.Tooling,
            _ => FailureCategory.Unknown
        };
    }

    private static FailureRecoveryAdvice CreateAdvice(FailureCategory category)
    {
        return category switch
        {
            FailureCategory.Authentication => new(
                category,
                "登录状态不可用或已过期，请重新登录后重试。",
                FailureRecoveryActionKeys.OpenAccountSettings),
            FailureCategory.Network => new(
                category,
                "网络连接失败，请检查网络后重试。",
                FailureRecoveryActionKeys.Retry),
            FailureCategory.Proxy => new(
                category,
                "代理连接不可用，请检查代理地址和认证信息。",
                FailureRecoveryActionKeys.OpenProxySettings),
            FailureCategory.Tooling => new(
                category,
                "下载组件缺失或无法运行，请检查并修复运行环境。",
                FailureRecoveryActionKeys.RepairTools),
            FailureCategory.Storage => new(
                category,
                "保存位置不可用或空间不足，请选择其他目录。",
                FailureRecoveryActionKeys.ChooseOutputFolder),
            FailureCategory.Access => new(
                category,
                "文件或目录无法访问，请检查权限及文件占用情况。",
                FailureRecoveryActionKeys.OpenAccessHelp),
            FailureCategory.RateLimit => new(
                category,
                "平台暂时限制了请求频率，请稍后再试。",
                FailureRecoveryActionKeys.RetryLater),
            FailureCategory.Unsupported => new(
                category,
                "当前链接或媒体类型暂不受支持。",
                FailureRecoveryActionKeys.OpenSupport),
            _ => new(
                FailureCategory.Unknown,
                "操作未能完成，可导出诊断信息后重试或寻求支持。",
                FailureRecoveryActionKeys.OpenDiagnostics)
        };
    }

    private static IEnumerable<Exception> EnumerateExceptions(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                foreach (var candidate in EnumerateExceptions(inner))
                    yield return candidate;
            }
            yield break;
        }

        for (var current = exception; current is not null; current = current.InnerException)
            yield return current;
    }

    private static bool ContainsAny(string text, params string[] values)
        => values.Any(text.Contains);
}
