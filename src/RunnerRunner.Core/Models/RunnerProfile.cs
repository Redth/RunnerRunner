namespace RunnerRunner.Core.Models;

public class RunnerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public RunnerProvider Provider { get; set; }
    public string? RunnerAgentVersion { get; set; } // null = latest
    public HostPlatform RequiredHostPlatform { get; set; }
    public ExecutionBackend ExecutionBackend { get; set; }

    // References to EnvironmentVariableSet IDs (composed in priority order)
    public List<string> EnvironmentVariableSetIds { get; set; } = [];
    public Dictionary<string, string> EnvironmentOverrides { get; set; } = new();
    public HashSet<string> EnvironmentOverrideSecretKeys { get; set; } = [];

    // Image configuration (embedded documents)
    public DockerImageConfig? DockerConfig { get; set; }
    public TartImageConfig? TartConfig { get; set; }

    // Provider credential reference
    public string? ProviderCredentialId { get; set; }

    // Runner configuration
    public List<string> Labels { get; set; } = [];
    public string RunnerGroup { get; set; } = "Default";
    public bool Ephemeral { get; set; }
    public int MaxParallelPerHost { get; set; } = 1;

    // Provider-specific configuration (org, repo, token reference, etc.)
    public Dictionary<string, string> ProviderConfig { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
