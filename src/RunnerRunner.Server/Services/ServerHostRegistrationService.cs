using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Registers this server as a host in the Orleans cluster.
/// The server silo can also execute runners (Docker on Linux).
/// </summary>
public class ServerHostRegistrationService : BackgroundService
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<ServerHostRegistrationService> _logger;
    private readonly IConfiguration _config;

    public ServerHostRegistrationService(
        IGrainFactory grainFactory,
        ILogger<ServerHostRegistrationService> logger,
        IConfiguration config)
    {
        _grainFactory = grainFactory;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for silo to be ready
        await Task.Delay(8000, stoppingToken);

        var hostId = _config["HostSilo:HostId"] ?? $"server-{Environment.MachineName}";
        var hostName = _config["HostSilo:HostName"] ?? hostId;

        _logger.LogInformation("Registering server as host: {HostId}", hostId);

        try
        {
            var hostGrain = _grainFactory.GetGrain<IHostGrain>(hostId);
            await hostGrain.Register(hostName, HostPlatform.Linux, 
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), 
                typeof(ServerHostRegistrationService).Assembly.GetName().Version?.ToString() ?? "1.0.0");

            var labels = new Dictionary<string, string>
            {
                ["os"] = "linux",
                ["arch"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
                ["native"] = "true",
                ["docker"] = "true",
                ["role"] = "server"
            };
            await hostGrain.UpdateLabels(labels);

            var scheduler = _grainFactory.GetGrain<ISchedulerGrain>(0);
            await scheduler.RegisterHost(hostId);

            _logger.LogInformation("Server host {HostId} registered with labels: {Labels}",
                hostId, string.Join(", ", labels.Select(kv => $"{kv.Key}={kv.Value}")));

            // Heartbeat loop
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await hostGrain.RecordHeartbeat("local-silo", 0); }
                catch { }
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register server as host");
        }
    }
}
