using Orleans;
using Shiny.DocumentDb;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Desired-state reconciliation engine. Periodically compares desired runner assignments
/// against actual running instances and issues deploy/stop commands to HostWorkers.
/// </summary>
public class OrchestrationEngine : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IHostCommandDispatcher _hostCommands;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<OrchestrationEngine> _logger;

    public OrchestrationEngine(
        IServiceProvider services,
        IHostCommandDispatcher hostCommands,
        IGrainFactory grainFactory,
        ILogger<OrchestrationEngine> logger)
    {
        _services = services;
        _hostCommands = hostCommands;
        _grainFactory = grainFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Orchestration engine started");

        try
        {
            // Wait a bit for HostWorkers to register on startup.
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Reconciliation loop error");
                }

                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        var assignments = (await store.Query<RunnerAssignment>().ToList()).ToList();
        var instances = (await store.Query<RunnerInstance>().ToList()).ToList();
        var profiles = (await store.Query<RunnerProfile>().ToList()).ToList();
        var credentials = (await store.Query<ProviderCredential>().ToList()).ToList();
        var hosts = (await store.Query<Host>().ToList()).ToList();

        if (assignments.Count == 0)
            return;

        var onlineHosts = hosts.Count(h => h.AgentStatus == AgentStatus.Online);

        _logger.LogInformation("Reconciling: {Assignments} assignments, {OnlineHosts} online HostWorkers, {Hosts} hosts",
            assignments.Count, onlineHosts, hosts.Count);

        foreach (var assignment in assignments)
        {
            var profile = profiles.FirstOrDefault(p => p.Id == assignment.ProfileId);
            if (profile == null)
            {
                _logger.LogWarning("Assignment {Id} references missing profile {ProfileId}", assignment.Id, assignment.ProfileId);
                continue;
            }

            // Find the host record and ensure its HostWorker is online.
            var host = hosts.FirstOrDefault(h => h.Id == assignment.HostId);
            if (host == null)
            {
                _logger.LogWarning("Assignment {Id} references missing host {HostId}", assignment.Id, assignment.HostId);
                continue;
            }

            if (host.AgentStatus != AgentStatus.Online)
            {
                _logger.LogInformation("Host {HostName} (id:{HostId}) HostWorker is {Status}, skipping.",
                    host.Name, host.Id, host.AgentStatus);
                continue;
            }

            // Count running instances for this assignment
            var runningForAssignment = instances
                .Where(i => i.HostId == assignment.HostId
                    && i.ProfileId == assignment.ProfileId
                    && i.Status is RunnerInstanceStatus.Running or RunnerInstanceStatus.Starting or RunnerInstanceStatus.Pending)
                .ToList();

            var currentCount = runningForAssignment.Count;
            var desiredCount = assignment.DesiredCount;

            _logger.LogInformation("Assignment {Profile} on {Host}: desired={Desired}, current={Current} ({Statuses})",
                profile.Name, host.Name, desiredCount, currentCount,
                string.Join(", ", runningForAssignment.Select(i => $"{i.RunnerName}:{i.Status}")));

            if (currentCount < desiredCount)
            {
                // Need to scale up
                var toStart = desiredCount - currentCount;
                _logger.LogInformation("Scaling up: {Count} more instance(s) of {Profile} on {Host}",
                    toStart, profile.Name, host.Name);

                for (var i = 0; i < toStart; i++)
                {
                    await DeployRunnerAsync(store, profile, assignment, credentials, ct);
                }
            }
            else if (currentCount > desiredCount)
            {
                // Need to scale down
                var toStop = currentCount - desiredCount;
                _logger.LogInformation("Scaling down: stopping {Count} instance(s) of {Profile} on {Host}",
                    toStop, profile.Name, host.Name);

                var instancesToStop = runningForAssignment.Take(toStop).ToList();
                foreach (var instance in instancesToStop)
                {
                    await StopRunnerAsync(store, instance, ct);
                }
            }
        }

        // Clean up instances that are stopped/crashed
        var staleInstances = instances.Where(i =>
            i.Status is RunnerInstanceStatus.Stopped or RunnerInstanceStatus.Failed or RunnerInstanceStatus.Crashed
            && i.StoppedAt.HasValue
            && i.StoppedAt.Value < DateTime.UtcNow.AddMinutes(-5));

        foreach (var stale in staleInstances)
        {
            await store.Remove<RunnerInstance>(stale.Id);
            _logger.LogDebug("Cleaned up stale instance {Id} ({RunnerName})", stale.Id, stale.RunnerName);
        }
    }

    private async Task DeployRunnerAsync(
        IDocumentStore store,
        RunnerProfile profile,
        RunnerAssignment assignment,
        List<ProviderCredential> credentials,
        CancellationToken ct)
    {
        // Resolve credential first (needed for both env var injection and registration token)
        var credential = credentials.FirstOrDefault(c => c.Id == profile.ProviderCredentialId);
        var provider = ResolveProvider(profile.Provider);

        // Compose environment variables (credential RR_* vars + profile sets + overrides + host overrides)
        var host = await store.Get<Host>(assignment.HostId);
        var envVars = await ComposeEnvironmentVariablesAsync(store, profile, host, credential);

        // Get registration token from provider
        string? registrationToken = null;
        string? runnerUrl = null;

        if (credential != null)
        {
            try
            {
                if (provider != null)
                {
                    registrationToken = await provider.GetRegistrationTokenAsync(credential, ct);
                    runnerUrl = GetRunnerUrl(credential);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get registration token for profile {Profile}", profile.Name);
                return;
            }
        }

        // Generate unique runner name
        var instanceId = Guid.NewGuid().ToString();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var runnerName = $"{profile.Name}-{suffix}";
        var runnerGrain = _grainFactory.GetGrain<IRunnerInstanceGrain>(instanceId);
        await runnerGrain.Initialize(assignment.HostId, profile.Id, runnerName, "static");

        // Resolve runner agent version — if null or "latest", look up actual version
        var agentVersion = profile.RunnerAgentVersion;
        if (string.IsNullOrEmpty(agentVersion) || agentVersion == "latest")
        {
            var versions = (await store.Query<RunnerAgentVersion>().ToList())
                .Where(v => v.Provider == profile.Provider)
                .OrderByDescending(v => v.IsLatest)
                .ThenByDescending(v => v.Version)
                .ToList();

            if (versions.Count == 0 && provider != null)
            {
                try
                {
                    versions = (await provider.GetAvailableVersionsAsync(ct))
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
                    agentVersion, profile.Provider);
        }

        // Install RR_HOOK_* sentinel + RR_META_* bag before the deploy command
        // is built so the HostWorker can both honor the job-started banner request
        // and populate it with consistent metadata.
        if (profile.EmitJobStartedBanner)
            envVars["RR_HOOK_JOB_STARTED_REQUESTED"] = "1";

        foreach (var kv in RunnerMetadataBuilder.BuildMetadataEnv(profile, host, agentVersion, instanceId))
            envVars[kv.Key] = kv.Value;

        // Enrich labels with rr-* metadata so the "Set up job" block surfaces
        // backend/image/host info without bloating the runner name.
        var effectiveLabels = RunnerMetadataBuilder.MergeMetadataLabels(profile.Labels, profile, host);

        // Send deploy command to the host-local HostWorker.
        var initSteps = await InitStepResolver.ResolveAsync(
            store, profile, envVars, profile.ExecutionBackend, host?.Platform ?? HostPlatform.Linux);

        // Resolve registry credentials for Docker image pulls
        var registryCred = await RegistryCredentialResolver.ResolveAsync(store, profile.DockerConfig, _logger);

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
            RunnerGroup = GetEffectiveRunnerGroup(profile, credential),
            Ephemeral = profile.Ephemeral,
            RegistrationToken = registrationToken,
            RunnerUrl = runnerUrl,
            RunnerBasePath = host?.RunnerBasePath,
            WorkDirectory = host?.WorkDirectory,
            InitSteps = initSteps,
            RegistryUsername = registryCred?.Username,
            RegistryPassword = registryCred?.Password,
            BackendCapacityLimit = host == null ? null : CapacityPlanningService.GetBackendLimit(host, profile.ExecutionBackend)
        };

        await runnerGrain.MarkStarting("Sending deploy command to host");
        try
        {
            await _hostCommands.DispatchDeployRunnerAsync(assignment.HostId, command);
            await runnerGrain.UpdateStatusMessage("Deploy command sent to host");
            await runnerGrain.MarkDeployed();
        }
        catch (Exception ex)
        {
            await runnerGrain.MarkFailed($"Failed to dispatch deploy command: {ex.Message}");
            throw;
        }

        _logger.LogInformation("Deploy command sent for {RunnerName} to host {HostId}", runnerName, assignment.HostId);
    }

    private async Task StopRunnerAsync(
        IDocumentStore store,
        RunnerInstance instance,
        CancellationToken ct)
    {
        var runnerGrain = _grainFactory.GetGrain<IRunnerInstanceGrain>(instance.Id);
        await runnerGrain.MarkStopping();

        await _hostCommands.DispatchStopRunnerAsync(instance.HostId, new StopRunnerCommand
        {
            InstanceId = instance.Id,
            InstanceHandle = instance.ContainerId ?? instance.VmName ?? instance.ProcessId?.ToString()
        });

        _logger.LogInformation("Stop command sent for {RunnerName}", instance.RunnerName);
    }

    private async Task<Dictionary<string, string>> ComposeEnvironmentVariablesAsync(
        IDocumentStore store, RunnerProfile profile, Host? host = null,
        ProviderCredential? credential = null)
    {
        var result = new Dictionary<string, string>();

        // Layer 0: Auto-injected provider credential vars (RR_ prefixed)
        if (credential != null)
        {
            InjectCredentialVars(result, credential);
        }

        // Layer 1: Environment variable sets (ordered by priority)
        var allSets = (await store.Query<EnvironmentVariableSet>().ToList()).ToList();
        var selectedSets = allSets
            .Where(s => profile.EnvironmentVariableSetIds.Contains(s.Id))
            .OrderBy(s => s.Priority)
            .ToList();

        foreach (var set in selectedSets)
        {
            foreach (var kvp in set.Variables)
                result[kvp.Key] = kvp.Value;
        }

        // Layer 2: Profile-level overrides
        foreach (var kvp in profile.EnvironmentOverrides)
            result[kvp.Key] = kvp.Value;

        // Layer 3: Host-level overrides
        if (host?.EnvironmentOverrides != null)
        {
            foreach (var kvp in host.EnvironmentOverrides)
                result[kvp.Key] = kvp.Value;
        }

        // Layer 4: Instance-level (runner name injected by caller)

        // Expand $RR_* variable references in all values
        ExpandVariableReferences(result);

        return result;
    }

    /// <summary>
    /// Injects provider credential fields as RR_-prefixed environment variables.
    /// These can be referenced in env var sets/profiles as $RR_GITHUB_TOKEN etc.
    /// </summary>
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

    /// <summary>
    /// Expands $VARNAME and ${VARNAME} references in environment variable values.
    /// Only expands references to variables that exist in the same dictionary.
    /// Example: GITHUB_TOKEN=$RR_GITHUB_TOKEN → GITHUB_TOKEN=ghp_abc123
    /// </summary>
    private static void ExpandVariableReferences(Dictionary<string, string> vars)
    {
        // Multiple passes to handle chained references (max 3 to prevent infinite loops)
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
            if (!changed) break;
        }
    }

    private IRunnerProviderPlugin? ResolveProvider(RunnerProvider provider)
    {
        using var scope = _services.CreateScope();
        var providers = scope.ServiceProvider.GetServices<IRunnerProviderPlugin>();
        return providers.FirstOrDefault(p => p.Provider == provider);
    }

    private static string? GetRunnerUrl(ProviderCredential credential) => credential.Provider switch
    {
        RunnerProvider.GitHubActions => GetGitHubRunnerUrl(credential),
        RunnerProvider.GiteaActions => credential.GiteaInstanceUrl?.TrimEnd('/'),
        RunnerProvider.AzureDevOps => credential.AzDoOrgUrl?.TrimEnd('/'),
        _ => null
    };

    private static string GetEffectiveRunnerGroup(RunnerProfile profile, ProviderCredential? credential)
    {
        if (profile.Provider == RunnerProvider.AzureDevOps
            && (string.IsNullOrWhiteSpace(profile.RunnerGroup) || string.Equals(profile.RunnerGroup, "Default", StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(credential?.AzDoPoolName))
        {
            return credential.AzDoPoolName;
        }

        return string.IsNullOrWhiteSpace(profile.RunnerGroup) ? "Default" : profile.RunnerGroup;
    }

    private static string? GetGitHubRunnerUrl(ProviderCredential credential)
        => GitHubCredentialResolver.GetRunnerUrl(credential);
}
