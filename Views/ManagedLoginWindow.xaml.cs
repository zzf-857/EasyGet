using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using EasyGet.Services.Cookies;
using Microsoft.Web.WebView2.Core;

namespace EasyGet.Views;

public partial class ManagedLoginWindow : Window, IManagedLoginWindow
{
    private readonly MediaPlatformDefinition _platform;
    private readonly string _sessionDirectory;
    private TaskCompletionSource<IReadOnlyList<BrowserCookie>>? _loginCompletion;
    private bool _disposed;
    private bool _isClosed;

    internal ManagedLoginWindow(
        MediaPlatformDefinition platform,
        string sessionDirectory)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        _platform = platform;
        _sessionDirectory = sessionDirectory;
        WindowHeading = $"登录 {platform.DisplayName}";
        AllowedDomainsText = string.Join("、", platform.CookieDomains);
        Title = $"{WindowHeading} - EasyGet";
        DataContext = this;
        InitializeComponent();
        Closing += OnClosing;
        Closed += (_, _) => _isClosed = true;
    }

    public string WindowHeading { get; }
    public string AllowedDomainsText { get; }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_sessionDirectory);
        _ = new WindowInteropHelper(this).EnsureHandle();

        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _sessionDirectory,
                options: null);
            await Browser.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            throw new InvalidOperationException(
                "未检测到 Microsoft Edge WebView2 Runtime，请安装 WebView2 运行时后重试智能登录。",
                ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
        Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        Browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
        Browser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
    }

    public Task<IReadOnlyList<BrowserCookie>> ReadCookiesAsync(
        IReadOnlyList<string> allowedDomains,
        CancellationToken cancellationToken)
        => Dispatcher.InvokeAsync(
                () => ReadCookiesOnUiThreadAsync(allowedDomains, cancellationToken))
            .Task
            .Unwrap();

    public async Task<IReadOnlyList<BrowserCookie>> ShowForLoginAsync(
        IReadOnlyList<string> allowedDomains,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(allowedDomains);
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<IReadOnlyList<BrowserCookie>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _loginCompletion = completion;

        async void CompleteLogin()
        {
            try
            {
                var cookies = await ReadCookiesOnUiThreadAsync(
                    allowedDomains,
                    CancellationToken.None);
                if (cookies.Count == 0)
                {
                    StatusText.Text = "尚未检测到登录状态，请确认登录完成后再继续。";
                    return;
                }

                completion.TrySetResult(cookies);
                CloseSafely();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"读取登录状态失败：{ex.Message}";
            }
        }

        void CancelLogin()
        {
            completion.TrySetResult([]);
            CloseSafely();
        }

        // Local callbacks are assigned for the button handlers while this login request is active.
        _completeLogin = CompleteLogin;
        _cancelLogin = CancelLogin;

        await Dispatcher.InvokeAsync(() =>
        {
            StatusText.Text = "请在上方网页完成登录，然后点击继续。";
            Browser.Source = _platform.LoginUri;
            Show();
            Activate();
        });

        using var registration = cancellationToken.Register(() =>
            Dispatcher.BeginInvoke(() =>
            {
                completion.TrySetCanceled(cancellationToken);
                CloseSafely();
            }));
        return await completion.Task.WaitAsync(cancellationToken);
    }

    private Action? _completeLogin;
    private Action? _cancelLogin;

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
        => _completeLogin?.Invoke();

    private void CancelButton_Click(object sender, RoutedEventArgs e)
        => (_cancelLogin ?? CancelPendingLogin).Invoke();

    private async Task<IReadOnlyList<BrowserCookie>> ReadCookiesOnUiThreadAsync(
        IReadOnlyList<string> allowedDomains,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(allowedDomains);
        cancellationToken.ThrowIfCancellationRequested();
        if (Browser.CoreWebView2 is null)
            throw new InvalidOperationException("智能登录浏览器尚未初始化。重试后如果仍失败，请安装 WebView2 Runtime。");

        var cookies = new Dictionary<string, BrowserCookie>(StringComparer.Ordinal);
        foreach (var domain in allowedDomains)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedDomain = domain.Trim().TrimStart('.');
            if (normalizedDomain.Length == 0)
                continue;

            var stored = await Browser.CoreWebView2.CookieManager.GetCookiesAsync(
                $"https://{normalizedDomain}/");
            foreach (var cookie in stored)
            {
                var mapped = new BrowserCookie(
                    cookie.Domain,
                    string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                    cookie.Name,
                    cookie.Value,
                    cookie.IsSecure,
                    ToUnixExpiry(cookie.Expires, cookie.IsSession));
                cookies[$"{mapped.Domain}\n{mapped.Path}\n{mapped.Name}"] = mapped;
            }
        }

        return cookies.Values.ToArray();
    }

    private static long ToUnixExpiry(DateTime expires, bool isSession)
    {
        if (isSession)
            return 0;

        try
        {
            var value = new DateTimeOffset(expires).ToUnixTimeSeconds();
            return Math.Max(0, value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
        => _loginCompletion?.TrySetResult([]);

    private void CancelPendingLogin()
    {
        _loginCompletion?.TrySetResult([]);
        CloseSafely();
    }

    private void CloseSafely()
    {
        if (_isClosed)
            return;

        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
            // The window is already in its closing transition.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await Dispatcher.InvokeAsync(() =>
        {
            _loginCompletion?.TrySetResult([]);
            _completeLogin = null;
            _cancelLogin = null;
            Browser.Dispose();
            CloseSafely();
        });
    }
}
