using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using EasyGet.Services;
using EasyGet.ViewModels;

namespace EasyGet;

public partial class MainWindow : Window
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly MainViewModel _viewModel;
    private readonly ConfigService _configService;

    public MainWindow(MainViewModel viewModel, ConfigService configService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _configService = configService;
        DataContext = _viewModel;

        SourceInitialized += (_, _) => TryEnableDarkSystemTitleBar();

        Loaded += async (_, _) =>
        {
            await _viewModel.InitializeAsync();
            RestoreWindowState();
        };

        Closing += async (_, _) =>
        {
            SaveWindowState();
            await _configService.SaveAsync();
        };
    }

    private void TryEnableDarkSystemTitleBar()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        var useDarkMode = 1;
        try
        {
            _ = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private void RestoreWindowState()
    {
        var ws = _configService.Config.Window;
        if (!double.IsNaN(ws.Left) && !double.IsNaN(ws.Top))
        {
            Left = ws.Left;
            Top = ws.Top;
        }
        if (ws.Width > 0) Width = ws.Width;
        if (ws.Height > 0) Height = ws.Height;
    }

    private void SaveWindowState()
    {
        if (WindowState == System.Windows.WindowState.Normal)
        {
            _configService.Config.Window.Left = Left;
            _configService.Config.Window.Top = Top;
            _configService.Config.Window.Width = Width;
            _configService.Config.Window.Height = Height;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
}
