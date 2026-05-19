using Orleans;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Core.Hub;

/// <summary>
/// Methods the server can invoke on connected agents via SignalR.
/// </summary>
public interface IAgentHubClient
{
    Task DeployRunner(DeployRunnerCommand command);
    Task StopRunner(StopRunnerCommand command);
    Task SyncDesiredState(SyncDesiredStateCommand command);
    Task PullImage(PullImageCommand command);
    Task ListImages(ListImagesCommand command);
    Task DeleteImage(DeleteImageCommand command);
    Task LoginRegistry(LoginRegistryCommand command);
    Task GetHostEnvironment();
    Task GetHostLogs(GetHostLogsCommand command);
    Task GetRunnerLogs(GetRunnerLogsCommand command);
    Task CleanupOrphan(CleanupOrphanCommand command);
}

/// <summary>
/// Methods agents can invoke on the server via SignalR.
/// </summary>
public interface IAgentHubServer
{
    Task AgentConnected(AgentInfo agentInfo);
    Task AgentDisconnected(string agentId);
    Task RunnerStarted(RunnerStartedEvent evt);
    Task RunnerStopped(RunnerStoppedEvent evt);
    Task RunnerHealthUpdate(RunnerHealthUpdateEvent evt);
    Task Heartbeat(HeartbeatEvent evt);
    Task ImageListResponse(ImageListEvent evt);
    Task ImageRefreshStatus(ImageRefreshStatusEvent evt);
    Task ImagePullProgress(ImagePullProgressEvent evt);
    Task ImagePullComplete(ImagePullCompleteEvent evt);
    Task ImageDeleted(ImageDeletedEvent evt);
    Task HostEnvironmentResponse(HostEnvironmentEvent evt);
    Task RunnerDiscovery(RunnerDiscoveryEvent evt);
    Task Reconciliation(ReconciliationReport report);
    Task HostLogsResponse(HostLogsEvent evt);
    Task RunnerLogsResponse(RunnerLogsEvent evt);
}

// --- Commands (Server → Agent) ---

[GenerateSerializer]
public class DeployRunnerCommand
{
    [Id(0)]
    public string InstanceId { get; set; } = "";
    [Id(1)]
    public string ProfileId { get; set; } = "";
    [Id(2)]
    public required string RunnerName { get; set; }
    [Id(3)]
    public ExecutionBackend Backend { get; set; }
    [Id(4)]
    public RunnerProvider Provider { get; set; }
    [Id(5)]
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    [Id(6)]
    public string? RunnerAgentVersion { get; set; }
    [Id(7)]
    public DockerImageConfig? DockerConfig { get; set; }
    [Id(8)]
    public TartImageConfig? TartConfig { get; set; }
    [Id(9)]
    public List<string> Labels { get; set; } = [];
    [Id(10)]
    public string RunnerGroup { get; set; } = "Default";
    [Id(11)]
    public bool Ephemeral { get; set; }
    [Id(12)]
    public string? RegistrationToken { get; set; }
    [Id(13)]
    public string? RunnerUrl { get; set; }
    [Id(14)]
    public string? RunnerBasePath { get; set; }
    [Id(15)]
    public string? WorkDirectory { get; set; }

    /// <summary>Base64-encoded JIT runner config (GitHub). When set, skip config.sh and use --jitconfig.</summary>
    [Id(16)]
    public string? JitConfig { get; set; }

    /// <summary>"static" or "dynamic" — determines lifecycle behavior.</summary>
    [Id(17)]
    public string ProvisioningMode { get; set; } = "static";

    /// <summary>
    /// Ordered list of custom provisioning steps to run on the host as part of runner
    /// startup (PreRunner) and teardown (PostExit). See <see cref="ResolvedInitStep"/>.
    /// </summary>
    [Id(18)]
    public List<ResolvedInitStep> InitSteps { get; set; } = [];

    /// <summary>Resolved registry username for pulling the Docker image.</summary>
    [Id(19)]
    public string? RegistryUsername { get; set; }

    /// <summary>Resolved registry password/token for pulling the Docker image.</summary>
    [Id(20)]
    public string? RegistryPassword { get; set; }

    /// <summary>
    /// Capacity limit for the selected backend on the target host at dispatch time.
    /// HostWorkers use this for a final local preflight before starting backends
    /// whose usage can change outside RunnerRunner.
    /// </summary>
    [Id(21)]
    public int? BackendCapacityLimit { get; set; }
}

[GenerateSerializer]
public class StopRunnerCommand
{
    [Id(0)]
    public string InstanceId { get; set; } = "";
    [Id(1)]
    public string? InstanceHandle { get; set; }
}

[GenerateSerializer]
public enum HostCommandKind
{
    DeployRunner = 0,
    StopRunner = 1,
    CleanupOrphan = 2,
    ListImages = 3,
    PullImage = 4,
    DeleteImage = 5,
    GetHostLogs = 6,
    GetRunnerLogs = 7,
    ApplyHostWorkerUpdate = 8
}

[GenerateSerializer]
public sealed class HostCommandEnvelope
{
    [Id(0)]
    public HostCommandKind Kind { get; set; }

    [Id(1)]
    public DeployRunnerCommand? DeployRunner { get; set; }

    [Id(2)]
    public StopRunnerCommand? StopRunner { get; set; }

    [Id(3)]
    public CleanupOrphanCommand? CleanupOrphan { get; set; }

    [Id(4)]
    public ListImagesCommand? ListImages { get; set; }

    [Id(5)]
    public PullImageCommand? PullImage { get; set; }

    [Id(6)]
    public DeleteImageCommand? DeleteImage { get; set; }

    [Id(7)]
    public GetHostLogsCommand? GetHostLogs { get; set; }

    [Id(8)]
    public GetRunnerLogsCommand? GetRunnerLogs { get; set; }

    [Id(9)]
    public HostWorkerUpdateCommand? ApplyHostWorkerUpdate { get; set; }
}

[GenerateSerializer]
public class SyncDesiredStateCommand
{
    [Id(0)]
    public List<DesiredRunnerAssignment> Assignments { get; set; } = [];
}

[GenerateSerializer]
public class DesiredRunnerAssignment
{
    [Id(0)]
    public string ProfileId { get; set; } = "";
    [Id(1)]
    public int DesiredCount { get; set; }
}

[GenerateSerializer]
public class PullImageCommand
{
    [Id(0)]
    public ImageType ImageType { get; set; }
    [Id(1)]
    public required string ImageName { get; set; }
    [Id(2)]
    public string Tag { get; set; } = "latest";
    [Id(3)]
    public string? RegistryUrl { get; set; }
    [Id(4)]
    public string? Username { get; set; }
    [Id(5)]
    public string? Password { get; set; }
    [Id(6)]
    public DockerImageConfig? DockerConfig { get; set; }
    [Id(7)]
    public TartImageConfig? TartConfig { get; set; }
    [Id(8)]
    public string? TaskId { get; set; }
}

// --- Events (Agent → Server) ---

[GenerateSerializer]
public class AgentInfo
{
    [Id(0)]
    public required string AgentId { get; set; }
    [Id(1)]
    public required string Name { get; set; }
    [Id(2)]
    public HostPlatform Platform { get; set; }
    [Id(3)]
    public string? OsVersion { get; set; }
    [Id(4)]
    public string? Architecture { get; set; }
    [Id(5)]
    public string? AgentVersion { get; set; }
    [Id(6)]
    public List<string> Capabilities { get; set; } = [];
    [Id(7)]
    public List<RunningRunnerInfo> CurrentRunners { get; set; } = [];
    [Id(8)]
    public HostWorkerRuntimeInfo? Runtime { get; set; }
    [Id(9)]
    public string? AgentCommitSha { get; set; }
    [Id(10)]
    public string? AgentBuildTag { get; set; }
}

[GenerateSerializer]
public class HostWorkerRuntimeInfo
{
    [Id(0)]
    public bool IsContainer { get; set; }
    [Id(1)]
    public string? ContainerId { get; set; }
    [Id(2)]
    public string? ContainerImage { get; set; }
}

[GenerateSerializer]
public class RunningRunnerInfo
{
    [Id(0)]
    public string InstanceId { get; set; } = "";
    [Id(1)]
    public required string RunnerName { get; set; }
    [Id(2)]
    public required string InstanceHandle { get; set; }
    [Id(3)]
    public RunnerInstanceStatus Status { get; set; }
}

[GenerateSerializer]
public class RunnerStartedEvent
{
    [Id(0)]
    public string InstanceId { get; set; } = "";
    [Id(1)]
    public required string RunnerName { get; set; }
    [Id(2)]
    public required string InstanceHandle { get; set; }
    [Id(3)]
    public ExecutionBackend Backend { get; set; }
}

[GenerateSerializer]
public class RunnerStoppedEvent
{
    [Id(0)]
    public string InstanceId { get; set; } = "";
    [Id(1)]
    public required string Reason { get; set; }
    [Id(2)]
    public string? ErrorMessage { get; set; }
}

[GenerateSerializer]
public class RunnerHealthUpdateEvent
{
    [Id(0)]
    public string InstanceId { get; set; } = "";
    [Id(1)]
    public RunnerInstanceStatus Status { get; set; }
    [Id(2)]
    public DateTime CheckedAt { get; set; }
    [Id(3)]
    public string? StatusMessage { get; set; }
}

[GenerateSerializer]
public class HeartbeatEvent
{
    [Id(0)]
    public required string AgentId { get; set; }
    [Id(1)]
    public double CpuUsagePercent { get; set; }
    [Id(2)]
    public double MemoryUsagePercent { get; set; }
    [Id(3)]
    public double DiskUsagePercent { get; set; }
    [Id(4)]
    public int RunningInstanceCount { get; set; }
    [Id(5)]
    public HostResourceUsage? ResourceUsage { get; set; }
}

// --- Image Management Commands (Server → Agent) ---

[GenerateSerializer]
public class ListImagesCommand
{
    [Id(0)]
    public ImageType? FilterType { get; set; }
}

[GenerateSerializer]
public class DeleteImageCommand
{
    [Id(0)]
    public ImageType ImageType { get; set; }
    [Id(1)]
    public required string ImageId { get; set; }
    [Id(2)]
    public required string ImageName { get; set; }
}

[GenerateSerializer]
public class LoginRegistryCommand
{
    [Id(0)]
    public required string RegistryUrl { get; set; }
    [Id(1)]
    public string? Username { get; set; }
    [Id(2)]
    public string? Password { get; set; }
}

// --- Image Management Events (Agent → Server) ---

[GenerateSerializer]
public class ImageListEvent
{
    [Id(0)]
    public required string HostId { get; set; }
    [Id(1)]
    public List<AgentImageInfo> Images { get; set; } = [];
}

[GenerateSerializer]
public class ImageRefreshStatusEvent
{
    [Id(0)]
    public required string HostId { get; set; }
    [Id(1)]
    public string Stage { get; set; } = "";
    [Id(2)]
    public string Message { get; set; } = "";
    [Id(3)]
    public bool IsComplete { get; set; }
    [Id(4)]
    public bool Success { get; set; } = true;
}

[GenerateSerializer]
public class AgentImageInfo
{
    [Id(0)]
    public ImageType ImageType { get; set; }
    [Id(1)]
    public required string Repository { get; set; }
    [Id(2)]
    public string Tag { get; set; } = "latest";
    [Id(3)]
    public string? ImageId { get; set; }
    [Id(4)]
    public long SizeBytes { get; set; }
    [Id(5)]
    public DateTime? CreatedAt { get; set; }
}

[GenerateSerializer]
public class ImagePullProgressEvent
{
    [Id(0)]
    public required string HostId { get; set; }
    [Id(1)]
    public ImageType ImageType { get; set; }
    [Id(2)]
    public required string ImageName { get; set; }
    [Id(3)]
    public double ProgressPercent { get; set; }
    [Id(4)]
    public long BytesDownloaded { get; set; }
    [Id(5)]
    public long BytesTotal { get; set; }
    [Id(6)]
    public string? Status { get; set; }
    [Id(7)]
    public string? TaskId { get; set; }
}

[GenerateSerializer]
public class ImagePullCompleteEvent
{
    [Id(0)]
    public required string HostId { get; set; }
    [Id(1)]
    public ImageType ImageType { get; set; }
    [Id(2)]
    public required string ImageName { get; set; }
    [Id(3)]
    public bool Success { get; set; }
    [Id(4)]
    public string? Error { get; set; }
    [Id(5)]
    public string? TaskId { get; set; }
}

[GenerateSerializer]
public class ImageDeletedEvent
{
    [Id(0)]
    public required string HostId { get; set; }
    [Id(1)]
    public ImageType ImageType { get; set; }
    [Id(2)]
    public required string ImageId { get; set; }
    [Id(3)]
    public bool Success { get; set; }
    [Id(4)]
    public string? Error { get; set; }
}

[GenerateSerializer]
public class HostEnvironmentEvent
{
    [Id(0)]
    public required string HostId { get; set; }
    [Id(1)]
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
}

[GenerateSerializer]
public class RunnerDiscoveryEvent
{
    [Id(0)]
    public required string HostId { get; set; }
    [Id(1)]
    public List<DiscoveredRunnerInfo> Runners { get; set; } = [];
}

[GenerateSerializer]
public class DiscoveredRunnerInfo
{
    [Id(0)]
    public string InstanceId { get; set; } = "";
    [Id(1)]
    public required string RunnerName { get; set; }
    [Id(2)]
    public string ContainerId { get; set; } = "";
    [Id(3)]
    public string? VmName { get; set; }
    [Id(4)]
    public int? ProcessId { get; set; }
    [Id(5)]
    public ExecutionBackend Backend { get; set; }
    [Id(6)]
    public bool IsRunning { get; set; }
    [Id(7)]
    public string Status { get; set; } = "";
    [Id(8)]
    public string? InstanceDir { get; set; }
}

/// <summary>
/// Full reconciliation report sent by agent during heartbeat.
/// Contains all RunnerRunner-managed resources on the host.
/// </summary>
[GenerateSerializer]
public class ReconciliationReport
{
    [Id(0)]
    public required string HostId { get; set; }
    [Id(1)]
    public List<DiscoveredRunnerInfo> Runners { get; set; } = [];
}

/// <summary>
/// Server tells agent to clean up an orphaned resource
/// (exists on host but has no matching DB record).
/// </summary>
[GenerateSerializer]
public class CleanupOrphanCommand
{
    [Id(0)]
    public ExecutionBackend Backend { get; set; }
    [Id(1)]
    public string? ContainerId { get; set; }
    [Id(2)]
    public string? VmName { get; set; }
    [Id(3)]
    public int? ProcessId { get; set; }
    [Id(4)]
    public string? InstanceDir { get; set; }
}

[GenerateSerializer]
public class GetHostLogsCommand
{
    [Id(0)]
    public int TailLines { get; set; } = 100;
}

[GenerateSerializer]
public class HostLogsEvent
{
    [Id(0)]
    public required string HostId { get; set; }
    [Id(1)]
    public string Logs { get; set; } = "";
}

[GenerateSerializer]
public class GetRunnerLogsCommand
{
    [Id(0)]
    public required string InstanceHandle { get; set; }
    [Id(1)]
    public int TailLines { get; set; } = 100;
}

[GenerateSerializer]
public class RunnerLogsEvent
{
    [Id(0)]
    public required string HostId { get; set; }
    [Id(1)]
    public required string InstanceHandle { get; set; }
    [Id(2)]
    public string Logs { get; set; } = "";
}

[GenerateSerializer]
public class HostWorkerUpdateCommand
{
    [Id(0)]
    public required string TargetVersion { get; set; }
    [Id(1)]
    public required string AssetName { get; set; }
    [Id(2)]
    public required string AssetUrl { get; set; }
    [Id(3)]
    public required string Sha256 { get; set; }
    [Id(4)]
    public bool Force { get; set; }
    [Id(5)]
    public string? ContainerImage { get; set; }
    [Id(6)]
    public string? TargetCommitSha { get; set; }
}

[GenerateSerializer]
public class HostWorkerUpdateStatusEvent
{
    [Id(0)]
    public required string HostId { get; set; }
    [Id(1)]
    public required string TargetVersion { get; set; }
    [Id(2)]
    public string? CurrentVersion { get; set; }
    [Id(3)]
    public required string Stage { get; set; }
    [Id(4)]
    public string Message { get; set; } = "";
    [Id(5)]
    public bool IsComplete { get; set; }
    [Id(6)]
    public bool Success { get; set; }
    [Id(7)]
    public string? Error { get; set; }
    [Id(8)]
    public string? CurrentCommitSha { get; set; }
    [Id(9)]
    public string? TargetCommitSha { get; set; }
}
