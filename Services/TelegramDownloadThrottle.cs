using System.Diagnostics;
using TL;

namespace EasyGet.Services;

/// <summary>Shared by files and replacement clients belonging to the same Telegram session.</summary>
internal sealed class TelegramDownloadThrottle
{
    internal const int MaximumConcurrency = 4;
    internal const int MaximumWaitSeconds = 600;
    private readonly DynamicConcurrencyGate _gate = new(MaximumConcurrency);
    private readonly object _sync = new();
    private readonly ITelegramDownloadClock _clock;
    private DateTimeOffset _blockedUntil;
    private string _waitMessage = "FLOOD_WAIT_X";
    private int _concurrencyLimit = MaximumConcurrency;

    internal TelegramDownloadThrottle() : this(new SystemClock()) { }
    internal TelegramDownloadThrottle(ITelegramDownloadClock clock) => _clock = clock;
    internal int ConcurrencyLimit { get { lock (_sync) return _concurrencyLimit; } }

    internal async Task WaitAsync(Action heartbeat, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Check after acquiring: waiters queued before a flood response must
            // also respect its cooldown, including waiters from the next file.
            while (true)
            {
                TimeSpan remaining;
                string waitMessage;
                lock (_sync)
                {
                    remaining = _blockedUntil - _clock.UtcNow;
                    waitMessage = _waitMessage;
                }
                if (remaining <= TimeSpan.Zero)
                    return;
                if (remaining.TotalSeconds > MaximumWaitSeconds)
                    throw new RpcException(420, waitMessage, (int)Math.Ceiling(remaining.TotalSeconds));

                await _clock.DelayAsync(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), ct)
                    .ConfigureAwait(false);
                // This is a local progress update only. No keepalive request is
                // sent while Telegram has told the account to wait.
                heartbeat();
            }
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    internal void Release() => _gate.Release();

    internal void RecordFlood(RpcException error)
    {
        lock (_sync)
        {
            var until = _clock.UtcNow.AddSeconds(error.X);
            if (until > _blockedUntil)
            {
                _blockedUntil = until;
                _waitMessage = error.Message;
            }
            // Keep the session conservative after its first flood response.
            _concurrencyLimit = 1;
            _gate.UpdateLimit(1);
        }
    }

    internal static bool IsFloodWait(RpcException error)
        => error.Code == 420 && error.X > 0
            && error.Message is "FLOOD_WAIT_X" or "FLOOD_PREMIUM_WAIT_X";

    private sealed class SystemClock : ITelegramDownloadClock
    {
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        // Wall-clock corrections must never shorten a server-mandated wait.
        public DateTimeOffset UtcNow => _startedAt + _elapsed.Elapsed;
        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
    }
}

internal interface ITelegramDownloadClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}
