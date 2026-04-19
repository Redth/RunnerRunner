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

        var sourceImage = ImageReference.Build(config.RegistryUrl, config.ImageName, config.Tag);
        var vmName = request.RunnerName.StartsWith("rr-", StringComparison.OrdinalIgnoreCase)
            ? request.RunnerName
            : $"rr-{request.RunnerName}";

        // Clone the base image for this runner instance
        _logger.LogInformation("Cloning tart image {Source} → {VM}", sourceImage, vmName);
        EnsureSuccess(await RunCommandAsync("tart", $"clone {sourceImage} {vmName}", ct),
            $"tart clone {sourceImage} {vmName}");

        // Configure VM resources if specified
        if (config.CpuCount.HasValue)
            EnsureSuccess(await RunCommandAsync("tart", $"set {vmName} --cpu {config.CpuCount.Value}", ct),
                $"tart set {vmName} --cpu {config.CpuCount.Value}");
        if (config.MemorySizeGb.HasValue)
            EnsureSuccess(await RunCommandAsync("tart", $"set {vmName} --memory {config.MemorySizeGb.Value * 1024}", ct),
                $"tart set {vmName} --memory {config.MemorySizeGb.Value * 1024}");
        if (config.DiskSizeGb.HasValue)
            EnsureSuccess(await RunCommandAsync("tart", $"set {vmName} --disk-size {config.DiskSizeGb.Value}", ct),
                $"tart set {vmName} --disk-size {config.DiskSizeGb.Value}");
        if (!string.IsNullOrEmpty(config.Display))
            EnsureSuccess(await RunCommandAsync("tart", $"set {vmName} --display {config.Display}", ct),
                $"tart set {vmName} --display {config.Display}");

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
                FileName = ResolveToolPath("tart", "/opt/homebrew/bin/tart", "/usr/local/bin/tart") ?? "tart",
                Arguments = $"run {vmName} --no-graphics {dirArgs}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = "/"
            }
        };
        process.StartInfo.Environment["PATH"] = "/opt/homebrew/bin:/opt/homebrew/sbin:/usr/local/bin:/usr/bin:/bin";
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
            throw new InvalidOperationException($"Tart VM {vmName} did not get an IP address within 90 seconds");
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

        // Pre-runner init steps: run via SSH before runner bits are touched.
        var preSteps = request.InitSteps.Where(s => s.Phase == Core.Models.InitStepPhase.PreRunner).ToList();
        if (preSteps.Count > 0)
        {
            var preFragment = InitStepShellBuilder.BuildLinuxFragment(preSteps, "PreRunner");
            _logger.LogInformation("Running {Count} pre-runner init step(s) in Tart VM {VM}", preSteps.Count, vmName);
            await SshExec(sshUser, vmIp, sshPassword, "set +e; " + preFragment + " exit 0", ct);
        }

        // Export env vars into the SSH session
        var envExports = string.Join(" ", request.EnvironmentVariables
            .Select(kv => $"export {kv.Key}='{EscapeShell(kv.Value)}'"));

        // Determine runner version and download URL
        var version = request.RunnerAgentVersion ?? "2.333.1";
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
            // JIT mode: write config to file first (too long for command line), then start
            _logger.LogInformation("Starting runner with JIT config in VM");

            // Write JIT config to a temp file in the VM
            await SshExec(sshUser, vmIp, sshPassword,
                $"echo '{EscapeShell(request.JitConfig)}' > /tmp/jitconfig.txt", ct);

            var runnerCmd = $"./run.sh --jitconfig \"$JIT_CONFIG\"";
            var preamble = $"cd {runnerDir} && JIT_CONFIG=$(cat /tmp/jitconfig.txt)";
            await LaunchRunnerWithPostStepsAsync(sshUser, vmIp, sshPassword, preamble, runnerCmd, request, ct);

            // Wait a moment, then check the log for confirmation
            await Task.Delay(5000, ct);
            await SshExec(sshUser, vmIp, sshPassword,
                "echo '--- Runner process check ---' && pgrep -f Runner.Listener && echo 'Runner is running' || echo 'Runner process not found' && echo '--- runner.log tail ---' && tail -5 /tmp/runner.log 2>/dev/null || echo 'No runner.log'", ct);
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

            await LaunchRunnerWithPostStepsAsync(
                sshUser, vmIp, sshPassword,
                $"{envExports} && cd {runnerDir}", "./run.sh", request, ct);
        }

        _logger.LogInformation("Runner started in Tart VM for {RunnerName}", request.RunnerName);
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

        await LaunchRunnerWithPostStepsAsync(
            sshUser, vmIp, sshPassword,
            $"{envExports} && cd {runnerDir}", "./act_runner daemon", request, ct);

        _logger.LogInformation("Gitea runner started in Tart VM");
    }

    /// <summary>
    /// Writes a wrapper script on the VM that runs the given runner command then
    /// executes PostExit init steps, and launches it via nohup. If there are no
    /// post steps, falls back to direct nohup of the runner command.
    /// </summary>
    private async Task LaunchRunnerWithPostStepsAsync(
        string sshUser, string vmIp, string? sshPassword,
        string preamble, string runnerCmd, RunnerStartRequest request, CancellationToken ct)
    {
        var postSteps = request.InitSteps.Where(s => s.Phase == Core.Models.InitStepPhase.PostExit).ToList();
        if (postSteps.Count == 0)
        {
            var runScript = $"{preamble} && nohup {runnerCmd} > /tmp/runner.log 2>&1 &";
            await SshExec(sshUser, vmIp, sshPassword, runScript, ct);
            return;
        }

        var postFragment = InitStepShellBuilder.BuildLinuxFragment(postSteps, "PostExit");
        var wrapperBody = new System.Text.StringBuilder();
        wrapperBody.AppendLine("#!/usr/bin/env bash");
        wrapperBody.AppendLine("set +e");
        wrapperBody.AppendLine(preamble.Replace(" && ", "\n"));
        wrapperBody.AppendLine(runnerCmd);
        wrapperBody.AppendLine("rr_rc=$?");
        wrapperBody.AppendLine("echo \"[RunnerRunner] runner exited rc=$rr_rc; running PostExit steps\"");
        wrapperBody.Append(postFragment);
        wrapperBody.AppendLine("exit $rr_rc");

        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(wrapperBody.ToString()));
        var writeWrapper = $"echo '{encoded}' | base64 -d > /tmp/rr-runner-wrapper.sh && chmod +x /tmp/rr-runner-wrapper.sh";
        await SshExec(sshUser, vmIp, sshPassword, writeWrapper, ct);

        var launchWrapper = "nohup /tmp/rr-runner-wrapper.sh > /tmp/runner.log 2>&1 &";
        await SshExec(sshUser, vmIp, sshPassword, launchWrapper, ct);
    }

    private async Task SshExec(string sshUser, string vmIp, string? sshPassword, string script, CancellationToken ct)
    {
        _logger.LogDebug("SSH exec on {User}@{Ip}: {Script}",
            sshUser, vmIp, script.Length > 200 ? script[..200] + "..." : script);

        var sshOpts = "-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o ConnectTimeout=15";
        ProcessStartInfo psi;

        if (!string.IsNullOrEmpty(sshPassword))
        {
            var sshpassPath = ResolveToolPath("sshpass", "/opt/homebrew/bin/sshpass", "/usr/local/bin/sshpass");
            if (!string.IsNullOrWhiteSpace(sshpassPath))
            {
                var fullCommand = $"{sshpassPath} -p '{EscapeShell(sshPassword)}' ssh {sshOpts} {sshUser}@{vmIp} bash -s";
                psi = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-lc \"{fullCommand.Replace("\"", "\\\"")}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true
                };
            }
            else
            {
                var expectPath = ResolveToolPath("expect", "/usr/bin/expect", "/opt/homebrew/bin/expect", "/usr/local/bin/expect");
                if (string.IsNullOrWhiteSpace(expectPath))
                    throw new InvalidOperationException("Neither sshpass nor expect is available for password-based Tart SSH setup.");

                var expectScript =
                    $"set timeout 60; " +
                    $"spawn ssh {sshOpts} {sshUser}@{vmIp} bash -s; " +
                    "expect { " +
                    "\"*assword:*\" { send -- \"$env(RR_SSH_PASSWORD)\\r\"; exp_continue } " +
                    "timeout { } " +
                    "eof { catch wait result; exit [lindex $result 3] } " +
                    "}; " +
                    "send -- \"$env(RR_SSH_SCRIPT)\\n\"; " +
                    "send -- \"\\004\"; " +
                    "expect eof; " +
                    "catch wait result; " +
                    "exit [lindex $result 3]";

                psi = new ProcessStartInfo
                {
                    FileName = expectPath,
                    Arguments = $"-c \"{expectScript.Replace("\"", "\\\"")}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.Environment["RR_SSH_PASSWORD"] = sshPassword;
                psi.Environment["RR_SSH_SCRIPT"] = script;
            }
        }
        else
        {
            var fullCommand = $"ssh {sshOpts} {sshUser}@{vmIp} bash -s";
            psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-lc \"{fullCommand.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };
        }

        // Add homebrew to PATH
        var path = psi.Environment.TryGetValue("PATH", out var existingPath) ? existingPath ?? "" : "";
        if (!path.Contains("/opt/homebrew"))
            psi.Environment["PATH"] = $"/opt/homebrew/bin:/opt/homebrew/sbin:{path}";

        var process = Process.Start(psi)!;

        if (psi.RedirectStandardInput)
        {
            await process.StandardInput.WriteAsync(script);
            await process.StandardInput.WriteAsync(Environment.NewLine);
            process.StandardInput.Close();
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var output = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\n{stderr}";

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"SSH command failed with exit code {process.ExitCode}: {output.Trim()}");
    }

    private static string EscapeShell(string value) =>
        value.Replace("'", "'\\''");

    private static void EnsureSuccess((int ExitCode, string Output) result, string operation)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{operation} failed: {result.Output}");
    }

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(
        string command, string arguments, CancellationToken ct)
    {
        if (string.Equals(command, "tart", StringComparison.OrdinalIgnoreCase))
            command = ResolveToolPath("tart", "/opt/homebrew/bin/tart", "/usr/local/bin/tart") ?? command;

        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = "/"
        };

        // Ensure homebrew paths are available (macOS)
        var path = psi.Environment.TryGetValue("PATH", out var existingPath) ? existingPath ?? "" : "";
        if (!path.Contains("/opt/homebrew"))
        {
            psi.Environment["PATH"] = $"/opt/homebrew/bin:/opt/homebrew/sbin:{path}";
        }

        var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var combinedOutput = string.IsNullOrEmpty(stderr) ? output : $"{output}\n{stderr}";
        return (process.ExitCode, combinedOutput.Trim());
    }

    private static string? ResolveToolPath(string command, params string[] preferredPaths)
    {
        foreach (var path in preferredPaths)
        {
            if (File.Exists(path))
                return path;
        }

        var envPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var pathPart in envPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(pathPart, command);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
