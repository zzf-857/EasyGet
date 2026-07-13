using System.Windows;
using System.Windows.Threading;
using EasyGet.Views;

namespace EasyGet.Services.Cookies;

public sealed class ManagedLoginWindowFactory : IManagedLoginWindowFactory
{
    public async Task<IManagedLoginWindow> CreateAsync(
        MediaPlatformDefinition platform,
        string sessionDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        if (dispatcher.CheckAccess())
            return await CreateOnUiThreadAsync(platform, sessionDirectory, cancellationToken);

        return await dispatcher.InvokeAsync(
                () => CreateOnUiThreadAsync(platform, sessionDirectory, cancellationToken))
            .Task
            .Unwrap();
    }

    private static async Task<IManagedLoginWindow> CreateOnUiThreadAsync(
        MediaPlatformDefinition platform,
        string sessionDirectory,
        CancellationToken cancellationToken)
    {
        var window = new ManagedLoginWindow(platform, sessionDirectory);
        try
        {
            await window.InitializeAsync(cancellationToken);
            return window;
        }
        catch
        {
            await window.DisposeAsync();
            throw;
        }
    }
}
