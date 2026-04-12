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
    Task ImagePullProgress(ImagePullProgressEvent evt);
    Task ImagePullComplete(ImagePullCompleteEvent evt);
    Task ImageDeleted(ImageDeletedEvent evt);
    Task HostEnvironmentResponse(HostEnvironmentEvent evt);
    Task RunnerDiscovery(RunnerDiscoveryEvent evt);
    Task Reconciliation(ReconciliationReport report);
    Task RunnerLogsResponse(RunnerLogsEvent evt);
}

// --- Commands (Server → Agent) ---

public class DeployRunnerCommand
{
    public string InstanceId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public required string RunnerName { get; set; }
    public ExecutionBackend Backend { get; set; }
    public RunnerProvider Provider { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public string? RunnerAgentVersion { get; set; }
    public DockerImageConfig? DockerConfig { get; set; }
    public TartImageConfig? TartConfig { get; set; }
    public List<string> Labels { get; set; } = [];
    public string RunnerGroup { get; set; } = "Default";
    public bool Ephemeral { get; set; }
    public string? RegistrationToken { get; set; }
    public string? RunnerUrl { get; set; }
    public string? RunnerBasePath { get; set; }
    public string? WorkDirectory { get; set; }

    /// <summary>Base64-encoded JIT runner config (GitHub). When set, skip config.sh and use --jitconfig.</summary>
    public string? JitConfig { get; set; }

    /// <summary>"static" or "dynamic" — determines lifecycle behavior.</summary>
    public string ProvisioningMode { get; set; } = "static";
}

public class StopRunnerCommand
{
    public string InstanceId { get; set; } = "";
    public string? InstanceHandle { get; set; }
}

public class SyncDesiredStateCommand
{
    public List<DesiredRunnerAssignment> Assignments { get; set; } = [];
}

public class DesiredRunnerAssignment
{
    public string ProfileId { get; set; } = "";
    public int DesiredCount { get; set; }
}

public class PullImageCommand
{
    public ImageType ImageType { get; set; }
    public required string ImageName { get; set; }
    public string Tag { get; set; } = "latest";
    public string? RegistryUrl { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public DockerImageConfig? DockerConfig { get; set; }
    public TartImageConfig? TartConfig { get; set; }
}

// --- Events (Agent → Server) ---

public class AgentInfo
{
    public required string AgentId { get; set; }
    public required string Name { get; set; }
    public HostPlatform Platform { get; set; }
    public string? OsVersion { get; set; }
    public string? Architecture { get; set; }
    public string? AgentVersion { get; set; }
    public List<string> Capabilities { get; set; } = [];
    public List<RunningRunnerInfo> CurrentRunners { get; set; } = [];
}

public class RunningRunnerInfo
{
    public string InstanceId { get; set; } = "";
    public required string RunnerName { get; set; }
    public required string InstanceHandle { get; set; }
    public RunnerInstanceStatus Status { get; set; }
}

public class RunnerStartedEvent
{
    public string InstanceId { get; set; } = "";
    public required string RunnerName { get; set; }
    public required string InstanceHandle { get; set; }
}

public class RunnerStoppedEvent
{
    public string InstanceId { get; set; } = "";
    public required string Reason { get; set; }
    public string? ErrorMessage { get; set; }
}

public class RunnerHealthUpdateEvent
{
    public string InstanceId { get; set; } = "";
    public RunnerInstanceStatus Status { get; set; }
    public DateTime CheckedAt { get; set; }
}

public class HeartbeatEvent
{
    public required string AgentId { get; set; }
    public double CpuUsagePercent { get; set; }
    public double MemoryUsagePercent { get; set; }
    public double DiskUsagePercent { get; set; }
    public int RunningInstanceCount { get; set; }
}

// --- Image Management Commands (Server → Agent) ---

public class ListImagesCommand
{
    public ImageType? FilterType { get; set; }
}

public class DeleteImageCommand
{
    public ImageType ImageType { get; set; }
    public required string ImageId { get; set; }
    public required string ImageName { get; set; }
}

public class LoginRegistryCommand
{
    public required string RegistryUrl { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}

// --- Image Management Events (Agent → Server) ---

public class ImageListEvent
{
    public required string HostId { get; set; }
    public List<AgentImageInfo> Images { get; set; } = [];
}

public class AgentImageInfo
{
    public ImageType ImageType { get; set; }
    public required string Repository { get; set; }
    public string Tag { get; set; } = "latest";
    public string? ImageId { get; set; }
    public long SizeBytes { get; set; }
    public DateTime? CreatedAt { get; set; }
}

public class ImagePullProgressEvent
{
    public required string HostId { get; set; }
    public ImageType ImageType { get; set; }
    public required string ImageName { get; set; }
    public double ProgressPercent { get; set; }
    public long BytesDownloaded { get; set; }
    public long BytesTotal { get; set; }
    public string? Status { get; set; }
}

public class ImagePullCompleteEvent
{
    public required string HostId { get; set; }
    public ImageType ImageType { get; set; }
    public required string ImageName { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class ImageDeletedEvent
{
    public required string HostId { get; set; }
    public ImageType ImageType { get; set; }
    public required string ImageId { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class HostEnvironmentEvent
{
    public required string HostId { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
}

public class RunnerDiscoveryEvent
{
    public required string HostId { get; set; }
    public List<DiscoveredRunnerInfo> Runners { get; set; } = [];
}

public class DiscoveredRunnerInfo
{
    public string InstanceId { get; set; } = "";
    public required string RunnerName { get; set; }
    public string ContainerId { get; set; } = "";
    public string? VmName { get; set; }
    public int? ProcessId { get; set; }
    public ExecutionBackend Backend { get; set; }
    public bool IsRunning { get; set; }
    public string Status { get; set; } = "";
}

/// <summary>
/// Full reconciliation report sent by agent during heartbeat.
/// Contains all RunnerRunner-managed resources on the host.
/// </summary>
public class ReconciliationReport
{
    public required string HostId { get; set; }
    public List<DiscoveredRunnerInfo> Runners { get; set; } = [];
}

/// <summary>
/// Server tells agent to clean up an orphaned resource
/// (exists on host but has no matching DB record).
/// </summary>
public class CleanupOrphanCommand
{
    public ExecutionBackend Backend { get; set; }
    public string? ContainerId { get; set; }
    public string? VmName { get; set; }
    public int? ProcessId { get; set; }
    public string? InstanceDir { get; set; }
}

public class GetRunnerLogsCommand
{
    public required string InstanceHandle { get; set; }
    public int TailLines { get; set; } = 100;
}

public class RunnerLogsEvent
{
    public required string HostId { get; set; }
    public required string InstanceHandle { get; set; }
    public string Logs { get; set; } = "";
}
