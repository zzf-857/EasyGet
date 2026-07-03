using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
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

        Activated += MainWindow_Activated;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 1. Ctrl + 1~5
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D5)
        {
            int index = e.Key - Key.D1;
            string page = index switch
            {
                0 => "download",
                1 => "batch",
                2 => "douyin",
                3 => "history",
                4 => "settings",
                _ => "download"
            };
            if (_viewModel.NavigateCommand.CanExecute(page))
            {
                _viewModel.NavigateCommand.Execute(page);
            }
            e.Handled = true;
            return;
        }

        // 2. Escape
        if (e.Key == Key.Escape)
        {
            if (_viewModel.DownloadVM.IsParsing)
            {
                if (_viewModel.DownloadVM.CancelParseCommand.CanExecute(null))
                {
                    _viewModel.DownloadVM.CancelParseCommand.Execute(null);
                }
                e.Handled = true;
                return;
            }

            if (_viewModel.Notifications.Count > 0)
            {
                if (_viewModel.DismissNotificationCommand.CanExecute(null))
                {
                    _viewModel.DismissNotificationCommand.Execute(null);
                }
                e.Handled = true;
                return;
            }
        }

        // 3. Ctrl + V (when focus is NOT in a TextBox)
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            var focused = Keyboard.FocusedElement;
            if (focused is not System.Windows.Controls.TextBox)
            {
                // Switch to download tab first
                if (_viewModel.NavigateCommand.CanExecute("download"))
                {
                    _viewModel.NavigateCommand.Execute("download");
                }

                // Read clipboard and parse
                try
                {
                    if (System.Windows.Clipboard.ContainsText())
                    {
                        var text = System.Windows.Clipboard.GetText().Trim();
                        var extracted = DownloadViewModel.ExtractUrl(text);
                        if (!string.IsNullOrWhiteSpace(extracted))
                        {
                            _viewModel.DownloadVM.Url = extracted;
                            if (_viewModel.DownloadVM.ParseCommand.CanExecute(null))
                            {
                                _viewModel.DownloadVM.ParseCommand.Execute(null);
                            }
                        }
                    }
                }
                catch (COMException)
                {
                    // Safe clipboard reading
                }
                catch (Exception)
                {
                    // Safety net
                }

                e.Handled = true;
                return;
            }
        }
    }

    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        try
        {
            if (_viewModel.SelectedNavIndex == 0 && System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText();
                _viewModel.DownloadVM.CheckClipboardAndPrompt(text);
            }
        }
        catch (COMException)
        {
            // Ignore clipboard access errors if other processes occupy it
        }
        catch (Exception)
        {
            // General safety net
        }
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

    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = System.Windows.WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == System.Windows.WindowState.Maximized
            ? System.Windows.WindowState.Normal
            : System.Windows.WindowState.Maximized;
    }

    private void ToastCard_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is NotificationItem item)
        {
            item.Pause();
        }
    }

    private void ToastCard_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is NotificationItem item)
        {
            item.Resume();
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
}
