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
            ? (_configuration["HostSilo:HostId"] ?? Guid.NewGuid().ToString("N")[..12])
            : _configuration["RunnerRunner:AgentId"]!;
        _agentName = string.IsNullOrEmpty(_configuration["RunnerRunner:AgentName"])
            ? (_configuration["HostSilo:HostName"] ?? Environment.MachineName)
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
        _signalR.OnGetHostLogs += HandleGetHostLogs;
        _signalR.OnGetRunnerLogs += HandleGetRunnerLogs;
        _signalR.OnCleanupOrphan += HandleCleanupOrphan;
        _signalR.OnReconnected += RegisterWithServer;

        // Wire up container exit detection — immediately notify server when a runner dies
        _lifecycleManager.OnRunnerExited += (instanceId, exitCode, reason) =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogWarning("Runner {InstanceId} exited (code {ExitCode}): {Reason}",
                        instanceId, exitCode, reason);

                    await _signalR.SendRunnerStopped(new RunnerStoppedEvent
                    {
                        InstanceId = instanceId,
                        Reason = exitCode == 0 ? "completed" : "crashed",
                        ErrorMessage = reason
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to report runner exit for {InstanceId}", instanceId);
                }
            });
        };

        // Initial connect attempt — must not throw out of ExecuteAsync, since
        // BackgroundServiceExceptionBehavior used to default to StopHost. The
        // heartbeat loop below also calls EnsureSignalRConnected, so a failure
        // here just means the first heartbeat retries.
        try
        {
            await EnsureSignalRConnected(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial SignalR connect failed; heartbeat loop will retry");
        }

        // Heartbeat loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await EnsureSignalRConnected(stoppingToken))
                    continue;

                var metrics = _healthReporter.CollectMetrics(_agentId);
                await _signalR.SendHeartbeat(metrics);

                // Send per-runner health updates using actual backend health so exited
                // native/docker/tart processes don't linger as phantom "Running" instances.
                foreach (var snapshot in await _lifecycleManager.CollectRunnerHealthAsync(stoppingToken))
                {
                    if (snapshot.Health.IsRunning)
                    {
                        await _signalR.SendRunnerHealthUpdate(new RunnerHealthUpdateEvent
                        {
                            InstanceId = snapshot.Runner.InstanceId,
                            Status = RunnerInstanceStatus.Running,
                            CheckedAt = DateTime.UtcNow,
                            StatusMessage = string.Equals(snapshot.Health.Status, "running", StringComparison.OrdinalIgnoreCase)
                                ? "Running"
                                : snapshot.Health.Status
                        });
                        continue;
                    }

                    var stopReason = string.Equals(snapshot.Health.Status, "exited:0", StringComparison.OrdinalIgnoreCase)
                        ? "Exited"
                        : "crashed";

                    await _signalR.SendRunnerStopped(new RunnerStoppedEvent
                    {
                        InstanceId = snapshot.Runner.InstanceId,
                        Reason = stopReason,
                        ErrorMessage = stopReason == "crashed"
                            ? $"Runner no longer active on host ({snapshot.Health.Status})"
                            : null
                    });
                }
                // Reconciliation: report actual state to server
                try
                {
                    var report = await CollectReconciliationReport();
                    await _signalR.SendReconciliation(report);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send reconciliation report");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send heartbeat");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task<bool> EnsureSignalRConnected(CancellationToken stoppingToken)
    {
        if (_signalR.IsConnected)
            return true;

        _logger.LogWarning("SignalR connection is {State}; reconnecting before sending host updates", _signalR.State);
        await _signalR.ConnectAsync(stoppingToken);

        if (!_signalR.IsConnected)
        {
            _logger.LogWarning("SignalR connection is still offline after reconnect attempt");
            return false;
        }

        await RegisterWithServer();
        return true;
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
                    InstanceHandle = result.InstanceHandle,
                    Backend = command.Backend
                });

                await _signalR.SendRunnerHealthUpdate(new RunnerHealthUpdateEvent
                {
                    InstanceId = command.InstanceId,
                    Status = RunnerInstanceStatus.Running,
                    CheckedAt = DateTime.UtcNow,
                    StatusMessage = command.ProvisioningMode == "dynamic"
                        ? "Runner deployed, waiting for job"
                        : "Runner started"
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
        var hadErrors = false;

        async Task ReportStatus(string stage, string message, bool isComplete = false, bool success = true)
        {
            await _signalR.SendImageRefreshStatus(new ImageRefreshStatusEvent
            {
                HostId = _agentId,
                Stage = stage,
                Message = message,
                IsComplete = isComplete,
                Success = success
            });
        }

        await ReportStatus("starting", "Starting image refresh...");

        if (command.FilterType is null or ImageType.Docker)
        {
            await ReportStatus("docker", "Loading Docker images...");
            try
            {
                var dockerImages = await _imageManager.ListDockerImagesAsync();
                images.AddRange(dockerImages);
                await ReportStatus("docker", $"Loaded {dockerImages.Count} Docker image(s).");
            }
            catch (Exception ex)
            {
                hadErrors = true;
                _logger.LogWarning(ex, "Failed to load Docker images");
                await ReportStatus("docker", $"Docker image refresh failed: {ex.Message}", success: false);
            }
        }

        if (command.FilterType is null or ImageType.Tart)
        {
            await ReportStatus("tart", "Loading Tart images...");
            try
            {
                var tartImages = await _imageManager.ListTartImagesAsync();
                images.AddRange(tartImages);
                await ReportStatus("tart", $"Loaded {tartImages.Count} Tart image(s).");
            }
            catch (Exception ex)
            {
                hadErrors = true;
                _logger.LogWarning(ex, "Failed to load Tart images");
                await ReportStatus("tart", $"Tart image refresh failed: {ex.Message}", success: false);
            }
        }

        await _signalR.SendImageList(new ImageListEvent
        {
            HostId = _agentId,
            Images = images
        });

        await ReportStatus(
            "complete",
            hadErrors
                ? $"Refresh finished with issues. {images.Count} image(s) were still reported."
                : $"Refresh complete. {images.Count} image(s) reported.",
            isComplete: true,
            success: !hadErrors);
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

            var fullImage = ImageReference.Build(command.RegistryUrl, command.ImageName, command.Tag);

            if (command.ImageType == ImageType.Docker)
            {
                await _imageManager.PullDockerImageAsync(command.ImageName, command.Tag, command.RegistryUrl,
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

    private async Task HandleGetHostLogs(GetHostLogsCommand command)
    {
        _logger.LogInformation("Fetching host logs (tail: {TailLines})", command.TailLines);

        try
        {
            var logs = await GetHostLogTail(command.TailLines);

            await _signalR.SendHostLogs(new HostLogsEvent
            {
                HostId = _agentId,
                Logs = logs
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch host logs");
            await _signalR.SendHostLogs(new HostLogsEvent
            {
                HostId = _agentId,
                Logs = $"Error fetching host logs: {ex.Message}"
            });
        }
    }

    private async Task HandleGetRunnerLogs(GetRunnerLogsCommand command)
    {
        _logger.LogInformation("Fetching logs for runner {Handle}", command.InstanceHandle);
        try
        {
            var logs = "";
            // Try Docker logs first
            if (_dockerBackend is Backends.DockerBackend docker && await docker.IsAvailableAsync())
            {
                try
                {
                    var stream = await docker.GetClient().Containers.GetContainerLogsAsync(
                        command.InstanceHandle,
                        false,
                        new Docker.DotNet.Models.ContainerLogsParameters
                        {
                            ShowStdout = true,
                            ShowStderr = true,
                            Tail = command.TailLines.ToString()
                        });
                    var (stdout, stderr) = await stream.ReadOutputToEndAsync(default);
                    logs = stdout + stderr;
                }
                catch { /* Not a Docker container */ }
            }

            // If no Docker logs, try native backend log files
            if (string.IsNullOrEmpty(logs) && _nativeBackend is Backends.NativeBackend native)
            {
                logs = await GetNativeRunnerLogs(native, command.InstanceHandle, command.TailLines);
            }

            if (string.IsNullOrEmpty(logs))
                logs = "(No logs available for this runner instance)";

            await _signalR.SendRunnerLogs(new RunnerLogsEvent
            {
                HostId = _agentId,
                InstanceHandle = command.InstanceHandle,
                Logs = logs
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch runner logs");
            await _signalR.SendRunnerLogs(new RunnerLogsEvent
            {
                HostId = _agentId,
                InstanceHandle = command.InstanceHandle,
                Logs = $"Error fetching logs: {ex.Message}"
            });
        }
    }

    private async Task<string> GetNativeRunnerLogs(Backends.NativeBackend native, string instanceHandle, int tailLines)
    {
        var tailCount = tailLines > 0 ? tailLines : 100;
        var logLines = new List<string>();

        // 1. Try direct lookup by PID handle (works if same agent session)
        var logFile = native.GetLogFilePath(instanceHandle);
        if (logFile != null && File.Exists(logFile))
            return await ReadTailLines(logFile, tailCount);

        // 2. Search instance directories by runner name (handle may be runner name)
        var basePath = _configuration["Runner:BasePath"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".runnerrunner");
        var instancesDir = Path.Combine(basePath, "instances");

        if (!Directory.Exists(instancesDir))
            return "";

        // Find instance dir matching the handle (could be runner name like "ailoha-macos-056ee04b")
        var matchingDir = Directory.GetDirectories(instancesDir)
            .FirstOrDefault(d => Path.GetFileName(d).Equals(instanceHandle, StringComparison.OrdinalIgnoreCase)
                              || Path.GetFileName(d).Contains(instanceHandle, StringComparison.OrdinalIgnoreCase));

        // If no match by name, search all instance dirs for any
        if (matchingDir == null)
        {
            // Try matching by iterating — the handle might be a partial name
            foreach (var dir in Directory.GetDirectories(instancesDir))
            {
                if (File.Exists(Path.Combine(dir, "runner.log")) ||
                    Directory.Exists(Path.Combine(dir, "_diag")))
                {
                    matchingDir = dir;
                    break;
                }
            }
        }

        if (matchingDir == null)
            return "";

        // 3. Check runner.log (our piped output)
        var runnerLog = Path.Combine(matchingDir, "runner.log");
        if (File.Exists(runnerLog))
            logLines.AddRange((await ReadTailLines(runnerLog, tailCount)).Split('\n'));

        // 4. Check _diag/ directory (runner's own diagnostic logs)
        var diagDir = Path.Combine(matchingDir, "_diag");
        if (Directory.Exists(diagDir))
        {
            var diagFiles = Directory.GetFiles(diagDir, "*.log")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .Take(2); // Latest runner + worker logs

            foreach (var diagFile in diagFiles)
            {
                var fileName = Path.GetFileName(diagFile);
                logLines.Add($"\n--- {fileName} ---");
                logLines.Add(await ReadTailLines(diagFile, tailCount / 2));
            }
        }

        return logLines.Count > 0 ? string.Join("\n", logLines) : "";
    }

    private async Task<string> GetHostLogTail(int tailLines)
    {
        var tailCount = tailLines > 0 ? tailLines : 100;

        var candidatePaths = new List<string>();
        void AddIfSet(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && !candidatePaths.Contains(path))
                candidatePaths.Add(path);
        }

        AddIfSet(_configuration["RunnerRunner:HostLogFile"]);
        AddIfSet(_configuration["HostSilo:LogFilePath"]);
        AddIfSet("/tmp/runnerrunner-hostsilo.log");
        AddIfSet(Path.Combine(Path.GetTempPath(), "runnerrunner-hostsilo.log"));
        AddIfSet(Path.Combine(AppContext.BaseDirectory, "logs", "runnerrunner-hostsilo.log"));

        var logPath = candidatePaths.FirstOrDefault(File.Exists);
        if (logPath != null)
            return await ReadTailLines(logPath, tailCount);

        var details = string.Join("\n", candidatePaths.Select(p => $"  - {p}"));
        return
            $"(No readable host log file was found on this host.\nChecked:\n{details}\n\n" +
            "This host can still provide live runner logs and status, but the HostSilo process log is not currently being written to a known file path.)";
    }

    private static async Task<string> ReadTailLines(string filePath, int lineCount)
    {
        var maxLines = Math.Max(1, lineCount);
        var tail = new Queue<string>(maxLines);

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync() is { } line)
        {
            if (tail.Count == maxLines)
                tail.Dequeue();

            tail.Enqueue(line);
        }

        return string.Join("\n", tail);
    }

    private async Task<ReconciliationReport> CollectReconciliationReport()
    {
        var runners = new List<DiscoveredRunnerInfo>();

        // Docker
        if (_dockerBackend is Backends.DockerBackend docker && await docker.IsAvailableAsync())
        {
            try
            {
                var discovered = await docker.DiscoverManagedContainersAsync();
                runners.AddRange(discovered.Select(d => new DiscoveredRunnerInfo
                {
                    InstanceId = d.InstanceId,
                    RunnerName = d.RunnerName,
                    ContainerId = d.ContainerId,
                    Backend = d.Backend,
                    IsRunning = d.IsRunning,
                    Status = d.Status
                }));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Docker discovery failed during reconciliation");
            }
        }

        // Tart
        if (_tartBackend is Backends.TartBackend tart && await tart.IsAvailableAsync())
        {
            try
            {
                var discovered = await tart.DiscoverManagedVmsAsync();
                runners.AddRange(discovered.Select(d => new DiscoveredRunnerInfo
                {
                    InstanceId = d.InstanceId,
                    RunnerName = d.RunnerName,
                    VmName = d.VmName,
                    Backend = d.Backend,
                    IsRunning = d.IsRunning,
                    Status = d.Status
                }));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tart discovery failed during reconciliation");
            }
        }

        // Native
        if (_nativeBackend is Backends.NativeBackend native)
        {
            try
            {
                var discovered = await native.DiscoverManagedProcessesAsync();
                runners.AddRange(discovered.Select(d => new DiscoveredRunnerInfo
                {
                    InstanceId = d.InstanceId,
                    RunnerName = d.RunnerName,
                    ProcessId = d.ProcessId,
                    InstanceDir = d.InstanceDir,
                    Backend = d.Backend,
                    IsRunning = d.IsRunning,
                    Status = d.Status
                }));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Native discovery failed during reconciliation");
            }
        }

        return new ReconciliationReport
        {
            HostId = _agentId,
            Runners = runners
        };
    }

    private async Task HandleCleanupOrphan(CleanupOrphanCommand command)
    {
        _logger.LogInformation("Cleaning up orphaned {Backend} resource: container={Container}, vm={Vm}, pid={Pid}",
            command.Backend, command.ContainerId, command.VmName, command.ProcessId);
        try
        {
            switch (command.Backend)
            {
                case ExecutionBackend.Docker when _dockerBackend is Backends.DockerBackend docker && command.ContainerId != null:
                    await docker.CleanupOrphanContainerAsync(command.ContainerId);
                    break;
                case ExecutionBackend.Tart when _tartBackend is Backends.TartBackend tart && command.VmName != null:
                    await tart.StopRunnerAsync(command.VmName);
                    break;
                case ExecutionBackend.Native when _nativeBackend is Backends.NativeBackend native && command.ProcessId.HasValue:
                    await native.CleanupOrphanProcessAsync(command.ProcessId.Value, command.InstanceDir);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup orphaned resource");
        }
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
            Capabilities = await DetectCapabilitiesAsync()
        });
        _logger.LogInformation("Agent registered with server");

        // Discover and report any RunnerRunner-managed containers already running
        await DiscoverAndReportRunners();
    }

    private async Task DiscoverAndReportRunners()
    {
        try
        {
            if (_dockerBackend is Backends.DockerBackend docker && await docker.IsAvailableAsync())
            {
                var discovered = await docker.DiscoverManagedContainersAsync();
                if (discovered.Count > 0)
                {
                    _logger.LogInformation("Discovered {Count} managed containers on this host", discovered.Count);
                    await _signalR.SendRunnerDiscovery(new RunnerDiscoveryEvent
                    {
                        HostId = _agentId,
                        Runners = discovered.Select(d => new DiscoveredRunnerInfo
                        {
                            InstanceId = d.InstanceId,
                            RunnerName = d.RunnerName,
                            ContainerId = d.ContainerId,
                            IsRunning = d.IsRunning,
                            Status = d.Status
                        }).ToList()
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover managed containers");
        }
    }

    private static HostPlatform GetCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return HostPlatform.MacOS;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return HostPlatform.Windows;
        return HostPlatform.Linux;
    }

    private async Task<List<string>> DetectCapabilitiesAsync()
    {
        var caps = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            caps.Add("apple-silicon"); // TODO: detect actual architecture
            if (_tartBackend is Backends.TartBackend tart && await tart.IsAvailableAsync())
                caps.Add("tart");
        }

        if (_dockerBackend is Backends.DockerBackend docker && await docker.IsAvailableAsync())
            caps.Add("docker");

        caps.Add("native"); // All hosts support native process execution

        return caps;
    }
}
