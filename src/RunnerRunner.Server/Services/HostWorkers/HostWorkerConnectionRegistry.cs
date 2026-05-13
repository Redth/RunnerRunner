using System.Collections.Concurrent;
using System.Threading.Channels;
using RunnerRunner.Core.HostWorkers;

namespace RunnerRunner.Server.Services.HostWorkers;

public sealed class HostWorkerConnectionRegistry
{
    private readonly ConcurrentDictionary<string, HostWorkerConnection> _connections = new(StringComparer.OrdinalIgnoreCase);

    public HostWorkerConnection Register(string canonicalHostId, params string[] aliases)
    {
        var keys = aliases
            .Append(canonicalHostId)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var connection = new HostWorkerConnection(canonicalHostId, keys, Remove);
        foreach (var key in keys)
        {
            if (_connections.TryGetValue(key, out var existing) && !ReferenceEquals(existing, connection))
                existing.Complete();

            _connections[key] = connection;
        }

        return connection;
    }

    public async Task SendAsync(string hostId, HostWorkerMessage message, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(hostId, out var connection))
            throw new InvalidOperationException($"HostWorker '{hostId}' is not connected.");

        await connection.SendAsync(message, cancellationToken);
    }

    public bool IsCurrent(string hostId, HostWorkerConnection connection)
        => _connections.TryGetValue(hostId, out var current) && ReferenceEquals(current, connection);

    private void Remove(HostWorkerConnection connection)
    {
        foreach (var key in connection.Keys)
        {
            if (_connections.TryGetValue(key, out var current) && ReferenceEquals(current, connection))
                _connections.TryRemove(key, out _);
        }
    }
}

public sealed class HostWorkerConnection : IAsyncDisposable
{
    private readonly Action<HostWorkerConnection> _onDispose;
    private readonly Channel<HostWorkerMessage> _outbound = Channel.CreateBounded<HostWorkerMessage>(
        new BoundedChannelOptions(1_000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private int _completed;

    internal HostWorkerConnection(string hostId, IReadOnlyCollection<string> keys, Action<HostWorkerConnection> onDispose)
    {
        HostId = hostId;
        Keys = keys;
        _onDispose = onDispose;
    }

    public string HostId { get; }
    public IReadOnlyCollection<string> Keys { get; }

    public ValueTask SendAsync(HostWorkerMessage message, CancellationToken cancellationToken)
        => _outbound.Writer.WriteAsync(message, cancellationToken);

    public IAsyncEnumerable<HostWorkerMessage> ReadAllAsync(CancellationToken cancellationToken)
        => _outbound.Reader.ReadAllAsync(cancellationToken);

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 1)
            return;

        _outbound.Writer.TryComplete();
        _onDispose(this);
    }

    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }
}
