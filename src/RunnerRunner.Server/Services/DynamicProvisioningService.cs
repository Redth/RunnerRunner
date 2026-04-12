using Microsoft.AspNetCore.SignalR;
using Shiny.DocumentDb;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Hubs;
using RunnerRunner.Server.Webhooks;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Subscribes to webhook-triggered job-queued events and orchestrates JIT runner provisioning:
/// host selection → JIT config generation → deploy runner to agent.
/// </summary>
public class DynamicProvisioningService : IHostedService
{
    private readonly ILogger<DynamicProvisioningService> _logger;
    private readonly IServiceProvider _services;
    private readonly JitConfigService _jitConfigService;
    private readonly IHubContext<AgentHub, IAgentHubClient> _hubContext;

    public DynamicProvisioningService(
        ILogger<DynamicProvisioningService> logger,
        IServiceProvider services,
        JitConfigService jitConfigService,
        IHubContext<AgentHub, IAgentHubClient> hubContext)
    {
        _logger = logger;
        _services = services;
        _jitConfigService = jitConfigService;
        _hubContext = hubContext;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        WebhookEndpoints.OnJobQueued += HandleJobQueued;
        _logger.LogInformation("DynamicProvisioningService started, subscribed to OnJobQueued");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        WebhookEndpoints.OnJobQueued -= HandleJobQueued;
        _logger.LogInformation("DynamicProvisioningService stopped");
        return Task.CompletedTask;
    }

    private async void HandleJobQueued(WebhookEvent evt, string profileId)
    {
        try
        {
            _logger.LogInformation("HandleJobQueued fired: repo={Repo}, jobId={JobId}, profileId={ProfileId}",
                evt.Repository, evt.JobId, profileId);
            await HandleJobQueuedAsync(evt, profileId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error provisioning runner for webhook event {EventId}", evt.Id);
        }
    }

    private async Task HandleJobQueuedAsync(WebhookEvent evt, string profileId)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        // Load profile
        var profile = await store.Get<RunnerProfile>(profileId);
        if (profile == null)
        {
            _logger.LogError("Profile {ProfileId} not found for webhook event {EventId}", profileId, evt.Id);
            await UpdateWebhookEventError(store, evt, $"Profile '{profileId}' not found");
            return;
        }

        // Load provider credential
        ProviderCredential? credential = null;
        if (!string.IsNullOrEmpty(profile.ProviderCredentialId))
        {
            credential = await store.Get<ProviderCredential>(profile.ProviderCredentialId);
            if (credential == null)
            {
                _logger.LogError("Credential {CredentialId} not found for profile {ProfileName}",
                    profile.ProviderCredentialId, profile.Name);
                await UpdateWebhookEventError(store, evt, $"Credential '{profile.ProviderCredentialId}' not found");
                return;
            }
        }

        // Host selection
        var connectedAgents = AgentHub.GetConnectedAgents();
        var hosts = (await store.Query<Host>().ToList()).ToList();
        var instances = (await store.Query<RunnerInstance>().ToList()).ToList();

        var backendName = profile.ExecutionBackend.ToString().ToLowerInvariant();

        var selectedHost = (Host?)null;
        ConnectedAgent? selectedAgent = null;
        var leastLoad = int.MaxValue;

        foreach (var host in hosts)
        {
            // Must match required platform
            if (host.Platform != profile.RequiredHostPlatform)
                continue;

            // Must be connected
            var agent = connectedAgents.Values.FirstOrDefault(a => a.AgentInfo.Name == host.Name);
            if (agent == null)
                continue;

            // Must have the backend capability
            if (!agent.AgentInfo.Capabilities.Any(c =>
                c.Equals(backendName, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Prefer least-loaded (fewest active instances)
            var activeCount = instances.Count(i =>
                i.HostId == host.Id &&
                i.Status is RunnerInstanceStatus.Running
                    or RunnerInstanceStatus.Starting
                    or RunnerInstanceStatus.Pending);

            if (activeCount < leastLoad)
            {
                leastLoad = activeCount;
                selectedHost = host;
                selectedAgent = agent;
            }
        }

        if (selectedHost == null || selectedAgent == null)
        {
            _logger.LogWarning(
                "No suitable host found for profile {ProfileName} (platform={Platform}, backend={Backend}). " +
                "Connected agents: [{Agents}]",
                profile.Name, profile.RequiredHostPlatform, backendName,
                string.Join(", ", connectedAgents.Values.Select(a => a.AgentInfo.Name)));
            await UpdateWebhookEventError(store, evt,
                $"No connected host matches platform '{profile.RequiredHostPlatform}' with backend '{backendName}'");
            return;
        }

        // Generate unique runner name
        var shortGuid = Guid.NewGuid().ToString("N")[..8];
        var runnerName = $"{profile.Name}-jit-{shortGuid}";

        _logger.LogInformation("Selected host {Host} (agent: {Agent}) for dynamic runner {RunnerName}",
            selectedHost.Label, selectedAgent.AgentInfo.Name, runnerName);

        // JIT config generation
        JitConfigResult? jitResult = null;
        if (credential != null)
        {
            jitResult = profile.Provider switch
            {
                RunnerProvider.GitHubActions => await _jitConfigService.GenerateGitHubJitConfig(
                    credential, runnerName, profile.Labels, profile.RunnerGroup, evt.Repository),
                RunnerProvider.GiteaActions => await _jitConfigService.GenerateGiteaJitConfig(
                    credential, runnerName),
                _ => null
            };

            if (jitResult is { Success: false })
            {
                _logger.LogError("JIT config generation failed for {RunnerName}: {Error}",
                    runnerName, jitResult.Error);
                await UpdateWebhookEventError(store, evt, $"JIT config failed: {jitResult.Error}");
                return;
            }
        }

        // Compose environment variables
        var envVars = await ComposeEnvironmentVariablesAsync(store, profile, selectedHost, credential);
        envVars["RR_INSTANCE_ID"] = ""; // will be set after insert
        envVars["RR_RUNNER_NAME"] = runnerName;

        // Create RunnerInstance record
        var instance = new RunnerInstance
        {
            RunnerName = runnerName,
            HostId = selectedHost.Id,
            ProfileId = profileId,
            ProvisioningMode = "dynamic",
            WebhookEventId = evt.Id,
            JitConfig = jitResult?.JitConfig,
            JobId = evt.JobId,
            Status = RunnerInstanceStatus.Pending
        };
        await store.Insert(instance);

        // Now set the actual instance ID in env vars
        envVars["RR_INSTANCE_ID"] = instance.Id;

        // Build and send deploy command
        var command = new DeployRunnerCommand
        {
            InstanceId = instance.Id,
            ProfileId = profileId,
            RunnerName = runnerName,
            Backend = profile.ExecutionBackend,
            Provider = profile.Provider,
            EnvironmentVariables = envVars,
            DockerConfig = profile.DockerConfig,
            TartConfig = profile.TartConfig,
            Labels = profile.Labels,
            RunnerGroup = profile.RunnerGroup,
            Ephemeral = true,
            JitConfig = jitResult?.JitConfig,
            RegistrationToken = jitResult?.RegistrationToken,
            ProvisioningMode = "dynamic",
            RunnerUrl = credential != null ? GetRunnerUrl(credential) : null,
            RunnerBasePath = selectedHost.RunnerBasePath,
            WorkDirectory = selectedHost.WorkDirectory
        };

        await _hubContext.Clients.Client(selectedAgent.ConnectionId).DeployRunner(command);

        // Update instance status
        instance.Status = RunnerInstanceStatus.Starting;
        instance.StartedAt = DateTime.UtcNow;
        await store.Update(instance);

        // Update webhook event
        evt.Status = "provisioned";
        evt.InstanceId = instance.Id;
        await store.Update(evt);

        _logger.LogInformation(
            "Dynamic runner {RunnerName} deployed to {HostName} for job {JobId} (event {EventId})",
            runnerName, selectedHost.Name, evt.JobId, evt.Id);
    }

    private static string? GetRunnerUrl(ProviderCredential credential)
    {
        return credential.Provider switch
        {
            RunnerProvider.GitHubActions => GetGitHubRunnerUrl(credential),
            RunnerProvider.GiteaActions => credential.GiteaInstanceUrl,
            _ => null
        };
    }

    private static string? GetGitHubRunnerUrl(ProviderCredential credential)
    {
        var serverUrl = credential.GitHubServerUrl?.TrimEnd('/') ?? "https://github.com";
        if (!string.IsNullOrEmpty(credential.GitHubRepo))
            return $"{serverUrl}/{credential.GitHubOrg}/{credential.GitHubRepo}";
        if (!string.IsNullOrEmpty(credential.GitHubOrg))
            return $"{serverUrl}/{credential.GitHubOrg}";
        return null;
    }

    private async Task<Dictionary<string, string>> ComposeEnvironmentVariablesAsync(
        IDocumentStore store, RunnerProfile profile, Host host, ProviderCredential? credential)
    {
        var result = new Dictionary<string, string>();

        // Layer 0: Provider credential vars (RR_ prefixed)
        if (credential != null)
            InjectCredentialVars(result, credential);

        // Layer 1: Environment variable sets (ordered by priority)
        var allSets = (await store.Query<EnvironmentVariableSet>().ToList()).ToList();
        var selectedSets = allSets
            .Where(s => profile.EnvironmentVariableSetIds.Contains(s.Id))
            .OrderBy(s => s.Priority)
            .ToList();

        foreach (var set in selectedSets)
            foreach (var kvp in set.Variables)
                result[kvp.Key] = kvp.Value;

        // Layer 2: Profile-level overrides
        foreach (var kvp in profile.EnvironmentOverrides)
            result[kvp.Key] = kvp.Value;

        // Layer 3: Host-level overrides
        if (host.EnvironmentOverrides != null)
            foreach (var kvp in host.EnvironmentOverrides)
                result[kvp.Key] = kvp.Value;

        // Expand $RR_* variable references
        ExpandVariableReferences(result);

        return result;
    }

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
            if (!changed) break;
        }
    }

    private static async Task UpdateWebhookEventError(IDocumentStore store, WebhookEvent evt, string error)
    {
        evt.Status = "error";
        evt.Error = error;
        await store.Update(evt);
    }
}
