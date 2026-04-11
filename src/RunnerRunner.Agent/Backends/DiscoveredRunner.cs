namespace RunnerRunner.Agent.Backends;

/// <summary>
/// Represents a running container/process discovered on the host
/// that was previously created by RunnerRunner.
/// </summary>
public class DiscoveredRunner
{
    public string InstanceId { get; set; } = "";
    public string RunnerName { get; set; } = "";
    public string ContainerId { get; set; } = "";
    public bool IsRunning { get; set; }
    public string Status { get; set; } = "";
}
