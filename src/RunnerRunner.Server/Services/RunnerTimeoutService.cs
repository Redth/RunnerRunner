using Microsoft.AspNetCore.SignalR;
using Shiny.DocumentDb;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Hubs;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Periodically scans RunnerInstance records and transitions stuck instances
/// to Failed or Crashed based on timeout rules.
/// </summary>
public class RunnerTimeoutService : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PendingTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StartingTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DynamicRunningTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan StoppingTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HealthCheckStaleTimeout = TimeSpan.FromMinutes(3);

    private readonly ILogger<RunnerTimeoutService> _logger;
    private readonly IServiceProvider _services;
    private readonly IHubContext<AgentHub, IAgentHubClient> _hubContext;

    public RunnerTimeoutService(
        ILogger<RunnerTimeoutService> logger,
        IServiceProvider services,
        IHubContext<AgentHub, IAgentHubClient> hubContext)
    {
        _logger = logger;
        _services = services;
        _hubContext = hubContext;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RunnerTimeoutService started");

        using var timer = new PeriodicTimer(ScanInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ScanForTimeoutsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during runner timeout scan");
            }
        }

        _logger.LogInformation("RunnerTimeoutService stopped");
    }

    private async Task ScanForTimeoutsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        var instances = (await store.Query<RunnerInstance>().ToList()).ToList();
        var now = DateTime.UtcNow;

        foreach (var instance in instances)
        {
            ct.ThrowIfCancellationRequested();

            switch (instance.Status)
            {
                case RunnerInstanceStatus.Pending:
                    await CheckPendingTimeout(store, instance, now);
                    break;
                case RunnerInstanceStatus.Starting:
                    await CheckStartingTimeout(store, instance, now);
                    break;
                case RunnerInstanceStatus.Running:
                    await CheckRunningTimeouts(store, instance, now);
                    break;
                case RunnerInstanceStatus.Stopping:
                    await CheckStoppingTimeout(store, instance, now);
                    break;
            }
        }
    }

    private async Task CheckPendingTimeout(IDocumentStore store, RunnerInstance instance, DateTime now)
    {
        var referenceTime = instance.DeployedAt ?? instance.CreatedAt;
        if (now - referenceTime <= PendingTimeout)
            return;

        _logger.LogWarning(
            "Runner {RunnerName} ({Id}) timed out in Pending state after {Elapsed}",
            instance.RunnerName, instance.Id, now - referenceTime);

        instance.Status = RunnerInstanceStatus.Failed;
        instance.StatusMessage = "Deploy timeout — agent did not acknowledge";
        await store.Update(instance);

        if (instance.ProvisioningMode == "dynamic")
        {
            await TrySendStopRunner(store, instance);
            await store.Remove<RunnerInstance>(instance.Id);
        }
    }

    private async Task CheckStartingTimeout(IDocumentStore store, RunnerInstance instance, DateTime now)
    {
        var referenceTime = instance.StartedAt ?? instance.DeployedAt;
        if (referenceTime == null || now - referenceTime.Value <= StartingTimeout)
            return;

        _logger.LogWarning(
            "Runner {RunnerName} ({Id}) timed out in Starting state after {Elapsed}",
            instance.RunnerName, instance.Id, now - referenceTime.Value);

        instance.Status = RunnerInstanceStatus.Failed;
        instance.StatusMessage = "Registration timeout — runner did not connect to provider";
        await store.Update(instance);

        if (instance.ProvisioningMode == "dynamic")
        {
            await TrySendStopRunner(store, instance);
            await store.Remove<RunnerInstance>(instance.Id);
        }
    }

    private async Task CheckRunningTimeouts(IDocumentStore store, RunnerInstance instance, DateTime now)
    {
        // Dynamic runner running too long without completion webhook
        if (instance.ProvisioningMode == "dynamic"
            && instance.StartedAt != null
            && now - instance.StartedAt.Value > DynamicRunningTimeout)
        {
            _logger.LogWarning(
                "Dynamic runner {RunnerName} ({Id}) timed out after {Elapsed} with no completion webhook",
                instance.RunnerName, instance.Id, now - instance.StartedAt.Value);

            instance.Status = RunnerInstanceStatus.Failed;
            instance.StatusMessage = "Dynamic runner timeout — no completion webhook received";
            await store.Update(instance);

            await TrySendStopRunner(store, instance);
            await store.Remove<RunnerInstance>(instance.Id);
            return;
        }

        // Static runner with stale health check
        if (instance.ProvisioningMode == "static"
            && instance.LastHealthCheck != null
            && now - instance.LastHealthCheck.Value > HealthCheckStaleTimeout)
        {
            _logger.LogWarning(
                "Static runner {RunnerName} ({Id}) has stale health check (last: {LastHealth})",
                instance.RunnerName, instance.Id, instance.LastHealthCheck.Value);

            instance.Status = RunnerInstanceStatus.Crashed;
            instance.StatusMessage = "Health check stale — runner may have crashed";
            await store.Update(instance);
        }
    }

    private async Task CheckStoppingTimeout(IDocumentStore store, RunnerInstance instance, DateTime now)
    {
        // Use StoppedAt as a proxy for when Stopping was set; fall back to StartedAt/DeployedAt/CreatedAt
        var referenceTime = instance.StoppedAt ?? instance.StartedAt ?? instance.DeployedAt ?? instance.CreatedAt;
        if (now - referenceTime <= StoppingTimeout)
            return;

        _logger.LogWarning(
            "Runner {RunnerName} ({Id}) timed out in Stopping state after {Elapsed}",
            instance.RunnerName, instance.Id, now - referenceTime);

        instance.Status = RunnerInstanceStatus.Failed;
        instance.StatusMessage = "Stop timeout";
        await store.Update(instance);
    }

    /// <summary>
    /// Best-effort attempt to send a StopRunner command to the agent hosting this instance.
    /// Mirrors the pattern from DynamicProvisioningService.HandleJobCompleted.
    /// </summary>
    private async Task TrySendStopRunner(IDocumentStore store, RunnerInstance instance)
    {
        try
        {
            var host = (await store.Query<Host>().ToList()).FirstOrDefault(h => h.Id == instance.HostId);
            if (host == null)
            {
                _logger.LogDebug("No host found for instance {Id}, skipping StopRunner", instance.Id);
                return;
            }

            var agent = AgentHub.GetConnectedAgents().Values
                .FirstOrDefault(a => a.AgentInfo.Name == host.Name);

            if (agent == null)
            {
                _logger.LogDebug("No connected agent for host {HostName}, skipping StopRunner", host.Name);
                return;
            }

            await _hubContext.Clients.Client(agent.ConnectionId).StopRunner(new StopRunnerCommand
            {
                InstanceId = instance.Id,
                InstanceHandle = instance.ContainerId ?? instance.VmName ?? instance.ProcessId?.ToString()
            });

            _logger.LogInformation("Sent StopRunner for timed-out instance {RunnerName} ({Id})",
                instance.RunnerName, instance.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send StopRunner for instance {Id}", instance.Id);
        }
    }
}
