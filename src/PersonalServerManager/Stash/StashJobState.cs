namespace PersonalServerManager.Stash;

public sealed class StashJobState
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, List<TrackedStashJob>> _jobs = [];
    private readonly HashSet<Guid> _starting = [];

    public event Action? Changed;

    public IReadOnlyList<TrackedStashJob> Jobs(Guid instanceId)
    {
        lock (_sync) return _jobs.TryGetValue(instanceId, out var jobs) ? jobs.ToArray() : [];
    }

    public bool IsStarting(Guid instanceId)
    {
        lock (_sync) return _starting.Contains(instanceId);
    }

    public async Task StartAsync(Guid instanceId, string kind, Func<Task<string>> start)
    {
        lock (_sync)
        {
            if (!_starting.Add(instanceId)) return;
        }
        try
        {
            NotifyChanged();
            var id = await start();
            lock (_sync)
            {
                if (!_jobs.TryGetValue(instanceId, out var jobs)) _jobs[instanceId] = jobs = [];
                jobs.Insert(0, new TrackedStashJob(id, kind));
            }
        }
        finally
        {
            lock (_sync) _starting.Remove(instanceId);
            NotifyChanged();
        }
    }

    private void NotifyChanged()
    {
        foreach (Action handler in Changed?.GetInvocationList() ?? [])
        {
            try { handler(); }
            catch (Exception) { }
        }
    }
}

public sealed class TrackedStashJob(string id, string kind)
{
    private readonly object _sync = new();
    private string? _status;
    private string? _error;
    private bool _statusUnavailable;

    public string Id { get; } = id;
    public string Kind { get; } = kind;
    public string? Status { get { lock (_sync) return _status; } set { lock (_sync) _status = value; } }
    public string? Error { get { lock (_sync) return _error; } set { lock (_sync) _error = value; } }
    public bool StatusUnavailable { get { lock (_sync) return _statusUnavailable; } set { lock (_sync) _statusUnavailable = value; } }
    public bool IsTerminal { get { lock (_sync) return !_statusUnavailable && _status is "FINISHED" or "FAILED" or "CANCELLED"; } }
}