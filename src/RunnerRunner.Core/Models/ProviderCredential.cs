namespace RunnerRunner.Core.Models;

public class ProviderCredential
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Name { get; set; }
    public RunnerProvider Provider { get; set; }

    // GitHub
    public string? GitHubOrg { get; set; }
    public string? GitHubRepo { get; set; }
    public string? GitHubToken { get; set; }
    public string? GitHubApiUrl { get; set; }
    public string? GitHubServerUrl { get; set; }

    // Gitea
    public string? GiteaInstanceUrl { get; set; }
    public string? GiteaRunnerToken { get; set; }

    // Azure DevOps
    public string? AzDoOrgUrl { get; set; }
    public string? AzDoProjectName { get; set; }
    public string? AzDoPat { get; set; }
    public string? AzDoPoolName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
