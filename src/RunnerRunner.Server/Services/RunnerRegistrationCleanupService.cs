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

        var profile = await store.Get<RunnerProfile>(instance.ProfileId);
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
                var repository = await ResolveRepositoryAsync(store, instance);
                scopedCredential = ScopeGitHubCredentialToRepository(credential, repository);

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
            .Where(c => c.Provider == RunnerProvider.GitHubActions && !string.IsNullOrWhiteSpace(c.GitHubToken))
            .ToList();

        var recentRepositories = (await store.Query<WebhookEvent>().ToList())
            .Where(e =>
                string.Equals(e.Provider, RunnerProvider.GitHubActions.ToString(), StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(e.Repository))
            .Select(e => e.Repository!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var removed = 0;
        foreach (var scopedCredential in BuildGitHubSweepScopes(credentials, recentRepositories))
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
        string? repository)
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
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repositories = recentRepositories
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim().Trim('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var credential in credentials)
        {
            if (string.IsNullOrWhiteSpace(credential.GitHubToken))
                continue;

            if (!string.IsNullOrWhiteSpace(credential.GitHubRepo))
            {
                var repoScoped = ScopeGitHubCredentialToRepository(credential, credential.GitHubRepo);
                var repoKey = $"repo:{repoScoped.GitHubRepo}";
                if (seen.Add(repoKey))
                    yield return repoScoped;
            }

            if (!string.IsNullOrWhiteSpace(credential.GitHubOrg))
            {
                var orgKey = $"org:{credential.GitHubOrg}";
                if (seen.Add(orgKey))
                    yield return ScopeGitHubCredentialToRepository(credential, null);

                foreach (var repo in repositories.Where(r =>
                             r.StartsWith($"{credential.GitHubOrg}/", StringComparison.OrdinalIgnoreCase)))
                {
                    var repoScoped = ScopeGitHubCredentialToRepository(credential, repo);
                    var repoKey = $"repo:{repoScoped.GitHubRepo}";
                    if (seen.Add(repoKey))
                        yield return repoScoped;
                }
            }
        }
    }

    private static async Task<string?> ResolveRepositoryAsync(IDocumentStore store, RunnerInstance instance)
    {
        if (!string.IsNullOrWhiteSpace(instance.WebhookEventId))
        {
            var evt = await store.Get<WebhookEvent>(instance.WebhookEventId);
            if (!string.IsNullOrWhiteSpace(evt?.Repository))
                return evt.Repository;
        }

        if (!string.IsNullOrWhiteSpace(instance.JobId))
        {
            var evt = (await store.Query<WebhookEvent>().ToList())
                .Where(x => x.JobId == instance.JobId)
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(evt?.Repository))
                return evt.Repository;
        }

        return null;
    }
}
