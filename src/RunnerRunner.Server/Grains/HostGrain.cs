using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Events;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Grains.State;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Grains;

// TODO: Add host-affinity grain placement so this grain activates on the silo
// whose "hostId" metadata matches the grain's primary key. Orleans 10 supports
// SiloMetadata (already configured in HostSilo/Program.cs). Options:
//   1. [SiloMetadataPlacement("hostId")] attribute if available
//   2. Custom IPlacementDirector that reads ISiloMetadataCache
// For now, grains can activate on any silo; the HostSilo will naturally host
// them once per-host silo deployment is in place.
public class HostGrain : Grain, IHostGrain
{
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
        _state.State.CreatedAt = DateTime.UtcNow;

        await _state.WriteStateAsync();
        StartHeartbeatTimer();

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
            var existing = await store.Get<Core.Models.Host>(hostId);

            if (existing != null)
            {
                existing.AgentStatus = _state.State.Status;
                existing.LastHeartbeat = _state.State.LastHeartbeat;
                existing.Labels = new Dictionary<string, string>(_state.State.Labels);
                await store.Update(existing);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync Host to DocumentDB");
        }
    }
}
