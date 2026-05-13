using System.Text.Json;
using System.Text.Json.Serialization;
using RunnerRunner.Core.Hub;

namespace RunnerRunner.Core.HostWorkers;

public static class HostWorkerProtocol
{
    public const string ProtocolVersion = "1";

    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static HostWorkerMessage CreateMessage<TPayload>(
        string hostId,
        string kind,
        TPayload payload,
        string? commandId = null,
        string? idempotencyKey = null,
        long sequence = 0)
    {
        return new HostWorkerMessage
        {
            MessageId = Guid.NewGuid().ToString("N"),
            HostId = hostId,
            Kind = kind,
            CommandId = commandId ?? string.Empty,
            IdempotencyKey = idempotencyKey ?? string.Empty,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            Sequence = sequence
        };
    }

    public static TPayload DeserializePayload<TPayload>(HostWorkerMessage message)
    {
        var payload = JsonSerializer.Deserialize<TPayload>(message.PayloadJson, JsonOptions);
        return payload ?? throw new InvalidOperationException(
            $"Host worker message '{message.Kind}' did not contain a valid {typeof(TPayload).Name} payload.");
    }

    public static HostCommandEnvelope DeserializeCommand(HostWorkerMessage message)
        => DeserializePayload<HostCommandEnvelope>(message);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public static class HostWorkerMessageKinds
{
    public const string Hello = "worker.hello";
    public const string Heartbeat = "worker.heartbeat";
    public const string Command = "worker.command";
    public const string CommandAccepted = "worker.command.accepted";
    public const string CommandRejected = "worker.command.rejected";
    public const string CommandCompleted = "worker.command.completed";
    public const string RunnerStarted = "runner.started";
    public const string RunnerStopped = "runner.stopped";
    public const string RunnerHealth = "runner.health";
    public const string Reconciliation = "worker.reconciliation";
    public const string ImageList = "image.list";
    public const string ImageRefreshStatus = "image.refresh.status";
    public const string ImagePullProgress = "image.pull.progress";
    public const string ImagePullComplete = "image.pull.complete";
    public const string ImageDeleted = "image.deleted";
    public const string HostLogs = "logs.host";
    public const string RunnerLogs = "logs.runner";
    public const string LogFrame = "logs.frame";
    public const string UpdateStatus = "worker.update.status";
}

public sealed class HostWorkerHello
{
    public required AgentInfo Agent { get; init; }
    public string ProtocolVersion { get; init; } = HostWorkerProtocol.ProtocolVersion;
    public string? EnrollmentToken { get; init; }
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
}

public sealed class HostWorkerCommandStatus
{
    public required string CommandId { get; init; }
    public required HostCommandKind Kind { get; init; }
    public bool Success { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
}

public sealed class HostWorkerLogFrame
{
    public required string StreamId { get; init; }
    public required string StreamKind { get; init; }
    public string? RunnerInstanceId { get; init; }
    public long Offset { get; init; }
    public string Text { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
