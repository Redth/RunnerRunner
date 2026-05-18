using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Grains.State;
using RunnerRunner.Server.Services;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Grains;

public class ProvisioningRuleGrain : Grain, IProvisioningRuleGrain, IRemindable
{
    private const string ReconcileReminderName = "reconcile";
    private static readonly TimeSpan ReconcileDueTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconcilePeriod = TimeSpan.FromSeconds(30);

    private readonly IPersistentState<ProvisioningRuleGrainState> _state;
    private readonly ILogger<ProvisioningRuleGrain> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHostCommandDispatcher _hostCommands;

    public ProvisioningRuleGrain(
        [PersistentState("provisioningRule", "PersistentStore")]
        IPersistentState<ProvisioningRuleGrainState> state,
        ILogger<ProvisioningRuleGrain> logger,
        IServiceProvider serviceProvider,
        IHostCommandDispatcher hostCommands)
    {
        _state = state;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _hostCommands = hostCommands;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);
        await SyncReconcileReminder();
    }

    public async Task SetConfig(ProvisioningRuleConfig config)
    {
        _state.State.Config = config;
        _state.State.CreatedAt = DateTime.UtcNow;

        await _state.WriteStateAsync();
        await SyncReconcileReminder();

        _logger.LogInformation("Provisioning rule {RuleId} configured: Type={Type}, Enabled={Enabled}",
            this.GetPrimaryKeyString(), config.Type, config.Enabled);
    }

    public async Task Enable()
    {
        _state.State.Config.Enabled = true;
        await _state.WriteStateAsync();
        await SyncReconcileReminder();

        _logger.LogInformation("Provisioning rule {RuleId} enabled", this.GetPrimaryKeyString());
    }

    public async Task Disable()
    {
        _state.State.Config.Enabled = false;
        await _state.WriteStateAsync();
        await SyncReconcileReminder();

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
        if (_state.State.Config.Type == ProvisioningType.Webhook)
        {
            _logger.LogDebug(
                "Rule {RuleId} received webhook event for job {JobId}; webhook dispatch is handled by DynamicProvisioningService during migration",
                this.GetPrimaryKeyString(),
                jobId);
            return;
        }

        if (_state.State.Config.Type != ProvisioningType.ScaleSet)
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
            var instanceState = await grain.GetState();
            await grain.MarkStopping();
            await _hostCommands.DispatchStopRunnerAsync(instanceState.HostId, new StopRunnerCommand
            {
                InstanceId = id,
                InstanceHandle = instanceState.ContainerId ?? instanceState.VmName ?? instanceState.ProcessId?.ToString()
            });
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
        using var scope = _serviceProvider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        var profile = await store.Get<RunnerProfile>(_state.State.Config.ProfileId);
        if (profile == null)
        {
            _logger.LogWarning("Rule {RuleId} references missing profile {ProfileId}",
                this.GetPrimaryKeyString(),
                _state.State.Config.ProfileId);
            return null;
        }

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
        var backendName = profile.ExecutionBackend.ToString().ToLowerInvariant();
        var host = analysis.Candidates
            .Where(candidate => candidate.CanRunNow)
            .Select(candidate => hosts.First(h => string.Equals(h.Id, candidate.HostId, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(h =>
                h.AgentStatus == AgentStatus.Online
                && h.Capabilities.Any(c => c.Equals(backendName, StringComparison.OrdinalIgnoreCase)));
        if (host == null)
        {
            _logger.LogWarning("No host available for rule {RuleId}: {Reason}",
                this.GetPrimaryKeyString(),
                analysis.SelectedHost == null
                    ? analysis.Reason
                    : $"No online HostWorker with backend '{backendName}' is currently available");
            return null;
        }

        var credential = string.IsNullOrWhiteSpace(profile.ProviderCredentialId)
            ? null
            : await store.Get<ProviderCredential>(profile.ProviderCredentialId);
        var provider = ResolveProvider(profile.Provider);
        string? registrationToken = null;
        string? runnerUrl = null;

        if (credential != null && provider != null)
        {
            try
            {
                registrationToken = await provider.GetRegistrationTokenAsync(credential);
                runnerUrl = GetRunnerUrl(credential);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get registration token for profile {Profile}", profile.Name);
                return null;
            }
        }

        var instanceId = Guid.NewGuid().ToString();
        var runnerGrain = GrainFactory.GetGrain<IRunnerInstanceGrain>(instanceId);
        var runnerName = $"{_state.State.Config.Name}-{instanceId[..8]}";

        var provisioningMode = _state.State.Config.Type == ProvisioningType.Webhook ? "dynamic" : "static";
        var envVars = await ComposeEnvironmentVariablesAsync(store, profile, host, credential);
        envVars["RR_INSTANCE_ID"] = instanceId;
        envVars["RR_RUNNER_NAME"] = runnerName;

        var agentVersion = await ResolveRunnerAgentVersion(store, profile, provider);
        if (profile.EmitJobStartedBanner)
            envVars["RR_HOOK_JOB_STARTED_REQUESTED"] = "1";

        foreach (var kv in RunnerMetadataBuilder.BuildMetadataEnv(profile, host, agentVersion, instanceId))
            envVars[kv.Key] = kv.Value;

        var effectiveLabels = RunnerMetadataBuilder.MergeMetadataLabels(profile.Labels, profile, host);
        var initSteps = await InitStepResolver.ResolveAsync(
            store,
            profile,
            envVars,
            profile.ExecutionBackend,
            host.Platform);
        var registryCred = await RegistryCredentialResolver.ResolveAsync(store, profile.DockerConfig, _logger);

        await runnerGrain.Initialize(host.Id, _state.State.Config.ProfileId, runnerName, provisioningMode, jobId, provisioningRuleId: this.GetPrimaryKeyString());
        await runnerGrain.MarkStarting("Sending deploy command to host");

        _state.State.ManagedInstanceIds.Add(instanceId);
        await _state.WriteStateAsync();

        var command = new DeployRunnerCommand
        {
            InstanceId = instanceId,
            ProfileId = profile.Id,
            RunnerName = runnerName,
            Backend = profile.ExecutionBackend,
            Provider = profile.Provider,
            EnvironmentVariables = envVars,
            RunnerAgentVersion = agentVersion,
            DockerConfig = profile.DockerConfig,
            TartConfig = profile.TartConfig,
            Labels = effectiveLabels,
            RunnerGroup = profile.RunnerGroup,
            Ephemeral = profile.Ephemeral,
            RegistrationToken = registrationToken,
            RunnerUrl = runnerUrl,
            RunnerBasePath = host.RunnerBasePath,
            WorkDirectory = host.WorkDirectory,
            InitSteps = initSteps,
            RegistryUsername = registryCred?.Username,
            RegistryPassword = registryCred?.Password,
            BackendCapacityLimit = CapacityPlanningService.GetBackendLimit(host, profile.ExecutionBackend),
            ProvisioningMode = provisioningMode
        };

        try
        {
            await _hostCommands.DispatchDeployRunnerAsync(host.Id, command);
            await runnerGrain.UpdateStatusMessage("Deploy command sent to host");
            await runnerGrain.MarkDeployed();
        }
        catch (Exception ex)
        {
            await runnerGrain.MarkFailed($"Failed to dispatch deploy command: {ex.Message}");
            _state.State.ManagedInstanceIds.Remove(instanceId);
            await _state.WriteStateAsync();
            throw;
        }

        _logger.LogInformation("Provisioned runner {InstanceId} ({RunnerName}) on host {HostId}",
            instanceId, runnerName, host.Id);

        return instanceId;
    }

    // --- Reminder management ---

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, ReconcileReminderName, StringComparison.Ordinal))
        {
            _logger.LogWarning("Provisioning rule {RuleId} received unknown reminder {ReminderName}",
                this.GetPrimaryKeyString(),
                reminderName);
            return Task.CompletedTask;
        }

        return OnReconcileReminder();
    }

    private async Task SyncReconcileReminder()
    {
        if (ShouldRunReconcileReminder(_state.State.Config))
        {
            await this.RegisterOrUpdateReminder(ReconcileReminderName, ReconcileDueTime, ReconcilePeriod);
            return;
        }

        var reminder = await this.GetReminder(ReconcileReminderName);
        if (reminder is not null)
            await this.UnregisterReminder(reminder);
    }

    private async Task OnReconcileReminder()
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

    private static bool ShouldRunReconcileReminder(ProvisioningRuleConfig config) =>
        config.Enabled
        && !string.IsNullOrWhiteSpace(config.Name)
        && !string.IsNullOrWhiteSpace(config.ProfileId)
        && config.Type is ProvisioningType.Static or ProvisioningType.ScaleSet;

    private async Task<Dictionary<string, string>> ComposeEnvironmentVariablesAsync(
        IDocumentStore store,
        RunnerProfile profile,
        Core.Models.Host host,
        ProviderCredential? credential)
    {
        var result = new Dictionary<string, string>();

        if (credential != null)
            InjectCredentialVars(result, credential);

        var allSets = (await store.Query<EnvironmentVariableSet>().ToList()).ToList();
        var selectedSets = allSets
            .Where(s => profile.EnvironmentVariableSetIds.Contains(s.Id))
            .OrderBy(s => s.Priority)
            .ToList();

        foreach (var set in selectedSets)
            foreach (var kvp in set.Variables)
                result[kvp.Key] = kvp.Value;

        foreach (var kvp in profile.EnvironmentOverrides)
            result[kvp.Key] = kvp.Value;

        foreach (var kvp in host.EnvironmentOverrides)
            result[kvp.Key] = kvp.Value;

        ExpandVariableReferences(result);
        return result;
    }

    private async Task<string?> ResolveRunnerAgentVersion(
        IDocumentStore store,
        RunnerProfile profile,
        IRunnerProviderPlugin? provider)
    {
        var agentVersion = profile.RunnerAgentVersion;
        if (!string.IsNullOrEmpty(agentVersion) && agentVersion != "latest")
            return agentVersion;

        var versions = (await store.Query<RunnerAgentVersion>().ToList())
            .Where(v => v.Provider == profile.Provider)
            .OrderByDescending(v => v.IsLatest)
            .ThenByDescending(v => v.Version)
            .ToList();

        if (versions.Count == 0 && provider != null)
        {
            try
            {
                versions = (await provider.GetAvailableVersionsAsync())
                    .OrderByDescending(v => v.IsLatest)
                    .ThenByDescending(v => v.Version)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to query live runner versions for {Provider}; deploy will rely on host-side fallback",
                    profile.Provider);
            }
        }

        agentVersion = versions.FirstOrDefault()?.Version;
        if (agentVersion != null)
            _logger.LogInformation("Resolved runner agent version to {Version} for {Provider}",
                agentVersion,
                profile.Provider);

        return agentVersion;
    }

    private IRunnerProviderPlugin? ResolveProvider(RunnerProvider provider)
    {
        using var scope = _serviceProvider.CreateScope();
        var providers = scope.ServiceProvider.GetServices<IRunnerProviderPlugin>();
        return providers.FirstOrDefault(p => p.Provider == provider);
    }

    private static void InjectCredentialVars(Dictionary<string, string> vars, ProviderCredential cred)
    {
        switch (cred.Provider)
        {
            case RunnerProvider.GitHubActions:
                var target = GitHubCredentialResolver.ResolveDefaultTarget(cred);
                if (!string.IsNullOrEmpty(cred.GitHubToken)) vars["RR_GITHUB_TOKEN"] = cred.GitHubToken;
                if (!string.IsNullOrEmpty(target?.Owner)) vars["RR_GITHUB_ORG"] = target.Owner;
                if (!string.IsNullOrEmpty(target?.Repository)) vars["RR_GITHUB_REPO"] = target.Repository;
                if (!string.IsNullOrEmpty(cred.GitHubApiUrl)) vars["RR_GITHUB_API_URL"] = cred.GitHubApiUrl;
                if (!string.IsNullOrEmpty(cred.GitHubServerUrl)) vars["RR_GITHUB_SERVER_URL"] = cred.GitHubServerUrl;
                break;

            case RunnerProvider.GiteaActions:
                if (!string.IsNullOrEmpty(cred.GiteaRunnerToken)) vars["RR_GITEA_RUNNER_TOKEN"] = cred.GiteaRunnerToken;
                if (!string.IsNullOrEmpty(cred.GiteaInstanceUrl)) vars["RR_GITEA_INSTANCE_URL"] = cred.GiteaInstanceUrl;
                break;

            case RunnerProvider.AzureDevOps:
                if (!string.IsNullOrEmpty(cred.AzDoPat)) vars["RR_AZDO_PAT"] = cred.AzDoPat;
                if (!string.IsNullOrEmpty(cred.AzDoOrgUrl)) vars["RR_AZDO_ORG_URL"] = cred.AzDoOrgUrl;
                if (!string.IsNullOrEmpty(cred.AzDoProjectName)) vars["RR_AZDO_PROJECT"] = cred.AzDoProjectName;
                if (!string.IsNullOrEmpty(cred.AzDoPoolName)) vars["RR_AZDO_POOL"] = cred.AzDoPoolName;
                break;
        }
    }

    private static void ExpandVariableReferences(Dictionary<string, string> vars)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            var changed = false;
            foreach (var key in vars.Keys.ToList())
            {
                var value = vars[key];
                if (!value.Contains('$')) continue;

                var expanded = value;
                foreach (var refKey in vars.Keys)
                {
                    expanded = expanded
                        .Replace($"${{{refKey}}}", vars[refKey])
                        .Replace($"${refKey}", vars[refKey]);
                }

                if (expanded != value)
                {
                    vars[key] = expanded;
                    changed = true;
                }
            }

            if (!changed)
                break;
        }
    }

    private static string? GetRunnerUrl(ProviderCredential credential) => credential.Provider switch
    {
        RunnerProvider.GitHubActions => GitHubCredentialResolver.GetRunnerUrl(credential),
        RunnerProvider.GiteaActions => credential.GiteaInstanceUrl?.TrimEnd('/'),
        RunnerProvider.AzureDevOps => credential.AzDoOrgUrl?.TrimEnd('/'),
        _ => null
    };
}
