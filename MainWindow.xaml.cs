using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using EasyGet.Services;
using EasyGet.ViewModels;
using EasyGet.Views;

namespace EasyGet;

public partial class MainWindow : Window
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly MainViewModel _viewModel;
    private readonly ConfigService _configService;
    private readonly WindowPlacementManager _windowPlacement;
    private readonly TrayIconService? _trayIconService;
    private bool _closeInProgress;
    private bool _closeCommitted;

    public MainWindow(
        MainViewModel viewModel,
        ConfigService configService,
        TrayIconService? trayIconService = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _configService = configService;
        _trayIconService = trayIconService;
        DataContext = _viewModel;
        _windowPlacement = new WindowPlacementManager(this, _configService.Config.Window);
        _windowPlacement.PrepareInitialBounds();

        SourceInitialized += MainWindow_SourceInitialized;

        Loaded += async (_, _) =>
        {
            await InitializeLoadedServicesAsync(
                _viewModel.InitializeAsync,
                _trayIconService is null ? null : () => _trayIconService.TryInitialize());
        };

        Closing += MainWindow_Closing;
        Closed += (_, _) => _windowPlacement.Dispose();

        Activated += MainWindow_Activated;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        SizeChanged += (_, _) => _viewModel.IsCompactLayout = ActualWidth < 1280;
        StateChanged += MainWindow_StateChanged;
        if (_trayIconService is not null)
        {
            _trayIconService.ShowRequested += RestoreFromTray;
            _trayIconService.ExitRequested += ExitFromTray;
        }
        _viewModel.IsCompactLayout = Width < 1280;
    }

    internal static async Task InitializeLoadedServicesAsync(
        Func<Task> initializeMainViewModel,
        Func<bool>? tryInitializeTrayIcon)
    {
        ArgumentNullException.ThrowIfNull(initializeMainViewModel);

        await initializeMainViewModel();
        if (tryInitializeTrayIcon is null)
            return;

        try
        {
            _ = tryInitializeTrayIcon();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] Tray initialization was skipped: {ex.Message}");
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        TryEnableDarkSystemTitleBar();
        _windowPlacement.InitializeSource();
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeCommitted)
            return;

        e.Cancel = true;
        if (_closeInProgress)
            return;

        _closeInProgress = true;
        try
        {
            SaveWindowState();
            var settingsSaved = await _viewModel.SettingsVM.FlushPendingSaveAsync();
            var configSaved = await _configService.SaveAsync();
            if (!settingsSaved || !configSaved)
            {
                System.Windows.MessageBox.Show(
                    this,
                    "设置保存失败，EasyGet 暂未退出。请检查配置目录权限后重试。",
                    "EasyGet",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                _closeInProgress = false;
                return;
            }

            _closeCommitted = true;
            Close();
        }
        catch (Exception)
        {
            _closeInProgress = false;
            System.Windows.MessageBox.Show(
                this,
                "设置保存时发生异常，EasyGet 暂未退出，请重试。",
                "EasyGet",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 1. Ctrl + 1~4
        var navigationPage = Keyboard.Modifiers == ModifierKeys.Control
            ? ResolveNavigationShortcut(e.Key)
            : null;
        if (navigationPage is not null)
        {
            if (_viewModel.NavigateCommand.CanExecute(navigationPage))
            {
                _viewModel.NavigateCommand.Execute(navigationPage);
            }
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control
            && e.Key == Key.F
            && _viewModel.SelectedNavIndex == 2)
        {
            FindVisualChild<HistoryView>(this)?.FocusSearch();
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

            if (_viewModel.SelectedNavIndex == 2
                && _viewModel.HistoryVM.ClearSelectionCommand.CanExecute(null))
            {
                _viewModel.HistoryVM.ClearSelectionCommand.Execute(null);
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

        if (Keyboard.Modifiers == ModifierKeys.None
            && _viewModel.SelectedNavIndex == 1
            && e.Key is Key.Space or Key.Delete
            && Keyboard.FocusedElement is not System.Windows.Controls.TextBox
            && FindVisualChild<BatchDownloadView>(this)?.TryHandleQueueShortcut(e.Key) == true)
        {
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None
            && _viewModel.SelectedNavIndex == 2
            && e.Key == Key.Delete
            && Keyboard.FocusedElement is not System.Windows.Controls.TextBox
            && _viewModel.HistoryVM.DeleteSelectedCommand.CanExecute(null))
        {
            _viewModel.HistoryVM.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
            return;
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

    internal static string? ResolveNavigationShortcut(Key key)
        => key switch
        {
            Key.D1 => "download",
            Key.D2 => "batch",
            Key.D3 => "history",
            Key.D4 => "settings",
            _ => null
        };

    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        try
        {
            if (_viewModel.SettingsVM.ClipboardMonitoringEnabled
                && System.Windows.Clipboard.ContainsText())
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

    private static T? FindVisualChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                return match;

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
                return descendant;
        }

        return null;
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

    private void SaveWindowState()
        => _windowPlacement.Save();

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
        if (_configService.Config.MinimizeToTray
            && _trayIconService?.TryInitialize() == true)
        {
            Hide();
            _trayIconService.ShowNotification("EasyGet", "EasyGet 正在系统托盘中运行。");
            return;
        }

        WindowState = System.Windows.WindowState.Minimized;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == System.Windows.WindowState.Minimized
            && _configService.Config.MinimizeToTray
            && _trayIconService?.TryInitialize() == true)
        {
            Hide();
        }
    }

    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = System.Windows.WindowState.Normal;
            Activate();
        });
    }

    private void ExitFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            Close();
        });
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
