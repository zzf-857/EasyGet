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

    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 注册全局异常捕获
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            LogCrash(args.ExceptionObject as Exception, "AppDomain.UnhandledException");
        
        DispatcherUnhandledException += (s, args) =>
        {
            LogCrash(args.Exception, "DispatcherUnhandledException");
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

    private void LogCrash(Exception? ex, string source)
    {
        var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var message = $"[{DateTime.Now}] {source}:\n{ex?.ToString() ?? "Unknown Error"}\n\n";
        File.AppendAllText(logFile, message);
        System.Windows.MessageBox.Show($"Application Crashed: {ex?.Message}\nCheck logs at {logFile}", "EasyGet Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
