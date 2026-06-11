using System;
using System.Timers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EasyGet.ViewModels;

public partial class NotificationItem : ObservableObject
{
    private readonly System.Timers.Timer _timer;
    private double _remainingMs = 4000;
    private const double TotalMs = 4000;
    private const double IntervalMs = 50;

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isSuccess;
    [ObservableProperty] private double _remainingRatio = 1.0;

    public event Action<NotificationItem>? Expired;
    public event Action<NotificationItem>? Closed;

    public NotificationItem(string message, bool isSuccess)
    {
        Message = message;
        IsSuccess = isSuccess;

        _timer = new System.Timers.Timer(IntervalMs);
        _timer.Elapsed += OnTimerElapsed;
        _timer.Start();
    }

    private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        _remainingMs -= IntervalMs;
        if (_remainingMs <= 0)
        {
            _timer.Stop();
            _timer.Dispose();
            RemainingRatio = 0;
            Expired?.Invoke(this);
        }
        else
        {
            RemainingRatio = _remainingMs / TotalMs;
        }
    }

    [RelayCommand]
    public void Close()
    {
        _timer.Stop();
        _timer.Dispose();
        Closed?.Invoke(this);
    }

    public void Pause()
    {
        _timer.Stop();
    }

    public void Resume()
    {
        _timer.Start();
    }
}
