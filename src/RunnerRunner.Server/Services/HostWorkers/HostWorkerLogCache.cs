using System.Collections.Concurrent;
using RunnerRunner.Core.HostWorkers;

namespace RunnerRunner.Server.Services.HostWorkers;

public sealed class HostWorkerLogCache
{
    private readonly ConcurrentDictionary<LogStreamKey, LogStreamBuffer> _streams = new();
    private readonly int _maxFramesPerStream;

    public HostWorkerLogCache(IConfiguration configuration)
    {
        _maxFramesPerStream = Math.Max(1, configuration.GetValue("HostWorker:LogCache:MaxFramesPerStream", 500));
    }

    public event Action<string, HostWorkerLogFrame>? OnFrameReceived;

    public void Ingest(string hostId, HostWorkerLogFrame frame)
    {
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Host id is required to ingest a log frame.", nameof(hostId));
        if (string.IsNullOrWhiteSpace(frame.StreamId))
            throw new ArgumentException("Log frame stream id is required.", nameof(frame));
        if (string.IsNullOrWhiteSpace(frame.StreamKind))
            throw new ArgumentException("Log frame stream kind is required.", nameof(frame));

        var key = new LogStreamKey(hostId, frame.StreamId);
        var buffer = _streams.GetOrAdd(key, _ => new LogStreamBuffer(_maxFramesPerStream));
        var accepted = buffer.TryAdd(frame);
        if (accepted)
            OnFrameReceived?.Invoke(hostId, frame);
    }

    public IReadOnlyList<HostWorkerLogFrame> GetTail(string hostId, string streamId, int maxFrames)
    {
        var key = new LogStreamKey(hostId, streamId);
        return _streams.TryGetValue(key, out var buffer)
            ? buffer.GetTail(maxFrames)
            : [];
    }

    public string GetTextTail(string hostId, string streamId, int maxFrames)
        => string.Concat(GetTail(hostId, streamId, maxFrames).Select(frame => frame.Text));

    public IReadOnlyList<HostWorkerLogStreamSnapshot> GetStreams()
        => _streams
            .Select(kvp => new HostWorkerLogStreamSnapshot(
                kvp.Key.HostId,
                kvp.Key.StreamId,
                kvp.Value.GetTail(_maxFramesPerStream)))
            .ToArray();

    private readonly record struct LogStreamKey(string HostId, string StreamId);

    public sealed record HostWorkerLogStreamSnapshot(
        string HostId,
        string StreamId,
        IReadOnlyList<HostWorkerLogFrame> Frames);

    private sealed class LogStreamBuffer
    {
        private readonly int _maxFrames;
        private readonly Queue<HostWorkerLogFrame> _frames = new();
        private long _lastOffset = -1;

        public LogStreamBuffer(int maxFrames)
        {
            _maxFrames = maxFrames;
        }

        public bool TryAdd(HostWorkerLogFrame frame)
        {
            lock (_frames)
            {
                if (_lastOffset >= 0 && frame.Offset <= _lastOffset)
                    return false;

                _frames.Enqueue(frame);
                _lastOffset = frame.Offset;

                while (_frames.Count > _maxFrames)
                    _frames.Dequeue();

                return true;
            }
        }

        public IReadOnlyList<HostWorkerLogFrame> GetTail(int maxFrames)
        {
            lock (_frames)
            {
                var take = Math.Clamp(maxFrames, 1, _maxFrames);
                return _frames.TakeLast(take).ToArray();
            }
        }
    }
}
