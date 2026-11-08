using Microsoft.Extensions.Logging;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Providers;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Best-effort provider-side cleanup for runner registrations that may linger
/// after host/process crashes or abrupt teardown.
/// </summary>
public class RunnerRegistrationCleanupService
{
    private readonly IEnumerable<IRunnerProviderPlugin> _providers;
    private readonly ILogger<RunnerRegistrationCleanupService> _logger;

    public RunnerRegistrationCleanupService(
        IEnumerable<IRunnerProviderPlugin> providers,
        ILogger<RunnerRegistrationCleanupService> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    public async Task TryRemoveRunnerAsync(
        IDocumentStore store,
        RunnerInstance instance,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instance.RunnerName) || string.IsNullOrWhiteSpace(instance.ProfileId))
            return;

        var profile = await ResolveProfileAsync(store, instance);
        if (profile == null || string.IsNullOrWhiteSpace(profile.ProviderCredentialId))
            return;

        var credential = await store.Get<ProviderCredential>(profile.ProviderCredentialId);
        if (credential == null)
            return;

        var provider = _providers.FirstOrDefault(p => p.Provider == profile.Provider);
        if (provider == null)
            return;

        try
        {
            var scopedCredential = credential;

            if (profile.Provider == RunnerProvider.GitHubActions)
            {
                var scope = await ResolveGitHubScopeAsync(store, instance);
                scopedCredential = ScopeGitHubCredentialToRepository(
                    credential,
                    scope.Repository,
                    scope.InstallationId);

                if (string.IsNullOrWhiteSpace(scopedCredential.GitHubOrg)
                    && string.IsNullOrWhiteSpace(scopedCredential.GitHubRepo))
                {
                    _logger.LogDebug(
                        "Skipping provider-side cleanup for runner {RunnerName}; no GitHub org/repo scope could be resolved",
                        instance.RunnerName);
                    return;
                }
            }

            await provider.RemoveRunnerAsync(scopedCredential, instance.RunnerName, ct);
            _logger.LogInformation(
                "Attempted provider-side cleanup for runner {RunnerName} via {Provider}",
                instance.RunnerName,
                profile.Provider);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Best-effort provider-side cleanup failed for runner {RunnerName}",
                instance.RunnerName);
        }
    }

    /// <summary>
    /// Clears a runner to be stopped when nothing proves it is done with a job of its own:
    /// it is deregistered first, and only if the provider says it is idle. A just-in-time
    /// runner is not pinned to the job it was minted for, so "the runner we started for a job
    /// that finished without it" (a job cancelled while queued, one that timed out waiting,
    /// a runner whose pickup timed out) may be running someone else's job by now, or be handed
    /// one while its container stops. Call <see cref="PermitsStop"/> on the result.
    /// A runner that the provider says ran a completed job needs no clearance.
    /// </summary>
    public async Task<RunnerReleaseOutcome> ReleaseIdleRunnerAsync(
        IDocumentStore store,
        RunnerInstance instance,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instance.RunnerName) || string.IsNullOrWhiteSpace(instance.ProfileId))
            return RunnerReleaseOutcome.NotChecked;

        var profile = await ResolveProfileAsync(store, instance);
        if (profile == null)
        {
            _logger.LogWarning(
                "Cannot tell which provider runner {RunnerName} belongs to (profile {ProfileId} not found); stopping it unchecked",
                instance.RunnerName, instance.ProfileId);
            return RunnerReleaseOutcome.NotChecked;
        }

        if (_providers.FirstOrDefault(p => p.Provider == profile.Provider) is not IIdleRunnerReleaser releaser)
            return RunnerReleaseOutcome.NotChecked;

        // From here on the provider can say whether the runner is busy, so a stop waits for it.
        var credential = string.IsNullOrWhiteSpace(profile.ProviderCredentialId)
            ? null
            : await store.Get<ProviderCredential>(profile.ProviderCredentialId);
        if (credential == null)
        {
            _logger.LogWarning(
                "Cannot ask {Provider} whether runner {RunnerName} is idle: profile {ProfileId} has no credential",
                profile.Provider, instance.RunnerName, profile.Id);
            return RunnerReleaseOutcome.Unverified;
        }

        try
        {
            var scope = await ResolveGitHubScopeAsync(store, instance);
            var scopedCredential = ScopeGitHubCredentialToRepository(credential, scope.Repository, scope.InstallationId);
            var outcome = await releaser.ReleaseIdleRunnerAsync(scopedCredential, scope.Repository, instance.RunnerName, ct);
            _logger.LogInformation(
                "Asked {Provider} to release runner {RunnerName} before stopping it: {Outcome}",
                profile.Provider, instance.RunnerName, outcome);
            return outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not ask {Provider} whether runner {RunnerName} is idle; not stopping it",
                profile.Provider, instance.RunnerName);
            return RunnerReleaseOutcome.Unverified;
        }
    }

    /// <summary>
    /// The profile the instance was provisioned from. A rule-owned runner target has no stored
    /// <see cref="RunnerProfile"/>: provisioning builds one from the rule (with the rule's
    /// credential) and gives it the target's id, so look there too. Without this, cleanup of
    /// every runner-target runner found no profile and silently skipped deregistration.
    /// </summary>
    internal static async Task<RunnerProfile?> ResolveProfileAsync(IDocumentStore store, RunnerInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.ProfileId))
            return null;

        var stored = await store.Get<RunnerProfile>(instance.ProfileId);
        if (stored != null)
            return stored;

        var targetId = string.IsNullOrWhiteSpace(instance.RunnerDefinitionId) ? instance.ProfileId : instance.RunnerDefinitionId;
        var ruleId = instance.ProvisioningRuleId;
        if (string.IsNullOrWhiteSpace(ruleId) && !string.IsNullOrWhiteSpace(instance.WebhookEventId))
            ruleId = (await store.Get<WebhookEvent>(instance.WebhookEventId))?.BindingId;

        if (!string.IsNullOrWhiteSpace(ruleId))
        {
            var rule = await store.Get<ProvisioningRule>(ruleId);
            var target = rule?.RunnerDefinitions.FirstOrDefault(r => string.Equals(r.Id, targetId, StringComparison.Ordinal));
            if (rule != null && target != null)
                return target.ToProfile(rule);
        }

        var rules = await store.Query<ProvisioningRule>().ToList();
        foreach (var rule in rules)
        {
            var target = rule.RunnerDefinitions.FirstOrDefault(r => string.Equals(r.Id, targetId, StringComparison.Ordinal));
            if (target != null)
                return target.ToProfile(rule);
        }

        return null;
    }

    /// <summary>True when <paramref name="outcome"/> lets the caller stop the runner.</summary>
    public static bool PermitsStop(RunnerReleaseOutcome outcome) =>
        outcome is RunnerReleaseOutcome.Deregistered
            or RunnerReleaseOutcome.NotRegistered
            or RunnerReleaseOutcome.NotChecked;

    public async Task<int> SweepStaleRegistrationsAsync(
        IDocumentStore store,
        CancellationToken ct = default)
    {
        var githubProvider = _providers.OfType<GitHubActionsProvider>().FirstOrDefault();
        if (githubProvider == null)
            return 0;

        var protectedRunnerNames = BuildGitHubProtectedRunnerNames(
            await store.Query<RunnerInstance>().ToList());

        var credentials = (await store.Query<ProviderCredential>().ToList())
            .Where(c => c.Provider == RunnerProvider.GitHubActions)
            .ToList();

        var recentEvents = (await store.Query<WebhookEvent>().ToList())
            .Where(e =>
                string.Equals(e.Provider, RunnerProvider.GitHubActions.ToString(), StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(e.Repository))
            .ToList();

        var removed = 0;
        foreach (var scopedCredential in BuildGitHubSweepScopes(credentials, recentEvents))
        {
            try
            {
                removed += await githubProvider.RemoveOfflineDynamicRunnersAsync(scopedCredential, protectedRunnerNames, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed stale GitHub runner sweep for scope org={Org} repo={Repo}",
                    scopedCredential.GitHubOrg,
                    scopedCredential.GitHubRepo);
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("Removed {Count} stale GitHub runner registrations during periodic sweep", removed);
        }

        return removed;
    }

    internal static HashSet<string> BuildGitHubProtectedRunnerNames(IEnumerable<RunnerInstance> instances)
        => instances
            .Where(CapacityPlanningService.IsRunnerRunnerManaged)
            .Select(i => i.RunnerName?.Trim())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

    internal static ProviderCredential ScopeGitHubCredentialToRepository(
        ProviderCredential credential,
        string? repository,
        string? installationId = null)
    {
        var clone = new ProviderCredential
        {
            Id = credential.Id,
            Name = credential.Name,
            Provider = credential.Provider,
            GitHubOrg = credential.GitHubOrg,
            GitHubRepo = credential.GitHubRepo,
            GitHubToken = credential.GitHubToken,
            GitHubApiUrl = credential.GitHubApiUrl,
            GitHubServerUrl = credential.GitHubServerUrl,
            GitHubAuthType = credential.GitHubAuthType,
            GitHubAppId = credential.GitHubAppId,
            GitHubAppPrivateKey = credential.GitHubAppPrivateKey,
            GitHubAppInstallationId = GitHubCredentialResolver.ResolveInstallationId(credential, installationId, repository),
            GitHubAppWebhookSecret = credential.GitHubAppWebhookSecret,
            GitHubAppInstallations = CloneInstallations(credential.GitHubAppInstallations),
            GiteaInstanceUrl = credential.GiteaInstanceUrl,
            GiteaRunnerToken = credential.GiteaRunnerToken,
            AzDoOrgUrl = credential.AzDoOrgUrl,
            AzDoProjectName = credential.AzDoProjectName,
            AzDoPat = credential.AzDoPat,
            AzDoPoolName = credential.AzDoPoolName,
            CreatedAt = credential.CreatedAt,
            UpdatedAt = credential.UpdatedAt
        };

        if (string.IsNullOrWhiteSpace(repository))
            return clone;

        var trimmed = repository.Trim().Trim('/');
        var parts = trimmed.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return clone;

        clone.GitHubOrg = parts[0];
        clone.GitHubRepo = $"{parts[0]}/{parts[1]}";
        return clone;
    }

    internal static IEnumerable<ProviderCredential> BuildGitHubSweepScopes(
        IEnumerable<ProviderCredential> credentials,
        IEnumerable<string> recentRepositories)
        => BuildGitHubSweepScopes(
            credentials,
            recentRepositories.Select(repository => new WebhookEvent
            {
                Provider = RunnerProvider.GitHubActions.ToString(),
                Repository = repository
            }));

    internal static IEnumerable<ProviderCredential> BuildGitHubSweepScopes(
        IEnumerable<ProviderCredential> credentials,
        IEnumerable<WebhookEvent> recentEvents)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repositoryScopes = recentEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.Repository))
            .GroupBy(e => e.Repository.Trim().Trim('/'), StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Repository = g.Key,
                InstallationId = g
                    .Select(e => e.GitHubInstallationId)
                    .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
            })
            .ToList();

        foreach (var credential in credentials)
        {
            if (!GitHubAuthenticationService.HasGitHubApiCredentials(credential)
                && !CanUseEventInstallationId(credential))
            {
                continue;
            }

            foreach (var target in GitHubCredentialResolver.GetConfiguredTargets(credential))
            {
                ProviderCredential scopedCredential;
                string key;
                if (!string.IsNullOrWhiteSpace(target.Repository))
                {
                    scopedCredential = ScopeGitHubCredentialToRepository(credential, target.Repository, target.InstallationId);
                    key = $"repo:{scopedCredential.GitHubRepo}";
                }
                else if (!string.IsNullOrWhiteSpace(target.Owner))
                {
                    scopedCredential = ScopeGitHubCredentialToRepository(credential, null, target.InstallationId);
                    scopedCredential.GitHubOrg = target.Owner;
                    scopedCredential.GitHubRepo = null;
                    key = $"org:{scopedCredential.GitHubOrg}";
                }
                else
                {
                    continue;
                }

                if (seen.Add(key) && GitHubAuthenticationService.HasGitHubApiCredentials(scopedCredential))
                    yield return scopedCredential;
            }

            var targetOwners = GitHubCredentialResolver.GetTargetOwners(credential).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var repoScope in repositoryScopes)
            {
                var owner = repoScope.Repository.Split('/', 2)[0];
                if (targetOwners.Count > 0 && !targetOwners.Contains(owner))
                    continue;

                var repoScoped = ScopeGitHubCredentialToRepository(
                    credential,
                    repoScope.Repository,
                    repoScope.InstallationId);
                var repoKey = $"repo:{repoScoped.GitHubRepo}";
                if (seen.Add(repoKey) && GitHubAuthenticationService.HasGitHubApiCredentials(repoScoped))
                    yield return repoScoped;
            }
        }
    }

    private static List<GitHubAppInstallation> CloneInstallations(IEnumerable<GitHubAppInstallation>? installations)
        => installations?
            .Select(i => new GitHubAppInstallation
            {
                Owner = i.Owner,
                Repository = i.Repository,
                InstallationId = i.InstallationId,
                IsDefault = i.IsDefault
            })
            .ToList() ?? [];

    private static bool CanUseEventInstallationId(ProviderCredential credential) =>
        credential.GitHubAuthType == GitHubAuthType.GitHubApp
        && !string.IsNullOrWhiteSpace(credential.GitHubAppId)
        && !string.IsNullOrWhiteSpace(credential.GitHubAppPrivateKey);

    private sealed record GitHubCleanupScope(string? Repository, string? InstallationId);

    private static async Task<GitHubCleanupScope> ResolveGitHubScopeAsync(IDocumentStore store, RunnerInstance instance)
    {
        if (!string.IsNullOrWhiteSpace(instance.WebhookEventId))
        {
            var evt = await store.Get<WebhookEvent>(instance.WebhookEventId);
            if (!string.IsNullOrWhiteSpace(evt?.Repository))
                return new GitHubCleanupScope(evt.Repository, evt.GitHubInstallationId);
        }

        if (!string.IsNullOrWhiteSpace(instance.JobId))
        {
            var evt = (await store.Query<WebhookEvent>().ToList())
                .Where(x => x.JobId == instance.JobId)
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(evt?.Repository))
                return new GitHubCleanupScope(evt.Repository, evt.GitHubInstallationId);
        }

        return new GitHubCleanupScope(null, null);
    }
}
