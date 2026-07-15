using System.Diagnostics;

namespace EasyGet.Services.Cookies;

public interface IDefaultBrowserLauncher
{
    Task OpenAsync(Uri uri, CancellationToken cancellationToken = default);
}

public sealed class DefaultBrowserLauncher : IDefaultBrowserLauncher
{
    private readonly Action<ProcessStartInfo> _start;

    public DefaultBrowserLauncher()
        : this(StartProcess)
    {
    }

    internal DefaultBrowserLauncher(Action<ProcessStartInfo> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        _start = start;
    }

    public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException(
                "Only absolute HTTPS addresses without embedded credentials can be opened.",
                nameof(uri));
        }

        _start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    private static void StartProcess(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("The system default browser could not be started.");
    }
}
