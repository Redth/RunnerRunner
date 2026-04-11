using Microsoft.AspNetCore.SignalR;
using Shiny.DocumentDb;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Hubs;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Desired-state reconciliation engine. Periodically compares desired runner assignments
/// against actual running instances and issues deploy/stop commands to agents via SignalR.
/// </summary>
public class OrchestrationEngine : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IHubContext<AgentHub, IAgentHubClient> _hubContext;
    private readonly ILogger<OrchestrationEngine> _logger;

    public OrchestrationEngine(
        IServiceProvider services,
        IHubContext<AgentHub, IAgentHubClient> hubContext,
        ILogger<OrchestrationEngine> logger)
    {
        _services = services;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Orchestration engine started");

        // Wait a bit for agents to connect on startup
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconciliation loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
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
        var connectedAgents = AgentHub.GetConnectedAgents();

        foreach (var assignment in assignments)
        {
            var profile = profiles.FirstOrDefault(p => p.Id == assignment.ProfileId);
            if (profile == null)
            {
                _logger.LogWarning("Assignment {Id} references missing profile {ProfileId}", assignment.Id, assignment.ProfileId);
                continue;
            }

            // Find the connected agent for this host
            var agent = connectedAgents.Values.FirstOrDefault(a =>
            {
                // Match agent by looking up host record
                var host = store.Get<Host>(assignment.HostId).GetAwaiter().GetResult();
                return host != null && a.AgentInfo.Name == host.Name;
            });

            if (agent == null)
            {
                // Host's agent not connected, skip
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

            if (currentCount < desiredCount)
            {
                // Need to scale up
                var toStart = desiredCount - currentCount;
                _logger.LogInformation("Scaling up: {Count} more instance(s) of {Profile} on {Host}",
                    toStart, profile.Name, agent.AgentInfo.Name);

                for (var i = 0; i < toStart; i++)
                {
                    await DeployRunnerAsync(store, profile, assignment, credentials, agent, ct);
                }
            }
            else if (currentCount > desiredCount)
            {
                // Need to scale down
                var toStop = currentCount - desiredCount;
                _logger.LogInformation("Scaling down: stopping {Count} instance(s) of {Profile} on {Host}",
                    toStop, profile.Name, agent.AgentInfo.Name);

                var instancesToStop = runningForAssignment.Take(toStop).ToList();
                foreach (var instance in instancesToStop)
                {
                    await StopRunnerAsync(store, instance, agent, ct);
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
        ConnectedAgent agent,
        CancellationToken ct)
    {
        // Resolve credential first (needed for both env var injection and registration token)
        var credential = credentials.FirstOrDefault(c => c.Id == profile.ProviderCredentialId);

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
                var provider = ResolveProvider(profile.Provider);
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
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var runnerName = $"{profile.Name}-{suffix}";

        // Create instance record
        var instance = new RunnerInstance
        {
            HostId = assignment.HostId,
            ProfileId = profile.Id,
            RunnerName = runnerName,
            Status = RunnerInstanceStatus.Pending
        };
        await store.Insert(instance);

        // Send deploy command to agent
        var command = new DeployRunnerCommand
        {
            InstanceId = instance.Id,
            ProfileId = profile.Id,
            RunnerName = runnerName,
            Backend = profile.ExecutionBackend,
            EnvironmentVariables = envVars,
            RunnerAgentVersion = profile.RunnerAgentVersion,
            DockerConfig = profile.DockerConfig,
            TartConfig = profile.TartConfig,
            Labels = profile.Labels,
            RunnerGroup = profile.RunnerGroup,
            Ephemeral = profile.Ephemeral,
            RegistrationToken = registrationToken,
            RunnerUrl = runnerUrl
        };

        await _hubContext.Clients.Client(agent.ConnectionId).DeployRunner(command);

        instance.Status = RunnerInstanceStatus.Starting;
        instance.StartedAt = DateTime.UtcNow;
        await store.Update(instance);

        _logger.LogInformation("Deploy command sent for {RunnerName} to {Host}", runnerName, agent.AgentInfo.Name);
    }

    private async Task StopRunnerAsync(
        IDocumentStore store,
        RunnerInstance instance,
        ConnectedAgent agent,
        CancellationToken ct)
    {
        instance.Status = RunnerInstanceStatus.Stopping;
        await store.Update(instance);

        await _hubContext.Clients.Client(agent.ConnectionId).StopRunner(new StopRunnerCommand
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
                if (!string.IsNullOrEmpty(cred.GitHubToken)) vars["RR_GITHUB_TOKEN"] = cred.GitHubToken;
                if (!string.IsNullOrEmpty(cred.GitHubOrg)) vars["RR_GITHUB_ORG"] = cred.GitHubOrg;
                if (!string.IsNullOrEmpty(cred.GitHubRepo)) vars["RR_GITHUB_REPO"] = cred.GitHubRepo;
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

    private static string? GetRunnerUrl(ProviderCredential credential)
    {
        var serverUrl = credential.GitHubServerUrl?.TrimEnd('/') ?? "https://github.com";
        if (!string.IsNullOrEmpty(credential.GitHubRepo))
            return $"{serverUrl}/{credential.GitHubOrg}/{credential.GitHubRepo}";
        if (!string.IsNullOrEmpty(credential.GitHubOrg))
            return $"{serverUrl}/{credential.GitHubOrg}";
        return null;
    }
}
