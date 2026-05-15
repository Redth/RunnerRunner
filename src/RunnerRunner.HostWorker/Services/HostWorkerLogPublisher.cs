using System.Diagnostics;
using RunnerRunner.Core.HostWorkers;

namespace RunnerRunner.HostWorker.Services;

internal sealed class HostWorkerLogPublisher
{
    private readonly HostWorkerIdentity _identity;
    private readonly HostWorkerLocalLogStore _logStore;
    private IHostWorkerEventSink? _eventSink;
    private long _sequence;

    public HostWorkerLogPublisher(HostWorkerIdentity identity, HostWorkerLocalLogStore logStore)
    {
        _identity = identity;
        _logStore = logStore;
    }

    public void AttachEventSink(IHostWorkerEventSink eventSink)
    {
        _eventSink = eventSink;
    }

    public void PublishProcessLog(
        string category,
        LogLevel level,
        string message,
        Exception? exception,
        IReadOnlyDictionary<string, string> tags)
    {
        if (string.IsNullOrWhiteSpace(message) && exception == null)
            return;

        var rendered = FormatRenderedLine(level, category, message, exception);
        var activity = Activity.Current;

        _ = Task.Run(async () =>
        {
            try
            {
                var frame = await _logStore.AppendAsync(
                    "worker.process",
                    "worker.process",
                    rendered,
                    runnerInstanceId: null,
                    CancellationToken.None,
                    category: category,
                    level: level.ToString(),
                    sourceType: "Host",
                    sourceName: _identity.HostName,
                    traceId: activity?.TraceId.ToString(),
                    spanId: activity?.SpanId.ToString(),
                    tags: tags);

                if (_eventSink != null)
                {
                    await _eventSink.PublishAsync(HostWorkerProtocol.CreateMessage(
                        _identity.HostId,
                        HostWorkerMessageKinds.LogFrame,
                        frame,
                        sequence: Interlocked.Increment(ref _sequence)), CancellationToken.None);
                }
            }
            catch
            {
                // Logging must never destabilize the worker.
            }
        });
    }

    private static string FormatRenderedLine(LogLevel level, string category, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss} {LevelToken(level)} {ShortCategory(category)}: {message}";
        return exception == null ? line : line + Environment.NewLine + exception;
    }

    private static string LevelToken(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none"
    };

    private static string ShortCategory(string category)
    {
        const string workerPrefix = "RunnerRunner.HostWorker.";
        const string agentPrefix = "RunnerRunner.Agent.";
        if (category.StartsWith(workerPrefix, StringComparison.Ordinal))
            return category[workerPrefix.Length..];
        if (category.StartsWith(agentPrefix, StringComparison.Ordinal))
            return "Agent." + category[agentPrefix.Length..];
        return category;
    }
}
