using System.Runtime.InteropServices;
using RunnerRunner.Agent.Services;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent;

/// <summary>
/// Main hosted service for the RunnerRunner agent.
/// Connects to the server, reports host capabilities, and handles runner lifecycle commands.
/// </summary>
public class AgentService : BackgroundService
{
    private readonly ILogger<AgentService> _logger;
    private readonly IConfiguration _configuration;
    private readonly SignalRConnection _signalR;
    private readonly RunnerLifecycleManager _lifecycleManager;
    private readonly HealthReporter _healthReporter;

    private string _agentId = "";
    private string _agentName = "";

    public AgentService(
        ILogger<AgentService> logger,
        IConfiguration configuration,
        SignalRConnection signalR,
        RunnerLifecycleManager lifecycleManager,
        HealthReporter healthReporter)
    {
        _logger = logger;
        _configuration = configuration;
        _signalR = signalR;
        _lifecycleManager = lifecycleManager;
        _healthReporter = healthReporter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _agentId = _configuration["RunnerRunner:AgentId"] ?? Guid.NewGuid().ToString("N")[..12];
        _agentName = _configuration["RunnerRunner:AgentName"] ?? Environment.MachineName;

        _logger.LogInformation("RunnerRunner Agent starting: {AgentName} ({AgentId})", _agentName, _agentId);

        // Wire up command handlers
        _signalR.OnDeployRunner += HandleDeployRunner;
        _signalR.OnStopRunner += HandleStopRunner;

        // Connect to server
        await _signalR.ConnectAsync(stoppingToken);

        // Register with server
        await _signalR.SendAgentConnected(new AgentInfo
        {
            AgentId = _agentId,
            Name = _agentName,
            Platform = GetCurrentPlatform(),
            OsVersion = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            AgentVersion = typeof(AgentService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            Capabilities = DetectCapabilities()
        });

        _logger.LogInformation("Agent registered with server");

        // Heartbeat loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var metrics = _healthReporter.CollectMetrics(_agentId);
                await _signalR.SendHeartbeat(metrics);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send heartbeat");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task HandleDeployRunner(DeployRunnerCommand command)
    {
        try
        {
            // TODO: Select the appropriate backend based on command.Backend
            _logger.LogInformation("Received deploy command for {RunnerName}", command.RunnerName);

            // For now just acknowledge
            await _signalR.SendRunnerStarted(new RunnerStartedEvent
            {
                InstanceId = command.InstanceId,
                RunnerName = command.RunnerName,
                InstanceHandle = "pending"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy runner {RunnerName}", command.RunnerName);
            await _signalR.SendRunnerStopped(new RunnerStoppedEvent
            {
                InstanceId = command.InstanceId,
                Reason = "DeployFailed",
                ErrorMessage = ex.Message
            });
        }
    }

    private async Task HandleStopRunner(StopRunnerCommand command)
    {
        try
        {
            await _lifecycleManager.StopRunnerAsync(command.InstanceId);
            await _signalR.SendRunnerStopped(new RunnerStoppedEvent
            {
                InstanceId = command.InstanceId,
                Reason = "StopRequested"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop runner {InstanceId}", command.InstanceId);
        }
    }

    private static HostPlatform GetCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return HostPlatform.MacOS;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return HostPlatform.Windows;
        return HostPlatform.Linux;
    }

    private List<string> DetectCapabilities()
    {
        var caps = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            caps.Add("apple-silicon"); // TODO: detect actual architecture
            // Check for tart
            if (File.Exists("/opt/homebrew/bin/tart") || File.Exists("/usr/local/bin/tart"))
                caps.Add("tart");
        }

        // Check for docker
        if (File.Exists("/usr/bin/docker") || File.Exists("/usr/local/bin/docker")
            || File.Exists("/opt/homebrew/bin/docker"))
            caps.Add("docker");

        caps.Add("native"); // All hosts support native process execution

        return caps;
    }
}
