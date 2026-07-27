using System;
using System.Timers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EasyGet.ViewModels;

public enum NotificationKind
{
    Success,
    Info,
    Failure
}

public partial class NotificationItem : ObservableObject
{
    private readonly System.Timers.Timer _timer;
    private readonly bool _autoDismiss;
    private readonly Action? _action;
    private readonly double _totalMs;
    private double _remainingMs;
    private const double IntervalMs = 50;

    private readonly object _lock = new();
    private bool _isDisposed;

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private double _remainingRatio = 1.0;
    public NotificationKind Kind { get; }
    public bool IsSuccess => Kind == NotificationKind.Success;
    public bool IsInfo => Kind == NotificationKind.Info;
    public bool IsFailure => Kind == NotificationKind.Failure;
    public TimeSpan? AutoDismissAfter { get; }
    public string ActionLabel { get; }
    public bool HasAction => _action is not null && !string.IsNullOrWhiteSpace(ActionLabel);

    public event Action<NotificationItem>? Expired;
    public event Action<NotificationItem>? Closed;

    public NotificationItem(string message, bool isSuccess, string? actionLabel = null, Action? action = null)
        : this(
            message,
            isSuccess ? NotificationKind.Success : NotificationKind.Failure,
            actionLabel,
            action)
    {
    }

    public NotificationItem(
        string message,
        NotificationKind kind,
        string? actionLabel = null,
        Action? action = null)
    {
        Message = message;
        Kind = kind;
        ActionLabel = actionLabel ?? "";
        _action = action;
        AutoDismissAfter = kind switch
        {
            NotificationKind.Success => TimeSpan.FromSeconds(4),
            NotificationKind.Info => TimeSpan.FromSeconds(5),
            _ => null
        };
        _autoDismiss = AutoDismissAfter.HasValue;
        _totalMs = AutoDismissAfter?.TotalMilliseconds ?? 0;
        _remainingMs = _totalMs;

        _timer = new System.Timers.Timer(IntervalMs);
        _timer.Elapsed += OnTimerElapsed;
        if (_autoDismiss)
            _timer.Start();
    }

    private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        lock (_lock)
        {
            if (_isDisposed) return;
        }

        _remainingMs -= IntervalMs;
        if (_remainingMs <= 0)
        {
            lock (_lock)
            {
                if (!_isDisposed)
                {
                    _isDisposed = true;
                    _timer.Stop();
                    _timer.Dispose();
                }
            }
            RemainingRatio = 0;
            Expired?.Invoke(this);
        }
        else
        {
            RemainingRatio = _remainingMs / _totalMs;
        }
    }

    [RelayCommand]
    public void Close()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _timer.Stop();
            _timer.Dispose();
        }
        Closed?.Invoke(this);
    }

    [RelayCommand]
    private void ExecuteAction()
    {
        _action?.Invoke();
        Close();
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _timer.Stop();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_isDisposed || !_autoDismiss) return;
            _timer.Start();
        }
    }
}
