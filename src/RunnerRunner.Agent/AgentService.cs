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
    private readonly ImageManager _imageManager;

    private string _agentId = "";
    private string _agentName = "";

    public AgentService(
        ILogger<AgentService> logger,
        IConfiguration configuration,
        SignalRConnection signalR,
        RunnerLifecycleManager lifecycleManager,
        HealthReporter healthReporter,
        ImageManager imageManager,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _configuration = configuration;
        _signalR = signalR;
        _lifecycleManager = lifecycleManager;
        _healthReporter = healthReporter;
        _imageManager = imageManager;

        _dockerBackend = new DockerBackend(loggerFactory.CreateLogger<DockerBackend>());
        _tartBackend = new TartBackend(loggerFactory.CreateLogger<TartBackend>());
        _nativeBackend = new NativeBackend(loggerFactory.CreateLogger<NativeBackend>());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _agentId = string.IsNullOrEmpty(_configuration["RunnerRunner:AgentId"])
            ? Guid.NewGuid().ToString("N")[..12]
            : _configuration["RunnerRunner:AgentId"]!;
        _agentName = string.IsNullOrEmpty(_configuration["RunnerRunner:AgentName"])
            ? Environment.MachineName
            : _configuration["RunnerRunner:AgentName"]!;

        _logger.LogInformation("RunnerRunner Agent starting: {AgentName} ({AgentId})", _agentName, _agentId);

        // Wire up command handlers
        _signalR.OnDeployRunner += HandleDeployRunner;
        _signalR.OnStopRunner += HandleStopRunner;
        _signalR.OnListImages += HandleListImages;
        _signalR.OnPullImage += HandlePullImage;
        _signalR.OnDeleteImage += HandleDeleteImage;
        _signalR.OnLoginRegistry += HandleLoginRegistry;
        _signalR.OnGetHostEnvironment += HandleGetHostEnvironment;
        _signalR.OnReconnected += RegisterWithServer;

        // Connect to server
        await _signalR.ConnectAsync(stoppingToken);

        // Register with server
        await RegisterWithServer();

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

    private async Task HandleListImages(ListImagesCommand command)
    {
        _logger.LogInformation("Listing images (filter: {Filter})", command.FilterType);
        var images = new List<AgentImageInfo>();

        if (command.FilterType is null or ImageType.Docker)
            images.AddRange(await _imageManager.ListDockerImagesAsync());

        if (command.FilterType is null or ImageType.Tart)
            images.AddRange(await _imageManager.ListTartImagesAsync());

        await _signalR.SendImageList(new ImageListEvent
        {
            HostId = _agentId,
            Images = images
        });
    }

    private async Task HandlePullImage(PullImageCommand command)
    {
        _logger.LogInformation("Pulling image {Image}:{Tag} (type: {Type})",
            command.ImageName, command.Tag, command.ImageType);

        try
        {
            // Login to registry if credentials provided
            if (!string.IsNullOrEmpty(command.Username))
            {
                await _imageManager.LoginDockerRegistryAsync(
                    command.RegistryUrl ?? "", command.Username, command.Password);
            }

            var fullImage = string.IsNullOrEmpty(command.RegistryUrl)
                ? $"{command.ImageName}:{command.Tag}"
                : $"{command.RegistryUrl}/{command.ImageName}:{command.Tag}";

            if (command.ImageType == ImageType.Docker)
            {
                await _imageManager.PullDockerImageAsync(command.ImageName, command.Tag,
                    async progress =>
                    {
                        progress.HostId = _agentId;
                        await _signalR.SendImagePullProgress(progress);
                    });
            }
            else if (command.ImageType == ImageType.Tart)
            {
                await _imageManager.PullTartImageAsync(fullImage,
                    async progress =>
                    {
                        progress.HostId = _agentId;
                        await _signalR.SendImagePullProgress(progress);
                    });
            }

            await _signalR.SendImagePullComplete(new ImagePullCompleteEvent
            {
                HostId = _agentId,
                ImageType = command.ImageType,
                ImageName = fullImage,
                Success = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pull image {Image}", command.ImageName);
            await _signalR.SendImagePullComplete(new ImagePullCompleteEvent
            {
                HostId = _agentId,
                ImageType = command.ImageType,
                ImageName = command.ImageName,
                Success = false,
                Error = ex.Message
            });
        }
    }

    private async Task HandleDeleteImage(DeleteImageCommand command)
    {
        _logger.LogInformation("Deleting image {Image} (type: {Type})", command.ImageName, command.ImageType);

        try
        {
            if (command.ImageType == ImageType.Docker)
                await _imageManager.DeleteDockerImageAsync(command.ImageId);
            else if (command.ImageType == ImageType.Tart)
                await _imageManager.DeleteTartImageAsync(command.ImageName);

            await _signalR.SendImageDeleted(new ImageDeletedEvent
            {
                HostId = _agentId,
                ImageType = command.ImageType,
                ImageId = command.ImageId,
                Success = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete image {Image}", command.ImageName);
            await _signalR.SendImageDeleted(new ImageDeletedEvent
            {
                HostId = _agentId,
                ImageType = command.ImageType,
                ImageId = command.ImageId,
                Success = false,
                Error = ex.Message
            });
        }
    }

    private async Task HandleLoginRegistry(LoginRegistryCommand command)
    {
        try
        {
            await _imageManager.LoginDockerRegistryAsync(
                command.RegistryUrl, command.Username, command.Password);
            _logger.LogInformation("Logged in to registry {Registry}", command.RegistryUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to login to registry {Registry}", command.RegistryUrl);
        }
    }

    private async Task HandleGetHostEnvironment()
    {
        _logger.LogInformation("Reporting host environment variables");

        var envVars = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                envVars[key] = value;
        }

        await _signalR.SendHostEnvironment(new HostEnvironmentEvent
        {
            HostId = _agentId,
            EnvironmentVariables = envVars
        });
    }

    private async Task RegisterWithServer()
    {
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
