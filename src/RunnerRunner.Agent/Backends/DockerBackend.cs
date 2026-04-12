using Docker.DotNet;
using Docker.DotNet.Models;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Backends;

/// <summary>
/// Execution backend that runs runner instances as Docker containers.
/// Used primarily for Linux hosts where container overhead is minimal.
/// </summary>
public class DockerBackend : IRunnerBackend
{
    private readonly ILogger<DockerBackend> _logger;
    private readonly DockerClient _client;

    public ExecutionBackend BackendType => ExecutionBackend.Docker;
    public DockerClient GetClient() => _client;

    public DockerBackend(ILogger<DockerBackend> logger)
    {
        _logger = logger;
        _client = new DockerClientConfiguration().CreateClient();
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            await _client.System.PingAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<RunnerInstanceInfo> StartRunnerAsync(RunnerStartRequest request, CancellationToken ct = default)
    {
        var config = request.DockerConfig
            ?? throw new InvalidOperationException("DockerConfig is required for Docker backend");

        var imageName = $"{config.RegistryUrl}/{config.ImageName}:{config.Tag}";

        // Pull image if needed
        if (config.PullPolicy == PullPolicy.Always ||
            (config.PullPolicy == PullPolicy.IfNotPresent && !await ImageExistsAsync(imageName, ct)))
        {
            _logger.LogInformation("Pulling image {Image}", imageName);
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = $"{config.RegistryUrl}/{config.ImageName}", Tag = config.Tag },
                null, new Progress<JSONMessage>(m => _logger.LogDebug("Pull: {Status}", m.Status)), ct);
        }

        // Build environment variables
        var envVars = request.EnvironmentVariables
            .Select(kvp => $"{kvp.Key}={kvp.Value}")
            .ToList();

        // Add RunnerRunner identity env vars
        envVars.Add($"RR_INSTANCE_ID={request.InstanceId}");
        envVars.Add($"RR_RUNNER_NAME={request.RunnerName}");

        // Pass JIT config for dynamic provisioning
        if (!string.IsNullOrEmpty(request.JitConfig))
        {
            envVars.Add($"RR_JIT_CONFIG={request.JitConfig}");
            envVars.Add($"RR_PROVISIONING_MODE={request.ProvisioningMode}");
        }

        // Create and start container with RunnerRunner labels for tracking
        // JIT config is passed as RR_JIT_CONFIG env var — the container image's
        // entrypoint is responsible for checking it and using --jitconfig if set.
        // We do NOT override the entrypoint to avoid breaking image setup logic.
        var createParams = new CreateContainerParameters
        {
            Image = imageName,
            Name = $"rr-{request.RunnerName}",
            Env = envVars,
            Labels = new Dictionary<string, string>
            {
                ["runnerrunner.managed"] = "true",
                ["runnerrunner.instance-id"] = request.InstanceId,
                ["runnerrunner.runner-name"] = request.RunnerName,
                ["runnerrunner.profile-id"] = request.InstanceId.Split('-').FirstOrDefault() ?? ""
            },
            HostConfig = new HostConfig
            {
                AutoRemove = request.Ephemeral,
                RestartPolicy = request.Ephemeral
                    ? new RestartPolicy { Name = RestartPolicyKind.No }
                    : new RestartPolicy { Name = RestartPolicyKind.UnlessStopped }
            }
        };

        var createResponse = await _client.Containers.CreateContainerAsync(createParams, ct);

        await _client.Containers.StartContainerAsync(createResponse.ID, null, ct);

        _logger.LogInformation("Container {ContainerId} started for runner {RunnerName}",
            createResponse.ID[..12], request.RunnerName);

        return new RunnerInstanceInfo
        {
            InstanceHandle = createResponse.ID,
            RunnerName = request.RunnerName
        };
    }

    public async Task StopRunnerAsync(string instanceHandle, CancellationToken ct = default)
    {
        _logger.LogInformation("Stopping container {ContainerId}", instanceHandle[..12]);

        // Send SIGTERM first for graceful runner deregistration
        await _client.Containers.StopContainerAsync(instanceHandle,
            new ContainerStopParameters { WaitBeforeKillSeconds = 30 }, ct);

        // Remove the container
        try
        {
            await _client.Containers.RemoveContainerAsync(instanceHandle,
                new ContainerRemoveParameters { Force = true }, ct);
        }
        catch (DockerContainerNotFoundException)
        {
            // Already removed (auto-remove was on)
        }
    }

    public async Task<RunnerHealthStatus> GetHealthAsync(string instanceHandle, CancellationToken ct = default)
    {
        try
        {
            var inspect = await _client.Containers.InspectContainerAsync(instanceHandle, ct);
            return new RunnerHealthStatus
            {
                IsRunning = inspect.State.Running,
                Status = inspect.State.Status
            };
        }
        catch (DockerContainerNotFoundException)
        {
            return new RunnerHealthStatus { IsRunning = false, Status = "not_found" };
        }
    }

    /// <summary>
    /// Discovers all RunnerRunner-managed containers currently on this host.
    /// Used on agent startup to reconcile state with the server.
    /// </summary>
    public async Task<List<DiscoveredRunner>> DiscoverManagedContainersAsync(CancellationToken ct = default)
    {
        var result = new List<DiscoveredRunner>();
        try
        {
            var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool> { ["runnerrunner.managed=true"] = true }
                }
            }, ct);

            foreach (var container in containers)
            {
                container.Labels.TryGetValue("runnerrunner.instance-id", out var instanceId);
                container.Labels.TryGetValue("runnerrunner.runner-name", out var runnerName);

                result.Add(new DiscoveredRunner
                {
                    InstanceId = instanceId ?? "",
                    RunnerName = runnerName ?? container.Names.FirstOrDefault()?.TrimStart('/') ?? "",
                    ContainerId = container.ID,
                    IsRunning = container.State == "running",
                    Status = container.State
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover managed containers");
        }

        return result;
    }

    private async Task<bool> ImageExistsAsync(string imageName, CancellationToken ct)
    {
        try
        {
            await _client.Images.InspectImageAsync(imageName, ct);
            return true;
        }
        catch (DockerImageNotFoundException)
        {
            return false;
        }
    }
}
