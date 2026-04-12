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
        await RunCommandAsync("tart", $"stop {instanceHandle}", ct);

        // Delete the cloned VM
        await RunCommandAsync("tart", $"delete {instanceHandle}", ct);

        // Clean up config directory
        var configDir = Path.Combine(Path.GetTempPath(), "runnerrunner", instanceHandle);
        if (Directory.Exists(configDir))
            Directory.Delete(configDir, recursive: true);
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

        var sshPrefix = BuildSshPrefix(sshUser, vmIp, sshPassword);

        // Export env vars into the SSH session
        var envExports = string.Join(" ", request.EnvironmentVariables
            .Select(kv => $"{kv.Key}='{EscapeShell(kv.Value)}'"));

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
            await SetupGitHubRunnerViaSsh(sshPrefix, sshUser, envExports, downloadUrl, runnerTar, version, request, ct);
        }
        else if (request.Provider == RunnerProvider.GiteaActions)
        {
            await SetupGiteaRunnerViaSsh(sshPrefix, sshUser, envExports, downloadUrl, version, request, ct);
        }
    }

    private async Task SetupGitHubRunnerViaSsh(
        string sshPrefix, string sshUser, string envExports,
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
        await SshExec(sshPrefix, setupScript, ct);

        if (!string.IsNullOrEmpty(request.JitConfig))
        {
            // JIT mode: skip config.sh, start with --jitconfig
            _logger.LogInformation("Starting runner with JIT config in VM");
            var jitScript =
                $"cd {runnerDir} && {envExports} " +
                $"nohup ./run.sh --jitconfig '{request.JitConfig}' > /tmp/runner.log 2>&1 &";
            await SshExec(sshPrefix, jitScript, ct);
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
                $"cd {runnerDir} && {envExports} " +
                $"./config.sh --url '{url}' --token '{token}' --name '{name}' " +
                $"--labels '{labels}' --unattended --replace{ephemeralFlag}";
            await SshExec(sshPrefix, configScript, ct);

            var runScript = $"cd {runnerDir} && {envExports} nohup ./run.sh > /tmp/runner.log 2>&1 &";
            await SshExec(sshPrefix, runScript, ct);
        }

        _logger.LogInformation("Runner started in Tart VM {VM}", $"rr-{request.RunnerName}");
    }

    private async Task SetupGiteaRunnerViaSsh(
        string sshPrefix, string sshUser, string envExports,
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
        await SshExec(sshPrefix, setupScript, ct);

        var registerScript =
            $"cd {runnerDir} && {envExports} " +
            $"./act_runner register --instance '{instanceUrl}' --token '{token}' " +
            $"--name '{request.RunnerName}' --no-interactive{ephemeralFlag}";
        await SshExec(sshPrefix, registerScript, ct);

        var runScript = $"cd {runnerDir} && {envExports} nohup ./act_runner daemon > /tmp/runner.log 2>&1 &";
        await SshExec(sshPrefix, runScript, ct);

        _logger.LogInformation("Gitea runner started in Tart VM");
    }

    private async Task SshExec(string sshPrefix, string script, CancellationToken ct)
    {
        var result = await RunCommandAsync("bash", $"-c \"{sshPrefix} '{EscapeShell(script)}'\"", ct);
        if (result.ExitCode != 0)
            _logger.LogWarning("SSH command returned {Code}: {Output}", result.ExitCode, result.Output);
    }

    private static string BuildSshPrefix(string user, string ip, string? password)
    {
        var sshOpts = "-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o ConnectTimeout=15";
        if (!string.IsNullOrEmpty(password))
            return $"sshpass -p '{password}' ssh {sshOpts} {user}@{ip}";
        return $"ssh {sshOpts} {user}@{ip}";
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
