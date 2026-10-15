namespace AiGateway.RunLog;

internal sealed class RunLogRing(int capacity = 500) : IRunLog
{
    private readonly object _gate = new();
    private readonly LinkedList<RunLogEntry> _entries = [];

    public int Capacity => capacity;

    public void Append(RunLogEntry entry)
    {
        lock (_gate)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > capacity)
            {
                _entries.RemoveLast();
            }
        }
    }

    public IReadOnlyList<RunLogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }
}
