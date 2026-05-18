using System.Threading.Channels;
using RunnerRunner.Core.HostWorkers;
using RunnerRunner.HostWorker.Services;

namespace RunnerRunner.HostWorker.Tests.TestSupport;

internal sealed class FakeHostWorkerEventSink : IHostWorkerEventSink
{
    private readonly object _gate = new();
    private readonly Channel<HostWorkerMessage> _messages = Channel.CreateUnbounded<HostWorkerMessage>();
    private readonly List<HostWorkerMessage> _published = [];

    public IReadOnlyList<HostWorkerMessage> Published
    {
        get
        {
            lock (_gate)
                return _published.ToArray();
        }
    }

    public ValueTask PublishAsync(HostWorkerMessage message, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            _published.Add(message);

        _messages.Writer.TryWrite(message);
        return ValueTask.CompletedTask;
    }

    public async Task<HostWorkerMessage> WaitForKindAsync(string kind, TimeSpan? timeout = null)
    {
        foreach (var message in Published)
        {
            if (message.Kind == kind)
                return message;
        }

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            while (await _messages.Reader.WaitToReadAsync(cts.Token))
            {
                while (_messages.Reader.TryRead(out var message))
                {
                    if (message.Kind == kind)
                        return message;
                }
            }
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"No HostWorker message of kind '{kind}' was published.", ex);
        }

        throw new TimeoutException($"No HostWorker message of kind '{kind}' was published.");
    }
}
