using Microsoft.AspNetCore.SignalR;
using Shiny.DocumentDb;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using System.Collections.Concurrent;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Hubs;

public class AgentHub : Hub<IAgentHubClient>, IAgentHubServer
{
    private static readonly ConcurrentDictionary<string, ConnectedAgent> ConnectedAgents = new();
    private readonly ILogger<AgentHub> _logger;
    private readonly IDocumentStore _store;

    public AgentHub(ILogger<AgentHub> logger, IDocumentStore store)
    {
        _logger = logger;
        _store = store;
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
            var hosts = (await _store.Query<Host>().Where(h => h.Name == agent.AgentInfo.Name).ToList()).ToList();
            foreach (var host in hosts)
            {
                host.AgentStatus = AgentStatus.Offline;
                host.UpdatedAt = DateTime.UtcNow;
                await _store.Update(host);
            }
        }

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
        var existingHosts = (await _store.Query<Host>().Where(h => h.Name == agentInfo.Name).ToList()).ToList();
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
            instance.ContainerId = evt.InstanceHandle;
            instance.StartedAt = DateTime.UtcNow;
            await _store.Update(instance);
        }
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
        }
    }

    public async Task RunnerHealthUpdate(RunnerHealthUpdateEvent evt)
    {
        _logger.LogDebug("Runner health update: {InstanceId} - {Status}", evt.InstanceId, evt.Status);

        var instance = await _store.Get<RunnerInstance>(evt.InstanceId);
        if (instance != null)
        {
            instance.Status = evt.Status;
            instance.LastHealthCheck = evt.CheckedAt;
            await _store.Update(instance);
        }
    }

    public async Task Heartbeat(HeartbeatEvent evt)
    {
        if (ConnectedAgents.TryGetValue(evt.AgentId, out var agent))
        {
            agent.LastHeartbeat = DateTime.UtcNow;
            agent.LastMetrics = evt;

            // Update host heartbeat in DB
            var hosts = (await _store.Query<Host>().Where(h => h.Name == agent.AgentInfo.Name).ToList()).ToList();
            foreach (var host in hosts)
            {
                host.LastHeartbeat = DateTime.UtcNow;
                await _store.Update(host);
            }
        }
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
