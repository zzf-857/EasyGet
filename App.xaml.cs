using System.Windows;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using EasyGet.Services;
using EasyGet.Services.Cookies;
using EasyGet.ViewModels;

namespace EasyGet;

/// <summary>
/// App 启动入口 — 配置依赖注入
/// </summary>
public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;
    private readonly ExceptionNotificationThrottle _exceptionNotifications =
        new(TimeSpan.FromMinutes(1));

    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 注册全局异常捕获
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            LogCrash(args.ExceptionObject as Exception, "AppDomain.UnhandledException");

        DispatcherUnhandledException += (s, args) =>
        {
            var source = "DispatcherUnhandledException";
            var showDialog = _exceptionNotifications.ShouldNotify(
                args.Exception,
                source,
                DateTime.UtcNow);
            LogCrash(
                args.Exception,
                source,
                showDialog,
                isFatal: false);
            args.Handled = true; // 防止立即崩溃，尝试优雅退出
        };

        base.OnStartup(e);

        CookieFileLease.CleanupStaleFiles(
            CookieFileLease.DefaultTemporaryDirectory,
            DateTime.UtcNow,
            TimeSpan.FromDays(1));

        try
        {
            var services = new ServiceCollection();

            // 服务层
            services.AddSingleton<ConfigService>();
            services.AddSingleton<EnvironmentService>();
            services.AddSingleton<HistoryService>();
            services.AddSingleton<IBrowserProfileDiscoveryService, BrowserProfileDiscoveryService>();
            services.AddSingleton<IBrowserCookieLoginDetector, BrowserCookieLoginDetector>();
            services.AddSingleton<IDefaultBrowserLauncher, DefaultBrowserLauncher>();
            services.AddSingleton<PlatformCookieVault>(provider =>
                new PlatformCookieVault(
                    provider.GetRequiredService<ConfigService>().ConfigDirectory));
            services.AddSingleton<ICookieHealthStore>(provider =>
                new CookieHealthStore(
                    provider.GetRequiredService<ConfigService>().ConfigDirectory));
            services.AddSingleton<IManagedLoginWindowFactory, ManagedLoginWindowFactory>();
            services.AddSingleton<IManagedLoginSessionService>(provider =>
                new ManagedLoginSessionService(
                    provider.GetRequiredService<IManagedLoginWindowFactory>(),
                    Path.Combine(
                        provider.GetRequiredService<ConfigService>().ConfigDirectory,
                        "sessions")));
            services.AddSingleton<CookieAcquisitionCoordinator>(provider =>
                new CookieAcquisitionCoordinator(
                    provider.GetRequiredService<ConfigService>(),
                    provider.GetRequiredService<PlatformCookieVault>(),
                    provider.GetRequiredService<IBrowserProfileDiscoveryService>(),
                    provider.GetRequiredService<ICookieHealthStore>(),
                    provider.GetRequiredService<IManagedLoginSessionService>(),
                    CookieFileLease.DefaultTemporaryDirectory,
                    provider.GetRequiredService<IBrowserCookieLoginDetector>()));
            services.AddSingleton<IYangshipinDownloadService, YangshipinDownloadService>();
            services.AddSingleton<YtDlpService>();
            services.AddSingleton<M3u8DownloadService>();
            services.AddSingleton<TelegramDownloadService>();
            services.AddSingleton<IAppUpdateService, AppUpdateService>();
            services.AddSingleton<IVideoInfoProvider, YtDlpVideoInfoProvider>();
            services.AddSingleton<DownloadManager>();

            // ViewModel 层
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<DownloadViewModel>();
            services.AddSingleton<BatchDownloadViewModel>();
            services.AddSingleton<HistoryViewModel>();
            services.AddSingleton<SettingsViewModel>();

            // 主窗口
            services.AddSingleton<MainWindow>();

            _serviceProvider = services.BuildServiceProvider();
            Services = _serviceProvider;

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            LogCrash(ex, "Startup Error");
            Shutdown();
        }
    }

    private void LogCrash(
        Exception? ex,
        string source,
        bool showDialog = true,
        bool isFatal = true)
    {
        string? logFile = null;
        try
        {
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            logFile = Path.Combine(logDir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var details = showDialog
                ? ex?.ToString() ?? "Unknown Error"
                : $"Duplicate dialog suppressed: {ex?.GetType().FullName}: {ex?.Message}";
            File.AppendAllText(logFile, $"[{DateTime.Now}] {source}:\n{details}\n\n");
        }
        catch (Exception logError)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EasyGet] Failed to write crash log: {logError.Message}");
        }

        if (!showDialog)
            return;

        var heading = isFatal
            ? "程序发生严重错误："
            : "界面发生异常，EasyGet 已阻止程序退出：";
        var logHint = logFile is null
            ? "错误日志写入失败。"
            : $"错误日志：{logFile}";
        System.Windows.MessageBox.Show(
            $"{heading}\n{ex?.Message ?? "未知错误"}\n{logHint}",
            "EasyGet 错误",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
