using Microsoft.AspNetCore.SignalR;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using System.Collections.Concurrent;

namespace RunnerRunner.Server.Hubs;

public class AgentHub : Hub<IAgentHubClient>, IAgentHubServer
{
    private static readonly ConcurrentDictionary<string, ConnectedAgent> ConnectedAgents = new();
    private readonly ILogger<AgentHub> _logger;

    public AgentHub(ILogger<AgentHub> logger)
    {
        _logger = logger;
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
        }

        await base.OnDisconnectedAsync(exception);
    }

    public Task AgentConnected(AgentInfo agentInfo)
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

        return Task.CompletedTask;
    }

    public Task AgentDisconnected(string agentId)
    {
        ConnectedAgents.TryRemove(agentId, out _);
        _logger.LogInformation("Agent explicitly disconnected: {AgentId}", agentId);
        return Task.CompletedTask;
    }

    public Task RunnerStarted(RunnerStartedEvent evt)
    {
        _logger.LogInformation("Runner started: {RunnerName} (Instance: {InstanceId})", evt.RunnerName, evt.InstanceId);
        return Task.CompletedTask;
    }

    public Task RunnerStopped(RunnerStoppedEvent evt)
    {
        _logger.LogInformation("Runner stopped: {InstanceId} - {Reason}", evt.InstanceId, evt.Reason);
        return Task.CompletedTask;
    }

    public Task RunnerHealthUpdate(RunnerHealthUpdateEvent evt)
    {
        _logger.LogDebug("Runner health update: {InstanceId} - {Status}", evt.InstanceId, evt.Status);
        return Task.CompletedTask;
    }

    public Task Heartbeat(HeartbeatEvent evt)
    {
        if (ConnectedAgents.TryGetValue(evt.AgentId, out var agent))
        {
            agent.LastHeartbeat = DateTime.UtcNow;
            agent.LastMetrics = evt;
        }
        return Task.CompletedTask;
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
