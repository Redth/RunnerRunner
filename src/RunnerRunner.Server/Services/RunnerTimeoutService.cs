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
    private static readonly TimeSpan DynamicPickupTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DynamicRunningTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan StoppingTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HealthCheckStaleTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DynamicTerminalRetention = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CompletedWebhookRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan StaleNoMatchRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan IgnoredWebhookRetention = TimeSpan.FromDays(2);
    private static readonly TimeSpan RetryAfterRunnerFailureDelay = TimeSpan.FromSeconds(10);

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

            await CleanupExpiredDynamicRecord(store, instance, now);
        }

        await CleanupExpiredWebhookEvents(store, now, ct);
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
            await TryRequeueLinkedWebhookEvent(
                store,
                instance,
                now,
                "Runner deployment timed out before the host acknowledged it; retrying provisioning");
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
            await TryRequeueLinkedWebhookEvent(
                store,
                instance,
                now,
                "Runner registration timed out before it connected to the provider; retrying provisioning");
            await TrySendStopRunner(store, instance);
            await store.Remove<RunnerInstance>(instance.Id);
        }
    }

    private async Task CheckRunningTimeouts(IDocumentStore store, RunnerInstance instance, DateTime now)
    {
        if (instance.ProvisioningMode == "dynamic"
            && !string.IsNullOrWhiteSpace(instance.WebhookEventId))
        {
            var linkedEvent = await store.Get<WebhookEvent>(instance.WebhookEventId);
            var eventStatus = linkedEvent?.Status?.Trim();
            if (eventStatus is "completed" or "timed_out" or "ignored" or "rejected")
            {
                _logger.LogWarning(
                    "Dynamic runner {RunnerName} ({Id}) is still active after its event resolved with status {EventStatus}",
                    instance.RunnerName, instance.Id, eventStatus);

                instance.Status = RunnerInstanceStatus.Failed;
                instance.StatusMessage = $"Dynamic runner cleanup after event resolved: {eventStatus}";
                await store.Update(instance);

                await TrySendStopRunner(store, instance);
                await store.Remove<RunnerInstance>(instance.Id);
                return;
            }

            if (instance.StartedAt != null
                && now - instance.StartedAt.Value > DynamicPickupTimeout
                && !string.Equals(eventStatus, "in_progress", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Dynamic runner {RunnerName} ({Id}) never picked up its queued job after {Elapsed}; recycling it",
                    instance.RunnerName, instance.Id, now - instance.StartedAt.Value);

                instance.Status = RunnerInstanceStatus.Failed;
                instance.StatusMessage = "Dynamic runner pickup timeout — job never started on provider";
                await store.Update(instance);

                await TryRequeueLinkedWebhookEvent(
                    store,
                    instance,
                    now,
                    "Runner came online but never picked up the queued job; provisioning will be retried");
                await TrySendStopRunner(store, instance);
                await store.Remove<RunnerInstance>(instance.Id);
                return;
            }
        }

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

            await TryRequeueLinkedWebhookEvent(
                store,
                instance,
                now,
                "Runner exceeded the dynamic execution timeout before the job completed; provisioning will be retried if the provider still shows it queued");
            await TrySendStopRunner(store, instance);
            await store.Remove<RunnerInstance>(instance.Id);
            return;
        }

        var lastSeenAt = instance.LastHealthCheck ?? instance.StartedAt;
        if (lastSeenAt != null
            && now - lastSeenAt.Value > HealthCheckStaleTimeout)
        {
            _logger.LogWarning(
                "Runner {RunnerName} ({Id}) has stale health check (last seen: {LastHealth})",
                instance.RunnerName, instance.Id, lastSeenAt.Value);

            instance.Status = RunnerInstanceStatus.Crashed;
            instance.StatusMessage = "Health check stale — runner may have crashed";
            await store.Update(instance);

            if (instance.ProvisioningMode == "dynamic")
            {
                await TryRequeueLinkedWebhookEvent(
                    store,
                    instance,
                    now,
                    "Runner health checks went stale before the job was picked up; provisioning will be retried");
                await TrySendStopRunner(store, instance);
                await store.Remove<RunnerInstance>(instance.Id);
            }
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
                .FirstOrDefault(a =>
                    a.AgentInfo.Name == host.Name
                    || a.AgentInfo.AgentId == host.Name
                    || a.AgentInfo.AgentId == host.Id);

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

    private async Task TryRequeueLinkedWebhookEvent(
        IDocumentStore store,
        RunnerInstance instance,
        DateTime now,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(instance.WebhookEventId))
            return;

        var linkedEvent = await store.Get<WebhookEvent>(instance.WebhookEventId);
        if (!PrepareLinkedEventRetry(linkedEvent, now, reason))
            return;

        await store.Update(linkedEvent!);
        _logger.LogInformation(
            "Re-queued webhook event {EventId} after runner failure for instance {InstanceId}",
            linkedEvent!.Id,
            instance.Id);
    }

    internal static bool PrepareLinkedEventRetry(WebhookEvent? linkedEvent, DateTime now, string reason)
    {
        if (linkedEvent == null
            || !string.Equals(linkedEvent.Action, "queued", StringComparison.OrdinalIgnoreCase)
            || linkedEvent.IsTerminal)
            return false;

        linkedEvent.ResolvedAt = null;
        linkedEvent.InstanceId = null;
        linkedEvent.ScheduleRetry(
            reason,
            now,
            RetryAfterRunnerFailureDelay,
            status: "pending",
            countAttempt: false);
        return true;
    }

    private static async Task CleanupExpiredDynamicRecord(IDocumentStore store, RunnerInstance instance, DateTime now)
    {
        if (instance.ProvisioningMode != "dynamic"
            || instance.Status is not (RunnerInstanceStatus.Stopped or RunnerInstanceStatus.Failed or RunnerInstanceStatus.Crashed))
        {
            return;
        }

        var terminalAt = instance.StoppedAt ?? instance.LastHealthCheck ?? instance.StartedAt ?? instance.DeployedAt ?? instance.CreatedAt;
        if (now - terminalAt > DynamicTerminalRetention)
            await store.Remove<RunnerInstance>(instance.Id);
    }

    private async Task CleanupExpiredWebhookEvents(IDocumentStore store, DateTime now, CancellationToken ct)
    {
        var webhookEvents = (await store.Query<WebhookEvent>().ToList()).ToList();
        var removed = 0;

        foreach (var evt in webhookEvents)
        {
            ct.ThrowIfCancellationRequested();

            if (!ShouldRemoveWebhookEvent(evt, now))
                continue;

            await store.Remove<WebhookEvent>(evt.Id);
            removed++;
        }

        if (removed > 0)
            _logger.LogInformation("Cleaned up {Count} expired webhook event records", removed);
    }

    private static bool ShouldRemoveWebhookEvent(WebhookEvent evt, DateTime now)
    {
        var age = now - evt.ReceivedAt;

        if (evt.Status is "no_match" or "pending_host_match" or "pending_config")
            return age > StaleNoMatchRetention;

        if (evt.Status is "completed" or "timed_out")
            return age > CompletedWebhookRetention;

        if (evt.Status is "ignored" or "rejected")
            return age > IgnoredWebhookRetention;

        if (evt.Status is "in_progress" or "provisioned")
            return age > CompletedWebhookRetention;

        return false;
    }
}
