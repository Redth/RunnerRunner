using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Events;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Grains.State;
using Shiny.DocumentDb;
using System.Text.RegularExpressions;

namespace RunnerRunner.Server.Grains;

// TODO: Add host-affinity grain placement so this grain activates on the silo
// whose "hostId" metadata matches the grain's primary key. Orleans 10 supports
// SiloMetadata was used by the retired HostWorker architecture. Options:
//   1. [SiloMetadataPlacement("hostId")] attribute if available
//   2. Custom IPlacementDirector that reads ISiloMetadataCache
// For now, grains can activate on the server silo; physical hosts connect as HostWorkers.
public class HostGrain : Grain, IHostGrain
{
    private static readonly Regex IpAddressRegex = new(@"\b\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.Compiled);
    private readonly IPersistentState<HostGrainState> _state;
    private readonly ILogger<HostGrain> _logger;
    private readonly IServiceProvider _serviceProvider;
    private IGrainTimer? _heartbeatTimer;

    public HostGrain(
        [PersistentState("host", "PersistentStore")]
        IPersistentState<HostGrainState> state,
        ILogger<HostGrain> logger,
        IServiceProvider serviceProvider)
    {
        _state = state;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task Register(string name, HostPlatform platform, string? architecture, string agentVersion)
    {
        _state.State.Name = name;
        _state.State.Platform = platform;
        _state.State.Architecture = architecture;
        _state.State.AgentVersion = agentVersion;

        // Auto-populate default labels
        _state.State.Labels["os"] = platform.ToString().ToLowerInvariant();
        if (architecture is not null)
            _state.State.Labels["arch"] = architecture.ToLowerInvariant();

        _state.State.Status = AgentStatus.Online;
        if (_state.State.CreatedAt == default)
            _state.State.CreatedAt = DateTime.UtcNow;
        _state.State.LastHeartbeat = DateTime.UtcNow;

        await _state.WriteStateAsync();
        StartHeartbeatTimer();
        await SyncToDocumentDb();
        await PublishHostStatusChange();

        _logger.LogInformation("Host {HostId} registered: {Name} ({Platform}/{Architecture})",
            this.GetPrimaryKeyString(), name, platform, architecture);
    }

    public async Task UpdateLabels(Dictionary<string, string> labels)
    {
        foreach (var kvp in labels)
            _state.State.Labels[kvp.Key] = kvp.Value;

        await _state.WriteStateAsync();
    }

    public async Task SetResourceLimits(int maxDocker, int maxTart, int maxNative)
    {
        _state.State.MaxDockerContainers = maxDocker;
        _state.State.MaxTartVMs = maxTart;
        _state.State.MaxNativeProcesses = maxNative;
        await _state.WriteStateAsync();
    }

    public async Task SetGroupId(string? groupId)
    {
        _state.State.GroupId = groupId;
        await _state.WriteStateAsync();
    }

    public async Task RecordHeartbeat(string connectionId, int runningCount)
    {
        _state.State.ConnectionId = connectionId;
        _state.State.LastHeartbeat = DateTime.UtcNow;
        _state.State.Status = AgentStatus.Online;
        await _state.WriteStateAsync();

        StartHeartbeatTimer();
        await PublishHostStatusChange();
        await SyncToDocumentDb();
    }

    public async Task MarkOffline()
    {
        _state.State.Status = AgentStatus.Offline;
        _state.State.ConnectionId = null;
        await _state.WriteStateAsync();

        _logger.LogWarning("Host {HostId} marked offline", this.GetPrimaryKeyString());
        await PublishHostStatusChange();
        await SyncToDocumentDb();
    }

    public Task<bool> CanAcceptRunner(ExecutionBackend backend)
    {
        var canAccept = backend switch
        {
            ExecutionBackend.Docker => _state.State.RunningDockerContainers < _state.State.MaxDockerContainers,
            ExecutionBackend.Tart => _state.State.RunningTartVMs < _state.State.MaxTartVMs,
            ExecutionBackend.Native => _state.State.RunningNativeProcesses < _state.State.MaxNativeProcesses,
            _ => false
        };
        return Task.FromResult(canAccept);
    }

    public async Task IncrementRunningCount(ExecutionBackend backend)
    {
        switch (backend)
        {
            case ExecutionBackend.Docker: _state.State.RunningDockerContainers++; break;
            case ExecutionBackend.Tart: _state.State.RunningTartVMs++; break;
            case ExecutionBackend.Native: _state.State.RunningNativeProcesses++; break;
        }
        await _state.WriteStateAsync();
    }

    public async Task DecrementRunningCount(ExecutionBackend backend)
    {
        switch (backend)
        {
            case ExecutionBackend.Docker:
                _state.State.RunningDockerContainers = Math.Max(0, _state.State.RunningDockerContainers - 1);
                break;
            case ExecutionBackend.Tart:
                _state.State.RunningTartVMs = Math.Max(0, _state.State.RunningTartVMs - 1);
                break;
            case ExecutionBackend.Native:
                _state.State.RunningNativeProcesses = Math.Max(0, _state.State.RunningNativeProcesses - 1);
                break;
        }
        await _state.WriteStateAsync();
    }

    public Task<string?> GetConnectionId() => Task.FromResult(_state.State.ConnectionId);

    public Task<HostGrainState> GetState() => Task.FromResult(_state.State);

    private async Task PublishHostStatusChange()
    {
        var streamProvider = this.GetStreamProvider("RunnerEvents");
        var streamId = StreamId.Create("HostStatus", "all");
        var stream = streamProvider.GetStream<HostStatusChangedEvent>(streamId);
        await stream.OnNextAsync(new HostStatusChangedEvent
        {
            HostId = this.GetPrimaryKeyString(),
            HostName = _state.State.Name,
            Status = _state.State.Status
        });
    }

    private void StartHeartbeatTimer()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = this.RegisterGrainTimer(
            OnHeartbeatTimeout,
            TimeSpan.FromSeconds(90),
            TimeSpan.FromSeconds(90));
    }

    private async Task OnHeartbeatTimeout(CancellationToken ct)
    {
        if (_state.State.Status == AgentStatus.Online)
        {
            _logger.LogWarning("Host {HostId} heartbeat timeout — marking offline", this.GetPrimaryKeyString());
            _state.State.Status = AgentStatus.Offline;
            _state.State.ConnectionId = null;
            await _state.WriteStateAsync();
            await PublishHostStatusChange();
            await SyncToDocumentDb();
        }

        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    private async Task SyncToDocumentDb()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

            var hostId = this.GetPrimaryKeyString();
            var allHosts = (await store.Query<Core.Models.Host>().ToList()).ToList();
            var existing = allHosts.FirstOrDefault(h => h.Id == hostId);
            var legacy = allHosts.FirstOrDefault(h => h.Id != hostId && string.Equals(h.Name, _state.State.Name, StringComparison.OrdinalIgnoreCase))
                ?? FindLegacyProjectionByIp(allHosts.Where(h => h.Id != hostId), hostId, _state.State.Name);

            if (existing == null)
            {
                existing = new Core.Models.Host
                {
                    Id = hostId,
                    Name = _state.State.Name,
                    CreatedAt = _state.State.CreatedAt == default ? DateTime.UtcNow : _state.State.CreatedAt
                };

                if (legacy != null)
                    CopyUserManagedFields(legacy, existing);

                ApplyProjection(existing);
                await store.Insert(existing);

                if (legacy != null)
                {
                    await MigrateHostReferences(store, legacy.Id, hostId);
                    await store.Remove<Core.Models.Host>(legacy.Id);
                }
            }
            else
            {
                if (legacy != null)
                {
                    CopyUserManagedFields(legacy, existing);
                    await MigrateHostReferences(store, legacy.Id, hostId);
                    await store.Remove<Core.Models.Host>(legacy.Id);
                }

                ApplyProjection(existing);
                await store.Update(existing);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync Host to DocumentDB");
        }
    }

    private void ApplyProjection(Core.Models.Host host)
    {
        host.Name = _state.State.Name;
        host.Platform = _state.State.Platform;
        host.Architecture = _state.State.Architecture;
        host.AgentVersion = _state.State.AgentVersion;
        host.AgentStatus = _state.State.Status;
        host.LastHeartbeat = _state.State.LastHeartbeat;
        host.Labels = new Dictionary<string, string>(_state.State.Labels);
        host.Capabilities = _state.State.Labels
            .Where(kv =>
                kv.Value.Equals("true", StringComparison.OrdinalIgnoreCase) &&
                kv.Key is not "os" and not "arch" and not "role")
            .Select(kv => kv.Key)
            .OrderBy(k => k)
            .ToList();
        host.MaxDockerContainers = _state.State.MaxDockerContainers;
        host.MaxTartVMs = _state.State.MaxTartVMs;
        host.MaxNativeProcesses = _state.State.MaxNativeProcesses;
        host.GroupId = _state.State.GroupId;
        host.IsApproved = true;
        host.UpdatedAt = DateTime.UtcNow;
    }

    private static void CopyUserManagedFields(Core.Models.Host source, Core.Models.Host target)
    {
        target.DisplayName ??= source.DisplayName;
        target.RunnerBasePath ??= source.RunnerBasePath;
        target.WorkDirectory ??= source.WorkDirectory;

        if (target.EnvironmentOverrides.Count == 0 && source.EnvironmentOverrides.Count > 0)
            target.EnvironmentOverrides = new Dictionary<string, string>(source.EnvironmentOverrides);

        if (target.ReportedEnvironment.Count == 0 && source.ReportedEnvironment.Count > 0)
            target.ReportedEnvironment = new Dictionary<string, string>(source.ReportedEnvironment);

        if (target.CreatedAt == default && source.CreatedAt != default)
            target.CreatedAt = source.CreatedAt;
    }

    private static async Task MigrateHostReferences(IDocumentStore store, string oldHostId, string newHostId)
    {
        if (string.Equals(oldHostId, newHostId, StringComparison.Ordinal))
            return;

        var assignments = (await store.Query<RunnerAssignment>().ToList())
            .Where(a => a.HostId == oldHostId)
            .ToList();
        foreach (var assignment in assignments)
        {
            assignment.HostId = newHostId;
            assignment.UpdatedAt = DateTime.UtcNow;
            await store.Update(assignment);
        }

        var instances = (await store.Query<RunnerInstance>().ToList())
            .Where(i => i.HostId == oldHostId)
            .ToList();
        foreach (var instance in instances)
        {
            instance.HostId = newHostId;
            await store.Update(instance);
        }

        var images = (await store.Query<AgentImage>().ToList())
            .Where(i => i.HostId == oldHostId)
            .ToList();
        foreach (var image in images)
        {
            image.HostId = newHostId;
            image.LastReportedAt = DateTime.UtcNow;
            await store.Update(image);
        }

        var rules = (await store.Query<ProvisioningRule>().ToList())
            .Where(r => r.TargetHostId == oldHostId)
            .ToList();
        foreach (var rule in rules)
        {
            rule.TargetHostId = newHostId;
            rule.UpdatedAt = DateTime.UtcNow;
            await store.Update(rule);
        }
    }

    private static Core.Models.Host? FindLegacyProjectionByIp(
        IEnumerable<Core.Models.Host> hosts,
        params string?[] values)
    {
        var ips = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .SelectMany(v => IpAddressRegex.Matches(v!).Cast<Match>().Select(m => m.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ips.Count == 0)
            return null;

        return hosts
            .Where(h => ips.Any(ip =>
                (h.Name?.Contains(ip, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (h.DisplayName?.Contains(ip, StringComparison.OrdinalIgnoreCase) ?? false) ||
                h.Id.Contains(ip, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(h => h.LastHeartbeat ?? h.UpdatedAt)
            .FirstOrDefault();
    }
}
