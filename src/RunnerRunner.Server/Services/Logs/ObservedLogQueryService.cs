using RunnerRunner.Core.HostWorkers;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services.HostWorkers;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services.Logs;

public sealed class ObservedLogQueryService
{
    private readonly ObservedLogStore _serverLogs;
    private readonly HostWorkerLogCache _hostWorkerLogs;
    private readonly LongRunningTaskService _tasks;
    private readonly IDocumentStore _store;

    public ObservedLogQueryService(
        ObservedLogStore serverLogs,
        HostWorkerLogCache hostWorkerLogs,
        LongRunningTaskService tasks,
        IDocumentStore store)
    {
        _serverLogs = serverLogs;
        _hostWorkerLogs = hostWorkerLogs;
        _tasks = tasks;
        _store = store;
    }

    public async Task<ObservedLogQueryResult> QueryAsync(ObservedLogQuery query)
    {
        var sources = await GetSourcesAsync();
        var serverEntries = _serverLogs.Query(query);
        var hostEntries = _hostWorkerLogs.GetStreams()
            .SelectMany(stream => stream.Frames.Select(frame => ToObservedEntry(stream.HostId, frame)))
            .Where(entry => Matches(entry, query))
            .ToArray();

        var allEntries = serverEntries
            .Concat(hostEntries)
            .OrderBy(entry => entry.Timestamp)
            .ThenBy(entry => entry.Sequence)
            .ToArray();

        var tail = Math.Clamp(query.Tail, 1, 10_000);
        var entries = allEntries.TakeLast(tail).ToArray();

        return new ObservedLogQueryResult
        {
            Entries = entries,
            Sources = sources,
            TotalMatched = allEntries.Length,
            IsTruncated = allEntries.Length > entries.Length
        };
    }

    public async Task<IReadOnlyList<ObservedLogSource>> GetSourcesAsync()
    {
        var hosts = (await _store.Query<Host>().ToList()).OrderBy(h => h.Label).ToArray();
        var runners = (await _store.Query<RunnerInstance>().ToList())
            .Where(CapacityPlanningService.IsRunnerRunnerManaged)
            .OrderBy(r => r.RunnerName)
            .ToArray();
        var hostMap = hosts.ToDictionary(h => h.Id, StringComparer.OrdinalIgnoreCase);
        var taskSnapshot = _tasks.GetSnapshot();

        var sources = new List<ObservedLogSource>
        {
            new()
            {
                SourceType = ObservedLogSourceType.All,
                SourceId = "all",
                Name = "All logs",
                Description = "Server, hosts, runners, tasks, and command streams",
                IsOnline = hosts.Any(h => h.AgentStatus == AgentStatus.Online),
                Categories = _serverLogs.GetCategories()
            },
            new()
            {
                SourceType = ObservedLogSourceType.Server,
                SourceId = "server",
                Name = "Server",
                Description = "RunnerRunner server process, Orleans, grains, and background services",
                IsOnline = true,
                Categories = _serverLogs.GetCategories(),
                StreamKinds = [ObservedLogStreamKind.Application, ObservedLogStreamKind.Grain]
            },
            new()
            {
                SourceType = ObservedLogSourceType.Grain,
                SourceId = "server:grains",
                Name = "Orleans grains",
                Description = "Orleans runtime and RunnerRunner grain categories",
                IsOnline = true,
                StreamKinds = [ObservedLogStreamKind.Grain]
            }
        };

        sources.AddRange(hosts.Select(host => new ObservedLogSource
        {
            SourceType = ObservedLogSourceType.Host,
            SourceId = $"host:{host.Id}",
            Name = host.Label,
            Description = $"{host.Platform} {host.Architecture}".Trim(),
            HostId = host.Id,
            IsOnline = host.AgentStatus == AgentStatus.Online,
            StreamKinds = [ObservedLogStreamKind.Worker, ObservedLogStreamKind.Command, ObservedLogStreamKind.Console]
        }));

        sources.AddRange(runners.Select(runner =>
        {
            hostMap.TryGetValue(runner.HostId, out var host);
            return new ObservedLogSource
            {
                SourceType = ObservedLogSourceType.Runner,
                SourceId = $"runner:{runner.Id}",
                Name = runner.RunnerName,
                Description = host?.Label ?? runner.HostId,
                HostId = runner.HostId,
                RunnerInstanceId = runner.Id,
                IsOnline = host?.AgentStatus == AgentStatus.Online,
                StreamKinds = [ObservedLogStreamKind.Runner, ObservedLogStreamKind.Stdout, ObservedLogStreamKind.Stderr]
            };
        }));

        sources.AddRange(taskSnapshot.Select(task => new ObservedLogSource
        {
            SourceType = ObservedLogSourceType.Task,
            SourceId = $"task:{task.Id}",
            Name = task.Title,
            Description = $"{task.Status}: {task.StatusText}",
            HostId = task.HostId,
            TaskId = task.Id,
            IsOnline = task.Status == LongRunningTaskStatus.Running,
            StreamKinds = [ObservedLogStreamKind.Task, ObservedLogStreamKind.Progress]
        }));

        return sources;
    }

    public static ObservedLogEntry ToObservedEntry(string hostId, HostWorkerLogFrame frame)
    {
        var sourceType = ParseEnum(frame.SourceType, string.IsNullOrWhiteSpace(frame.RunnerInstanceId)
            ? ObservedLogSourceType.Host
            : ObservedLogSourceType.Runner);
        var streamKind = ParseStreamKind(frame.StreamKind);
        var sourceId = sourceType switch
        {
            ObservedLogSourceType.Runner when !string.IsNullOrWhiteSpace(frame.RunnerInstanceId) => $"runner:{frame.RunnerInstanceId}",
            ObservedLogSourceType.Task when !string.IsNullOrWhiteSpace(frame.TaskId) => $"task:{frame.TaskId}",
            ObservedLogSourceType.Command => $"command:{frame.CommandId ?? frame.StreamId}",
            _ => $"host:{hostId}"
        };

        return new ObservedLogEntry
        {
            Id = $"{hostId}:{frame.StreamId}:{frame.Offset}",
            Offset = frame.Offset,
            Timestamp = frame.Timestamp,
            SourceType = sourceType,
            SourceId = sourceId,
            SourceName = string.IsNullOrWhiteSpace(frame.SourceName) ? hostId : frame.SourceName,
            HostId = hostId,
            RunnerInstanceId = frame.RunnerInstanceId,
            TaskId = frame.TaskId,
            Category = frame.Category ?? frame.StreamKind,
            StreamKind = streamKind,
            Level = ParseEnum(frame.Level, ObservedLogLevel.Information),
            Provider = ParseNullableEnum<RunnerProvider>(frame.Provider),
            Backend = ParseNullableEnum<ExecutionBackend>(frame.Backend),
            Tags = new Dictionary<string, string>(frame.Tags, StringComparer.OrdinalIgnoreCase),
            CommandId = frame.CommandId,
            CorrelationId = frame.CorrelationId,
            TraceId = frame.TraceId,
            SpanId = frame.SpanId,
            Message = frame.Text,
            RenderedMessage = frame.Text
        };
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
            return false;
        if (query.Since is { } since && entry.Timestamp < since)
            return false;
        if (query.Until is { } until && entry.Timestamp > until)
            return false;
        if (!string.IsNullOrWhiteSpace(query.SearchText) &&
            !entry.Message.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase) &&
            !entry.RenderedMessage.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool IsNullOrEqual(string? expected, string? actual)
        => string.IsNullOrWhiteSpace(expected) || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static ObservedLogStreamKind ParseStreamKind(string value)
    {
        var normalized = value.Replace(".", "", StringComparison.OrdinalIgnoreCase).Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (normalized.Contains("runner", StringComparison.OrdinalIgnoreCase))
            return ObservedLogStreamKind.Runner;
        if (normalized.Contains("command", StringComparison.OrdinalIgnoreCase))
            return ObservedLogStreamKind.Command;
        if (normalized.Contains("progress", StringComparison.OrdinalIgnoreCase))
            return ObservedLogStreamKind.Progress;
        if (normalized.Contains("task", StringComparison.OrdinalIgnoreCase))
            return ObservedLogStreamKind.Task;
        if (normalized.Contains("stderr", StringComparison.OrdinalIgnoreCase))
            return ObservedLogStreamKind.Stderr;
        if (normalized.Contains("stdout", StringComparison.OrdinalIgnoreCase))
            return ObservedLogStreamKind.Stdout;
        if (normalized.Contains("worker", StringComparison.OrdinalIgnoreCase))
            return ObservedLogStreamKind.Worker;
        return ParseEnum(value, ObservedLogStreamKind.Console);
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct
        => Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static TEnum? ParseNullableEnum<TEnum>(string? value)
        where TEnum : struct
        => Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : null;
}
