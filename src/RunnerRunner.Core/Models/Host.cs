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
    public string? AgentCommitSha { get; set; }
    public string? AgentBuildTag { get; set; }
    public string? OsVersion { get; set; }
    public string? Architecture { get; set; }
    public bool IsContainerized { get; set; }
    public string? ContainerId { get; set; }
    public string? ContainerImage { get; set; }
    /// <summary>
    /// HostWorker-advertised backend and feature support, e.g. docker, tart,
    /// native, gpu, xcode16. Backend scheduling is controlled by backend
    /// resource limits; capabilities provide discovered defaults and optional
    /// target-specific routing filters.
    /// </summary>
    public List<string> Capabilities { get; set; } = [];
    public Dictionary<string, string> EnvironmentOverrides { get; set; } = new();
    public string? EnrollmentToken { get; set; }
    public string? EnrollmentTokenHash { get; set; }
    public DateTime? EnrollmentTokenCreatedAt { get; set; }
    public DateTime? EnrolledAt { get; set; }
    public string? WorkerId { get; set; }
    public string? LatestAvailableVersion { get; set; }
    public string? LatestAvailableCommitSha { get; set; }
    public DateTime? LastUpdateCheckAt { get; set; }
    public string? UpdateStatus { get; set; }
    public string? UpdateMessage { get; set; }
    public DateTime? LastUpdateStartedAt { get; set; }
    public DateTime? LastUpdateCompletedAt { get; set; }
    public bool IsApproved { get; set; }

    /// <summary>
    /// Base directory for runner agent binaries, instance dirs, and work.
    /// Default: C:\rr on Windows, ~/.runnerrunner elsewhere.
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
    /// Host routing labels for matching key/value constraints.
    /// e.g., os=linux, arch=x64, docker_os=linux, pool=build-farm
    /// </summary>
    public Dictionary<string, string> Labels { get; set; } = new();

    /// <summary>
    /// Resource limits per execution backend. Set a limit to 0 to disable
    /// scheduling for that backend on this host.
    /// </summary>
    public int MaxDockerContainers { get; set; } = 10;
    public int MaxTartVMs { get; set; }
    public int MaxNativeProcesses { get; set; } = 5;

    /// <summary>
    /// Total running Tart VMs observed on the host, including VMs not started by RunnerRunner.
    /// Used to reserve Tart capacity consumed by native jobs or other local workloads.
    /// </summary>
    public int? ObservedRunningTartVMs { get; set; }
    public DateTime? ObservedResourceUsageAt { get; set; }

    /// <summary>Host group ID for logical grouping.</summary>
    public string? GroupId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
