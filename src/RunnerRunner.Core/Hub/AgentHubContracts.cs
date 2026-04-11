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
}

// --- Commands (Server → Agent) ---

public class DeployRunnerCommand
{
    public string InstanceId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public required string RunnerName { get; set; }
    public ExecutionBackend Backend { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public string? RunnerAgentVersion { get; set; }
    public DockerImageConfig? DockerConfig { get; set; }
    public TartImageConfig? TartConfig { get; set; }
    public List<string> Labels { get; set; } = [];
    public string RunnerGroup { get; set; } = "Default";
    public bool Ephemeral { get; set; }
    public string? RegistrationToken { get; set; }
    public string? RunnerUrl { get; set; }
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
