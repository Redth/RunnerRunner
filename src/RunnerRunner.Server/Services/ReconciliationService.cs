using Microsoft.AspNetCore.SignalR;
using Shiny.DocumentDb;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Hubs;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Processes reconciliation reports from agents, marking stale runners and cleaning up orphans.
/// </summary>
public class ReconciliationService : IHostedService, IDisposable
{
    private readonly ILogger<ReconciliationService> _logger;
    private readonly IServiceProvider _services;
    private readonly IHubContext<AgentHub, IAgentHubClient> _hubContext;

    public ReconciliationService(
        ILogger<ReconciliationService> logger,
        IServiceProvider services,
        IHubContext<AgentHub, IAgentHubClient> hubContext)
    {
        _logger = logger;
        _services = services;
        _hubContext = hubContext;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        AgentHub.OnReconciliationReceived += HandleReconciliation;
        _logger.LogInformation("ReconciliationService started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        AgentHub.OnReconciliationReceived -= HandleReconciliation;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        AgentHub.OnReconciliationReceived -= HandleReconciliation;
    }

    private async void HandleReconciliation(ReconciliationReport report)
    {
        try
        {
            using var scope = _services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

            // Find the host by matching agent name from connected agents
            var hostName = report.HostId;
            var hosts = (await store.Query<Host>().ToList()).Where(h => h.Name == hostName).ToList();
            if (hosts.Count == 0)
            {
                _logger.LogWarning("No host found for reconciliation report from {Host}", hostName);
                return;
            }
            var hostId = hosts.First().Id;

            // Load all runner instances for this host
            var dbInstances = (await store.Query<RunnerInstance>().ToList())
                .Where(i => i.HostId == hostId)
                .ToList();

            // Find stale DB records (in DB but NOT in report)
            foreach (var instance in dbInstances)
            {
                if (instance.Status is not (RunnerInstanceStatus.Running
                    or RunnerInstanceStatus.Starting
                    or RunnerInstanceStatus.Pending))
                    continue;

                var matched = report.Runners.Any(r =>
                    (!string.IsNullOrEmpty(r.InstanceId) && r.InstanceId == instance.Id) ||
                    (!string.IsNullOrEmpty(r.ContainerId) && r.ContainerId == instance.ContainerId) ||
                    (!string.IsNullOrEmpty(r.VmName) && r.VmName == instance.VmName) ||
                    (r.ProcessId.HasValue && r.ProcessId == instance.ProcessId));

                if (!matched)
                {
                    var newStatus = instance.Status == RunnerInstanceStatus.Running
                        ? RunnerInstanceStatus.Crashed
                        : RunnerInstanceStatus.Stopped;

                    instance.Status = newStatus;
                    instance.StoppedAt = DateTime.UtcNow;
                    await store.Update(instance);

                    _logger.LogWarning(
                        "Marking stale runner {Name} as {Status} — not found on host",
                        instance.RunnerName, newStatus);
                }
            }

            // Find orphans on host (in report but NOT in DB)
            foreach (var runner in report.Runners.Where(r => r.IsRunning))
            {
                var hasMatch = dbInstances.Any(i =>
                    (!string.IsNullOrEmpty(runner.InstanceId) && runner.InstanceId == i.Id) ||
                    (!string.IsNullOrEmpty(runner.ContainerId) && runner.ContainerId == i.ContainerId) ||
                    (!string.IsNullOrEmpty(runner.VmName) && runner.VmName == i.VmName));

                if (!hasMatch)
                {
                    _logger.LogWarning(
                        "Sending cleanup for orphaned {Backend} resource {Id} on host {Host}",
                        runner.Backend, runner.InstanceId, hostName);

                    var command = new CleanupOrphanCommand
                    {
                        Backend = runner.Backend,
                        ContainerId = runner.ContainerId,
                        VmName = runner.VmName,
                        ProcessId = runner.ProcessId
                    };

                    var agent = AgentHub.GetConnectedAgents().Values
                        .FirstOrDefault(a => a.AgentInfo.Name == hostName);

                    if (agent != null)
                    {
                        await _hubContext.Clients.Client(agent.ConnectionId).CleanupOrphan(command);
                    }
                    else
                    {
                        _logger.LogWarning("No connected agent found for host {Host} to send cleanup", hostName);
                    }
                }
            }

            // Update health for matched runners
            foreach (var instance in dbInstances)
            {
                var matched = report.Runners.Any(r =>
                    (!string.IsNullOrEmpty(r.InstanceId) && r.InstanceId == instance.Id) ||
                    (!string.IsNullOrEmpty(r.ContainerId) && r.ContainerId == instance.ContainerId) ||
                    (!string.IsNullOrEmpty(r.VmName) && r.VmName == instance.VmName) ||
                    (r.ProcessId.HasValue && r.ProcessId == instance.ProcessId));

                if (matched)
                {
                    instance.LastHealthCheck = DateTime.UtcNow;
                    await store.Update(instance);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing reconciliation report from {Host}", report.HostId);
        }
    }
}
