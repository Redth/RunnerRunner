namespace RunnerRunner.Core.Models;

public sealed class ObservedLogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long Sequence { get; set; }
    public long? Offset { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public ObservedLogSourceType SourceType { get; set; } = ObservedLogSourceType.Server;
    public string SourceId { get; set; } = "server";
    public string SourceName { get; set; } = "Server";
    public string? HostId { get; set; }
    public string? RunnerInstanceId { get; set; }
    public string? TaskId { get; set; }
    public string? Category { get; set; }
    public ObservedLogStreamKind StreamKind { get; set; } = ObservedLogStreamKind.Application;
    public ObservedLogLevel Level { get; set; } = ObservedLogLevel.Information;
    public RunnerProvider? Provider { get; set; }
    public ExecutionBackend? Backend { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? CommandId { get; set; }
    public string? CorrelationId { get; set; }
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public string Message { get; set; } = "";
    public string RenderedMessage { get; set; } = "";
    public string? Exception { get; set; }
}

public sealed class ObservedLogSource
{
    public required ObservedLogSourceType SourceType { get; init; }
    public required string SourceId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? HostId { get; init; }
    public string? RunnerInstanceId { get; init; }
    public string? TaskId { get; init; }
    public RunnerProvider? Provider { get; init; }
    public ExecutionBackend? Backend { get; init; }
    public bool IsOnline { get; init; }
    public IReadOnlyCollection<string> Categories { get; init; } = [];
    public IReadOnlyCollection<ObservedLogStreamKind> StreamKinds { get; init; } = [];
}

public sealed class ObservedLogQuery
{
    public string? SourceId { get; set; }
    public ObservedLogSourceType? SourceType { get; set; }
    public string? HostId { get; set; }
    public string? RunnerInstanceId { get; set; }
    public string? TaskId { get; set; }
    public RunnerProvider? Provider { get; set; }
    public ExecutionBackend? Backend { get; set; }
    public ObservedLogStreamKind? StreamKind { get; set; }
    public ObservedLogLevel? MinimumLevel { get; set; }
    public string? Category { get; set; }
    public string? SearchText { get; set; }
    public DateTimeOffset? Since { get; set; }
    public DateTimeOffset? Until { get; set; }
    public int Tail { get; set; } = 200;
}

public sealed class ObservedLogQueryResult
{
    public IReadOnlyList<ObservedLogEntry> Entries { get; init; } = [];
    public IReadOnlyList<ObservedLogSource> Sources { get; init; } = [];
    public int TotalMatched { get; init; }
    public bool IsTruncated { get; init; }
}

public enum ObservedLogSourceType
{
    All,
    Server,
    Host,
    Runner,
    Task,
    Grain,
    Provider,
    Command
}

public enum ObservedLogStreamKind
{
    Application,
    Console,
    Stdout,
    Stderr,
    Progress,
    Command,
    Runner,
    Worker,
    Grain,
    Task,
    System
}

public enum ObservedLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
    None
}
