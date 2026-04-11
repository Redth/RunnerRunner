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
            EnvironmentVariables = command.EnvironmentVariables,
            RunnerAgentVersion = command.RunnerAgentVersion,
            DockerConfig = command.DockerConfig,
            TartConfig = command.TartConfig,
            Labels = command.Labels,
            RunnerGroup = command.RunnerGroup,
            Ephemeral = command.Ephemeral,
            RegistrationToken = command.RegistrationToken,
            RunnerUrl = command.RunnerUrl
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
