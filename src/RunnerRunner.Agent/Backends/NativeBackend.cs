using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Backends;

/// <summary>
/// Execution backend that runs runner agents directly as native processes on the host.
/// Supports GitHub Actions runner, Gitea act_runner, and AzDO agent.
/// Each instance gets its own isolated directory to support parallel runners.
/// </summary>
public class NativeBackend : IRunnerBackend
{
    private readonly ILogger<NativeBackend> _logger;
    private readonly Dictionary<string, ManagedNativeRunner> _runners = new();

    public ExecutionBackend BackendType => ExecutionBackend.Native;

    public NativeBackend(ILogger<NativeBackend> logger)
    {
        _logger = logger;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public async Task<RunnerInstanceInfo> StartRunnerAsync(RunnerStartRequest request, CancellationToken ct = default)
    {
        var basePath = request.RunnerBasePath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".runnerrunner");
        var provider = request.Provider;
        var agentVersion = request.RunnerAgentVersion ?? "latest";

        // Path token context for ${TOKEN} expansion
        var tokens = new Dictionary<string, string>
        {
            ["BASE_PATH"] = basePath,
            ["RUNNER_NAME"] = request.RunnerName,
            ["INSTANCE_ID"] = request.InstanceId,
            ["PROVIDER"] = provider.ToString().ToLower(),
            ["VERSION"] = agentVersion,
            ["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        var workBase = ExpandTokens(request.WorkDirectory ?? "${BASE_PATH}/work", tokens);
        var workDir = Path.Combine(workBase, request.RunnerName);

        // Inject path tokens as env vars too
        request.EnvironmentVariables["RR_BASE_PATH"] = basePath;
        request.EnvironmentVariables["RR_WORK_DIR"] = workDir;

        // Step 1: Ensure the runner agent binary is downloaded
        var agentDir = Path.Combine(basePath, "agents", provider.ToString().ToLower(), agentVersion);
        if (!Directory.Exists(agentDir) || Directory.GetFiles(agentDir, "*", SearchOption.AllDirectories).Length == 0)
        {
            if (Directory.Exists(agentDir)) Directory.Delete(agentDir, recursive: true);
            _logger.LogInformation("Runner agent not found at {Dir}, attempting download...", agentDir);
            await DownloadRunnerAgentAsync(provider, agentVersion, agentDir, ct);
        }

        // Verify download succeeded
        var configExists = File.Exists(Path.Combine(agentDir, "config.sh"))
            || File.Exists(Path.Combine(agentDir, "config.cmd"))
            || File.Exists(Path.Combine(agentDir, "act_runner"));
        if (!configExists)
        {
            throw new InvalidOperationException(
                $"Runner agent at {agentDir} is missing config scripts. Download may have failed. " +
                $"Contents: [{string.Join(", ", Directory.GetFiles(agentDir).Select(Path.GetFileName).Take(10))}]");
        }

        // Step 2: Create isolated instance directory (clone the agent)
        var instanceDir = Path.Combine(basePath, "instances", request.RunnerName);
        if (Directory.Exists(instanceDir))
            Directory.Delete(instanceDir, recursive: true);

        _logger.LogInformation("Creating isolated instance at {Dir}", instanceDir);
        CopyDirectory(agentDir, instanceDir);

        // Step 3: Set up work directory (already expanded with tokens above)
        Directory.CreateDirectory(workDir);

        // Inject RunnerRunner identity env vars
        request.EnvironmentVariables["RR_INSTANCE_ID"] = request.InstanceId;
        request.EnvironmentVariables["RR_RUNNER_NAME"] = request.RunnerName;

        // Step 4: Configure and start based on provider
        Process runProcess;

        switch (provider)
        {
            case RunnerProvider.GitHubActions:
                await ConfigureGitHubRunnerAsync(instanceDir, workDir, request, ct);
                runProcess = StartGitHubRunner(instanceDir, request);
                break;

            case RunnerProvider.GiteaActions:
                await ConfigureGiteaRunnerAsync(instanceDir, workDir, request, ct);
                runProcess = StartGiteaRunner(instanceDir, request);
                break;

            case RunnerProvider.AzureDevOps:
                await ConfigureAzDoAgentAsync(instanceDir, workDir, request, ct);
                runProcess = StartAzDoAgent(instanceDir, request);
                break;

            default:
                throw new NotSupportedException($"Provider {provider} not supported for native backend");
        }

        var instanceHandle = runProcess.Id.ToString();
        _runners[instanceHandle] = new ManagedNativeRunner
        {
            Process = runProcess,
            InstanceDir = instanceDir,
            WorkDir = workDir,
            RunnerName = request.RunnerName
        };

        _logger.LogInformation("Native runner {RunnerName} started (PID: {PID})", request.RunnerName, runProcess.Id);

        return new RunnerInstanceInfo
        {
            InstanceHandle = instanceHandle,
            RunnerName = request.RunnerName
        };
    }

    public async Task StopRunnerAsync(string instanceHandle, CancellationToken ct = default)
    {
        if (!_runners.TryGetValue(instanceHandle, out var runner))
            return;

        _logger.LogInformation("Stopping native runner {RunnerName} (PID: {PID})",
            runner.RunnerName, instanceHandle);

        if (!runner.Process.HasExited)
        {
            // Send SIGTERM for graceful shutdown (runner deregisters itself)
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    // Unix: send SIGTERM via kill
                    Process.Start("kill", $"-TERM {runner.Process.Id}")?.WaitForExit(1000);
                }
                else
                {
                    runner.Process.CloseMainWindow();
                }

                // Wait up to 30 seconds for graceful shutdown
                runner.Process.WaitForExit(30_000);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during graceful shutdown");
            }

            // Force kill if still running
            if (!runner.Process.HasExited)
            {
                _logger.LogWarning("Runner {RunnerName} did not exit gracefully, force killing", runner.RunnerName);
                runner.Process.Kill(entireProcessTree: true);
            }
        }

        runner.Process.Dispose();

        // Clean up instance directory
        try
        {
            if (Directory.Exists(runner.InstanceDir))
            {
                Directory.Delete(runner.InstanceDir, recursive: true);
                _logger.LogInformation("Cleaned up instance dir {Dir}", runner.InstanceDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up instance directory {Dir}", runner.InstanceDir);
        }

        _runners.Remove(instanceHandle);
    }

    public Task<RunnerHealthStatus> GetHealthAsync(string instanceHandle, CancellationToken ct = default)
    {
        if (_runners.TryGetValue(instanceHandle, out var runner))
        {
            return Task.FromResult(new RunnerHealthStatus
            {
                IsRunning = !runner.Process.HasExited,
                Status = runner.Process.HasExited ? $"exited:{runner.Process.ExitCode}" : "running"
            });
        }

        return Task.FromResult(new RunnerHealthStatus { IsRunning = false, Status = "not_found" });
    }

    // ─── GitHub Actions Runner ─────────────────────────────

    private async Task ConfigureGitHubRunnerAsync(
        string instanceDir, string workDir, RunnerStartRequest request, CancellationToken ct)
    {
        var isWindows = OperatingSystem.IsWindows();
        var configScript = Path.Combine(instanceDir, isWindows ? "config.cmd" : "config.sh");

        // Clean up any pre-existing configuration
        foreach (var file in new[] { ".runner", ".credentials", ".credentials_rsaparams" })
        {
            var path = Path.Combine(instanceDir, file);
            if (File.Exists(path)) File.Delete(path);
        }

        var args = new List<string>
        {
            "--url", request.RunnerUrl ?? "",
            "--token", request.RegistrationToken ?? "",
            "--name", request.RunnerName,
            "--work", workDir,
            "--labels", string.Join(",", request.Labels),
            "--runnergroup", request.RunnerGroup,
            "--unattended",
            "--replace"
        };

        if (request.Ephemeral)
        {
            args.Add("--ephemeral");
            args.Add("--disableupdate");
        }

        await RunScriptAsync(configScript, args, instanceDir, request.EnvironmentVariables, ct);
    }

    private Process StartGitHubRunner(string instanceDir, RunnerStartRequest request)
    {
        var isWindows = OperatingSystem.IsWindows();
        var runScript = Path.Combine(instanceDir, isWindows ? "run.cmd" : "run.sh");
        return StartProcess(runScript, [], instanceDir, request.EnvironmentVariables);
    }

    // ─── Gitea Actions Runner (act_runner) ─────────────────

    private async Task ConfigureGiteaRunnerAsync(
        string instanceDir, string workDir, RunnerStartRequest request, CancellationToken ct)
    {
        // act_runner uses a config.yaml and `register` command
        var actRunner = FindExecutable(instanceDir, "act_runner");

        var args = new List<string>
        {
            "register",
            "--instance", request.RunnerUrl ?? "",
            "--token", request.RegistrationToken ?? "",
            "--name", request.RunnerName,
            "--labels", string.Join(",", request.Labels),
            "--no-interactive"
        };

        await RunCommandAsync(actRunner, string.Join(" ", args), instanceDir, request.EnvironmentVariables, ct);
    }

    private Process StartGiteaRunner(string instanceDir, RunnerStartRequest request)
    {
        var actRunner = FindExecutable(instanceDir, "act_runner");
        return StartProcess(actRunner, ["daemon"], instanceDir, request.EnvironmentVariables);
    }

    // ─── Azure DevOps Agent ────────────────────────────────

    private async Task ConfigureAzDoAgentAsync(
        string instanceDir, string workDir, RunnerStartRequest request, CancellationToken ct)
    {
        var isWindows = OperatingSystem.IsWindows();
        var configScript = Path.Combine(instanceDir, isWindows ? "config.cmd" : "config.sh");

        var args = new List<string>
        {
            "--unattended",
            "--url", request.RunnerUrl ?? "",
            "--auth", "pat",
            "--token", request.RegistrationToken ?? "",
            "--agent", request.RunnerName,
            "--work", workDir,
            "--replace",
            "--acceptTeeEula"
        };

        if (!string.IsNullOrEmpty(request.RunnerGroup) && request.RunnerGroup != "Default")
            args.AddRange(["--pool", request.RunnerGroup]);

        await RunScriptAsync(configScript, args, instanceDir, request.EnvironmentVariables, ct);
    }

    private Process StartAzDoAgent(string instanceDir, RunnerStartRequest request)
    {
        var isWindows = OperatingSystem.IsWindows();
        var runScript = Path.Combine(instanceDir, isWindows ? "run.cmd" : "run.sh");
        return StartProcess(runScript, [], instanceDir, request.EnvironmentVariables);
    }

    // ─── Runner Agent Download ─────────────────────────────

    private async Task DownloadRunnerAgentAsync(
        RunnerProvider provider, string version, string targetDir, CancellationToken ct)
    {
        // Determine the download URL based on provider, version, and platform
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        string? url = null;

        if (provider == RunnerProvider.GitHubActions)
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var ext = OperatingSystem.IsWindows() ? "zip" : "tar.gz";
            url = $"https://github.com/actions/runner/releases/download/v{version}/actions-runner-{os}-{arch}-{version}.{ext}";
        }
        else if (provider == RunnerProvider.GiteaActions)
        {
            var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
            url = $"https://gitea.com/gitea/act_runner/releases/download/v{version}/act_runner-{version}-{os}-{arch}";
        }
        else if (provider == RunnerProvider.AzureDevOps)
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var ext = OperatingSystem.IsWindows() ? "zip" : "tar.gz";
            url = $"https://vstsagentpackage.azureedge.net/agent/{version}/vsts-agent-{os}-{arch}-{version}.{ext}";
        }

        if (url == null)
            throw new NotSupportedException($"Cannot auto-download runner for provider {provider}");

        _logger.LogInformation("Downloading runner agent from {Url}", url);

        Directory.CreateDirectory(targetDir);

        using var http = new HttpClient();
        var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var tempFile = Path.GetTempFileName();
        await using (var fs = File.OpenWrite(tempFile))
        {
            await response.Content.CopyToAsync(fs, ct);
        }

        // Extract
        if (url.EndsWith(".tar.gz"))
        {
            await RunCommandAsync("tar", $"-xzf {tempFile} -C {targetDir}", targetDir, new(), ct);
        }
        else if (url.EndsWith(".zip"))
        {
            ZipFile.ExtractToDirectory(tempFile, targetDir);
        }
        else
        {
            // Single binary (Gitea act_runner)
            var destPath = Path.Combine(targetDir, "act_runner");
            File.Move(tempFile, destPath, overwrite: true);
            if (!OperatingSystem.IsWindows())
                await RunCommandAsync("chmod", $"+x {destPath}", targetDir, new(), ct);
        }

        File.Delete(tempFile);
        _logger.LogInformation("Runner agent extracted to {Dir}", targetDir);
    }

    // ─── Helpers ───────────────────────────────────────────

    private async Task RunScriptAsync(
        string script, List<string> args, string workDir,
        Dictionary<string, string> envVars, CancellationToken ct)
    {
        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            Arguments = isWindows
                ? $"/c \"{script}\" {string.Join(" ", args)}"
                : $"\"{script}\" {string.Join(" ", args)}",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var kv in envVars)
            psi.EnvironmentVariables[kv.Key] = kv.Value;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {script}");

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            throw new InvalidOperationException(
                $"Script {Path.GetFileName(script)} failed (exit {process.ExitCode}): {error}\n{output}");
        }
    }

    private static async Task RunCommandAsync(
        string command, string arguments, string workDir,
        Dictionary<string, string> envVars, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var kv in envVars)
            psi.EnvironmentVariables[kv.Key] = kv.Value;

        var process = Process.Start(psi);
        if (process != null) await process.WaitForExitAsync(ct);
    }

    private Process StartProcess(
        string script, List<string> args, string workDir,
        Dictionary<string, string> envVars)
    {
        var isWindows = OperatingSystem.IsWindows();
        var argsStr = args.Count > 0 ? " " + string.Join(" ", args) : "";
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            Arguments = isWindows
                ? $"/c \"{script}\"{argsStr}"
                : $"\"{script}\"{argsStr}",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var kv in envVars)
            psi.EnvironmentVariables[kv.Key] = kv.Value;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {script}");

        return process;
    }

    private static string FindExecutable(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (File.Exists(path)) return path;
        path = Path.Combine(dir, name + ".exe");
        if (File.Exists(path)) return path;
        return name; // Fall back to PATH
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }
        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
        }
    }

    /// <summary>
    /// Expands ${TOKEN} references in a path string.
    /// Available tokens: BASE_PATH, RUNNER_NAME, INSTANCE_ID, PROVIDER, VERSION, HOME
    /// </summary>
    private static string ExpandTokens(string input, Dictionary<string, string> tokens)
    {
        var result = input;
        foreach (var kv in tokens)
        {
            result = result.Replace($"${{{kv.Key}}}", kv.Value);
        }
        return result;
    }

    private class ManagedNativeRunner
    {
        public required Process Process { get; set; }
        public required string InstanceDir { get; set; }
        public required string WorkDir { get; set; }
        public required string RunnerName { get; set; }
    }
}
