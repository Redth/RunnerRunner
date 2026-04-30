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
            var registrationCleanup = scope.ServiceProvider.GetRequiredService<RunnerRegistrationCleanupService>();

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

                var matchedRunner = report.Runners.FirstOrDefault(r => MatchesRunner(instance, r));

                if (matchedRunner == null)
                {
                    var newStatus = instance.Status == RunnerInstanceStatus.Running
                        ? RunnerInstanceStatus.Crashed
                        : RunnerInstanceStatus.Stopped;

                    instance.Status = newStatus;
                    instance.StatusMessage = "Host no longer reported the runner";
                    instance.StoppedAt = DateTime.UtcNow;
                    await store.Update(instance);

                    await TryRecoverDynamicRunnerAsync(
                        store,
                        registrationCleanup,
                        instance,
                        "Runner disappeared from the host before the queued job started; provisioning will be retried");

                    _logger.LogWarning(
                        "Marking stale runner {Name} as {Status} — not found on host",
                        instance.RunnerName, newStatus);
                }
                else if (!IsRunnerStillActive(matchedRunner))
                {
                    instance.Status = RunnerInstanceStatus.Stopped;
                    instance.StatusMessage = $"Host reported runner exited ({matchedRunner.Status})";
                    instance.StoppedAt = DateTime.UtcNow;
                    await store.Update(instance);

                    _logger.LogInformation(
                        "Marking runner {Name} as stopped — host reported it exited (status: {HostStatus})",
                        instance.RunnerName, matchedRunner.Status);

                    await TryRecoverDynamicRunnerAsync(
                        store,
                        registrationCleanup,
                        instance,
                        $"Runner exited on the host before the queued job started ({matchedRunner.Status}); provisioning will be retried");
                    await SendCleanupCommandAsync(hostName, matchedRunner);
                }
            }

            // Find orphans on host (in report but NOT in DB)
            // Two paths:
            //   - IsRunning=true: try to ADOPT (create a RunnerInstance record so we
            //     don't kill an actively-executing JIT runner that survived a silo
            //     crash/restart). The job is real work in progress.
            //   - IsRunning=false: send cleanup command (the original behavior for
            //     dead leftover directories).
            var profiles = await store.Query<RunnerProfile>().ToList();
            foreach (var runner in report.Runners)
            {
                var hasMatch = dbInstances.Any(i => MatchesRunner(i, runner));
                if (hasMatch) continue;

                if (runner.IsRunning)
                {
                    var adopted = await TryAdoptOrphanRunnerAsync(store, hostId, hostName, runner, profiles);
                    if (adopted != null)
                    {
                        dbInstances.Add(adopted);
                        continue;
                    }
                    // No matching profile to adopt under — fall through to cleanup.
                    _logger.LogWarning(
                        "Could not adopt running orphan {RunnerName} on host {Host}; no matching profile. Sending cleanup.",
                        runner.RunnerName, hostName);
                }

                _logger.LogWarning(
                    "Sending cleanup for orphaned {Backend} resource {Resource} on host {Host} (running: {Running}, status: {Status})",
                    runner.Backend,
                    runner.ContainerId ?? runner.VmName ?? runner.ProcessId?.ToString() ?? runner.RunnerName,
                    hostName,
                    runner.IsRunning,
                    runner.Status);

                var command = new CleanupOrphanCommand
                {
                    Backend = runner.Backend,
                    ContainerId = runner.ContainerId,
                    VmName = runner.VmName,
                    ProcessId = runner.ProcessId,
                    InstanceDir = runner.InstanceDir
                };

                var agent = AgentHub.GetConnectedAgents().Values
                    .FirstOrDefault(a => a.AgentInfo.Name == hostName);

                if (agent != null)
                    await _hubContext.Clients.Client(agent.ConnectionId).CleanupOrphan(command);
                else
                    _logger.LogWarning("No connected agent found for host {Host} to send cleanup", hostName);
            }

            // Update health for matched runners
            foreach (var instance in dbInstances)
            {
                var matched = report.Runners.FirstOrDefault(r => MatchesRunner(instance, r));

                if (matched != null && IsRunnerStillActive(matched))
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

    internal static bool MatchesRunner(RunnerInstance instance, DiscoveredRunnerInfo runner)
    {
        if (!string.IsNullOrEmpty(runner.InstanceId) && runner.InstanceId == instance.Id)
            return true;

        if (!string.IsNullOrEmpty(runner.ContainerId) && runner.ContainerId == instance.ContainerId)
            return true;

        if (!string.IsNullOrEmpty(runner.VmName) && runner.VmName == instance.VmName)
            return true;

        if (runner.ProcessId.HasValue && runner.ProcessId == instance.ProcessId)
            return true;

        return !string.IsNullOrEmpty(runner.RunnerName)
            && string.Equals(runner.RunnerName, instance.RunnerName, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsRunnerStillActive(DiscoveredRunnerInfo runner)
    {
        if (runner.IsRunning) return true;

        // A container/VM that the agent has just created but not yet started
        // reports status="created" (Docker) and IsRunning=false. The previous
        // heuristic (only "running" counts) treated those as exited and triggered
        // an orphan-cleanup that killed the resource the agent was simultaneously
        // bringing up — the classic deploy/heartbeat race that produces Windows
        // exit code 3221225786 (CTRL_C_EXIT) immediately after StartContainer.
        // Treat known transitional/non-terminal statuses as still active so the
        // deploy can complete; reconciliation will catch genuinely-stopped
        // resources on the next heartbeat once the agent reports them as such.
        var status = runner.Status ?? string.Empty;
        return TransitionalStatuses.Contains(status);
    }

    private static readonly HashSet<string> TransitionalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "running",        // belt-and-suspenders
        "created",        // Docker: container created but not started yet
        "restarting",     // Docker: in the middle of a restart
        "starting",       // generic transitional
        "paused"          // Docker: actively paused, not exited
    };

    internal static bool TryPrepareDynamicWebhookRetry(
        RunnerInstance instance,
        WebhookEvent? linkedEvent,
        DateTime nowUtc,
        string reason)
        => string.Equals(instance.ProvisioningMode, "dynamic", StringComparison.OrdinalIgnoreCase)
            && RunnerTimeoutService.PrepareLinkedEventRetry(linkedEvent, nowUtc, reason);

    private async Task TryRecoverDynamicRunnerAsync(
        IDocumentStore store,
        RunnerRegistrationCleanupService registrationCleanup,
        RunnerInstance instance,
        string reason)
    {
        if (string.Equals(instance.ProvisioningMode, "dynamic", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(instance.WebhookEventId))
        {
            var linkedEvent = await store.Get<WebhookEvent>(instance.WebhookEventId);
            if (TryPrepareDynamicWebhookRetry(instance, linkedEvent, DateTime.UtcNow, reason) && linkedEvent != null)
                await store.Update(linkedEvent);
        }

        await registrationCleanup.TryRemoveRunnerAsync(store, instance);
    }

    private async Task SendCleanupCommandAsync(string hostName, DiscoveredRunnerInfo runner)
    {
        var command = new CleanupOrphanCommand
        {
            Backend = runner.Backend,
            ContainerId = runner.ContainerId,
            VmName = runner.VmName,
            ProcessId = runner.ProcessId,
            InstanceDir = runner.InstanceDir
        };

        var agent = AgentHub.GetConnectedAgents().Values
            .FirstOrDefault(a => a.AgentInfo.Name == hostName);

        if (agent != null)
            await _hubContext.Clients.Client(agent.ConnectionId).CleanupOrphan(command);
        else
            _logger.LogWarning("No connected agent found for host {Host} to send cleanup", hostName);
    }

    /// <summary>
    /// Resolves the RunnerProfile that an orphan runner belongs to by parsing its
    /// name. Dynamic JIT runners are named "{profile.Name}-jit-{shortGuid}"
    /// (see DynamicProvisioningService); we match the profile name prefix.
    /// </summary>
    internal static RunnerProfile? ResolveProfileForOrphan(
        DiscoveredRunnerInfo runner,
        IReadOnlyList<RunnerProfile> profiles)
    {
        if (string.IsNullOrWhiteSpace(runner.RunnerName))
            return null;

        const string jitMarker = "-jit-";
        var idx = runner.RunnerName.IndexOf(jitMarker, StringComparison.Ordinal);
        if (idx <= 0)
            return null;

        var profileName = runner.RunnerName[..idx];
        return profiles.FirstOrDefault(p =>
            string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adopts an orphaned-but-running JIT runner: creates a RunnerInstance record
    /// so the server tracks it. Returns the new instance on success, null on
    /// failure (caller should fall back to cleanup).
    ///
    /// Triggered when the silo crashes/restarts after dispatching a runner. The
    /// runner is mid-job; we don't want to kill it. Adopting also lets later
    /// in_progress / completed webhooks rebind by RunnerName.
    /// </summary>
    internal async Task<RunnerInstance?> TryAdoptOrphanRunnerAsync(
        IDocumentStore store,
        string hostId,
        string hostName,
        DiscoveredRunnerInfo runner,
        IReadOnlyList<RunnerProfile> profiles)
    {
        var profile = ResolveProfileForOrphan(runner, profiles);
        if (profile == null)
            return null;

        var now = DateTime.UtcNow;
        var instance = new RunnerInstance
        {
            HostId = hostId,
            ProfileId = profile.Id,
            RunnerName = runner.RunnerName,
            Status = RunnerInstanceStatus.Running,
            ProvisioningMode = "dynamic",
            ManagedByRunnerRunner = true,
            ProcessId = runner.ProcessId,
            ContainerId = string.IsNullOrEmpty(runner.ContainerId) ? null : runner.ContainerId,
            VmName = runner.VmName,
            CreatedAt = now,
            StartedAt = now,
            LastHealthCheck = now,
            StatusMessage = "Adopted on host reconciliation (silo lost track of dispatched runner)"
        };

        await store.Insert(instance);

        _logger.LogWarning(
            "Adopted running orphan runner {RunnerName} on host {Host} as instance {InstanceId} (profile {ProfileName}); silo had lost track but the runner is mid-job",
            runner.RunnerName, hostName, instance.Id, profile.Name);

        return instance;
    }
}
