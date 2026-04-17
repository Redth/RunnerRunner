using Orleans;

namespace RunnerRunner.Core.Models;

[GenerateSerializer]
public class ProviderCredential
{
    [Id(0)]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    [Id(1)]
    public required string Name { get; set; }
    [Id(2)]
    public RunnerProvider Provider { get; set; }

    // GitHub
    [Id(3)]
    public string? GitHubOrg { get; set; }
    [Id(4)]
    public string? GitHubRepo { get; set; }
    [Id(5)]
    public string? GitHubToken { get; set; }
    [Id(6)]
    public string? GitHubApiUrl { get; set; }
    [Id(7)]
    public string? GitHubServerUrl { get; set; }

    // Gitea
    [Id(8)]
    public string? GiteaInstanceUrl { get; set; }
    [Id(9)]
    public string? GiteaRunnerToken { get; set; }

    // Azure DevOps
    [Id(10)]
    public string? AzDoOrgUrl { get; set; }
    [Id(11)]
    public string? AzDoProjectName { get; set; }
    [Id(12)]
    public string? AzDoPat { get; set; }
    [Id(13)]
    public string? AzDoPoolName { get; set; }

    [Id(14)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Id(15)]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
