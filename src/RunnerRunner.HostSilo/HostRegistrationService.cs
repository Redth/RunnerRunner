using RunnerRunner.Core.Models;
using RunnerRunner.Agent.Services;
using RunnerRunner.Server.Grains.Interfaces;
using Docker.DotNet;

namespace RunnerRunner.HostSilo;

/// <summary>
/// On startup, activates this host's HostGrain and registers it with the cluster.
/// Periodically sends heartbeats to keep the host "online".
/// </summary>
public class HostRegistrationService : BackgroundService
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<HostRegistrationService> _logger;
    private readonly IConfiguration _config;
    private readonly RunnerLifecycleManager _lifecycleManager;

    public HostRegistrationService(
        IGrainFactory grainFactory,
        ILogger<HostRegistrationService> logger,
        IConfiguration config,
        RunnerLifecycleManager lifecycleManager)
    {
        _grainFactory = grainFactory;
        _logger = logger;
        _config = config;
        _lifecycleManager = lifecycleManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var identity = HostSiloIdentityResolver.Resolve(_config);
        var hostId = identity.HostId;
        var hostName = identity.HostName;
        var platform = identity.Platform;
        var architecture = identity.Architecture;
        var agentVersion = typeof(HostRegistrationService).Assembly.GetName().Version?.ToString() ?? "1.0.0";

        // Wait a moment for the silo to fully start
        await Task.Delay(5000, stoppingToken);

        _logger.LogInformation("Registering host grain: {HostId} ({HostName}, {Platform} {Architecture})",
            hostId, hostName, platform, architecture);

        // Retry registration with backoff — the cluster may still be reorganizing after deploy
        var registered = false;
        for (var attempt = 1; attempt <= 10 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                var hostGrain = _grainFactory.GetGrain<IHostGrain>(hostId);
                await hostGrain.Register(hostName, platform, architecture, agentVersion);

                var labels = new Dictionary<string, string>
                {
                    ["os"] = platform.ToString().ToLowerInvariant(),
                    ["arch"] = architecture.ToLowerInvariant(),
                    ["native"] = "true"
                };

                // Detect Docker availability
                if (await IsDockerAvailable())
                {
                    labels["docker"] = "true";
                    var dockerOs = await DetectDockerOsAsync();
                    if (!string.IsNullOrWhiteSpace(dockerOs))
                    {
                        labels["docker_os"] = dockerOs;
                        if (platform == HostPlatform.Windows && string.Equals(dockerOs, "windows", StringComparison.OrdinalIgnoreCase))
                            labels["windows_containers"] = "true";
                    }
                }

                if (platform == HostPlatform.MacOS && await IsTartAvailable())
                    labels["tart"] = "true";

                await hostGrain.UpdateLabels(labels);

                var scheduler = _grainFactory.GetGrain<ISchedulerGrain>(0);
                await scheduler.RegisterHost(hostId);

                _logger.LogInformation("Host {HostId} registered successfully with labels: {Labels}",
                    hostId, string.Join(", ", labels.Select(kv => $"{kv.Key}={kv.Value}")));

                registered = true;
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Registration attempt {Attempt}/10 failed for host {HostId}, retrying...",
                    attempt, hostId);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 5), stoppingToken);
            }
        }

        if (!registered)
        {
            _logger.LogError("Failed to register host {HostId} after 10 attempts", hostId);
            return;
        }

        // Heartbeat loop
        var hostHeartbeatGrain = _grainFactory.GetGrain<IHostGrain>(hostId);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await hostHeartbeatGrain.RecordHeartbeat("orleans-stream", _lifecycleManager.RunningInstances.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send heartbeat for host {HostId}", hostId);
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private static async Task<bool> IsDockerAvailable()
    {
        if (File.Exists("/var/run/docker.sock"))
            return true;

        try
        {
            using var client = new DockerClientConfiguration(ResolveDockerEndpoint()).CreateClient();
            await client.System.PingAsync();
            return true;
        }
        catch
        {
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("docker", "version --format '{{.Server.Version}}'")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<string?> DetectDockerOsAsync()
    {
        try
        {
            var endpoint = ResolveDockerEndpoint();
            using var client = new DockerClientConfiguration(endpoint).CreateClient();
            var info = await client.System.GetSystemInfoAsync();
            if (!string.IsNullOrWhiteSpace(info.OSType))
                return info.OSType.ToLowerInvariant();
        }
        catch
        {
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("docker", "info --format '{{.OSType}}'")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
                return null;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                return output.Trim().ToLowerInvariant();
        }
        catch
        {
        }

        return null;
    }

    private static async Task<bool> IsTartAvailable()
    {
        var tartPath = ResolveToolPath("tart", "/opt/homebrew/bin/tart", "/usr/local/bin/tart");
        if (tartPath != null)
            return true;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(tartPath ?? "tart", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = "/"
            };
            // Add homebrew to PATH for macOS
            var path = psi.Environment.TryGetValue("PATH", out var existing) ? existing ?? "" : "";
            if (!path.Contains("/opt/homebrew"))
                psi.Environment["PATH"] = $"/opt/homebrew/bin:/opt/homebrew/sbin:{path}";

            var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch { return false; }
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
            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
                return candidate + ".exe";
        }

        return null;
    }

    private static Uri ResolveDockerEndpoint()
    {
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (!string.IsNullOrWhiteSpace(dockerHost) && Uri.TryCreate(dockerHost, UriKind.Absolute, out var configuredEndpoint))
            return configuredEndpoint;

        return OperatingSystem.IsWindows()
            ? new Uri("npipe://./pipe/docker_engine")
            : new Uri("unix:///var/run/docker.sock");
    }
}
