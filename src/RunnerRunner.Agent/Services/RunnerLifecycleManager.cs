using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Hub;
using System.Collections.Concurrent;

namespace RunnerRunner.Agent.Services;

/// <summary>
/// Manages the lifecycle of runner instances on this host.
/// Tracks running instances and delegates to execution backends.
/// </summary>
public class RunnerLifecycleManager
{
    private readonly ILogger<RunnerLifecycleManager> _logger;
    private readonly ConcurrentDictionary<string, ManagedRunner> _runners = new();

    /// <summary>
    /// Fired when a runner exits unexpectedly (container/VM/process died).
    /// Parameters: (instanceId, exitCode, reason)
    /// </summary>
    public event Action<string, long, string>? OnRunnerExited;

    public RunnerLifecycleManager(ILogger<RunnerLifecycleManager> logger)
    {
        _logger = logger;
    }

    public IReadOnlyDictionary<string, ManagedRunner> RunningInstances => _runners;

    public async Task<RunnerInstanceInfo?> StartRunnerAsync(
        DeployRunnerCommand command,
        IRunnerBackend backend,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Starting runner {RunnerName} (instance {InstanceId})",
            command.RunnerName, command.InstanceId);

        var request = new RunnerStartRequest
        {
            InstanceId = command.InstanceId,
            RunnerName = command.RunnerName,
            Provider = command.Provider,
            EnvironmentVariables = command.EnvironmentVariables,
            RunnerAgentVersion = command.RunnerAgentVersion,
            DockerConfig = command.DockerConfig,
            TartConfig = command.TartConfig,
            Labels = command.Labels,
            RunnerGroup = command.RunnerGroup,
            Ephemeral = command.Ephemeral,
            RegistrationToken = command.RegistrationToken,
            RunnerUrl = command.RunnerUrl,
            RunnerBasePath = command.RunnerBasePath,
            WorkDirectory = command.WorkDirectory,
            JitConfig = command.JitConfig,
            ProvisioningMode = command.ProvisioningMode
        };

        var info = await backend.StartRunnerAsync(request, ct);

        _runners[command.InstanceId] = new ManagedRunner
        {
            InstanceId = command.InstanceId,
            ProfileId = command.ProfileId,
            RunnerName = info.RunnerName,
            InstanceHandle = info.InstanceHandle,
            Backend = backend,
            StartedAt = DateTime.UtcNow
        };

        _logger.LogInformation("Runner {RunnerName} started (handle: {Handle})",
            info.RunnerName, info.InstanceHandle);

        // Start background exit monitor for Docker containers
        if (backend is Backends.DockerBackend docker)
        {
            _ = MonitorContainerExitAsync(docker, command.InstanceId, info.InstanceHandle, info.RunnerName);
        }

        return info;
    }

    public async Task StopRunnerAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_runners.TryRemove(instanceId, out var runner))
        {
            _logger.LogWarning("Runner instance {InstanceId} not found", instanceId);
            return;
        }

        _logger.LogInformation("Stopping runner {RunnerName}", runner.RunnerName);
        await runner.Backend.StopRunnerAsync(runner.InstanceHandle, ct);
        _logger.LogInformation("Runner {RunnerName} stopped", runner.RunnerName);
    }

    public async Task<RunnerHealthStatus?> CheckHealthAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_runners.TryGetValue(instanceId, out var runner))
            return null;

        return await runner.Backend.GetHealthAsync(runner.InstanceHandle, ct);
    }

    private async Task MonitorContainerExitAsync(
        Backends.DockerBackend docker, string instanceId, string containerId, string runnerName)
    {
        try
        {
            var (exitCode, error) = await docker.WaitForExitAsync(containerId);

            // Only fire if we still consider this runner active
            if (_runners.TryRemove(instanceId, out _))
            {
                var reason = exitCode switch
                {
                    0 => "Runner exited cleanly (job completed or ephemeral exit)",
                    137 => "Container killed by OOM or SIGKILL (exit code 137)",
                    143 => "Container received SIGTERM (exit code 143)",
                    _ => $"Container exited with code {exitCode}"
                };

                if (!string.IsNullOrEmpty(error))
                    reason += $": {error}";

                _logger.LogWarning(
                    "Runner {RunnerName} ({InstanceId}) container exited: code={ExitCode}, reason={Reason}",
                    runnerName, instanceId, exitCode, reason);

                OnRunnerExited?.Invoke(instanceId, exitCode, reason);
            }
        }
        catch (OperationCanceledException)
        {
            // Agent shutting down, ignore
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error monitoring container exit for runner {RunnerName}", runnerName);
        }

    }
    public async Task<List<ManagedRunnerHealth>> CollectRunnerHealthAsync(CancellationToken ct = default)
    {
        var snapshots = new List<ManagedRunnerHealth>();

        foreach (var (instanceId, runner) in _runners.ToArray())
        {
            RunnerHealthStatus health;
            try
            {
                health = await runner.Backend.GetHealthAsync(runner.InstanceHandle, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health check failed for runner {RunnerName} ({InstanceId})", runner.RunnerName, instanceId);
                health = new RunnerHealthStatus { IsRunning = false, Status = "health_check_failed" };
            }

            if (!health.IsRunning)
                _runners.TryRemove(instanceId, out _);

            snapshots.Add(new ManagedRunnerHealth
            {
                Runner = runner,
                Health = health
            });
        }

        return snapshots;
    }
}

public class ManagedRunner
{
    public required string InstanceId { get; set; }
    public required string ProfileId { get; set; }
    public required string RunnerName { get; set; }
    public required string InstanceHandle { get; set; }
    public required IRunnerBackend Backend { get; set; }
    public DateTime StartedAt { get; set; }
}

public class ManagedRunnerHealth
{
    public required ManagedRunner Runner { get; set; }
    public required RunnerHealthStatus Health { get; set; }
}
