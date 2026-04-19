using Docker.DotNet;
using Docker.DotNet.Models;
using System.Runtime.InteropServices;
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
    private readonly Uri _endpoint;
    private readonly bool _hasDockerHint;
    private readonly TimeSpan _availabilityCacheWindow = TimeSpan.FromSeconds(30);
    private DateTime _lastAvailabilityCheckUtc = DateTime.MinValue;
    private bool _cachedAvailability;

    public ExecutionBackend BackendType => ExecutionBackend.Docker;
    public DockerClient GetClient() => _client;

    public DockerBackend(ILogger<DockerBackend> logger)
    {
        _logger = logger;
        _hasDockerHint = HasDockerInstallHint();
        _endpoint = ResolveDockerEndpoint();
        _client = new DockerClientConfiguration(_endpoint).CreateClient();
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!_hasDockerHint)
            return false;

        if (DateTime.UtcNow - _lastAvailabilityCheckUtc < _availabilityCacheWindow)
            return _cachedAvailability;

        try
        {
            await _client.System.PingAsync(ct);
            _cachedAvailability = true;
        }
        catch (Exception ex)
        {
            _cachedAvailability = false;
            _logger.LogDebug("Docker backend unavailable at {Endpoint}: {Message}", _endpoint, ex.Message);
        }

        _lastAvailabilityCheckUtc = DateTime.UtcNow;
        return _cachedAvailability;
    }

    public async Task<RunnerInstanceInfo> StartRunnerAsync(RunnerStartRequest request, CancellationToken ct = default)
    {
        var config = request.DockerConfig
            ?? throw new InvalidOperationException("DockerConfig is required for Docker backend");

        var imageName = ImageReference.Build(config.RegistryUrl, config.ImageName, config.Tag);
        var repository = ImageReference.BuildRepository(config.RegistryUrl, config.ImageName);

        // Pull image if needed
        if (config.PullPolicy == PullPolicy.Always ||
            (config.PullPolicy == PullPolicy.IfNotPresent && !await ImageExistsAsync(imageName, ct)))
        {
            _logger.LogInformation("Pulling image {Image}", imageName);
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = repository, Tag = config.Tag },
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
                // Keep managed containers until RunnerRunner explicitly removes them so
                // reconciliation and logs can distinguish "exited" from "never existed".
                AutoRemove = false,
                RestartPolicy = request.Ephemeral
                    ? new RestartPolicy { Name = RestartPolicyKind.No }
                    : new RestartPolicy { Name = RestartPolicyKind.UnlessStopped }
            }
        };

        // JIT mode: inspect image for original entrypoint, then override with
        // a wrapper that does JIT setup and exec's the original entrypoint.
        if (!string.IsNullOrEmpty(request.JitConfig))
        {
            var imageInspect = await _client.Images.InspectImageAsync(imageName, ct);
            var originalEntrypoint = imageInspect.Config?.Entrypoint;
            var originalCmd = imageInspect.Config?.Cmd;
            var imageShell = imageInspect.Config?.Shell;
            var isWindowsContainer = IsWindowsContainerImage(imageInspect.Os);

            createParams.Entrypoint = BuildJitEntrypointOverride(
                isWindowsContainer,
                originalEntrypoint,
                originalCmd,
                imageShell,
                request.InitSteps);
            createParams.Cmd = new List<string>();

            _logger.LogInformation("JIT mode: overriding entrypoint for {Image} ({ContainerOs} container, {StepCount} init steps)",
                imageName, isWindowsContainer ? "Windows" : "Linux", request.InitSteps.Count);
        }

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
    /// Waits for a container to exit and returns the exit code.
    /// Uses Docker's WaitContainer API for immediate notification.
    /// </summary>
    public async Task<(long ExitCode, string? Error)> WaitForExitAsync(string containerId, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.Containers.WaitContainerAsync(containerId, ct);
            var error = response.Error?.Message;
            _logger.LogInformation("Container {ContainerId} exited with code {ExitCode}{Error}",
                containerId[..Math.Min(12, containerId.Length)],
                response.StatusCode,
                string.IsNullOrEmpty(error) ? "" : $": {error}");
            return (response.StatusCode, error);
        }
        catch (DockerContainerNotFoundException)
        {
            _logger.LogWarning("Container {ContainerId} not found while waiting for exit (already removed)",
                containerId[..Math.Min(12, containerId.Length)]);
            return (-1, "Container not found (already removed)");
        }
    }

    /// <summary>
    /// Discovers all RunnerRunner-managed containers currently on this host.
    /// Used on agent startup to reconcile state with the server.
    /// </summary>
    public async Task<List<DiscoveredRunner>> DiscoverManagedContainersAsync(CancellationToken ct = default)
    {
        var result = new List<DiscoveredRunner>();
        if (!await IsAvailableAsync(ct))
            return result;

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
                    Backend = ExecutionBackend.Docker,
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

    public async Task CleanupOrphanContainerAsync(string containerId, CancellationToken ct = default)
    {
        _logger.LogInformation("Cleaning up orphaned container {Id}", containerId[..Math.Min(12, containerId.Length)]);
        try { await _client.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct); } catch { }
        try { await _client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }, ct); } catch { }
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

    /// <summary>
    /// Detect the shell available in the image by inspecting its SHELL config,
    /// entrypoint, and cmd. Prefers /bin/bash if the image uses it, falls back to /bin/sh.
    /// </summary>
    internal static bool IsWindowsContainerImage(string? imageOs)
        => string.Equals(imageOs, "windows", StringComparison.OrdinalIgnoreCase);

    internal static List<string> BuildJitEntrypointOverride(
        bool isWindowsContainer,
        IList<string>? entrypoint,
        IList<string>? cmd,
        IList<string>? shell,
        IList<Core.Models.ResolvedInitStep>? initSteps = null)
        => isWindowsContainer
            ? BuildWindowsJitEntrypointOverride(entrypoint, cmd, initSteps)
            : BuildLinuxJitEntrypointOverride(entrypoint, cmd, shell, initSteps);

    private static List<string> BuildLinuxJitEntrypointOverride(
        IList<string>? entrypoint,
        IList<string>? cmd,
        IList<string>? shell,
        IList<Core.Models.ResolvedInitStep>? initSteps)
    {
        var shellPath = DetectShell(entrypoint, cmd, shell);
        var execParts = new List<string>();
        if (entrypoint?.Count > 0)
            execParts.AddRange(entrypoint);
        if (cmd?.Count > 0)
            execParts.AddRange(cmd);

        var pre = initSteps?.Where(s => s.Phase == Core.Models.InitStepPhase.PreRunner).ToList() ?? [];
        var post = initSteps?.Where(s => s.Phase == Core.Models.InitStepPhase.PostExit).ToList() ?? [];

        var preFragment = pre.Count > 0
            ? InitStepShellBuilder.BuildLinuxFragment(pre, "PreRunner")
            : "";
        var postFragment = post.Count > 0
            ? InitStepShellBuilder.BuildLinuxFragment(post, "PostExit")
            : "";

        var wrapperScript = new System.Text.StringBuilder();
        wrapperScript.Append("set -e; ");
        if (!string.IsNullOrEmpty(preFragment))
        {
            wrapperScript.Append("set +e; ");
            wrapperScript.Append(preFragment);
            wrapperScript.Append(" set -e; ");
        }

        wrapperScript.Append(
            "runner_cmd=''; " +
            "for candidate in /actions-runner/run.sh /runner/run.sh ./run.sh /home/*/actions-runner/run.sh /home/*/runner/run.sh; do " +
            "  if [ -x \"$candidate\" ]; then runner_cmd=\"$candidate\"; break; fi; " +
            "done; " +
            "if [ -z \"$runner_cmd\" ]; then " +
            "  runner_cmd=$(find /home /actions-runner /runner -maxdepth 4 -type f \\( -path '*/actions-runner/run.sh' -o -path '*/runner/run.sh' \\) 2>/dev/null | head -n 1 || true); " +
            "fi; " +
            "if [ -n \"$runner_cmd\" ] && [ -n \"${RR_JIT_CONFIG:-}\" ]; then " +
            "  echo \"[RunnerRunner] Starting GitHub runner via JIT config: $runner_cmd\"; cd \"$(dirname \"$runner_cmd\")\"; ");

        if (string.IsNullOrEmpty(postFragment))
        {
            wrapperScript.Append("exec \"$runner_cmd\" --jitconfig \"$RR_JIT_CONFIG\"; ");
        }
        else
        {
            wrapperScript.Append("\"$runner_cmd\" --jitconfig \"$RR_JIT_CONFIG\"; rr_rc=$?; ");
            wrapperScript.Append("set +e; ");
            wrapperScript.Append(postFragment);
            wrapperScript.Append(" exit $rr_rc; ");
        }

        wrapperScript.Append(
            "fi; " +
            "echo '[RunnerRunner] ERROR: No GitHub JIT runner script was found in the container image'; " +
            "echo '[RunnerRunner] Refusing to idle on the image entrypoint because this would consume capacity without registering a runner'; " +
            "exit 91");

        _ = execParts; // (not used in JIT path — runner_cmd is auto-discovered)
        return [shellPath, "-lc", wrapperScript.ToString()];
    }

    private static List<string> BuildWindowsJitEntrypointOverride(
        IList<string>? entrypoint,
        IList<string>? cmd,
        IList<Core.Models.ResolvedInitStep>? initSteps)
    {
        var pre = initSteps?.Where(s => s.Phase == Core.Models.InitStepPhase.PreRunner).ToList() ?? [];
        var post = initSteps?.Where(s => s.Phase == Core.Models.InitStepPhase.PostExit).ToList() ?? [];
        var preFragment = pre.Count > 0 ? InitStepShellBuilder.BuildWindowsFragment(pre, "PreRunner") : "";
        var postFragment = post.Count > 0 ? InitStepShellBuilder.BuildWindowsFragment(post, "PostExit") : "";

        var fallbackInvocation = BuildPowerShellFallbackInvocation(entrypoint, cmd);
        _ = fallbackInvocation;

        var scriptLines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
        };
        if (!string.IsNullOrEmpty(preFragment))
        {
            scriptLines.Add(preFragment);
        }
        scriptLines.AddRange(new[]
        {
            "$candidates = @('C:\\actions-runner\\run.cmd', 'C:\\runner\\run.cmd', '.\\run.cmd')",
            "$runCmd = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1",
            "if (-not $runCmd) { $runCmd = Get-ChildItem -Path C:\\ -Filter 'run.cmd' -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match 'actions-runner|runner' } | Select-Object -First 1 -ExpandProperty FullName }",
            "if ($runCmd -and $env:RR_JIT_CONFIG) { Write-Host '[RunnerRunner] Starting Windows GitHub runner via JIT config'; Set-Location (Split-Path -Parent $runCmd); & $runCmd --jitconfig $env:RR_JIT_CONFIG; $rrRc = $LASTEXITCODE } else { Write-Host '[RunnerRunner] ERROR: No Windows GitHub JIT runner script was found in the container image'; Write-Host '[RunnerRunner] Refusing to idle on the image entrypoint because this would consume capacity without registering a runner'; exit 91 }",
        });
        if (!string.IsNullOrEmpty(postFragment))
        {
            scriptLines.Add(postFragment);
        }
        scriptLines.Add("exit $rrRc");

        var wrapperScript = string.Join("; ", scriptLines);
        return ["powershell.exe", "-NoLogo", "-NoProfile", "-Command", wrapperScript];
    }

    private static string BuildPowerShellFallbackInvocation(IList<string>? entrypoint, IList<string>? cmd)
    {
        var execParts = new List<string>();
        if (entrypoint?.Count > 0)
            execParts.AddRange(entrypoint);
        if (cmd?.Count > 0)
            execParts.AddRange(cmd);

        if (execParts.Count == 0)
            return "Start-Sleep -Seconds 3600";

        return string.Join(" ", execParts.Select((part, index) =>
            index == 0 ? $"& {QuotePowerShellArgument(part)}" : QuotePowerShellArgument(part)));
    }

    private static string DetectShell(IList<string>? entrypoint, IList<string>? cmd, IList<string>? shell)
    {
        // 1. Check SHELL instruction from Dockerfile (most authoritative)
        if (shell?.Count > 0 && shell[0].Contains("sh"))
            return shell[0];

        // 2. Check if entrypoint/cmd reference bash
        var allParts = new List<string>();
        if (entrypoint != null) allParts.AddRange(entrypoint);
        if (cmd != null) allParts.AddRange(cmd);

        if (allParts.Any(p => p.Contains("/bash") || p == "bash"))
            return "/bin/bash";

        // 3. Default to /bin/sh (POSIX, most portable)
        return "/bin/sh";
    }

    private static bool HasDockerInstallHint()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
            return true;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return true;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return File.Exists("/usr/bin/docker")
            || File.Exists("/usr/local/bin/docker")
            || File.Exists("/opt/homebrew/bin/docker")
            || File.Exists("/var/run/docker.sock")
            || File.Exists(Path.Combine(home, ".docker", "run", "docker.sock"));
    }

    private static Uri ResolveDockerEndpoint()
    {
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (!string.IsNullOrWhiteSpace(dockerHost)
            && Uri.TryCreate(dockerHost, UriKind.Absolute, out var configuredEndpoint))
        {
            return configuredEndpoint;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new Uri("npipe://./pipe/docker_engine");

        var userSocket = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".docker",
            "run",
            "docker.sock");

        if (File.Exists(userSocket))
            return new Uri($"unix://{userSocket}");

        return new Uri("unix:///var/run/docker.sock");
    }

    private static string QuoteShellArgument(string value)
        => "'" + value.Replace("'", "'\"'\"'") + "'";

    private static string QuotePowerShellArgument(string value)
        => "'" + value.Replace("'", "''") + "'";
}
