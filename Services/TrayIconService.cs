using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace EasyGet.Services;

public sealed class TrayIconService : IDisposable
{
    private const int WmApp = 0x8000;
    private const int TrayCallbackMessage = WmApp + 1;
    private const int WmContextMenu = 0x007B;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int WmRightButtonUp = 0x0205;
    private const uint NotifyIconAdd = 0x00000000;
    private const uint NotifyIconModify = 0x00000001;
    private const uint NotifyIconDelete = 0x00000002;
    private const uint NotifyIconMessage = 0x00000001;
    private const uint NotifyIconIcon = 0x00000002;
    private const uint NotifyIconTip = 0x00000004;
    private const uint NotifyIconInfo = 0x00000010;
    private const uint NotifyInfo = 0x00000001;
    private const uint NotifyError = 0x00000003;

    private HwndSource? _messageSource;
    private ContextMenu? _contextMenu;
    private IntPtr _iconHandle;
    private bool _ownsIcon;
    private bool _isAdded;
    private bool _disposed;

    public event Action? ShowRequested;
    public event Action? ExitRequested;

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isAdded || !OperatingSystem.IsWindows())
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(Initialize);
            return;
        }

        var parameters = new HwndSourceParameters("EasyGet.TrayIcon")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0
        };
        _messageSource = new HwndSource(parameters);
        _messageSource.AddHook(WindowProc);
        (_iconHandle, _ownsIcon) = LoadApplicationIcon();
        _contextMenu = CreateContextMenu();

        var data = CreateNotifyData(NotifyIconMessage | NotifyIconIcon | NotifyIconTip);
        data.IconHandle = _iconHandle;
        data.Tooltip = TrimBalloonText("EasyGet", 127);
        if (!ShellNotifyIcon(NotifyIconAdd, ref data))
        {
            CleanupResources();
            return;
        }

        _isAdded = true;
    }

    public void ShowNotification(string title, string message, bool isError = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ShowNotification(title, message, isError));
            return;
        }

        Initialize();
        if (!_isAdded)
            return;

        var data = CreateNotifyData(NotifyIconInfo);
        data.InfoTitle = TrimBalloonText(title, 63);
        data.Info = TrimBalloonText(message, 255);
        data.InfoFlags = isError ? NotifyError : NotifyInfo;
        _ = ShellNotifyIcon(NotifyIconModify, ref data);
    }

    internal static string TrimBalloonText(string? value, int maximumLength)
    {
        if (maximumLength < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));

        var text = string.IsNullOrWhiteSpace(value) ? "EasyGet" : value.Trim();
        return text.Length <= maximumLength
            ? text
            : text[..Math.Max(1, maximumLength - 1)] + "…";
    }

    private ContextMenu CreateContextMenu()
    {
        var openItem = new MenuItem { Header = "打开 EasyGet" };
        openItem.Click += (_, _) => ShowRequested?.Invoke();
        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(openItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        return menu;
    }

    private IntPtr WindowProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != TrayCallbackMessage)
            return IntPtr.Zero;

        switch (unchecked((int)lParam.ToInt64()))
        {
            case WmLeftButtonDoubleClick:
                ShowRequested?.Invoke();
                handled = true;
                break;
            case WmContextMenu:
            case WmRightButtonUp:
                if (_contextMenu is not null)
                {
                    _contextMenu.IsOpen = true;
                    handled = true;
                }
                break;
        }

        return IntPtr.Zero;
    }

    private NotifyIconData CreateNotifyData(uint flags)
        => new()
        {
            Size = Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _messageSource?.Handle ?? IntPtr.Zero,
            Identifier = 1,
            Flags = flags,
            CallbackMessage = TrayCallbackMessage
        };

    private static (IntPtr Handle, bool Owned) LoadApplicationIcon()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var smallIcons = new IntPtr[1];
            if (ExtractIconEx(processPath, 0, null, smallIcons, 1) > 0
                && smallIcons[0] != IntPtr.Zero)
            {
                return (smallIcons[0], true);
            }
        }

        return (LoadIcon(IntPtr.Zero, new IntPtr(32512)), false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess() && !dispatcher.HasShutdownStarted)
        {
            dispatcher.Invoke(CleanupResources);
            return;
        }

        CleanupResources();
    }

    private void CleanupResources()
    {
        if (_isAdded)
        {
            var data = CreateNotifyData(0);
            _ = ShellNotifyIcon(NotifyIconDelete, ref data);
            _isAdded = false;
        }

        _contextMenu = null;
        if (_messageSource is not null)
        {
            _messageSource.RemoveHook(WindowProc);
            _messageSource.Dispose();
            _messageSource = null;
        }

        if (_ownsIcon && _iconHandle != IntPtr.Zero)
            _ = DestroyIcon(_iconHandle);
        _iconHandle = IntPtr.Zero;
        _ownsIcon = false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr WindowHandle;
        public uint Identifier;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tooltip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIconHandle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string file,
        int iconIndex,
        IntPtr[]? largeIcons,
        IntPtr[]? smallIcons,
        uint iconCount);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
