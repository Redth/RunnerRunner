using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Services;

internal sealed record GitHubCredentialTarget(
    string? Owner,
    string? Repository,
    string? InstallationId,
    bool IsDefault);

internal static class GitHubCredentialResolver
{
    public static string? NormalizeOwner(string? owner)
    {
        var value = owner?.Trim().Trim('/');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string? NormalizeRepository(string? repository, string? owner = null)
    {
        var trimmed = repository?.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        if (trimmed.Contains('/'))
            return trimmed;

        var normalizedOwner = NormalizeOwner(owner);
        return string.IsNullOrWhiteSpace(normalizedOwner) ? null : $"{normalizedOwner}/{trimmed}";
    }

    public static IReadOnlyList<GitHubCredentialTarget> GetInstallationTargets(ProviderCredential credential)
    {
        var targets = new List<GitHubCredentialTarget>();

        foreach (var installation in credential.GitHubAppInstallations ?? [])
        {
            var owner = NormalizeOwner(installation.Owner);
            var repository = NormalizeRepository(installation.Repository, owner);

            if (repository != null)
                owner = repository.Split('/', 2)[0];

            if (owner == null && repository == null)
                continue;

            var installationId = installation.InstallationId?.Trim();
            targets.Add(new GitHubCredentialTarget(
                owner,
                repository,
                string.IsNullOrWhiteSpace(installationId) ? null : installationId,
                installation.IsDefault));
        }

        return targets;
    }

    public static GitHubCredentialTarget? ResolveDefaultTarget(ProviderCredential credential)
    {
        var targets = GetInstallationTargets(credential);
        var explicitDefault = targets.FirstOrDefault(t => t.IsDefault);
        if (explicitDefault != null)
            return explicitDefault;

        if (targets.Count == 1
            && string.IsNullOrWhiteSpace(credential.GitHubOrg)
            && string.IsNullOrWhiteSpace(credential.GitHubRepo))
        {
            return targets[0];
        }

        var configuredTarget = ResolveConfiguredTarget(credential);
        if (configuredTarget != null)
            return configuredTarget;

        return targets.FirstOrDefault();
    }

    public static GitHubCredentialTarget? ResolveTargetForRepository(
        ProviderCredential credential,
        string? repository)
    {
        var normalizedRepository = NormalizeRepository(repository);
        if (string.IsNullOrWhiteSpace(normalizedRepository))
            return null;

        var owner = normalizedRepository.Split('/', 2)[0];
        var targets = GetInstallationTargets(credential);

        return targets.FirstOrDefault(t =>
                !string.IsNullOrWhiteSpace(t.Repository)
                && string.Equals(t.Repository, normalizedRepository, StringComparison.OrdinalIgnoreCase))
            ?? targets.FirstOrDefault(t =>
                string.IsNullOrWhiteSpace(t.Repository)
                && string.Equals(t.Owner, owner, StringComparison.OrdinalIgnoreCase))
            ?? ResolveLegacyTargetForRepository(credential, normalizedRepository);
    }

    public static string? ResolveInstallationId(
        ProviderCredential credential,
        string? installationId = null,
        string? repository = null)
    {
        if (!string.IsNullOrWhiteSpace(installationId))
            return installationId.Trim();

        var repositoryTarget = ResolveTargetForRepository(credential, repository);
        if (!string.IsNullOrWhiteSpace(repositoryTarget?.InstallationId))
            return repositoryTarget.InstallationId.Trim();

        var defaultTarget = ResolveDefaultTarget(credential);
        if (!string.IsNullOrWhiteSpace(defaultTarget?.InstallationId))
            return defaultTarget.InstallationId.Trim();

        return string.IsNullOrWhiteSpace(credential.GitHubAppInstallationId)
            ? null
            : credential.GitHubAppInstallationId.Trim();
    }

    public static IEnumerable<string> GetTargetOwners(ProviderCredential credential)
    {
        var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(credential.GitHubOrg))
            owners.Add(credential.GitHubOrg.Trim().Trim('/'));

        foreach (var target in GetInstallationTargets(credential))
        {
            if (!string.IsNullOrWhiteSpace(target.Owner))
                owners.Add(target.Owner);
        }

        return owners;
    }

    public static IEnumerable<GitHubCredentialTarget> GetConfiguredTargets(ProviderCredential credential)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var configuredTarget = ResolveConfiguredTarget(credential);
        if (configuredTarget != null && seen.Add(TargetKey(configuredTarget)))
            yield return configuredTarget;

        foreach (var target in GetInstallationTargets(credential))
        {
            if (seen.Add(TargetKey(target)))
                yield return target;
        }
    }

    public static string? GetRunnerUrl(ProviderCredential credential)
    {
        var serverUrl = credential.GitHubServerUrl?.TrimEnd('/') ?? "https://github.com";
        var target = ResolveDefaultTarget(credential);

        if (!string.IsNullOrWhiteSpace(target?.Repository))
            return $"{serverUrl}/{target.Repository}";

        if (!string.IsNullOrWhiteSpace(target?.Owner))
            return $"{serverUrl}/{target.Owner}";

        return null;
    }

    private static GitHubCredentialTarget? ResolveConfiguredTarget(ProviderCredential credential)
    {
        var normalizedRepository = NormalizeRepository(credential.GitHubRepo, credential.GitHubOrg);
        if (!string.IsNullOrWhiteSpace(normalizedRepository))
        {
            var installationTarget = ResolveTargetForRepository(credential, normalizedRepository);
            return new GitHubCredentialTarget(
                normalizedRepository.Split('/', 2)[0],
                normalizedRepository,
                installationTarget?.InstallationId ?? NormalizeInstallationId(credential.GitHubAppInstallationId),
                true);
        }

        var owner = NormalizeOwner(credential.GitHubOrg);
        if (!string.IsNullOrWhiteSpace(owner))
        {
            var target = GetInstallationTargets(credential).FirstOrDefault(t =>
                    string.IsNullOrWhiteSpace(t.Repository)
                    && string.Equals(t.Owner, owner, StringComparison.OrdinalIgnoreCase))
                ?? GetInstallationTargets(credential).FirstOrDefault(t =>
                    string.Equals(t.Owner, owner, StringComparison.OrdinalIgnoreCase));

            return new GitHubCredentialTarget(
                owner,
                null,
                target?.InstallationId ?? NormalizeInstallationId(credential.GitHubAppInstallationId),
                true);
        }

        return null;
    }

    private static GitHubCredentialTarget? ResolveLegacyTargetForRepository(
        ProviderCredential credential,
        string normalizedRepository)
    {
        var configuredRepository = NormalizeRepository(credential.GitHubRepo, credential.GitHubOrg);
        if (!string.IsNullOrWhiteSpace(configuredRepository)
            && string.Equals(configuredRepository, normalizedRepository, StringComparison.OrdinalIgnoreCase))
        {
            return new GitHubCredentialTarget(
                normalizedRepository.Split('/', 2)[0],
                normalizedRepository,
                NormalizeInstallationId(credential.GitHubAppInstallationId),
                true);
        }

        var owner = normalizedRepository.Split('/', 2)[0];
        if (!string.IsNullOrWhiteSpace(credential.GitHubOrg)
            && string.Equals(credential.GitHubOrg.Trim().Trim('/'), owner, StringComparison.OrdinalIgnoreCase))
        {
            return new GitHubCredentialTarget(
                owner,
                null,
                NormalizeInstallationId(credential.GitHubAppInstallationId),
                true);
        }

        return null;
    }

    private static string? NormalizeInstallationId(string? installationId)
    {
        var trimmed = installationId?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string TargetKey(GitHubCredentialTarget target) =>
        string.IsNullOrWhiteSpace(target.Repository)
            ? $"owner:{target.Owner}"
            : $"repo:{target.Repository}";
}
