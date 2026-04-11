using System.Runtime.InteropServices;
using RunnerRunner.Agent.Backends;
using RunnerRunner.Agent.Services;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Interfaces;
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

    private readonly IRunnerBackend _dockerBackend;
    private readonly IRunnerBackend _tartBackend;
    private readonly IRunnerBackend _nativeBackend;

    private string _agentId = "";
    private string _agentName = "";

    public AgentService(
        ILogger<AgentService> logger,
        IConfiguration configuration,
        SignalRConnection signalR,
        RunnerLifecycleManager lifecycleManager,
        HealthReporter healthReporter,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _configuration = configuration;
        _signalR = signalR;
        _lifecycleManager = lifecycleManager;
        _healthReporter = healthReporter;

        _dockerBackend = new DockerBackend(loggerFactory.CreateLogger<DockerBackend>());
        _tartBackend = new TartBackend(loggerFactory.CreateLogger<TartBackend>());
        _nativeBackend = new NativeBackend(loggerFactory.CreateLogger<NativeBackend>());
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
            _logger.LogInformation("Received deploy command for {RunnerName} (backend: {Backend})",
                command.RunnerName, command.Backend);

            // Select the appropriate backend
            IRunnerBackend? backend = command.Backend switch
            {
                ExecutionBackend.Docker => _dockerBackend,
                ExecutionBackend.Tart => _tartBackend,
                ExecutionBackend.Native => _nativeBackend,
                _ => null
            };

            if (backend == null)
            {
                _logger.LogError("No backend available for {Backend}", command.Backend);
                await _signalR.SendRunnerStopped(new RunnerStoppedEvent
                {
                    InstanceId = command.InstanceId,
                    Reason = "DeployFailed",
                    ErrorMessage = $"Backend {command.Backend} not available on this agent"
                });
                return;
            }

            // Check if backend is available on this host
            if (!await backend.IsAvailableAsync())
            {
                _logger.LogError("Backend {Backend} is not available on this host", command.Backend);
                await _signalR.SendRunnerStopped(new RunnerStoppedEvent
                {
                    InstanceId = command.InstanceId,
                    Reason = "DeployFailed",
                    ErrorMessage = $"Backend {command.Backend} not available on this host"
                });
                return;
            }

            var result = await _lifecycleManager.StartRunnerAsync(command, backend);

            if (result != null)
            {
                _logger.LogInformation("Runner {RunnerName} started with handle {Handle}",
                    result.RunnerName, result.InstanceHandle);

                await _signalR.SendRunnerStarted(new RunnerStartedEvent
                {
                    InstanceId = command.InstanceId,
                    RunnerName = result.RunnerName,
                    InstanceHandle = result.InstanceHandle
                });
            }
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
