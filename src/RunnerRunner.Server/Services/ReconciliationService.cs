using Shiny.DocumentDb;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Processes reconciliation reports from HostWorkers, marking stale runners and cleaning up orphans.
/// </summary>
public class ReconciliationService : IHostedService, IDisposable
{
    // HostWorker sends a reconciliation report every 30s (see HostCommandProcessor), listing
    // only processes/containers that have actually launched. A Starting/Pending instance whose
    // deploy is merely slow (host under load, large runner package extraction, etc.) looks
    // identical to a failed one until it's had a real chance to finish — so give it the same
    // grace window as RunnerInstanceGrain's own "Deploy timeout" (2 minutes) before treating a
    // missing report as evidence of failure. Without this, a deploy that legitimately takes
    // longer than 30s gets marked stale and replaced on every single sweep.
    private static readonly TimeSpan DeployGracePeriod = TimeSpan.FromMinutes(2);

    private readonly ILogger<ReconciliationService> _logger;
    private readonly IServiceProvider _services;
    private readonly IHostCommandDispatcher _hostCommands;

    public ReconciliationService(
        ILogger<ReconciliationService> logger,
        IServiceProvider services,
        IHostCommandDispatcher hostCommands)
    {
        _logger = logger;
        _services = services;
        _hostCommands = hostCommands;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StreamSubscriptionService.OnReconciliationReportReceived += HandleReconciliation;
        _logger.LogInformation("ReconciliationService started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StreamSubscriptionService.OnReconciliationReportReceived -= HandleReconciliation;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StreamSubscriptionService.OnReconciliationReportReceived -= HandleReconciliation;
    }

    private async void HandleReconciliation(ReconciliationReport report)
    {
        try
        {
            using var scope = _services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var registrationCleanup = scope.ServiceProvider.GetRequiredService<RunnerRegistrationCleanupService>();

            var reportHostId = report.HostId;
            var hosts = (await store.Query<Host>().ToList())
                .Where(h => h.Id == reportHostId || h.Name == reportHostId)
                .ToList();
            if (hosts.Count == 0)
            {
                _logger.LogWarning("No host found for reconciliation report from {Host}", reportHostId);
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
                    if (instance.Status != RunnerInstanceStatus.Running
                        && DateTime.UtcNow - instance.CreatedAt < DeployGracePeriod)
                        continue;

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
                    await SendCleanupCommandAsync(hostId, matchedRunner);
                }
            }

            // Find orphans on host (in report but NOT in DB)
            foreach (var runner in report.Runners)
            {
                var hasMatch = dbInstances.Any(i => MatchesRunner(i, runner));

                if (!hasMatch)
                {
                    _logger.LogWarning(
                        "Sending cleanup for orphaned {Backend} resource {Resource} on host {Host} (running: {Running}, status: {Status})",
                        runner.Backend,
                        runner.ContainerId ?? runner.VmName ?? runner.ProcessId?.ToString() ?? runner.RunnerName,
                        reportHostId,
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

                    await _hostCommands.DispatchCleanupOrphanAsync(hostId, command);
                }
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

    internal static bool IsRunnerStillActive(DiscoveredRunnerInfo runner) =>
        runner.IsRunning || string.Equals(runner.Status, "running", StringComparison.OrdinalIgnoreCase);

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

    private async Task SendCleanupCommandAsync(string hostId, DiscoveredRunnerInfo runner)
    {
        var command = new CleanupOrphanCommand
        {
            Backend = runner.Backend,
            ContainerId = runner.ContainerId,
            VmName = runner.VmName,
            ProcessId = runner.ProcessId,
            InstanceDir = runner.InstanceDir
        };

        await _hostCommands.DispatchCleanupOrphanAsync(hostId, command);
    }
}
