namespace EasyGet.Services;

internal enum SettingsSaveIntent
{
    Automatic,
    Explicit
}

/// <summary>
/// Coalesces setting edits and serializes writes. A successful write only acknowledges
/// the edits requested before that write, so changes made during persistence are not lost.
/// The persistence callback runs on the caller's synchronization context.
/// </summary>
internal sealed class SettingsSaveCoordinator(
    Func<SettingsSaveIntent, Task<bool>> persist,
    TimeSpan debounceDelay)
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _debounce;
    private Task _pendingSave = Task.CompletedTask;
    private long _requestedVersion;
    private long _persistedVersion;

    public void RequestAutoSave()
    {
        CancellationTokenSource? previous;
        lock (_stateGate)
        {
            var version = ++_requestedVersion;
            previous = _debounce;
            var debounce = new CancellationTokenSource();
            _debounce = debounce;
            _pendingSave = RunAutoSaveAsync(version, debounce);
        }
        TryCancel(previous);
    }

    public async Task SaveExplicitAsync()
    {
        var (version, debounce, _) = GetPendingSave();
        TryCancel(debounce);
        if (await SaveAsync(SettingsSaveIntent.Explicit))
            MarkPersisted(version);
    }

    public async Task<bool> FlushAsync()
    {
        while (true)
        {
            var (version, debounce, pendingSave) = GetPendingSave();
            TryCancel(debounce);
            await pendingSave;

            lock (_stateGate)
            {
                if (_persistedVersion >= version && _requestedVersion == version)
                    return true;
            }

            if (!await SaveAsync(SettingsSaveIntent.Automatic))
                return false;

            lock (_stateGate)
            {
                _persistedVersion = Math.Max(_persistedVersion, version);
                if (_requestedVersion == version)
                    return true;
            }
        }
    }

    private async Task RunAutoSaveAsync(long version, CancellationTokenSource debounce)
    {
        try
        {
            await Task.Delay(debounceDelay, debounce.Token);
            if (await SaveAsync(SettingsSaveIntent.Automatic))
                MarkPersisted(version);
        }
        catch (OperationCanceledException) when (debounce.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_debounce, debounce))
                    _debounce = null;
            }
            debounce.Dispose();
        }
    }

    private async Task<bool> SaveAsync(SettingsSaveIntent intent)
    {
        await _saveGate.WaitAsync();
        try
        {
            return await persist(intent);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private (long Version, CancellationTokenSource? Debounce, Task PendingSave) GetPendingSave()
    {
        lock (_stateGate)
            return (_requestedVersion, _debounce, _pendingSave);
    }

    private void MarkPersisted(long version)
    {
        lock (_stateGate)
            _persistedVersion = Math.Max(_persistedVersion, version);
    }

    private static void TryCancel(CancellationTokenSource? debounce)
    {
        try
        {
            debounce?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The pending save finished between taking the snapshot and cancellation.
        }
    }
}
