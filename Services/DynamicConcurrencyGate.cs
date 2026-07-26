namespace EasyGet.Services;

internal sealed class DynamicConcurrencyGate
{
    private readonly object _syncRoot = new();
    private readonly LinkedList<Waiter> _waiters = [];
    private int _limit;
    private int _activeCount;

    public DynamicConcurrencyGate(int initialLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialLimit, 1);
        _limit = initialLimit;
    }

    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        lock (_syncRoot)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);

            if (_activeCount < _limit && _waiters.Count == 0)
            {
                _activeCount++;
                return Task.CompletedTask;
            }

            var waiter = new Waiter(this, cancellationToken);
            waiter.Node = _waiters.AddLast(waiter);
            waiter.RegisterCancellation();
            return waiter.Task;
        }
    }

    public void UpdateLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        List<Waiter>? grantedWaiters;
        lock (_syncRoot)
        {
            _limit = limit;
            grantedWaiters = GrantAvailableWaiters();
        }

        CompleteGrantedWaiters(grantedWaiters);
    }

    public void Release()
    {
        List<Waiter>? grantedWaiters;
        lock (_syncRoot)
        {
            if (_activeCount == 0)
                throw new InvalidOperationException("No active concurrency slot to release.");

            _activeCount--;
            grantedWaiters = GrantAvailableWaiters();
        }

        CompleteGrantedWaiters(grantedWaiters);
    }

    private List<Waiter>? GrantAvailableWaiters()
    {
        List<Waiter>? grantedWaiters = null;
        while (_activeCount < _limit && _waiters.First is not null)
        {
            var waiter = _waiters.First.Value;
            _waiters.RemoveFirst();
            waiter.Node = null;
            _activeCount++;
            (grantedWaiters ??= []).Add(waiter);
        }

        return grantedWaiters;
    }

    private static void CompleteGrantedWaiters(List<Waiter>? waiters)
    {
        if (waiters is null)
            return;

        foreach (var waiter in waiters)
            waiter.Grant();
    }

    private void Cancel(Waiter waiter)
    {
        lock (_syncRoot)
        {
            if (waiter.Node is null)
                return;

            _waiters.Remove(waiter.Node);
            waiter.Node = null;
        }

        waiter.Cancel();
    }

    private sealed class Waiter(
        DynamicConcurrencyGate owner,
        CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _cancellationRegistration;

        public LinkedListNode<Waiter>? Node { get; set; }
        public Task Task => _completion.Task;

        public void RegisterCancellation()
        {
            if (cancellationToken.CanBeCanceled)
            {
                _cancellationRegistration = cancellationToken.Register(
                    static state => ((Waiter)state!).RequestCancellation(),
                    this);
            }
        }

        private void RequestCancellation() => owner.Cancel(this);

        public void Grant()
        {
            _cancellationRegistration.Unregister();
            _completion.TrySetResult();
        }

        public void Cancel()
        {
            _completion.TrySetCanceled(cancellationToken);
        }
    }
}
