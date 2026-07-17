namespace EasyGet.Services;

/// <summary>
/// Prevents a burst of identical handled UI exceptions from opening one modal dialog per item.
/// Distinct exceptions and repeats outside the configured window are still reported.
/// </summary>
public sealed class ExceptionNotificationThrottle
{
    private readonly TimeSpan _window;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _lastNotificationUtc =
        new(StringComparer.Ordinal);

    public ExceptionNotificationThrottle(TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));
        _window = window;
    }

    public bool ShouldNotify(Exception? exception, string source, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var fingerprint = BuildFingerprint(exception, source);
        lock (_gate)
        {
            foreach (var staleKey in _lastNotificationUtc
                         .Where(pair => utcNow >= pair.Value
                                        && utcNow - pair.Value >= _window)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _lastNotificationUtc.Remove(staleKey);
            }

            if (_lastNotificationUtc.TryGetValue(fingerprint, out var previousUtc)
                && utcNow >= previousUtc
                && utcNow - previousUtc < _window)
            {
                return false;
            }

            _lastNotificationUtc[fingerprint] = utcNow;
            return true;
        }
    }

    private static string BuildFingerprint(Exception? exception, string source)
        => string.Join(
            '\n',
            source,
            exception?.GetType().FullName ?? "UnknownException",
            exception?.Message ?? "Unknown Error",
            exception?.InnerException?.GetType().FullName ?? "",
            exception?.InnerException?.Message ?? "");
}
