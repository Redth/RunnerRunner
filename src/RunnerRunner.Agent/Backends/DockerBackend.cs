using Docker.DotNet;
using Docker.DotNet.Models;
using System.Formats.Tar;
using System.Runtime.InteropServices;
using System.Text;
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
            AuthConfig? authConfig = null;
            if (!string.IsNullOrEmpty(request.RegistryUsername))
            {
                authConfig = new AuthConfig
                {
                    Username = request.RegistryUsername,
                    Password = request.RegistryPassword ?? ""
                };
                _logger.LogInformation("Using registry credentials (user: {User}) for image pull", request.RegistryUsername);
            }
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = repository, Tag = config.Tag },
                authConfig, new Progress<JSONMessage>(m => _logger.LogDebug("Pull: {Status}", m.Status)), ct);
        }

        // Build environment variables
        var envVars = request.EnvironmentVariables
            .Select(kvp => $"{kvp.Key}={kvp.Value}")
            .ToList();

        // Add RunnerRunner identity env vars
        envVars.Add($"RR_INSTANCE_ID={request.InstanceId}");
        envVars.Add($"RR_RUNNER_NAME={request.RunnerName}");

        // GitHub Actions runner refuses to run as root without this
        envVars.Add("RUNNER_ALLOW_RUNASROOT=1");

        // Pass JIT config for dynamic provisioning
        if (!string.IsNullOrEmpty(request.JitConfig))
        {
            envVars.Add($"RR_JIT_CONFIG={request.JitConfig}");
            envVars.Add($"RR_PROVISIONING_MODE={request.ProvisioningMode}");
            if (!string.IsNullOrEmpty(request.RunnerAgentVersion))
                envVars.Add($"RR_RUNNER_AGENT_VERSION={request.RunnerAgentVersion}");
            envVars.Add($"RR_RUNNER_PROVIDER={request.Provider}");
        }

        // Install the job-started banner hook if requested. We copy the
        // script directly into the container after it's created (via tar
        // archive extract) instead of bind-mounting from the host — that
        // way it works whether the agent is running on the Docker daemon's
        // host or inside another container talking to a remote daemon.
        string? hookScriptBody = null;
        string? hookContainerDir = null;
        string? hookScriptFileName = null;
        string? hookContainerScriptPath = null;
        bool hookIsWindows = false;
        if (Services.JobHookScriptBuilder.IsHookRequested(request.EnvironmentVariables))
        {
            // Peek at the image to decide bash-vs-powershell; fall back to
            // Linux (bash) if the inspect fails or there's no image yet.
            try
            {
                var imageInspect = await _client.Images.InspectImageAsync(imageName, ct);
                hookIsWindows = IsWindowsContainerImage(imageInspect.Os);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unable to inspect image {Image} for hook platform; defaulting to bash", imageName);
            }

            if (hookIsWindows)
            {
                hookScriptBody = Services.JobHookScriptBuilder.BuildPowerShellScript();
                hookScriptFileName = Services.JobHookScriptBuilder.PowerShellFileName;
                hookContainerDir = "runnerrunner";
                hookContainerScriptPath = "C:\\runnerrunner\\" + hookScriptFileName;
            }
            else
            {
                hookScriptBody = Services.JobHookScriptBuilder.BuildBashScript();
                hookScriptFileName = Services.JobHookScriptBuilder.BashFileName;
                hookContainerDir = "runnerrunner";
                hookContainerScriptPath = "/runnerrunner/" + hookScriptFileName;
            }

            envVars.Add($"{Services.JobHookScriptBuilder.HookEnvVarName}={hookContainerScriptPath}");
            _logger.LogDebug("Will install job-started hook inside container at {Path}", hookContainerScriptPath);
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
                Binds = null,
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

        // Copy the job-started hook script into the container filesystem.
        // This avoids any dependency on host-side paths being visible to dockerd.
        if (hookScriptBody is not null && hookContainerDir is not null && hookScriptFileName is not null)
        {
            try
            {
                using var tarStream = BuildHookTarArchive(hookContainerDir, hookScriptFileName, hookScriptBody, hookIsWindows);
                var targetPath = hookIsWindows ? "C:\\" : "/";
                await _client.Containers.ExtractArchiveToContainerAsync(
                    createResponse.ID,
                    new ContainerPathStatParameters { Path = targetPath, AllowOverwriteDirWithFile = false },
                    tarStream,
                    ct);
                _logger.LogDebug("Copied job-started hook into container {ContainerId}: {Path}",
                    createResponse.ID[..12], hookContainerScriptPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to install job-started hook into container {ContainerId}; job banner will be missing but runner will continue",
                    createResponse.ID[..12]);
            }
        }

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
    /// <summary>
    /// Builds an in-memory tar archive containing a single executable script
    /// at <c>{dirName}/{fileName}</c>, for use with Docker's
    /// <c>ExtractArchiveToContainerAsync</c>. Docker accepts a POSIX tar
    /// stream for both Linux and Windows container copy operations.
    /// </summary>
    internal static Stream BuildHookTarArchive(string dirName, string fileName, string scriptBody, bool isWindows)
    {
        var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, TarEntryFormat.Ustar, leaveOpen: true))
        {
            // Directory entry. Docker tolerates either / or \ but POSIX tar wants /.
            var dirEntry = new UstarTarEntry(TarEntryType.Directory, dirName + "/")
            {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                     | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                     | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
            };
            writer.WriteEntry(dirEntry);

            var scriptBytes = Encoding.UTF8.GetBytes(scriptBody);
            var fileEntry = new UstarTarEntry(TarEntryType.RegularFile, dirName + "/" + fileName)
            {
                // Executable on Linux; Windows ignores mode but it's harmless.
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                     | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                     | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
                DataStream = new MemoryStream(scriptBytes),
            };
            writer.WriteEntry(fileEntry);
            _ = isWindows; // reserved for future per-platform tweaks
        }
        ms.Position = 0;
        return ms;
    }

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

            // Auto-download runner agent if not found in image
            "if [ -z \"$runner_cmd\" ] && [ -n \"${RR_JIT_CONFIG:-}\" ]; then " +
            "  rr_version=\"${RR_RUNNER_AGENT_VERSION:-}\"; " +
            "  rr_provider=\"${RR_RUNNER_PROVIDER:-GitHubActions}\"; " +
            "  rr_arch=$(uname -m); " +
            "  case \"$rr_arch\" in aarch64|arm64) rr_arch=arm64;; x86_64|amd64) rr_arch=x64;; esac; " +
            "  rr_uid=$(id -u 2>/dev/null || echo 0); " +
            "  if [ \"$rr_uid\" = \"0\" ]; then rr_install_dir=/actions-runner; rr_sudo=''; " +
            "  else rr_install_dir=\"${HOME:-/tmp}/actions-runner\"; " +
            "    if command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null; then rr_sudo='sudo -n'; else rr_sudo=''; fi; " +
            "  fi; " +

            // Install runner dependencies (libicu for .NET globalization, plus other common needs).
            // Only attempt when we have root or passwordless sudo; otherwise assume the image has them.
            "  if [ \"$rr_uid\" = \"0\" ] || [ -n \"$rr_sudo\" ]; then " +
            "    echo '[RunnerRunner] Installing runner dependencies...'; " +
            "    if command -v apt-get >/dev/null 2>&1; then " +
            "      $rr_sudo apt-get update -qq && $rr_sudo apt-get install -y -qq libicu-dev libssl-dev git jq >/dev/null 2>&1 || true; " +
            "    elif command -v apk >/dev/null 2>&1; then " +
            "      $rr_sudo apk add --no-cache icu-libs openssl git jq >/dev/null 2>&1 || true; " +
            "    elif command -v dnf >/dev/null 2>&1; then " +
            "      $rr_sudo dnf install -y libicu openssl git jq >/dev/null 2>&1 || true; " +
            "    elif command -v yum >/dev/null 2>&1; then " +
            "      $rr_sudo yum install -y libicu openssl git jq >/dev/null 2>&1 || true; " +
            "    fi; " +
            "  else " +
            "    echo '[RunnerRunner] Non-root user; skipping dependency install (expecting image to provide libicu/openssl/git/jq).'; " +
            "  fi; " +

            // Resolve latest version from GitHub API if not specified
            "  if [ -z \"$rr_version\" ]; then " +
            "    echo '[RunnerRunner] No runner found in image; resolving latest runner version...'; " +
            "    if [ \"$rr_provider\" = 'GitHubActions' ] || [ \"$rr_provider\" = '0' ]; then " +
            "      rr_version=$(curl -sL https://api.github.com/repos/actions/runner/releases/latest | " +
            "        grep -o '\"tag_name\":\\s*\"v[^\"]*\"' | head -1 | sed 's/.*\"v\\([^\"]*\\)\".*/\\1/' || true); " +
            "    elif [ \"$rr_provider\" = 'GiteaActions' ] || [ \"$rr_provider\" = '2' ]; then " +
            "      rr_version=$(curl -sL https://gitea.com/api/v1/repos/gitea/act_runner/releases/latest | " +
            "        grep -o '\"tag_name\":\\s*\"v[^\"]*\"' | head -1 | sed 's/.*\"v\\([^\"]*\\)\".*/\\1/' || true); " +
            "    fi; " +
            "  fi; " +

            // Download and extract
            "  if [ -n \"$rr_version\" ]; then " +
            "    echo \"[RunnerRunner] Downloading runner agent v${rr_version} (${rr_arch})...\"; " +
            "    mkdir -p \"$rr_install_dir\"; " +
            "    if [ \"$rr_provider\" = 'GitHubActions' ] || [ \"$rr_provider\" = '0' ]; then " +
            "      rr_url=\"https://github.com/actions/runner/releases/download/v${rr_version}/actions-runner-linux-${rr_arch}-${rr_version}.tar.gz\"; " +
            "      curl -sL \"$rr_url\" | tar xz -C \"$rr_install_dir\"; " +
            "    elif [ \"$rr_provider\" = 'GiteaActions' ] || [ \"$rr_provider\" = '2' ]; then " +
            "      rr_url=\"https://gitea.com/gitea/act_runner/releases/download/v${rr_version}/act_runner-${rr_version}-linux-${rr_arch}\"; " +
            "      curl -sL -o \"$rr_install_dir/act_runner\" \"$rr_url\" && chmod +x \"$rr_install_dir/act_runner\"; " +
            "    elif [ \"$rr_provider\" = 'AzureDevOps' ] || [ \"$rr_provider\" = '1' ]; then " +
            "      rr_url=\"https://vstsagentpackage.azureedge.net/agent/${rr_version}/vsts-agent-linux-${rr_arch}-${rr_version}.tar.gz\"; " +
            "      curl -sL \"$rr_url\" | tar xz -C \"$rr_install_dir\"; " +
            "    fi; " +
            "    echo '[RunnerRunner] Runner agent installed to '\"$rr_install_dir\"; " +

            // Re-search for the runner after install
            "    for candidate in \"$rr_install_dir/run.sh\" \"$rr_install_dir/act_runner\"; do " +
            "      if [ -x \"$candidate\" ]; then runner_cmd=\"$candidate\"; break; fi; " +
            "    done; " +
            "  else " +
            "    echo '[RunnerRunner] ERROR: Could not resolve runner agent version for auto-install'; " +
            "  fi; " +
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
            "echo '[RunnerRunner] ERROR: No GitHub JIT runner script was found and auto-install failed'; " +
            "echo '[RunnerRunner] Ensure the image has a runner agent or that RR_RUNNER_AGENT_VERSION is set'; " +
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
