using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Services;

public static class ProviderNavigationLinks
{
    public static string? BuildJobUrl(WebhookEvent evt, ProviderCredential? credential)
    {
        if (string.IsNullOrWhiteSpace(evt.RunId))
            return null;

        return ParseProvider(evt.Provider) switch
        {
            RunnerProvider.GitHubActions => BuildGitHubJobUrl(evt, credential),
            RunnerProvider.GiteaActions => BuildGiteaJobUrl(evt, credential),
            RunnerProvider.AzureDevOps => BuildAzureDevOpsJobUrl(evt, credential),
            _ => null
        };
    }

    public static string? BuildRunnerPageUrl(RunnerProfile profile, ProviderCredential? credential)
    {
        if (credential == null)
            return null;

        return profile.Provider switch
        {
            RunnerProvider.GitHubActions => BuildGitHubRunnerPageUrl(credential),
            RunnerProvider.GiteaActions => BuildGiteaRunnerPageUrl(credential),
            RunnerProvider.AzureDevOps => BuildAzureDevOpsRunnerPageUrl(credential, profile.RunnerGroup),
            _ => null
        };
    }

    public static string GetProviderLabel(string? provider)
        => ParseProvider(provider) switch
        {
            RunnerProvider.GitHubActions => "GitHub",
            RunnerProvider.GiteaActions => "Gitea",
            RunnerProvider.AzureDevOps => "Azure DevOps",
            _ => string.IsNullOrWhiteSpace(provider) ? "provider" : provider
        };

    public static RunnerProvider? ParseProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return null;

        return provider.Trim().ToLowerInvariant() switch
        {
            "github" or "githubactions" or "github actions" => RunnerProvider.GitHubActions,
            "gitea" or "giteaactions" or "gitea actions" => RunnerProvider.GiteaActions,
            "azdo" or "azuredevops" or "azure devops" or "azurepipelines" or "azure pipelines" => RunnerProvider.AzureDevOps,
            _ when Enum.TryParse<RunnerProvider>(provider, ignoreCase: true, out var parsed) => parsed,
            _ => null
        };
    }

    private static string? BuildGitHubJobUrl(WebhookEvent evt, ProviderCredential? credential)
    {
        var repository = NormalizeRepository(evt.Repository);
        if (repository == null)
            return null;

        var serverUrl = credential?.GitHubServerUrl?.TrimEnd('/') ?? "https://github.com";
        var runPath = $"{serverUrl}/{EscapeRepositoryPath(repository)}/actions/runs/{EscapePathSegment(evt.RunId)}";
        return string.IsNullOrWhiteSpace(evt.JobId)
            ? runPath
            : $"{runPath}/job/{EscapePathSegment(evt.JobId)}";
    }

    private static string? BuildGiteaJobUrl(WebhookEvent evt, ProviderCredential? credential)
    {
        var repository = NormalizeRepository(evt.Repository);
        var serverUrl = credential?.GiteaInstanceUrl?.TrimEnd('/');
        if (repository == null || string.IsNullOrWhiteSpace(serverUrl))
            return null;

        var runPath = $"{serverUrl}/{EscapeRepositoryPath(repository)}/actions/runs/{EscapePathSegment(evt.RunId)}";
        return string.IsNullOrWhiteSpace(evt.JobId)
            ? runPath
            : $"{runPath}/jobs/{EscapePathSegment(evt.JobId)}";
    }

    private static string? BuildAzureDevOpsJobUrl(WebhookEvent evt, ProviderCredential? credential)
    {
        var orgUrl = credential?.AzDoOrgUrl?.TrimEnd('/');
        var projectName = credential?.AzDoProjectName?.Trim();
        if (string.IsNullOrWhiteSpace(orgUrl) || string.IsNullOrWhiteSpace(projectName))
            return null;

        var url = $"{orgUrl}/{EscapePathSegment(projectName)}/_build/results?buildId={Uri.EscapeDataString(evt.RunId)}";
        return string.IsNullOrWhiteSpace(evt.JobId)
            ? url
            : $"{url}&view=logs&j={Uri.EscapeDataString(evt.JobId)}";
    }

    private static string? BuildGitHubRunnerPageUrl(ProviderCredential credential)
    {
        var serverUrl = credential.GitHubServerUrl?.TrimEnd('/') ?? "https://github.com";
        var target = GitHubCredentialResolver.ResolveDefaultTarget(credential);

        if (!string.IsNullOrWhiteSpace(target?.Repository))
            return $"{serverUrl}/{EscapeRepositoryPath(target.Repository)}/settings/actions/runners";

        if (!string.IsNullOrWhiteSpace(target?.Owner))
            return $"{serverUrl}/organizations/{EscapePathSegment(target.Owner)}/settings/actions/runners";

        return null;
    }

    private static string? BuildGiteaRunnerPageUrl(ProviderCredential credential)
    {
        var serverUrl = credential.GiteaInstanceUrl?.TrimEnd('/');
        return string.IsNullOrWhiteSpace(serverUrl) ? null : $"{serverUrl}/admin/actions/runners";
    }

    private static string? BuildAzureDevOpsRunnerPageUrl(ProviderCredential credential, string? profileRunnerGroup)
    {
        var orgUrl = credential.AzDoOrgUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(orgUrl))
            return null;

        var poolName = string.IsNullOrWhiteSpace(credential.AzDoPoolName)
            ? profileRunnerGroup
            : credential.AzDoPoolName;
        var poolQuery = string.IsNullOrWhiteSpace(poolName)
            ? ""
            : $"?poolName={Uri.EscapeDataString(poolName.Trim())}";

        if (!string.IsNullOrWhiteSpace(credential.AzDoProjectName))
            return $"{orgUrl}/{EscapePathSegment(credential.AzDoProjectName.Trim())}/_settings/agentqueues{poolQuery}";

        return $"{orgUrl}/_settings/agentpools{poolQuery}";
    }

    private static string? NormalizeRepository(string? repository)
    {
        var value = repository?.Trim().Trim('/');
        return string.IsNullOrWhiteSpace(value) || !value.Contains('/') ? null : value;
    }

    private static string EscapeRepositoryPath(string repository)
        => string.Join('/', repository.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(EscapePathSegment));

    private static string EscapePathSegment(string value)
        => Uri.EscapeDataString(value);
}
