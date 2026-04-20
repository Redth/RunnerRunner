using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Grains.State;
using RunnerRunner.Server.Services;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Grains;

public class ProvisioningRuleGrain : Grain, IProvisioningRuleGrain
{
    private readonly IPersistentState<ProvisioningRuleGrainState> _state;
    private readonly ILogger<ProvisioningRuleGrain> _logger;
    private readonly IServiceProvider _serviceProvider;
    private IGrainTimer? _reconcileTimer;

    public ProvisioningRuleGrain(
        [PersistentState("provisioningRule", "PersistentStore")]
        IPersistentState<ProvisioningRuleGrainState> state,
        ILogger<ProvisioningRuleGrain> logger,
        IServiceProvider serviceProvider)
    {
        _state = state;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task SetConfig(ProvisioningRuleConfig config)
    {
        _state.State.Config = config;
        _state.State.CreatedAt = DateTime.UtcNow;

        if (config.Enabled &&
            config.Type is ProvisioningType.Static or ProvisioningType.ScaleSet)
        {
            StartReconcileTimer();
        }

        await _state.WriteStateAsync();

        _logger.LogInformation("Provisioning rule {RuleId} configured: Type={Type}, Enabled={Enabled}",
            this.GetPrimaryKeyString(), config.Type, config.Enabled);
    }

    public async Task Enable()
    {
        _state.State.Config.Enabled = true;
        StartReconcileTimer();
        await _state.WriteStateAsync();

        _logger.LogInformation("Provisioning rule {RuleId} enabled", this.GetPrimaryKeyString());
    }

    public async Task Disable()
    {
        _state.State.Config.Enabled = false;
        StopReconcileTimer();
        await _state.WriteStateAsync();

        _logger.LogInformation("Provisioning rule {RuleId} disabled", this.GetPrimaryKeyString());
    }

    public Task<ProvisioningRuleGrainState> GetState() => Task.FromResult(_state.State);

    public Task<List<string>> GetManagedInstanceIds() => Task.FromResult(_state.State.ManagedInstanceIds.ToList());

    public async Task Reconcile()
    {
        if (!_state.State.Config.Enabled)
            return;

        var aliveIds = await GetAliveInstanceIds();
        var aliveCount = aliveIds.Count;

        switch (_state.State.Config.Type)
        {
            case ProvisioningType.Static:
                await ReconcileStatic(aliveIds, aliveCount);
                break;

            case ProvisioningType.ScaleSet:
                await ReconcileScaleSet(aliveIds, aliveCount);
                break;

            case ProvisioningType.Webhook:
                await ReconcileWebhook(aliveIds);
                break;
        }

        _state.State.LastReconciliation = DateTime.UtcNow;
        await _state.WriteStateAsync();
    }

    public async Task HandleWebhookEvent(string jobId, string repo, List<string> labels, string? jitConfig, string? imageTagOverride = null)
    {
        if (_state.State.Config.Type is not (ProvisioningType.Webhook or ProvisioningType.ScaleSet))
        {
            _logger.LogWarning("Rule {RuleId} received webhook event but type is {Type}",
                this.GetPrimaryKeyString(), _state.State.Config.Type);
            return;
        }

        // If the webhook supplied an image tag override, don't reuse a warm
        // runner — warm runners were pre-pulled with the profile's default
        // tag and can't be retagged mid-life. Skip straight to provisioning a
        // fresh JIT runner so the caller actually gets the tag they asked for.
        if (string.IsNullOrEmpty(imageTagOverride))
        {
            // Try to find an idle warm runner to assign
            var idleInstanceId = await FindIdleInstance();
            if (idleInstanceId != null)
            {
                _logger.LogInformation("Assigning idle runner {InstanceId} to job {JobId}",
                    idleInstanceId, jobId);
                var grain = GrainFactory.GetGrain<IRunnerInstanceGrain>(idleInstanceId);
                await grain.UpdateStatusMessage($"Assigned to job {jobId}");
                return;
            }
        }
        else
        {
            _logger.LogInformation(
                "Skipping warm runner reuse for job {JobId}: webhook requested image tag override '{Tag}'",
                jobId, imageTagOverride);
        }

        // Check capacity
        var aliveIds = await GetAliveInstanceIds();
        var maxInstances = _state.State.Config.Type == ProvisioningType.Webhook
            ? _state.State.Config.MaxConcurrent
            : _state.State.Config.MaxInstances;

        if (aliveIds.Count >= maxInstances)
        {
            _logger.LogWarning("Rule {RuleId} at max capacity ({Max}) — cannot provision for job {JobId}",
                this.GetPrimaryKeyString(), maxInstances, jobId);
            return;
        }

        // Provision a JIT runner
        var instanceId = await ProvisionRunner(jitConfig, jobId);
        if (instanceId != null)
        {
            _logger.LogInformation("Provisioned JIT runner {InstanceId} for job {JobId}",
                instanceId, jobId);
        }
    }

    public async Task HandleJobCompleted(string jobId)
    {
        string? matchedId = null;

        foreach (var id in _state.State.ManagedInstanceIds)
        {
            var grain = GrainFactory.GetGrain<IRunnerInstanceGrain>(id);
            var instanceState = await grain.GetState();
            if (instanceState.JobId == jobId)
            {
                matchedId = id;
                break;
            }
        }

        if (matchedId == null)
        {
            _logger.LogWarning("No managed instance found for completed job {JobId}", jobId);
            return;
        }

        var runnerGrain = GrainFactory.GetGrain<IRunnerInstanceGrain>(matchedId);
        await runnerGrain.MarkStopped();
        _state.State.ManagedInstanceIds.Remove(matchedId);
        await _state.WriteStateAsync();

        _logger.LogInformation("Job {JobId} completed — instance {InstanceId} stopped and removed",
            jobId, matchedId);
    }

    // --- Reconcile helpers ---

    private async Task ReconcileStatic(List<string> aliveIds, int aliveCount)
    {
        var desired = _state.State.Config.DesiredCount;

        if (aliveCount < desired)
        {
            var toProvision = desired - aliveCount;
            _logger.LogInformation("Static rule {RuleId}: {Alive} alive, {Desired} desired — provisioning {Count}",
                this.GetPrimaryKeyString(), aliveCount, desired, toProvision);

            for (var i = 0; i < toProvision; i++)
                await ProvisionRunner();
        }
        else if (aliveCount > desired)
        {
            var toStop = aliveCount - desired;
            _logger.LogInformation("Static rule {RuleId}: {Alive} alive, {Desired} desired — stopping {Count}",
                this.GetPrimaryKeyString(), aliveCount, desired, toStop);

            await StopExcessInstances(aliveIds, toStop);
        }
    }

    private async Task ReconcileScaleSet(List<string> aliveIds, int aliveCount)
    {
        var minReady = Math.Min(_state.State.Config.MinReady, _state.State.Config.MaxInstances);

        if (aliveCount < minReady)
        {
            var toProvision = minReady - aliveCount;
            _logger.LogInformation("ScaleSet rule {RuleId}: {Alive} alive, {MinReady} min — provisioning {Count}",
                this.GetPrimaryKeyString(), aliveCount, minReady, toProvision);

            for (var i = 0; i < toProvision; i++)
                await ProvisionRunner();
        }
    }

    private async Task ReconcileWebhook(List<string> aliveIds)
    {
        var minReady = _state.State.Config.MinReady;
        if (minReady <= 0)
            return;

        var idleCount = 0;
        foreach (var id in aliveIds)
        {
            var grain = GrainFactory.GetGrain<IRunnerInstanceGrain>(id);
            var instanceState = await grain.GetState();
            if (instanceState.JobId == null && instanceState.Status == RunnerInstanceStatus.Running)
                idleCount++;
        }

        if (idleCount < minReady)
        {
            var toProvision = minReady - idleCount;
            _logger.LogInformation("Webhook rule {RuleId}: {Idle} idle, {MinReady} min — provisioning {Count} warm runners",
                this.GetPrimaryKeyString(), idleCount, minReady, toProvision);

            for (var i = 0; i < toProvision; i++)
                await ProvisionRunner();
        }
    }

    private async Task StopExcessInstances(List<string> aliveIds, int count)
    {
        // Pick least-recently-started instances
        var instances = new List<(string Id, DateTime CreatedAt)>();
        foreach (var id in aliveIds)
        {
            var grain = GrainFactory.GetGrain<IRunnerInstanceGrain>(id);
            var instanceState = await grain.GetState();
            instances.Add((id, instanceState.StartedAt ?? instanceState.CreatedAt));
        }

        var toStop = instances
            .OrderBy(i => i.CreatedAt)
            .Take(count)
            .ToList();

        foreach (var (id, _) in toStop)
        {
            var grain = GrainFactory.GetGrain<IRunnerInstanceGrain>(id);
            await grain.MarkStopping();
            _logger.LogInformation("Stopping excess instance {InstanceId}", id);
        }
    }

    private async Task<string?> FindIdleInstance()
    {
        foreach (var id in _state.State.ManagedInstanceIds)
        {
            var grain = GrainFactory.GetGrain<IRunnerInstanceGrain>(id);
            var instanceState = await grain.GetState();
            if (instanceState.Status == RunnerInstanceStatus.Running && instanceState.JobId == null)
                return id;
        }
        return null;
    }

    private async Task<List<string>> GetAliveInstanceIds()
    {
        var alive = new List<string>();
        var dead = new List<string>();

        foreach (var id in _state.State.ManagedInstanceIds)
        {
            var grain = GrainFactory.GetGrain<IRunnerInstanceGrain>(id);
            var instanceState = await grain.GetState();

            if (instanceState.Status is RunnerInstanceStatus.Running
                or RunnerInstanceStatus.Starting
                or RunnerInstanceStatus.Pending)
            {
                alive.Add(id);
            }
            else
            {
                dead.Add(id);
            }
        }

        // Clean up dead instances from tracked list
        if (dead.Count > 0)
        {
            foreach (var id in dead)
                _state.State.ManagedInstanceIds.Remove(id);
        }

        return alive;
    }

    // --- Provisioning ---

    private async Task<string?> ProvisionRunner(string? jitConfig = null, string? jobId = null)
    {
        var profileGrain = GrainFactory.GetGrain<IProfileGrain>(_state.State.Config.ProfileId);
        var profile = await profileGrain.GetProfile();
        if (profile == null)
        {
            _logger.LogWarning("Rule {RuleId} references missing profile {ProfileId}",
                this.GetPrimaryKeyString(),
                _state.State.Config.ProfileId);
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var hosts = (await store.Query<Core.Models.Host>().ToList()).ToList();
        var instances = (await store.Query<RunnerInstance>().ToList()).ToList();
        var profilesById = (await store.Query<RunnerProfile>().ToList())
            .ToDictionary(p => p.Id, p => p, StringComparer.OrdinalIgnoreCase);
        profilesById[profile.Id] = profile;

        var ruleModel = new ProvisioningRule
        {
            Id = this.GetPrimaryKeyString(),
            Name = _state.State.Config.Name,
            ProfileId = _state.State.Config.ProfileId,
            Type = _state.State.Config.Type,
            DesiredCount = _state.State.Config.DesiredCount,
            TargetHostId = _state.State.Config.TargetHostId,
            MinReady = _state.State.Config.MinReady,
            MaxInstances = _state.State.Config.MaxInstances,
            MaxConcurrent = _state.State.Config.MaxConcurrent,
            RequiredHostLabels = new Dictionary<string, string>(_state.State.Config.RequiredHostLabels),
            TargetGroupId = _state.State.Config.TargetGroupId
        };

        var analysis = CapacityPlanningService.AnalyzeHostSelection(profile, ruleModel, hosts, profilesById, instances);
        var hostId = analysis.SelectedHost?.Id;
        if (hostId == null)
        {
            _logger.LogWarning("No host available for rule {RuleId}: {Reason}",
                this.GetPrimaryKeyString(),
                analysis.Reason);
            return null;
        }

        var instanceId = Guid.NewGuid().ToString();
        var runnerGrain = GrainFactory.GetGrain<IRunnerInstanceGrain>(instanceId);
        var runnerName = $"{_state.State.Config.Name}-{instanceId[..8]}";

        var provisioningMode = _state.State.Config.Type == ProvisioningType.Webhook ? "dynamic" : "static";

        await runnerGrain.Initialize(hostId, _state.State.Config.ProfileId, runnerName, provisioningMode, jobId, provisioningRuleId: this.GetPrimaryKeyString());

        _state.State.ManagedInstanceIds.Add(instanceId);
        await _state.WriteStateAsync();

        _logger.LogInformation("Provisioned runner {InstanceId} ({RunnerName}) on host {HostId}",
            instanceId, runnerName, hostId);

        return instanceId;
    }

    // --- Timer management ---

    private void StartReconcileTimer()
    {
        StopReconcileTimer();
        _reconcileTimer = this.RegisterGrainTimer<object?>(
            (_, ct) => OnReconcileTimer(ct),
            null,
            new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.FromSeconds(5),
                Period = TimeSpan.FromSeconds(30)
            });
    }

    private void StopReconcileTimer()
    {
        _reconcileTimer?.Dispose();
        _reconcileTimer = null;
    }

    private async Task OnReconcileTimer(CancellationToken ct)
    {
        try
        {
            await Reconcile();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconcile failed for rule {RuleId}", this.GetPrimaryKeyString());
        }
    }
}
