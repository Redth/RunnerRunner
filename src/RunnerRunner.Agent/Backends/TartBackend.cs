using System.Diagnostics;
using System.Text.Json;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Backends;

/// <summary>
/// Execution backend that runs runner instances in Tart macOS VMs.
/// Uses the tart CLI for VM lifecycle management and SSH for runner setup.
/// </summary>
public class TartBackend : IRunnerBackend
{
    private readonly ILogger<TartBackend> _logger;

    public ExecutionBackend BackendType => ExecutionBackend.Tart;

    public TartBackend(ILogger<TartBackend> logger)
    {
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await RunCommandAsync("tart", "--version", ct);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<RunnerInstanceInfo> StartRunnerAsync(RunnerStartRequest request, CancellationToken ct = default)
    {
        var config = request.TartConfig
            ?? throw new InvalidOperationException("TartConfig is required for Tart backend");

        var sourceImage = $"{config.RegistryUrl}/{config.ImageName}:{config.Tag}";
        var vmName = $"rr-{request.RunnerName}";

        // Clone the base image for this runner instance
        _logger.LogInformation("Cloning tart image {Source} → {VM}", sourceImage, vmName);
        await RunCommandAsync("tart", $"clone {sourceImage} {vmName}", ct);

        // Configure VM resources if specified
        if (config.CpuCount.HasValue)
            await RunCommandAsync("tart", $"set {vmName} --cpu {config.CpuCount.Value}", ct);
        if (config.MemorySizeGb.HasValue)
            await RunCommandAsync("tart", $"set {vmName} --memory {config.MemorySizeGb.Value * 1024}", ct);
        if (config.DiskSizeGb.HasValue)
            await RunCommandAsync("tart", $"set {vmName} --disk-size {config.DiskSizeGb.Value}", ct);
        if (!string.IsNullOrEmpty(config.Display))
            await RunCommandAsync("tart", $"set {vmName} --display {config.Display}", ct);

        // Build tart run arguments with shared directories
        var dirArgs = "";
        foreach (var dir in config.SharedDirs)
        {
            var roFlag = dir.ReadOnly ? ":ro" : "";
            dirArgs += $" --dir {dir.Name}:{dir.HostPath}{roFlag}";
        }

        // Start the VM in the background
        _logger.LogInformation("Starting tart VM {VM}", vmName);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "tart",
                Arguments = $"run {vmName} --no-graphics {dirArgs}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.Start();

        // Wait for VM to get an IP, then set up runner via SSH
        var vmIp = await WaitForVmIp(vmName, ct);
        if (vmIp != null)
        {
            var sshUser = config.SshUser;
            await SetupRunnerViaSsh(vmName, vmIp, sshUser, config.SshPassword, request, ct);
        }
        else
        {
            _logger.LogError("Tart VM {VM} did not get an IP address within 90 seconds", vmName);
        }

        return new RunnerInstanceInfo
        {
            InstanceHandle = vmName,
            RunnerName = request.RunnerName
        };
    }

    public async Task StopRunnerAsync(string instanceHandle, CancellationToken ct = default)
    {
        _logger.LogInformation("Stopping tart VM {VM}", instanceHandle);

        // Gracefully stop the VM
        var stopResult = await RunCommandAsync("tart", $"stop {instanceHandle}", ct);
        if (stopResult.ExitCode != 0)
            _logger.LogWarning("tart stop {VM} returned {Code}: {Output}", instanceHandle, stopResult.ExitCode, stopResult.Output);

        // Delete the cloned VM to free disk space
        var deleteResult = await RunCommandAsync("tart", $"delete {instanceHandle}", ct);
        if (deleteResult.ExitCode != 0)
            _logger.LogWarning("tart delete {VM} returned {Code}: {Output}", instanceHandle, deleteResult.ExitCode, deleteResult.Output);
        else
            _logger.LogInformation("Tart VM {VM} stopped and deleted", instanceHandle);
    }

    public async Task<RunnerHealthStatus> GetHealthAsync(string instanceHandle, CancellationToken ct = default)
    {
        var result = await RunCommandAsync("tart", $"ip {instanceHandle} --resolver=arp", ct);

        return new RunnerHealthStatus
        {
            IsRunning = result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output),
            Status = result.ExitCode == 0 ? "running" : "stopped"
        };
    }

    /// <summary>
    /// Discovers all RunnerRunner-managed Tart VMs currently on this host.
    /// Filters by VMs whose name starts with "rr-".
    /// </summary>
    public async Task<List<DiscoveredRunner>> DiscoverManagedVmsAsync(CancellationToken ct = default)
    {
        var result = new List<DiscoveredRunner>();
        try
        {
            var listResult = await RunCommandAsync("tart", "list --format json", ct);
            if (listResult.ExitCode != 0 || string.IsNullOrWhiteSpace(listResult.Output))
                return result;

            using var doc = JsonDocument.Parse(listResult.Output);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var name = element.GetProperty("Name").GetString() ?? "";
                if (!name.StartsWith("rr-"))
                    continue;

                var state = element.GetProperty("State").GetString() ?? "";

                result.Add(new DiscoveredRunner
                {
                    VmName = name,
                    RunnerName = name.StartsWith("rr-") ? name[3..] : name,
                    Backend = ExecutionBackend.Tart,
                    IsRunning = string.Equals(state, "running", StringComparison.OrdinalIgnoreCase),
                    Status = state
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover managed Tart VMs");
        }

        return result;
    }

    private async Task<string?> WaitForVmIp(string vmName, CancellationToken ct)
    {
        for (int i = 0; i < 45; i++)
        {
            await Task.Delay(2000, ct);
            var result = await RunCommandAsync("tart", $"ip {vmName} --resolver=arp", ct);
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
                return result.Output.Trim();
        }
        return null;
    }

    private async Task SetupRunnerViaSsh(
        string vmName, string vmIp, string sshUser, string? sshPassword,
        RunnerStartRequest request, CancellationToken ct)
    {
        _logger.LogInformation("Setting up runner in Tart VM {VM} ({IP}) via SSH", vmName, vmIp);

        // Export env vars into the SSH session
        var envExports = string.Join(" ", request.EnvironmentVariables
            .Select(kv => $"export {kv.Key}='{EscapeShell(kv.Value)}'"));

        // Determine runner version and download URL
        var version = request.RunnerAgentVersion ?? "2.321.0";
        var arch = "arm64"; // Tart VMs are always Apple Silicon
        var runnerTar = $"actions-runner-osx-{arch}-{version}.tar.gz";
        var downloadUrl = request.Provider switch
        {
            RunnerProvider.GitHubActions =>
                $"https://github.com/actions/runner/releases/download/v{version}/{runnerTar}",
            RunnerProvider.GiteaActions =>
                $"https://gitea.com/gitea/act_runner/releases/download/v{version}/act_runner-{version}-darwin-{arch}",
            _ => throw new NotSupportedException($"Provider {request.Provider} not supported for Tart")
        };

        if (request.Provider == RunnerProvider.GitHubActions)
        {
            await SetupGitHubRunnerViaSsh(sshUser, vmIp, sshPassword, envExports, downloadUrl, runnerTar, version, request, ct);
        }
        else if (request.Provider == RunnerProvider.GiteaActions)
        {
            await SetupGiteaRunnerViaSsh(sshUser, vmIp, sshPassword, envExports, downloadUrl, version, request, ct);
        }
    }

    private async Task SetupGitHubRunnerViaSsh(
        string sshUser, string vmIp, string? sshPassword, string envExports,
        string downloadUrl, string runnerTar, string version,
        RunnerStartRequest request, CancellationToken ct)
    {
        var runnerDir = $"/Users/{sshUser}/actions-runner";

        // Download and extract runner
        var setupScript =
            $"mkdir -p {runnerDir} && cd {runnerDir} && " +
            $"curl -sL -o {runnerTar} '{downloadUrl}' && " +
            $"tar xzf {runnerTar} && rm -f {runnerTar}";

        _logger.LogInformation("Downloading GitHub Actions runner v{Version} in VM", version);
        await SshExec(sshUser, vmIp, sshPassword, setupScript, ct);

        if (!string.IsNullOrEmpty(request.JitConfig))
        {
            // JIT mode: skip config.sh, start with --jitconfig
            _logger.LogInformation("Starting runner with JIT config in VM");
            var jitScript =
                $"{envExports} && cd {runnerDir} && " +
                $"nohup ./run.sh --jitconfig '{request.JitConfig}' > /tmp/runner.log 2>&1 &";
            await SshExec(sshUser, vmIp, sshPassword, jitScript, ct);
        }
        else
        {
            // Static mode: config.sh + run.sh
            var url = request.EnvironmentVariables.GetValueOrDefault("RR_RUNNER_URL", "");
            var token = request.EnvironmentVariables.GetValueOrDefault("RR_GITHUB_TOKEN", "");
            var labels = string.Join(",", request.Labels);
            var name = request.RunnerName;
            var ephemeralFlag = request.Ephemeral ? " --ephemeral" : "";

            _logger.LogInformation("Configuring runner {Name} in VM (static mode)", name);
            var configScript =
                $"{envExports} && cd {runnerDir} && " +
                $"./config.sh --url '{url}' --token '{token}' --name '{name}' " +
                $"--labels '{labels}' --unattended --replace{ephemeralFlag}";
            await SshExec(sshUser, vmIp, sshPassword, configScript, ct);

            var runScript = $"{envExports} && cd {runnerDir} && nohup ./run.sh > /tmp/runner.log 2>&1 &";
            await SshExec(sshUser, vmIp, sshPassword, runScript, ct);
        }

        _logger.LogInformation("Runner started in Tart VM {VM}", $"rr-{request.RunnerName}");
    }

    private async Task SetupGiteaRunnerViaSsh(
        string sshUser, string vmIp, string? sshPassword, string envExports,
        string downloadUrl, string version,
        RunnerStartRequest request, CancellationToken ct)
    {
        var runnerDir = $"/Users/{sshUser}/act-runner";
        var instanceUrl = request.EnvironmentVariables.GetValueOrDefault("RR_GITEA_INSTANCE_URL", "");
        var token = request.EnvironmentVariables.GetValueOrDefault("RR_GITEA_RUNNER_TOKEN", "");
        var ephemeralFlag = request.Ephemeral ? " --ephemeral" : "";

        var setupScript =
            $"mkdir -p {runnerDir} && cd {runnerDir} && " +
            $"curl -sL -o act_runner '{downloadUrl}' && chmod +x act_runner";

        _logger.LogInformation("Downloading Gitea act_runner v{Version} in VM", version);
        await SshExec(sshUser, vmIp, sshPassword, setupScript, ct);

        var registerScript =
            $"{envExports} && cd {runnerDir} && " +
            $"./act_runner register --instance '{instanceUrl}' --token '{token}' " +
            $"--name '{request.RunnerName}' --no-interactive{ephemeralFlag}";
        await SshExec(sshUser, vmIp, sshPassword, registerScript, ct);

        var runScript = $"{envExports} && cd {runnerDir} && nohup ./act_runner daemon > /tmp/runner.log 2>&1 &";
        await SshExec(sshUser, vmIp, sshPassword, runScript, ct);

        _logger.LogInformation("Gitea runner started in Tart VM");
    }

    private async Task SshExec(string sshUser, string vmIp, string? sshPassword, string script, CancellationToken ct)
    {
        _logger.LogDebug("SSH exec on {User}@{Ip}: {Script}",
            sshUser, vmIp, script.Length > 200 ? script[..200] + "..." : script);

        var sshOpts = "-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o ConnectTimeout=15";

        (int ExitCode, string Output) result;
        if (!string.IsNullOrEmpty(sshPassword))
        {
            result = await RunCommandAsync("sshpass",
                $"-p {EscapeShell(sshPassword)} ssh {sshOpts} {sshUser}@{vmIp} {EscapeShell(script)}", ct);
        }
        else
        {
            result = await RunCommandAsync("ssh",
                $"{sshOpts} {sshUser}@{vmIp} {EscapeShell(script)}", ct);
        }

        if (result.ExitCode != 0)
            _logger.LogWarning("SSH command returned {Code}: {Output}", result.ExitCode, result.Output);
    }

    private static string EscapeShell(string value) =>
        value.Replace("'", "'\\''");

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(
        string command, string arguments, CancellationToken ct)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, output.Trim());
    }
}
