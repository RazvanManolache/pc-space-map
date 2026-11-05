namespace PcSpaceMap.Service;

internal sealed class DiskScanSlots(int capacity)
{
    private readonly object _gate = new();
    private readonly List<Request> _waiting = [];
    private readonly List<Request> _running = [];
    private TaskCompletionSource _changed = NewSignal();

    internal async Task<IDisposable> AcquireAsync(uint[] disks, CancellationToken token, Priority? priority = null,
        Action? yieldBackground = null)
    {
        var request = new Request(disks, priority ?? new(false), yieldBackground);
        lock (_gate) _waiting.Add(request);
        try
        {
            while (true)
            {
                Task signal;
                lock (_gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (CanStart(request))
                    {
                        _running.Add(request);
                        return new Slot(this, request);
                    }
                    if (request.Priority.Foreground) YieldBlockingBackground(request);
                    signal = _changed.Task;
                }
                await signal.WaitAsync(token);
            }
        }
        finally
        {
            lock (_gate)
            {
                _waiting.Remove(request);
                NotifyWaiters();
            }
        }
    }

    internal bool TryPromote(Priority priority)
    {
        lock (_gate)
        {
            if (_running.Any(job => job.Priority == priority && job.YieldRequested)) return false;
            priority.Foreground = true;
            NotifyWaiters();
            return true;
        }
    }

    private IEnumerable<Request> InPriorityOrder() => _waiting.OrderByDescending(waiting => waiting.Priority.Foreground);

    private bool CanStart(Request request)
    {
        if (_running.Count >= capacity || _running.Any(job => Conflicts(request, job))) return false;

        // Preserve order for conflicting requests while letting independent disks use free slots.
        var blocked = _running.SelectMany(job => job.Disks).ToHashSet();
        foreach (var waiting in InPriorityOrder())
        {
            var disks = waiting.Disks;
            if (disks.Length == 0) return waiting == request && _running.Count == 0;
            if (!disks.Any(blocked.Contains) && !_running.Any(job => job.Disks.Length == 0)) return waiting == request;
            if (waiting == request) return false;
            blocked.UnionWith(disks);
        }
        return false;
    }

    private void YieldBlockingBackground(Request request)
    {
        // An older foreground request on the same disks has the first claim.
        if (InPriorityOrder().TakeWhile(job => job != request).Any(job => job.Priority.Foreground && Conflicts(job, request))) return;
        var conflicting = _running.Where(job => Conflicts(job, request)).ToArray();
        if (conflicting.Any(job => job.Priority.Foreground)) return;
        if (conflicting.Length > 0)
        {
            foreach (var job in conflicting) job.RequestYield();
        }
        else if (_running.Count >= capacity && !_running.Any(job => job.YieldRequested))
            _running.FirstOrDefault(job => !job.Priority.Foreground && job.YieldBackground is not null)?.RequestYield();
    }

    private static bool Conflicts(Request first, Request second) =>
        first.Disks.Length == 0 || second.Disks.Length == 0 || first.Disks.Any(second.Disks.Contains);

    private void Release(Request request)
    {
        lock (_gate)
        {
            _running.Remove(request);
            NotifyWaiters();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void NotifyWaiters()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    internal sealed class Priority(bool foreground)
    {
        internal volatile bool Foreground = foreground;
    }

    private sealed class Request(uint[] disks, Priority priority, Action? yieldBackground)
    {
        internal uint[] Disks { get; } = disks;
        internal Priority Priority { get; } = priority;
        internal Action? YieldBackground { get; } = yieldBackground;
        internal bool YieldRequested { get; private set; }

        internal void RequestYield()
        {
            if (YieldRequested || YieldBackground is null) return;
            YieldRequested = true;
            YieldBackground();
        }
    }

    private sealed class Slot(DiskScanSlots owner, Request request) : IDisposable
    {
        private DiskScanSlots? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(request);
    }
}
