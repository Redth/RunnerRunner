using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;

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

    public HostRegistrationService(
        IGrainFactory grainFactory,
        ILogger<HostRegistrationService> logger,
        IConfiguration config)
    {
        _grainFactory = grainFactory;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hostId = _config["HostSilo:HostId"] ?? Environment.MachineName;
        var hostName = _config["HostSilo:HostName"] ?? hostId;
        var platform = Enum.TryParse<HostPlatform>(_config["HostSilo:Platform"], true, out var p) ? p : HostPlatform.Linux;
        var architecture = _config["HostSilo:Architecture"]
            ?? System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
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

                if (await IsDockerAvailable())
                    labels["docker"] = "true";

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
                await hostHeartbeatGrain.RecordHeartbeat("local-silo", 0);
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

    private static async Task<bool> IsTartAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("tart", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
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
}
