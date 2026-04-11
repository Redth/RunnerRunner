using RunnerRunner.Core.Models;

namespace RunnerRunner.Core.Interfaces;

/// <summary>
/// Abstraction for execution backends that run runner instances on a host.
/// Implemented agent-side (Docker, Tart, Native, HyperV).
/// </summary>
public interface IRunnerBackend
{
    ExecutionBackend BackendType { get; }

    /// <summary>
    /// Check if this backend is available on the current host.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Start a new runner instance with the given configuration.
    /// </summary>
    Task<RunnerInstanceInfo> StartRunnerAsync(RunnerStartRequest request, CancellationToken ct = default);

    /// <summary>
    /// Stop a running runner instance gracefully.
    /// </summary>
    Task StopRunnerAsync(string instanceHandle, CancellationToken ct = default);

    /// <summary>
    /// Check health/status of a running instance.
    /// </summary>
    Task<RunnerHealthStatus> GetHealthAsync(string instanceHandle, CancellationToken ct = default);
}

public class RunnerStartRequest
{
    public string InstanceId { get; set; } = "";
    public required string RunnerName { get; set; }
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
}

public class RunnerInstanceInfo
{
    public required string InstanceHandle { get; set; } // container ID, VM name, or PID
    public required string RunnerName { get; set; }
}

public class RunnerHealthStatus
{
    public bool IsRunning { get; set; }
    public string? Status { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}
