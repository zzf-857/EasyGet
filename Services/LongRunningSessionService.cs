using System.Runtime.InteropServices;

namespace EasyGet.Services;

public sealed class LongRunningSessionService : IDisposable
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private readonly Func<uint, uint> _setExecutionState;
    private bool _isActive;
    private bool _disposed;

    public LongRunningSessionService()
        : this(SetThreadExecutionState)
    {
    }

    internal LongRunningSessionService(Func<uint, uint> setExecutionState)
    {
        _setExecutionState = setExecutionState;
    }

    public bool IsActive => _isActive;

    public void SetActive(bool active)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isActive == active)
            return;

        if (OperatingSystem.IsWindows())
        {
            var result = _setExecutionState(active ? EsContinuous | EsSystemRequired : EsContinuous);
            if (result == 0)
                throw new InvalidOperationException("无法更新 Windows 电源会话状态。");
        }

        _isActive = active;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_isActive && OperatingSystem.IsWindows())
            _ = _setExecutionState(EsContinuous);

        _isActive = false;
        _disposed = true;
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint executionState);
}
