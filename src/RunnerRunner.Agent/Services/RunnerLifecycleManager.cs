using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Hub;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

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
    /// Parameters: (owned runner, exitCode, reason)
    /// </summary>
    public event Action<ManagedRunner, long, string>? OnRunnerExited;

    public RunnerLifecycleManager(ILogger<RunnerLifecycleManager> logger)
    {
        _logger = logger;
    }

    public IReadOnlyDictionary<string, ManagedRunner> RunningInstances => _runners;

    public async Task<RunnerInstanceInfo?> StartRunnerAsync(
        DeployRunnerCommand command,
        IRunnerBackend backend,
        CancellationToken ct = default,
        NativeRunnerOwnership? ownership = null,
        Action<NativeRunnerOwnership>? beforeNativeCreate = null,
        Action<string>? nativeCreated = null)
    {
        _logger.LogInformation("Starting runner {RunnerName} (instance {InstanceId})",
            command.RunnerName, command.InstanceId);

        var request = new RunnerStartRequest
        {
            DeferRegistration = command.DeferRegistration,
            Ownership = ownership,
            BeforeNativeCreate = beforeNativeCreate,
            NativeCreated = nativeCreated,
            InstanceId = command.InstanceId,
            RunnerName = command.RunnerName,
            Provider = command.Provider,
            EnvironmentVariables = ExpandHostEnvironmentVariables(command.EnvironmentVariables),
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
            ProvisioningMode = command.ProvisioningMode,
            InitSteps = command.InitSteps,
            RegistryUsername = command.RegistryUsername,
            RegistryPassword = command.RegistryPassword
        };

        var info = await backend.StartRunnerAsync(request, ct);

        _runners[command.InstanceId] = new ManagedRunner
        {
            InstanceId = command.InstanceId,
            ProfileId = command.ProfileId,
            RunnerName = info.RunnerName,
            InstanceHandle = info.InstanceHandle,
            Backend = backend,
            StartedAt = DateTime.UtcNow,
            IsEphemeral = command.Ephemeral || command.DeferRegistration || !string.IsNullOrEmpty(command.JitConfig)
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

    /// <summary>Resume observation of an exact existing resource without replaying startup.</summary>
    public void AdoptRunner(ManagedRunner runner)
    {
        if (!_runners.TryAdd(runner.InstanceId, runner))
        {
            var current = _runners[runner.InstanceId];
            if (current.InstanceHandle != runner.InstanceHandle || current.Backend.BackendType != runner.Backend.BackendType)
                throw new InvalidOperationException("A different native resource is already tracked for this instance.");
            return;
        }
        if (runner.Backend is Backends.DockerBackend docker)
            _ = MonitorContainerExitAsync(docker, runner.InstanceId, runner.InstanceHandle, runner.RunnerName);
    }

    public async Task StopRunnerAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_runners.TryGetValue(instanceId, out var runner))
        {
            _logger.LogWarning("Runner instance {InstanceId} not found", instanceId);
            return;
        }

        await ReleaseRunnerAsync(runner, ct);
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
            if (_runners.TryGetValue(instanceId, out var runner)
                && await ReleaseRunnerAsync(runner, CancellationToken.None))
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

                OnRunnerExited?.Invoke(runner, exitCode, reason);
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health check failed for runner {RunnerName} ({InstanceId})", runner.RunnerName, instanceId);
                health = new RunnerHealthStatus { IsRunning = false, Status = "health_check_failed" };
            }

            var cleanupConfirmed = false;
            if (!health.IsRunning && IsDefinitiveExit(health.Status))
            {
                // The runner exited on its own (e.g. an ephemeral/JIT runner finishing its job).
                // Let the backend release what it holds — instance directory, post-exit steps,
                // leftover child processes — otherwise it leaks until the next orphan sweep.
                cleanupConfirmed = await ReleaseExitedRunnerAsync(runner, ct);
            }

            snapshots.Add(new ManagedRunnerHealth
            {
                Runner = runner,
                Health = health,
                CleanupConfirmed = cleanupConfirmed
            });
        }

        return snapshots;
    }

    /// <summary>
    /// Only statuses that prove the runner process/container is gone. Anything else
    /// (e.g. Tart's "stopped" when <c>tart ip</c> fails during boot, "not_found",
    /// "health_check_failed") may be transient, and releasing would destroy a live runner.
    /// </summary>
    internal static bool IsDefinitiveExit(string? status)
        => status is not null
           && (status.StartsWith("exited", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "dead", StringComparison.OrdinalIgnoreCase));

    private async Task<bool> ReleaseExitedRunnerAsync(ManagedRunner runner, CancellationToken ct)
    {
        try
        {
            return await ReleaseRunnerAsync(runner, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release exited runner {RunnerName} ({InstanceId})", runner.RunnerName, runner.InstanceId);
            return false;
        }
    }

    private async Task<bool> ReleaseRunnerAsync(ManagedRunner runner, CancellationToken ct)
    {
        await runner.CleanupLock.WaitAsync(ct);
        try
        {
            if (!_runners.TryGetValue(runner.InstanceId, out var current) || !ReferenceEquals(current, runner))
                return false;

            _logger.LogInformation("Stopping runner {RunnerName}", runner.RunnerName);
            await runner.Backend.StopRunnerAsync(runner.InstanceHandle, ct);
            // Keep ownership when cleanup throws or its outcome is uncertain. A later
            // health check/stop can retry the same native handle without freeing capacity.
            var removed = _runners.TryRemove(new KeyValuePair<string, ManagedRunner>(runner.InstanceId, runner));
            _logger.LogInformation("Runner {RunnerName} stopped", runner.RunnerName);
            return removed;
        }
        finally
        {
            runner.CleanupLock.Release();
        }
    }

    /// <summary>
    /// Expands system environment variable references ($VAR, ${VAR}) and tilde (~)
    /// in env var values using the host's current environment. Shells out to a login
    /// shell so that changes made after the HostWorker started (e.g. xcode-select,
    /// PATH modifications in .zshrc) are picked up without a restart.
    /// </summary>
    private static Dictionary<string, string> ExpandHostEnvironmentVariables(Dictionary<string, string> vars)
    {
        var systemEnv = GetFreshSystemEnvironment();
        var home = systemEnv.GetValueOrDefault("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var result = new Dictionary<string, string>(vars.Count);

        foreach (var kvp in vars)
        {
            var value = kvp.Value;

            // Expand ~ at start of value to home directory
            if (!string.IsNullOrEmpty(home))
            {
                if (value == "~")
                    value = home;
                else if (value.StartsWith("~/"))
                    value = home + value[1..];
            }

            // Expand ${VAR} and $VAR references against fresh system environment
            if (value.Contains('$'))
            {
                value = Regex.Replace(value, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", match =>
                {
                    var name = match.Groups[1].Value;
                    return systemEnv.GetValueOrDefault(name) ?? match.Value;
                });

                value = Regex.Replace(value, @"\$([A-Za-z_][A-Za-z0-9_]*)", match =>
                {
                    var name = match.Groups[1].Value;
                    return systemEnv.GetValueOrDefault(name) ?? match.Value;
                });
            }

            result[kvp.Key] = value;
        }

        return result;
    }

    /// <summary>
    /// Spawns a login shell to capture the current system environment variables,
    /// reflecting any changes made after this process started.
    /// Falls back to the process environment if the shell fails.
    /// </summary>
    private static Dictionary<string, string> GetFreshSystemEnvironment()
    {
        try
        {
            var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = shell,
                Arguments = "-l -c env",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return GetProcessEnvironment();

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            var env = new Dictionary<string, string>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var eqIdx = line.IndexOf('=');
                if (eqIdx > 0)
                    env[line[..eqIdx]] = line[(eqIdx + 1)..].TrimEnd('\r');
            }

            return env.Count > 0 ? env : GetProcessEnvironment();
        }
        catch
        {
            return GetProcessEnvironment();
        }
    }

    private static Dictionary<string, string> GetProcessEnvironment()
    {
        var env = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string val)
                env[key] = val;
        }
        return env;
    }
}

public class ManagedRunner
{
    internal SemaphoreSlim CleanupLock { get; } = new(1, 1);
    public required string InstanceId { get; set; }
    public required string ProfileId { get; set; }
    public required string RunnerName { get; set; }
    public required string InstanceHandle { get; set; }
    public required IRunnerBackend Backend { get; set; }
    public DateTime StartedAt { get; set; }

    /// <summary>Single-use runner (ephemeral or JIT) that exits normally after one job.</summary>
    public bool IsEphemeral { get; set; }
}

public class ManagedRunnerHealth
{
    public required ManagedRunner Runner { get; set; }
    public required RunnerHealthStatus Health { get; set; }

    /// <summary>True only after the backend released this instance successfully.</summary>
    public bool CleanupConfirmed { get; set; }
}
