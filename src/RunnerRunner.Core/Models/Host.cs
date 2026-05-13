namespace RunnerRunner.Core.Models;

public class Host
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Name { get; set; }

    /// <summary>
    /// Optional friendly name / alias. Shown in UI when set, falls back to Name.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>Returns DisplayName if set, otherwise Name.</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
    public HostPlatform Platform { get; set; }
    public AgentStatus AgentStatus { get; set; } = AgentStatus.Offline;
    public DateTime? LastHeartbeat { get; set; }
    public string? AgentVersion { get; set; }
    public string? OsVersion { get; set; }
    public string? Architecture { get; set; }
    public List<string> Capabilities { get; set; } = [];
    public Dictionary<string, string> EnvironmentOverrides { get; set; } = new();
    public string? EnrollmentToken { get; set; }
    public string? EnrollmentTokenHash { get; set; }
    public DateTime? EnrollmentTokenCreatedAt { get; set; }
    public DateTime? EnrolledAt { get; set; }
    public string? WorkerId { get; set; }
    public string? LatestAvailableVersion { get; set; }
    public DateTime? LastUpdateCheckAt { get; set; }
    public string? UpdateStatus { get; set; }
    public string? UpdateMessage { get; set; }
    public DateTime? LastUpdateStartedAt { get; set; }
    public DateTime? LastUpdateCompletedAt { get; set; }
    public bool IsApproved { get; set; }

    /// <summary>
    /// Base directory for runner agent binaries, instance dirs, and work.
    /// Default: ~/.runnerrunner/
    /// </summary>
    public string? RunnerBasePath { get; set; }

    /// <summary>
    /// Override for runner work directory (where job files go).
    /// Default: {RunnerBasePath}/work/
    /// </summary>
    public string? WorkDirectory { get; set; }

    /// <summary>
    /// Cached host environment variables reported by the agent.
    /// Used as reference in the env var editor UI.
    /// </summary>
    public Dictionary<string, string> ReportedEnvironment { get; set; } = new();

    /// <summary>
    /// Capability labels for matching (key=value pairs).
    /// e.g., os=linux, arch=x64, docker=true, pool=build-farm
    /// </summary>
    public Dictionary<string, string> Labels { get; set; } = new();

    /// <summary>Resource limits per execution backend.</summary>
    public int MaxDockerContainers { get; set; } = 10;
    public int MaxTartVMs { get; set; } = 3;
    public int MaxNativeProcesses { get; set; } = 5;

    /// <summary>Host group ID for logical grouping.</summary>
    public string? GroupId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
