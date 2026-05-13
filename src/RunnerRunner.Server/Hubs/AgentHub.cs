using Microsoft.AspNetCore.SignalR;
using Shiny.DocumentDb;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Services;
using System.Collections.Concurrent;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Hubs;

/// <summary>
/// LEGACY: SignalR hub kept only for compatibility with pre-HostSilo clients.
/// Supported release artifacts use Orleans stream commands to HostSilo instead.
/// </summary>
public class AgentHub : Hub<IAgentHubClient>, IAgentHubServer
{
    private static readonly ConcurrentDictionary<string, ConnectedAgent> ConnectedAgents = new();
    public static event Action? OnQueueRelevantChange;
    private readonly ILogger<AgentHub> _logger;
    private readonly IDocumentStore _store;
    private readonly IGrainFactory _grainFactory;

    public AgentHub(ILogger<AgentHub> logger, IDocumentStore store, IGrainFactory grainFactory)
    {
        _logger = logger;
        _store = store;
        _grainFactory = grainFactory;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Agent connection established: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var agentId = ConnectedAgents.Values
            .FirstOrDefault(a => a.ConnectionId == Context.ConnectionId)?.AgentInfo.AgentId;

        if (agentId != null && ConnectedAgents.TryRemove(agentId, out var agent))
        {
            _logger.LogInformation("Agent disconnected: {AgentName} ({AgentId})", agent.AgentInfo.Name, agentId);

            // Mark host as offline
            var hosts = (await _store.Query<Host>().ToList()).Where(h => h.Name == agent.AgentInfo.Name).ToList();
            foreach (var host in hosts)
            {
                host.AgentStatus = AgentStatus.Offline;
                host.UpdatedAt = DateTime.UtcNow;
                await _store.Update(host);
            }

            // Sync with Orleans grains
            try
            {
                var hostGrain = _grainFactory.GetGrain<IHostGrain>(agent.AgentInfo.AgentId);
                await hostGrain.MarkOffline();

                var scheduler = _grainFactory.GetGrain<ISchedulerGrain>(0);
                await scheduler.UnregisterHost(agent.AgentInfo.AgentId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync OnDisconnectedAsync with Orleans grains for {AgentId}", agentId);
            }
        }

        OnQueueRelevantChange?.Invoke();

        await base.OnDisconnectedAsync(exception);
    }

    public async Task AgentConnected(AgentInfo agentInfo)
    {
        var connectedAgent = new ConnectedAgent
        {
            ConnectionId = Context.ConnectionId,
            AgentInfo = agentInfo,
            ConnectedAt = DateTime.UtcNow
        };

        ConnectedAgents.AddOrUpdate(agentInfo.AgentId, connectedAgent, (_, _) => connectedAgent);
        _logger.LogInformation(
            "Agent registered: {AgentName} ({AgentId}) - {Platform} {Architecture}",
            agentInfo.Name, agentInfo.AgentId, agentInfo.Platform, agentInfo.Architecture);

        // Upsert host record in the document store
        var existingHosts = (await _store.Query<Host>().ToList()).Where(h => h.Name == agentInfo.Name).ToList();
        if (existingHosts.Count != 0)
        {
            var host = existingHosts[0];
            host.AgentStatus = AgentStatus.Online;
            host.Platform = agentInfo.Platform;
            host.OsVersion = agentInfo.OsVersion;
            host.Architecture = agentInfo.Architecture;
            host.AgentVersion = agentInfo.AgentVersion;
            host.Capabilities = agentInfo.Capabilities;
            host.LastHeartbeat = DateTime.UtcNow;
            host.IsApproved = true;
            host.UpdatedAt = DateTime.UtcNow;
            await _store.Update(host);
        }
        else
        {
            var host = new Host
            {
                Name = agentInfo.Name,
                Platform = agentInfo.Platform,
                AgentStatus = AgentStatus.Online,
                OsVersion = agentInfo.OsVersion,
                Architecture = agentInfo.Architecture,
                AgentVersion = agentInfo.AgentVersion,
                Capabilities = agentInfo.Capabilities,
                LastHeartbeat = DateTime.UtcNow,
                IsApproved = true
            };
            await _store.Insert(host);
        }

        // Sync with Orleans grains
        try
        {
            var hostGrain = _grainFactory.GetGrain<IHostGrain>(agentInfo.AgentId);
            await hostGrain.Register(agentInfo.Name, agentInfo.Platform, agentInfo.Architecture, agentInfo.AgentVersion ?? "");

            var scheduler = _grainFactory.GetGrain<ISchedulerGrain>(0);
            await scheduler.RegisterHost(agentInfo.AgentId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync AgentConnected with Orleans grains for {AgentId}", agentInfo.AgentId);
        }

        OnQueueRelevantChange?.Invoke();
    }

    public Task AgentDisconnected(string agentId)
    {
        ConnectedAgents.TryRemove(agentId, out _);
        _logger.LogInformation("Agent explicitly disconnected: {AgentId}", agentId);
        return Task.CompletedTask;
    }

    public async Task RunnerStarted(RunnerStartedEvent evt)
    {
        _logger.LogInformation("Runner started: {RunnerName} (Instance: {InstanceId})", evt.RunnerName, evt.InstanceId);

        var instance = await _store.Get<RunnerInstance>(evt.InstanceId);
        if (instance != null)
        {
            instance.Status = RunnerInstanceStatus.Running;
            instance.StartedAt = DateTime.UtcNow;

            switch (evt.Backend)
            {
                case ExecutionBackend.Docker:
                    instance.ContainerId = evt.InstanceHandle;
                    break;
                case ExecutionBackend.Tart:
                    instance.VmName = evt.InstanceHandle;
                    break;
                case ExecutionBackend.Native:
                    if (int.TryParse(evt.InstanceHandle, out var pid))
                        instance.ProcessId = pid;
                    break;
                default:
                    instance.ContainerId = evt.InstanceHandle;
                    break;
            }

            await _store.Update(instance);
        }

        // Sync with Orleans grains
        try
        {
            var runnerGrain = _grainFactory.GetGrain<IRunnerInstanceGrain>(evt.InstanceId);
            switch (evt.Backend)
            {
                case ExecutionBackend.Docker:
                    await runnerGrain.MarkRunning(containerId: evt.InstanceHandle);
                    break;
                case ExecutionBackend.Tart:
                    await runnerGrain.MarkRunning(vmName: evt.InstanceHandle);
                    break;
                case ExecutionBackend.Native:
                    await runnerGrain.MarkRunning(
                        processId: int.TryParse(evt.InstanceHandle, out var pid) ? pid : null);
                    break;
                default:
                    await runnerGrain.MarkRunning(containerId: evt.InstanceHandle);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync RunnerStarted with Orleans grains for {InstanceId}", evt.InstanceId);
        }

        OnQueueRelevantChange?.Invoke();
    }

    public async Task RunnerStopped(RunnerStoppedEvent evt)
    {
        _logger.LogInformation("Runner stopped: {InstanceId} - {Reason}", evt.InstanceId, evt.Reason);

        var instance = await _store.Get<RunnerInstance>(evt.InstanceId);
        if (instance != null)
        {
            instance.Status = evt.Reason == "crashed" ? RunnerInstanceStatus.Crashed
                : evt.ErrorMessage != null ? RunnerInstanceStatus.Failed
                : RunnerInstanceStatus.Stopped;
            instance.ErrorMessage = evt.ErrorMessage;
            instance.StoppedAt = DateTime.UtcNow;
            await _store.Update(instance);

            if (instance.ProvisioningMode == "dynamic"
                && !string.IsNullOrWhiteSpace(instance.WebhookEventId))
            {
                var webhookEvent = await _store.Get<WebhookEvent>(instance.WebhookEventId);
                var now = DateTime.UtcNow;
                if (webhookEvent is not null && RunnerTimeoutService.PrepareLinkedEventRetry(
                        webhookEvent,
                        now,
                        $"Runner stopped before the queued job was confirmed in progress: {evt.ErrorMessage ?? evt.Reason}"))
                {
                    await _store.Update(webhookEvent);

                    _logger.LogWarning(
                        "Re-queued webhook event {EventId} after dynamic runner stop/failure for instance {InstanceId}",
                        webhookEvent.Id, instance.Id);
                }
            }
        }

        // Sync with Orleans grains
        try
        {
            var runnerGrain = _grainFactory.GetGrain<IRunnerInstanceGrain>(evt.InstanceId);
            if (evt.Reason == "crashed")
                await runnerGrain.MarkCrashed(evt.Reason);
            else
                await runnerGrain.MarkStopped();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync RunnerStopped with Orleans grains for {InstanceId}", evt.InstanceId);
        }

        OnQueueRelevantChange?.Invoke();
    }

    public async Task RunnerHealthUpdate(RunnerHealthUpdateEvent evt)
    {
        _logger.LogDebug("Runner health update: {InstanceId} - {Status}", evt.InstanceId, evt.Status);

        var instance = await _store.Get<RunnerInstance>(evt.InstanceId);
        if (instance != null)
        {
            instance.Status = evt.Status;
            instance.LastHealthCheck = evt.CheckedAt;
            if (!string.IsNullOrEmpty(evt.StatusMessage))
                instance.StatusMessage = evt.StatusMessage;
            await _store.Update(instance);
        }

        // Sync with Orleans grains
        try
        {
            var runnerGrain = _grainFactory.GetGrain<IRunnerInstanceGrain>(evt.InstanceId);
            await runnerGrain.UpdateHealth(evt.StatusMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync RunnerHealthUpdate with Orleans grains for {InstanceId}", evt.InstanceId);
        }
    }

    public async Task Heartbeat(HeartbeatEvent evt)
    {
        if (ConnectedAgents.TryGetValue(evt.AgentId, out var agent))
        {
            agent.LastHeartbeat = DateTime.UtcNow;
            agent.LastMetrics = evt;

            var hosts = (await _store.Query<Host>().ToList()).Where(h => h.Name == agent.AgentInfo.Name).ToList();
            foreach (var host in hosts)
            {
                host.LastHeartbeat = DateTime.UtcNow;
                await _store.Update(host);
            }
        }

        // Sync with Orleans grains
        try
        {
            var hostGrain = _grainFactory.GetGrain<IHostGrain>(evt.AgentId);
            await hostGrain.RecordHeartbeat(Context.ConnectionId, evt.RunningInstanceCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync Heartbeat with Orleans grains for {AgentId}", evt.AgentId);
        }
    }

    public async Task ImageListResponse(ImageListEvent evt)
    {
        _logger.LogInformation("Received image list from agent {HostId}: {Count} images", evt.HostId, evt.Images.Count);

        var hostId = await ResolveHostIdAsync(evt.HostId);

        // Clear old cached images for this host and replace
        var oldImages = (await _store.Query<AgentImage>().ToList()).Where(i => i.HostId == hostId).ToList();
        foreach (var old in oldImages)
            await _store.Remove<AgentImage>(old.Id);

        foreach (var img in evt.Images)
        {
            await _store.Insert(new AgentImage
            {
                HostId = hostId,
                ImageType = img.ImageType,
                Repository = img.Repository,
                Tag = img.Tag,
                ImageId = img.ImageId,
                SizeBytes = img.SizeBytes,
                ImageCreatedAt = img.CreatedAt,
                LastReportedAt = DateTime.UtcNow
            });
        }
    }

    public async Task ImageRefreshStatus(ImageRefreshStatusEvent evt)
    {
        var mappedEvent = new ImageRefreshStatusEvent
        {
            HostId = await ResolveHostIdAsync(evt.HostId),
            Stage = evt.Stage,
            Message = evt.Message,
            IsComplete = evt.IsComplete,
            Success = evt.Success
        };

        _logger.LogDebug("Image refresh status for {HostId}: {Stage} - {Message}",
            mappedEvent.HostId, mappedEvent.Stage, mappedEvent.Message);

        OnImageRefreshStatusReceived?.Invoke(mappedEvent);
    }

    public async Task ImagePullProgress(ImagePullProgressEvent evt)
    {
        _logger.LogDebug("Pull progress {Image}: {Percent:F1}%", evt.ImageName, evt.ProgressPercent);
        OnImagePullProgressReceived?.Invoke(new ImagePullProgressEvent
        {
            HostId = await ResolveHostIdAsync(evt.HostId),
            ImageType = evt.ImageType,
            ImageName = evt.ImageName,
            ProgressPercent = evt.ProgressPercent,
            BytesDownloaded = evt.BytesDownloaded,
            BytesTotal = evt.BytesTotal,
            Status = evt.Status
        });
    }

    public async Task ImagePullComplete(ImagePullCompleteEvent evt)
    {
        _logger.LogInformation("Pull complete {Image}: {Success}", evt.ImageName, evt.Success);
        OnImagePullCompleteReceived?.Invoke(new ImagePullCompleteEvent
        {
            HostId = await ResolveHostIdAsync(evt.HostId),
            ImageType = evt.ImageType,
            ImageName = evt.ImageName,
            Success = evt.Success,
            Error = evt.Error
        });

        // Refresh image list from the agent that just finished pulling
        var agent = ConnectedAgents.Values.FirstOrDefault(a => a.AgentInfo.AgentId == evt.HostId);
        if (agent != null)
        {
            // Ask agent to re-report its image list
            // (handled via the hub context in the calling code)
        }
    }

    public async Task ImageDeleted(ImageDeletedEvent evt)
    {
        _logger.LogInformation("Image deleted {ImageId}: {Success}", evt.ImageId, evt.Success);
        OnImageDeletedReceived?.Invoke(new ImageDeletedEvent
        {
            HostId = await ResolveHostIdAsync(evt.HostId),
            ImageType = evt.ImageType,
            ImageId = evt.ImageId,
            Success = evt.Success,
            Error = evt.Error
        });
    }

    public async Task HostEnvironmentResponse(HostEnvironmentEvent evt)
    {
        _logger.LogInformation("Received {Count} host env vars from agent {HostId}",
            evt.EnvironmentVariables.Count, evt.HostId);

        var agent = ConnectedAgents.Values.FirstOrDefault(a => a.AgentInfo.AgentId == evt.HostId);
        var hostName = agent?.AgentInfo.Name;
        var host = hostName != null
            ? (await _store.Query<Host>().ToList()).FirstOrDefault(h => h.Name == hostName)
            : null;

        if (host != null)
        {
            host.ReportedEnvironment = evt.EnvironmentVariables;
            host.UpdatedAt = DateTime.UtcNow;
            await _store.Update(host);
        }
    }

    public async Task RunnerDiscovery(RunnerDiscoveryEvent evt)
    {
        _logger.LogInformation("Agent {HostId} discovered {Count} managed runners",
            evt.HostId, evt.Runners.Count);

        var agent = ConnectedAgents.Values.FirstOrDefault(a => a.AgentInfo.AgentId == evt.HostId);
        var hostName = agent?.AgentInfo.Name;
        var host = hostName != null
            ? (await _store.Query<Host>().ToList()).FirstOrDefault(h => h.Name == hostName)
            : null;

        if (host == null) return;

        var dbInstances = (await _store.Query<RunnerInstance>().ToList())
            .Where(i => i.HostId == host.Id).ToList();

        foreach (var discovered in evt.Runners)
        {
            // Match by instance ID or runner name
            var existing = dbInstances.FirstOrDefault(i =>
                i.Id == discovered.InstanceId ||
                i.RunnerName == discovered.RunnerName);

            if (existing != null)
            {
                // Update status from actual container state
                existing.ContainerId = discovered.ContainerId;
                existing.Status = discovered.IsRunning
                    ? RunnerInstanceStatus.Running
                    : RunnerInstanceStatus.Stopped;
                existing.LastHealthCheck = DateTime.UtcNow;
                if (!discovered.IsRunning)
                    existing.StoppedAt ??= DateTime.UtcNow;
                await _store.Update(existing);
                _logger.LogInformation("Reconciled instance {RunnerName}: {Status}",
                    discovered.RunnerName, existing.Status);
            }
            else if (discovered.IsRunning)
            {
                // Ignore unmanaged backend resources here. The Runners page should reflect
                // orchestrated RunnerInstance lifecycles, not every raw container/process
                // a host happens to report.
                _logger.LogWarning(
                    "Ignoring unmanaged discovered runner {RunnerName} on host {HostName} (instanceId: {InstanceId})",
                    discovered.RunnerName, host.Name, discovered.InstanceId);
            }
        }

        // Mark DB instances as stopped if not found in discovered containers
        foreach (var dbInst in dbInstances.Where(i =>
            i.Status is RunnerInstanceStatus.Running or RunnerInstanceStatus.Starting))
        {
            var stillExists = evt.Runners.Any(d =>
                d.InstanceId == dbInst.Id ||
                d.RunnerName == dbInst.RunnerName);

            if (!stillExists)
            {
                dbInst.Status = RunnerInstanceStatus.Stopped;
                dbInst.StoppedAt = DateTime.UtcNow;
                await _store.Update(dbInst);
                _logger.LogInformation("Marked {RunnerName} as stopped (container not found on host)",
                    dbInst.RunnerName);
            }
        }
    }

    // Static events for UI components to subscribe to
    public static event Action<ImageRefreshStatusEvent>? OnImageRefreshStatusReceived;
    public static event Action<ImagePullProgressEvent>? OnImagePullProgressReceived;
    public static event Action<ImagePullCompleteEvent>? OnImagePullCompleteReceived;
    public static event Action<ImageDeletedEvent>? OnImageDeletedReceived;
    public static event Action<HostLogsEvent>? OnHostLogsReceived;
    public static event Action<RunnerLogsEvent>? OnRunnerLogsReceived;

    public Task HostLogsResponse(HostLogsEvent evt)
    {
        _logger.LogDebug("Received host logs for {HostId}", evt.HostId);
        OnHostLogsReceived?.Invoke(evt);
        return Task.CompletedTask;
    }

    public Task RunnerLogsResponse(RunnerLogsEvent evt)
    {
        _logger.LogDebug("Received runner logs for {Handle}", evt.InstanceHandle);
        OnRunnerLogsReceived?.Invoke(evt);
        return Task.CompletedTask;
    }

    public static event Action<ReconciliationReport>? OnReconciliationReceived;

    public Task Reconciliation(ReconciliationReport report)
    {
        _logger.LogDebug("Received reconciliation report from {Host} with {Count} runners",
            report.HostId, report.Runners.Count);
        OnReconciliationReceived?.Invoke(report);
        return Task.CompletedTask;
    }

    private async Task<string> ResolveHostIdAsync(string hostOrAgentId)
    {
        var hosts = await _store.Query<Host>().ToList();
        var directHost = hosts.FirstOrDefault(h =>
            string.Equals(h.Id, hostOrAgentId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(h.Name, hostOrAgentId, StringComparison.OrdinalIgnoreCase));
        if (directHost != null)
            return directHost.Id;

        var agent = ConnectedAgents.Values.FirstOrDefault(a =>
            string.Equals(a.AgentInfo.AgentId, hostOrAgentId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.AgentInfo.Name, hostOrAgentId, StringComparison.OrdinalIgnoreCase));
        if (agent == null)
            return hostOrAgentId;

        var matchedHost = hosts.FirstOrDefault(h =>
            string.Equals(h.Name, agent.AgentInfo.Name, StringComparison.OrdinalIgnoreCase));

        return matchedHost?.Id ?? hostOrAgentId;
    }

    // Static accessors for the orchestration engine
    public static IReadOnlyDictionary<string, ConnectedAgent> GetConnectedAgents() => ConnectedAgents;

    public static ConnectedAgent? GetAgent(string agentId) =>
        ConnectedAgents.TryGetValue(agentId, out var agent) ? agent : null;
}

public class ConnectedAgent
{
    public required string ConnectionId { get; set; }
    public required AgentInfo AgentInfo { get; set; }
    public DateTime ConnectedAt { get; set; }
    public DateTime? LastHeartbeat { get; set; }
    public HeartbeatEvent? LastMetrics { get; set; }
}
