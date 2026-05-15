using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Services.Logs;

public sealed class ObservedLogStore
{
    private readonly object _gate = new();
    private readonly Queue<ObservedLogEntry> _entries = new();
    private readonly int _maxEntries;
    private long _sequence;

    public ObservedLogStore(IConfiguration configuration)
    {
        _maxEntries = Math.Max(1, configuration.GetValue("Logs:Recent:MaxEntries", 10_000));
    }

    public event Action<ObservedLogEntry>? OnEntryAdded;

    public ObservedLogEntry Add(ObservedLogEntry entry)
    {
        entry.Sequence = Interlocked.Increment(ref _sequence);
        if (string.IsNullOrWhiteSpace(entry.RenderedMessage))
            entry.RenderedMessage = entry.Message;

        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _maxEntries)
                _entries.Dequeue();
        }

        OnEntryAdded?.Invoke(entry);
        return entry;
    }

    public IReadOnlyList<ObservedLogEntry> Query(ObservedLogQuery query)
    {
        ObservedLogEntry[] snapshot;
        lock (_gate)
            snapshot = _entries.ToArray();

        var filtered = snapshot.Where(entry => Matches(entry, query));
        var tail = Math.Clamp(query.Tail, 1, _maxEntries);
        return filtered
            .OrderBy(entry => entry.Sequence)
            .TakeLast(tail)
            .ToArray();
    }

    public IReadOnlyCollection<string> GetCategories()
    {
        lock (_gate)
        {
            return _entries
                .Select(e => e.Category)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()!;
        }
    }

    private static bool Matches(ObservedLogEntry entry, ObservedLogQuery query)
    {
        if (query.SourceType is { } sourceType && sourceType != ObservedLogSourceType.All && entry.SourceType != sourceType)
            return false;
        if (!IsNullOrEqual(query.SourceId, entry.SourceId))
            return false;
        if (!IsNullOrEqual(query.HostId, entry.HostId))
            return false;
        if (!IsNullOrEqual(query.RunnerInstanceId, entry.RunnerInstanceId))
            return false;
        if (!IsNullOrEqual(query.TaskId, entry.TaskId))
            return false;
        if (query.Provider is { } provider && entry.Provider != provider)
            return false;
        if (query.Backend is { } backend && entry.Backend != backend)
            return false;
        if (query.StreamKind is { } streamKind && entry.StreamKind != streamKind)
            return false;
        if (query.MinimumLevel is { } minimumLevel && entry.Level < minimumLevel)
            return false;
        if (!string.IsNullOrWhiteSpace(query.Category) &&
            (entry.Category == null || !entry.Category.Contains(query.Category, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        if (query.Since is { } since && entry.Timestamp < since)
            return false;
        if (query.Until is { } until && entry.Timestamp > until)
            return false;
        if (!string.IsNullOrWhiteSpace(query.SearchText) &&
            !entry.Message.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase) &&
            !entry.RenderedMessage.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase) &&
            (entry.Exception == null || !entry.Exception.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private static bool IsNullOrEqual(string? expected, string? actual)
        => string.IsNullOrWhiteSpace(expected) || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
}
