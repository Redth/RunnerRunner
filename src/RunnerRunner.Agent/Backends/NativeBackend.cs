using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private const int RunnerDirectoryHashBytes = 8;
    private const string InstanceMetadataFileName = "rr-instance.json";

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
        var basePath = request.RunnerBasePath ?? GetDefaultRunnerBasePath();
        var provider = request.Provider;
        var agentVersion = await ResolveAgentVersionAsync(provider, request.RunnerAgentVersion, ct);
        var runnerDirectoryName = CreateSafeRunnerDirectoryName(request.RunnerName, request.InstanceId);

        // Path token context for ${TOKEN} expansion
        var tokens = new Dictionary<string, string>
        {
            ["BASE_PATH"] = basePath,
            ["RUNNER_NAME"] = runnerDirectoryName,
            ["RUNNER_DISPLAY_NAME"] = request.RunnerName,
            ["INSTANCE_ID"] = request.InstanceId,
            ["PROVIDER"] = provider.ToString().ToLower(),
            ["VERSION"] = agentVersion,
            ["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        var workBase = ExpandTokens(request.WorkDirectory ?? "${BASE_PATH}/work", tokens);
        var workDir = Path.Combine(workBase, runnerDirectoryName);

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
        var instanceDir = Path.Combine(basePath, "instances", runnerDirectoryName);
        if (Directory.Exists(instanceDir))
            Directory.Delete(instanceDir, recursive: true);

        _logger.LogInformation("Creating isolated instance at {Dir}", instanceDir);
        CopyDirectory(agentDir, instanceDir);
        await WriteInstanceMetadataAsync(instanceDir, request, ct);

        // Step 3: Set up work directory (already expanded with tokens above)
        Directory.CreateDirectory(workDir);

        // Inject RunnerRunner identity env vars
        request.EnvironmentVariables["RR_INSTANCE_ID"] = request.InstanceId;
        request.EnvironmentVariables["RR_RUNNER_NAME"] = request.RunnerName;

        // Install the job-started banner hook if the server requested it.
        // actions/runner picks this up via ACTIONS_RUNNER_HOOK_JOB_STARTED.
        if (Services.JobHookScriptBuilder.IsHookRequested(request.EnvironmentVariables))
        {
            var hookPath = OperatingSystem.IsWindows()
                ? Services.JobHookScriptBuilder.WritePowerShellScript(instanceDir)
                : Services.JobHookScriptBuilder.WriteBashScript(instanceDir);
            request.EnvironmentVariables[Services.JobHookScriptBuilder.HookEnvVarName] = hookPath;
            _logger.LogDebug("Installed job-started hook at {Path}", hookPath);
        }

        // Pre-runner init steps (fail-fast unless the step has ContinueOnError).
        var instanceLogFile = Path.Combine(instanceDir, "runner.log");
        var initExecutor = new InitStepExecutor(_logger);
        var preSteps = request.InitSteps.Where(s => s.Phase == Core.Models.InitStepPhase.PreRunner).ToList();
        if (preSteps.Count > 0)
        {
            _logger.LogInformation("Running {Count} pre-runner init step(s) for {RunnerName}", preSteps.Count, request.RunnerName);
            await initExecutor.RunAsync(preSteps, Core.Models.InitStepPhase.PreRunner, workDir, request.EnvironmentVariables, instanceLogFile, ct);
        }

        // Step 4: Configure and start based on provider
        Process runProcess;

        switch (provider)
        {
            case RunnerProvider.GitHubActions:
                if (!string.IsNullOrEmpty(request.JitConfig))
                {
                    // JIT config mode — skip config.sh, use --jitconfig directly
                    _logger.LogInformation("Using JIT config for runner {RunnerName} (dynamic provisioning)", request.RunnerName);
                    runProcess = StartGitHubJitRunner(instanceDir, request);
                }
                else
                {
                    await ConfigureGitHubRunnerAsync(instanceDir, workDir, request, ct);
                    runProcess = StartGitHubRunner(instanceDir, request);
                }
                break;

            case RunnerProvider.GiteaActions:
                if (request.Ephemeral)
                {
                    await ConfigureGiteaRunnerAsync(instanceDir, workDir, request, ct, ephemeral: true);
                }
                else
                {
                    await ConfigureGiteaRunnerAsync(instanceDir, workDir, request, ct);
                }
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
        await File.WriteAllTextAsync(Path.Combine(instanceDir, "rr.pid"), runProcess.Id.ToString());
        _runners[instanceHandle] = new ManagedNativeRunner
        {
            Process = runProcess,
            InstanceDir = instanceDir,
            WorkDir = workDir,
            RunnerName = request.RunnerName,
            LogFile = instanceLogFile,
            PostExitSteps = request.InitSteps.Where(s => s.Phase == Core.Models.InitStepPhase.PostExit).ToList(),
            RunnerEnvironment = new Dictionary<string, string>(request.EnvironmentVariables)
        };

        // Pipe stdout/stderr to log file
        _ = Task.Run(() => PipeOutputToLogFile(runProcess, instanceLogFile), ct);

        _logger.LogInformation("Native runner {RunnerName} started (PID: {PID}, log: {LogFile})", request.RunnerName, runProcess.Id, instanceLogFile);

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

        // Run PostExit init steps before wiping the instance dir.
        if (runner.PostExitSteps.Count > 0)
        {
            _logger.LogInformation("Running {Count} post-exit init step(s) for {RunnerName}",
                runner.PostExitSteps.Count, runner.RunnerName);
            try
            {
                var executor = new InitStepExecutor(_logger);
                await executor.RunAsync(
                    runner.PostExitSteps,
                    Core.Models.InitStepPhase.PostExit,
                    runner.WorkDir,
                    runner.RunnerEnvironment,
                    runner.LogFile,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-exit init steps failed for {RunnerName}", runner.RunnerName);
            }
        }

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

    /// <summary>
    /// Discovers RunnerRunner-managed native processes by scanning instance directories for PID files.
    /// </summary>
    public async Task<List<DiscoveredRunner>> DiscoverManagedProcessesAsync(CancellationToken ct = default)
    {
        var result = new List<DiscoveredRunner>();
        var basePath = GetDefaultRunnerBasePath();
        var instancesDir = Path.Combine(basePath, "instances");

        if (!Directory.Exists(instancesDir))
            return result;

        foreach (var dir in Directory.GetDirectories(instancesDir))
        {
            var pidFile = Path.Combine(dir, "rr.pid");
            if (!File.Exists(pidFile))
                continue;

            try
            {
                var pidText = await File.ReadAllTextAsync(pidFile, ct);
                if (!int.TryParse(pidText.Trim(), out var pid))
                    continue;

                var isRunning = false;
                try
                {
                    var proc = Process.GetProcessById(pid);
                    isRunning = !proc.HasExited;
                }
                catch
                {
                    // Process not found — not running
                }

                var metadata = await ReadInstanceMetadataAsync(dir, ct);
                result.Add(new DiscoveredRunner
                {
                    InstanceId = metadata?.InstanceId ?? "",
                    ProcessId = pid,
                    RunnerName = metadata?.RunnerName ?? Path.GetFileName(dir),
                    InstanceDir = dir,
                    Backend = ExecutionBackend.Native,
                    IsRunning = isRunning,
                    Status = isRunning ? "running" : "exited"
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to discover native runner in {Dir}", dir);
            }
        }

        return result;
    }

    internal static string CreateSafeRunnerDirectoryName(string runnerName, string instanceId)
    {
        var hashInput = string.IsNullOrWhiteSpace(instanceId)
            ? runnerName
            : $"{instanceId}\n{runnerName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        return "rr-" + Convert.ToHexString(hash.AsSpan(0, RunnerDirectoryHashBytes)).ToLowerInvariant();
    }

    public static string GetDefaultRunnerBasePath() =>
        GetDefaultRunnerBasePath(OperatingSystem.IsWindows(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string GetDefaultRunnerBasePath(bool isWindows, string homePath) =>
        isWindows
            ? @"C:\rr"
            : Path.Combine(homePath, ".runnerrunner");

    private static async Task WriteInstanceMetadataAsync(string instanceDir, RunnerStartRequest request, CancellationToken ct)
    {
        var metadata = new NativeRunnerInstanceMetadata
        {
            InstanceId = request.InstanceId,
            RunnerName = request.RunnerName
        };

        var path = Path.Combine(instanceDir, InstanceMetadataFileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(metadata), ct);
    }

    private static async Task<NativeRunnerInstanceMetadata?> ReadInstanceMetadataAsync(string instanceDir, CancellationToken ct)
    {
        var path = Path.Combine(instanceDir, InstanceMetadataFileName);
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<NativeRunnerInstanceMetadata>(stream, cancellationToken: ct);
    }

    /// <summary>
    /// Cleans up an orphaned native runner process and its instance directory.
    /// </summary>
    public async Task CleanupOrphanProcessAsync(int processId, string? instanceDir, CancellationToken ct = default)
    {
        _logger.LogInformation("Cleaning up orphaned native runner (PID: {PID})", processId);

        try
        {
            var proc = Process.GetProcessById(processId);
            if (!proc.HasExited)
            {
                if (!OperatingSystem.IsWindows())
                {
                    Process.Start("kill", $"-TERM {processId}")?.WaitForExit(5000);
                    await Task.Delay(3000, ct);
                }

                proc.Refresh();
                if (!proc.HasExited)
                {
                    _logger.LogWarning("Orphan PID {PID} did not exit after SIGTERM, force killing", processId);
                    proc.Kill(entireProcessTree: true);
                }
            }
        }
        catch (ArgumentException)
        {
            // Process already gone
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error killing orphan process {PID}", processId);
        }

        if (!string.IsNullOrEmpty(instanceDir) && Directory.Exists(instanceDir))
        {
            try
            {
                Directory.Delete(instanceDir, recursive: true);
                _logger.LogInformation("Cleaned up orphan instance dir {Dir}", instanceDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up orphan instance directory {Dir}", instanceDir);
            }
        }
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

    private Process StartGitHubJitRunner(string instanceDir, RunnerStartRequest request)
    {
        var isWindows = OperatingSystem.IsWindows();
        var runScript = Path.Combine(instanceDir, isWindows ? "run.cmd" : "run.sh");
        return StartProcess(runScript, ["--jitconfig", request.JitConfig!], instanceDir, request.EnvironmentVariables);
    }

    private Process StartGitHubRunner(string instanceDir, RunnerStartRequest request)
    {
        var isWindows = OperatingSystem.IsWindows();
        var runScript = Path.Combine(instanceDir, isWindows ? "run.cmd" : "run.sh");
        return StartProcess(runScript, [], instanceDir, request.EnvironmentVariables);
    }

    // ─── Gitea Actions Runner (act_runner) ─────────────────

    private async Task ConfigureGiteaRunnerAsync(
        string instanceDir, string workDir, RunnerStartRequest request, CancellationToken ct, bool ephemeral = false)
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

        if (ephemeral)
            args.Add("--ephemeral");

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

    private async Task<string> ResolveAgentVersionAsync(
        RunnerProvider provider, string? requestedVersion, CancellationToken ct)
    {
        if (!NeedsLatestResolution(requestedVersion))
            return requestedVersion!;

        var resolvedVersion = await TryResolveLatestVersionAsync(provider, ct);
        if (!string.IsNullOrWhiteSpace(resolvedVersion))
        {
            _logger.LogInformation("Resolved latest {Provider} runner version to {Version}", provider, resolvedVersion);
            return resolvedVersion;
        }

        throw new InvalidOperationException(
            $"Unable to resolve the latest runner agent version for {provider}. " +
            "Set an explicit runner version on the profile or ensure version discovery can reach the provider release feed.");
    }

    private async Task<string?> TryResolveLatestVersionAsync(RunnerProvider provider, CancellationToken ct)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RunnerRunner-Agent");

        var url = provider switch
        {
            RunnerProvider.GitHubActions => "https://api.github.com/repos/actions/runner/releases/latest",
            RunnerProvider.GiteaActions => "https://gitea.com/api/v1/repos/gitea/act_runner/releases/latest",
            RunnerProvider.AzureDevOps => "https://api.github.com/repos/microsoft/azure-pipelines-agent/releases/latest",
            _ => null
        };

        if (url == null)
            return null;

        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Latest version lookup for {Provider} returned HTTP {StatusCode}",
                    provider, (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var tagName = doc.RootElement.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString()
                : null;

            return NormalizeVersionTag(tagName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve latest runner agent version for {Provider}", provider);
            return null;
        }
    }

    internal static bool NeedsLatestResolution(string? requestedVersion) =>
        string.IsNullOrWhiteSpace(requestedVersion)
        || string.Equals(requestedVersion, "latest", StringComparison.OrdinalIgnoreCase);

    internal static string? NormalizeVersionTag(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            return null;

        return tagName.Trim().TrimStart('v', 'V');
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
    /// Available tokens include BASE_PATH, RUNNER_NAME (safe directory token),
    /// RUNNER_DISPLAY_NAME, INSTANCE_ID, PROVIDER, VERSION, and HOME.
    /// </summary>
    internal static string ExpandTokens(string input, Dictionary<string, string> tokens)
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
        public string? LogFile { get; set; }
        public List<Core.Models.ResolvedInitStep> PostExitSteps { get; set; } = [];
        public Dictionary<string, string> RunnerEnvironment { get; set; } = new();
    }

    private sealed class NativeRunnerInstanceMetadata
    {
        public string InstanceId { get; set; } = "";
        public string RunnerName { get; set; } = "";
    }

    /// <summary>
    /// Get the log file path for a running native instance by its handle (PID).
    /// </summary>
    public string? GetLogFilePath(string instanceHandle)
    {
        return _runners.TryGetValue(instanceHandle, out var runner) ? runner.LogFile : null;
    }

    private static async Task PipeOutputToLogFile(Process process, string logFile)
    {
        try
        {
            using var writer = new StreamWriter(logFile, append: true) { AutoFlush = true };
            var stdoutTask = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                    await writer.WriteLineAsync(line);
            });
            var stderrTask = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                    await writer.WriteLineAsync($"[stderr] {line}");
            });
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch { /* Process exited */ }
    }
}
