using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>
/// Restores and validates the main window against the current native monitor topology.
/// </summary>
internal sealed class WindowPlacementManager : IDisposable
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DPICHANGED = 0x02E0;
    private const int SPI_SETWORKAREA = 0x002F;
    private const int MaxTopologyValidationRetries = 3;
    private const uint SW_SHOWNORMAL = 1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint MONITORINFOF_PRIMARY = 0x0001;

    private const double SidebarWidth = 216;
    private const double WindowControlsWidth = 120;
    private const double TitleBarHeight = 46;
    private const double MinimumVisibleTitleBarWidth = 96;
    private const double MinimumVisibleTitleBarHeight = 24;

    private static readonly TimeSpan TopologyValidationDelay = TimeSpan.FromMilliseconds(250);

    private readonly Window _window;
    private readonly Models.WindowState _state;
    private readonly DispatcherTimer _validationTimer;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _fitOversizedBoundsOnNextValidation;
    private bool _resizeOnNextValidation;
    private int _remainingValidationRetries;
    private bool _disposed;

    public WindowPlacementManager(Window window, Models.WindowState state)
    {
        _window = window;
        _state = state;
        _validationTimer = new DispatcherTimer(
            DispatcherPriority.ApplicationIdle,
            window.Dispatcher)
        {
            Interval = TopologyValidationDelay
        };
        _validationTimer.Tick += ValidationTimer_Tick;
    }

    public void PrepareInitialBounds()
    {
        _window.Width = _state.Width;
        _window.Height = _state.Height;

        if (!HasValidNativePlacement(_state.NativePlacement)
            && double.IsFinite(_state.Left)
            && double.IsFinite(_state.Top))
        {
            _window.Left = _state.Left;
            _window.Top = _state.Top;
            return;
        }

        var workArea = SystemParameters.WorkArea;
        _window.Left = workArea.Left + Math.Max(0, (workArea.Width - _window.Width) / 2);
        _window.Top = workArea.Top + Math.Max(0, (workArea.Height - _window.Height) / 2);
    }

    public void InitializeSource()
    {
        _window.Dispatcher.VerifyAccess();
        if (_disposed || _source is not null)
            return;

        _handle = new WindowInteropHelper(_window).Handle;
        if (_handle == IntPtr.Zero)
            return;

        _ = ApplyStoredNativePlacement();

        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowProc);
        _window.StateChanged += Window_StateChanged;
        _window.Activated += Window_Activated;

        if (!EnsureVisibleOnCurrentDisplays(
                resizeImmediately: true,
                fitOversizedBounds: true))
        {
            ScheduleValidation(fitOversizedBounds: true);
        }
    }

    public void Save()
    {
        _window.Dispatcher.VerifyAccess();

        var restoreBounds = _window.RestoreBounds;
        if (IsUsableBounds(restoreBounds))
        {
            _state.Left = restoreBounds.Left;
            _state.Top = restoreBounds.Top;
            _state.Width = restoreBounds.Width;
            _state.Height = restoreBounds.Height;
        }

        if (_handle == IntPtr.Zero)
            return;

        var placement = CreateWindowPlacement();
        if (!GetWindowPlacement(_handle, ref placement)
            || !IsUsableNativeRect(placement.NormalPosition))
        {
            return;
        }

        _state.NativePlacement = new NativeWindowPlacement
        {
            Left = placement.NormalPosition.Left,
            Top = placement.NormalPosition.Top,
            Right = placement.NormalPosition.Right,
            Bottom = placement.NormalPosition.Bottom
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _validationTimer.Stop();
        _validationTimer.Tick -= ValidationTimer_Tick;
        _window.StateChanged -= Window_StateChanged;
        _window.Activated -= Window_Activated;
        _source?.RemoveHook(WindowProc);
        _source = null;
        _handle = IntPtr.Zero;
    }

    internal static Rect EnsureRestoredBoundsVisible(
        Rect requestedBounds,
        IReadOnlyList<Rect> workAreas,
        Rect fallbackWorkArea,
        double dpiScale = 1,
        bool fitOversizedBounds = false)
    {
        ArgumentNullException.ThrowIfNull(workAreas);

        var validWorkAreas = workAreas.Where(IsUsableBounds).ToArray();
        if (validWorkAreas.Length == 0)
            return requestedBounds;

        if (!IsUsableBounds(fallbackWorkArea))
            fallbackWorkArea = validWorkAreas[0];

        dpiScale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        if (IsUsableBounds(requestedBounds))
        {
            var leftInset = Math.Min(requestedBounds.Width, SidebarWidth * dpiScale);
            var rightInset = Math.Min(
                Math.Max(0, requestedBounds.Width - leftInset),
                WindowControlsWidth * dpiScale);
            var draggableWidth = Math.Max(
                0,
                requestedBounds.Width - leftInset - rightInset);
            var draggableTitleBar = new Rect(
                requestedBounds.Left + leftInset,
                requestedBounds.Top,
                draggableWidth,
                Math.Min(requestedBounds.Height, TitleBarHeight * dpiScale));

            foreach (var workArea in validWorkAreas)
            {
                var visibleTitleBar = Rect.Intersect(draggableTitleBar, workArea);
                if (!visibleTitleBar.IsEmpty
                    && visibleTitleBar.Width >= MinimumVisibleTitleBarWidth * dpiScale
                    && visibleTitleBar.Height >= MinimumVisibleTitleBarHeight * dpiScale)
                {
                    return fitOversizedBounds
                           && (requestedBounds.Width > workArea.Width
                               || requestedBounds.Height > workArea.Height)
                        ? FitAndCenter(requestedBounds, workArea, dpiScale)
                        : requestedBounds;
                }
            }
        }

        return FitAndCenter(requestedBounds, fallbackWorkArea, dpiScale);
    }

    private static Rect FitAndCenter(
        Rect requestedBounds,
        Rect workArea,
        double dpiScale)
    {
        var requestedWidth = IsFinitePositive(requestedBounds.Width)
            ? requestedBounds.Width
            : Models.WindowState.DefaultWidth * dpiScale;
        var requestedHeight = IsFinitePositive(requestedBounds.Height)
            ? requestedBounds.Height
            : Models.WindowState.DefaultHeight * dpiScale;
        var width = Math.Min(requestedWidth, workArea.Width);
        var height = Math.Min(requestedHeight, workArea.Height);
        var left = workArea.Left + Math.Max(0, (workArea.Width - width) / 2);
        var top = workArea.Top + Math.Max(0, (workArea.Height - height) / 2);
        return new Rect(left, top, width, height);
    }

    private bool ApplyStoredNativePlacement()
    {
        if (!HasValidNativePlacement(_state.NativePlacement))
            return false;

        var saved = _state.NativePlacement!;
        var placement = CreateWindowPlacement();
        if (!GetWindowPlacement(_handle, ref placement))
            return false;

        placement.Flags = 0;
        placement.ShowCommand = SW_SHOWNORMAL;
        placement.NormalPosition = new NativeRect
        {
            Left = saved.Left,
            Top = saved.Top,
            Right = saved.Right,
            Bottom = saved.Bottom
        };
        return SetWindowPlacement(_handle, ref placement);
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (_window.WindowState == System.Windows.WindowState.Normal)
            ScheduleValidation();
    }

    private void Window_Activated(object? sender, EventArgs e)
        => ScheduleValidation();

    private IntPtr WindowProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WM_DISPLAYCHANGE
            || message == WM_DPICHANGED
            || (message == WM_SETTINGCHANGE && wParam.ToInt64() == SPI_SETWORKAREA))
        {
            ScheduleValidation(fitOversizedBounds: true);
        }

        return IntPtr.Zero;
    }

    private void ScheduleValidation(
        bool fitOversizedBounds = false,
        bool resetRetryBudget = true)
    {
        if (_disposed)
            return;

        _window.Dispatcher.VerifyAccess();
        _fitOversizedBoundsOnNextValidation |= fitOversizedBounds;
        if (resetRetryBudget)
            _remainingValidationRetries = MaxTopologyValidationRetries;
        _validationTimer.Stop();
        _validationTimer.Start();
    }

    private void ValidationTimer_Tick(object? sender, EventArgs e)
    {
        _validationTimer.Stop();
        if (_window.WindowState != System.Windows.WindowState.Normal)
            return;

        var fitOversizedBounds = _fitOversizedBoundsOnNextValidation;
        var resizeImmediately = _resizeOnNextValidation;
        _fitOversizedBoundsOnNextValidation = false;
        _resizeOnNextValidation = false;
        if (!EnsureVisibleOnCurrentDisplays(
                resizeImmediately,
                fitOversizedBounds || resizeImmediately))
        {
            _fitOversizedBoundsOnNextValidation |= fitOversizedBounds;
            _resizeOnNextValidation |= resizeImmediately;
            if (_remainingValidationRetries > 0)
            {
                _remainingValidationRetries--;
                ScheduleValidation(resetRetryBudget: false);
            }
        }
    }

    private bool EnsureVisibleOnCurrentDisplays(
        bool resizeImmediately = false,
        bool fitOversizedBounds = false)
    {
        _window.Dispatcher.VerifyAccess();
        if (_disposed
            || _handle == IntPtr.Zero
            || _window.WindowState != System.Windows.WindowState.Normal
            || !GetWindowRect(_handle, out var windowRect)
            || !TryGetMonitorWorkAreas(out var workAreas, out var primaryWorkArea))
        {
            return false;
        }

        var requestedBounds = windowRect.ToRect();
        var restoredBounds = EnsureRestoredBoundsVisible(
            requestedBounds,
            workAreas,
            primaryWorkArea,
            GetWindowDpiScale(),
            fitOversizedBounds);
        if (requestedBounds == restoredBounds)
            return true;

        var requiresResize = requestedBounds.Width != restoredBounds.Width
                             || requestedBounds.Height != restoredBounds.Height;
        var flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        if (!resizeImmediately)
            flags |= SWP_NOSIZE;

        var repositioned = SetWindowPos(
            _handle,
            IntPtr.Zero,
            (int)Math.Round(restoredBounds.Left),
            (int)Math.Round(restoredBounds.Top),
            Math.Max(1, (int)Math.Round(restoredBounds.Width)),
            Math.Max(1, (int)Math.Round(restoredBounds.Height)),
            flags);
        if (!repositioned)
            return false;

        if (!resizeImmediately && requiresResize)
        {
            _fitOversizedBoundsOnNextValidation = true;
            _resizeOnNextValidation = true;
            ScheduleValidation(resetRetryBudget: false);
        }

        return true;
    }

    private double GetWindowDpiScale()
    {
        try
        {
            var dpi = GetDpiForWindow(_handle);
            return dpi > 0 ? dpi / 96d : 1;
        }
        catch (EntryPointNotFoundException)
        {
            return 1;
        }
    }

    private static bool TryGetMonitorWorkAreas(
        out IReadOnlyList<Rect> workAreas,
        out Rect primaryWorkArea)
    {
        var discovered = new List<(Rect WorkArea, bool IsPrimary)>();
        MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            var info = new MonitorInfo
            {
                Size = (uint)Marshal.SizeOf<MonitorInfo>()
            };
            if (GetMonitorInfo(monitor, ref info))
            {
                discovered.Add((
                    info.WorkArea.ToRect(),
                    (info.Flags & MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        };

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)
            || discovered.Count == 0)
        {
            workAreas = Array.Empty<Rect>();
            primaryWorkArea = Rect.Empty;
            return false;
        }

        workAreas = discovered.Select(item => item.WorkArea).ToArray();
        primaryWorkArea = discovered.FirstOrDefault(item => item.IsPrimary).WorkArea;
        if (!IsUsableBounds(primaryWorkArea))
            primaryWorkArea = discovered[0].WorkArea;
        return true;
    }

    private static bool HasValidNativePlacement(NativeWindowPlacement? placement)
        => placement is not null
           && (long)placement.Right - placement.Left > 0
           && (long)placement.Bottom - placement.Top > 0;

    private static bool IsUsableNativeRect(NativeRect rect)
        => (long)rect.Right - rect.Left > 0
           && (long)rect.Bottom - rect.Top > 0;

    private static bool IsUsableBounds(Rect bounds)
        => double.IsFinite(bounds.Left)
           && double.IsFinite(bounds.Top)
           && IsFinitePositive(bounds.Width)
           && IsFinitePositive(bounds.Height);

    private static bool IsFinitePositive(double value)
        => double.IsFinite(value) && value > 0;

    private static NativeWindowPlacementData CreateWindowPlacement()
        => new()
        {
            Length = (uint)Marshal.SizeOf<NativeWindowPlacementData>()
        };

    private delegate bool MonitorEnumProc(
        IntPtr monitor,
        IntPtr monitorDc,
        IntPtr monitorRect,
        IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly Rect ToRect()
            => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPlacementData
    {
        public uint Length;
        public uint Flags;
        public uint ShowCommand;
        public NativePoint MinimumPosition;
        public NativePoint MaximumPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRect,
        MonitorEnumProc callback,
        IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(
        IntPtr window,
        ref NativeWindowPlacementData placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(
        IntPtr window,
        ref NativeWindowPlacementData placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
