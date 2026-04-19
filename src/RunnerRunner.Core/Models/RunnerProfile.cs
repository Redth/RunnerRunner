using Orleans;

namespace RunnerRunner.Core.Models;

[GenerateSerializer]
public class RunnerProfile
{
    [Id(0)]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    [Id(1)]
    public required string Name { get; set; }
    [Id(2)]
    public string? Description { get; set; }
    [Id(3)]
    public RunnerProvider Provider { get; set; }
    [Id(4)]
    public string? RunnerAgentVersion { get; set; } // null = latest
    [Id(5)]
    public HostPlatform RequiredHostPlatform { get; set; }
    [Id(6)]
    public ExecutionBackend ExecutionBackend { get; set; }

    // References to EnvironmentVariableSet IDs (composed in priority order)
    [Id(7)]
    public List<string> EnvironmentVariableSetIds { get; set; } = [];
    [Id(8)]
    public Dictionary<string, string> EnvironmentOverrides { get; set; } = new();
    [Id(9)]
    public HashSet<string> EnvironmentOverrideSecretKeys { get; set; } = [];

    // Image configuration (embedded documents)
    [Id(10)]
    public DockerImageConfig? DockerConfig { get; set; }
    [Id(11)]
    public TartImageConfig? TartConfig { get; set; }

    // Provider credential reference
    [Id(12)]
    public string? ProviderCredentialId { get; set; }

    // Runner configuration
    [Id(13)]
    public List<string> Labels { get; set; } = [];
    [Id(14)]
    public string RunnerGroup { get; set; } = "Default";
    [Id(15)]
    public bool Ephemeral { get; set; }
    [Id(16)]
    public int MaxParallelPerHost { get; set; } = 1;

    // Provider-specific configuration (org, repo, token reference, etc.)
    [Id(17)]
    public Dictionary<string, string> ProviderConfig { get; set; } = new();

    [Id(18)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Id(19)]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Ordered list of custom provisioning steps executed on the host as part of runner
    // startup (and teardown for PostExit steps). See RunnerInitStep.
    [Id(20)]
    public List<RunnerInitStep> InitSteps { get; set; } = [];
}
