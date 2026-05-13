using Orleans.Streams;
using RunnerRunner.Agent.Backends;
using RunnerRunner.Agent.Services;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Grains.State;
using RunnerRunner.Server.Services;

namespace RunnerRunner.HostSilo;

public sealed class HostCommandService : BackgroundService
{
    private readonly IClusterClient _client;
    private readonly IGrainFactory _grainFactory;
    private readonly IConfiguration _configuration;
    private readonly RunnerLifecycleManager _lifecycleManager;
    private readonly ImageManager _imageManager;
    private readonly ILogger<HostCommandService> _logger;
    private readonly IRunnerBackend _dockerBackend;
    private readonly IRunnerBackend _tartBackend;
    private readonly IRunnerBackend _nativeBackend;
    private readonly HostSiloIdentity _identity;
    private StreamSubscriptionHandle<HostCommandEnvelope>? _subscription;

    public HostCommandService(
        IClusterClient client,
        IGrainFactory grainFactory,
        IConfiguration configuration,
        RunnerLifecycleManager lifecycleManager,
        ImageManager imageManager,
        ILogger<HostCommandService> logger,
        ILoggerFactory loggerFactory)
    {
        _client = client;
        _grainFactory = grainFactory;
        _configuration = configuration;
        _lifecycleManager = lifecycleManager;
        _imageManager = imageManager;
        _logger = logger;
        _identity = HostSiloIdentityResolver.Resolve(configuration);

        _dockerBackend = new DockerBackend(loggerFactory.CreateLogger<DockerBackend>());
        _tartBackend = new TartBackend(loggerFactory.CreateLogger<TartBackend>());
        _nativeBackend = new NativeBackend(loggerFactory.CreateLogger<NativeBackend>());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting HostSilo command listener for host {HostId}", _identity.HostId);

        _lifecycleManager.OnRunnerExited += OnRunnerExited;

        await SubscribeAsync(stoppingToken);
        await RunHealthLoopAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _lifecycleManager.OnRunnerExited -= OnRunnerExited;

        if (_subscription != null)
            await _subscription.UnsubscribeAsync();

        await base.StopAsync(cancellationToken);
    }

    private async Task SubscribeAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 30 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);

                var streamProvider = _client.GetStreamProvider(OrleansHostCommandDispatcher.StreamProviderName);
                var stream = streamProvider.GetStream<HostCommandEnvelope>(
                    StreamId.Create(OrleansHostCommandDispatcher.StreamNamespace, _identity.HostId));

                _subscription = await stream.SubscribeAsync(OnCommandAsync);

                _logger.LogInformation("Subscribed to HostSilo command stream for host {HostId}", _identity.HostId);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "HostSilo command stream subscription attempt {Attempt}/30 failed for host {HostId}",
                    attempt,
                    _identity.HostId);
            }
        }

        throw new InvalidOperationException($"HostSilo command stream subscription failed for host '{_identity.HostId}'.");
    }

    private async Task OnCommandAsync(HostCommandEnvelope envelope, StreamSequenceToken? token)
    {
        switch (envelope.Kind)
        {
            case HostCommandKind.DeployRunner when envelope.DeployRunner != null:
                await HandleDeployRunnerAsync(envelope.DeployRunner);
                break;
            case HostCommandKind.StopRunner when envelope.StopRunner != null:
                await HandleStopRunnerAsync(envelope.StopRunner);
                break;
            case HostCommandKind.CleanupOrphan when envelope.CleanupOrphan != null:
                await HandleCleanupOrphanAsync(envelope.CleanupOrphan);
                break;
            case HostCommandKind.ListImages when envelope.ListImages != null:
                await HandleListImagesAsync(envelope.ListImages);
                break;
            case HostCommandKind.PullImage when envelope.PullImage != null:
                await HandlePullImageAsync(envelope.PullImage);
                break;
            case HostCommandKind.DeleteImage when envelope.DeleteImage != null:
                await HandleDeleteImageAsync(envelope.DeleteImage);
                break;
            case HostCommandKind.GetHostLogs when envelope.GetHostLogs != null:
                await HandleGetHostLogsAsync(envelope.GetHostLogs);
                break;
            case HostCommandKind.GetRunnerLogs when envelope.GetRunnerLogs != null:
                await HandleGetRunnerLogsAsync(envelope.GetRunnerLogs);
                break;
            default:
                _logger.LogWarning("Ignoring malformed HostSilo command envelope with kind {Kind}", envelope.Kind);
                break;
        }
    }

    private async Task HandleDeployRunnerAsync(DeployRunnerCommand command)
    {
        var runnerGrain = _grainFactory.GetGrain<IRunnerInstanceGrain>(command.InstanceId);

        try
        {
            _logger.LogInformation(
                "Deploying runner {RunnerName} on HostSilo {HostId} with backend {Backend}",
                command.RunnerName,
                _identity.HostId,
                command.Backend);

            var backend = SelectBackend(command.Backend);
            if (!await backend.IsAvailableAsync())
            {
                await runnerGrain.MarkFailed($"Backend {command.Backend} is not available on host {_identity.HostName}");
                return;
            }

            await runnerGrain.MarkStarting("Deploy command received by HostSilo");
            var result = await _lifecycleManager.StartRunnerAsync(command, backend);
            if (result == null)
            {
                await runnerGrain.MarkFailed("Runner backend did not return a started runner handle");
                return;
            }

            await MarkRunnerStartedAsync(runnerGrain, command, result);

            _logger.LogInformation(
                "Runner {RunnerName} started on HostSilo {HostId} with handle {Handle}",
                result.RunnerName,
                _identity.HostId,
                result.InstanceHandle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy runner {RunnerName} on HostSilo {HostId}", command.RunnerName, _identity.HostId);
            await runnerGrain.MarkFailed(ex.Message);
        }
    }

    private async Task MarkRunnerStartedAsync(
        IRunnerInstanceGrain runnerGrain,
        DeployRunnerCommand command,
        RunnerInstanceInfo result)
    {
        var statusMessage = command.ProvisioningMode == "dynamic"
            ? "Runner deployed, waiting for job"
            : "Runner started";

        switch (command.Backend)
        {
            case ExecutionBackend.Docker:
                await runnerGrain.MarkRunning(containerId: result.InstanceHandle, statusMessage: statusMessage);
                break;
            case ExecutionBackend.Tart:
                await runnerGrain.MarkRunning(vmName: result.InstanceHandle, statusMessage: statusMessage);
                break;
            case ExecutionBackend.Native when int.TryParse(result.InstanceHandle, out var processId):
                await runnerGrain.MarkRunning(processId: processId, statusMessage: statusMessage);
                break;
            case ExecutionBackend.Native:
                await runnerGrain.MarkRunning(statusMessage: statusMessage);
                break;
            default:
                await runnerGrain.MarkRunning(containerId: result.InstanceHandle, statusMessage: statusMessage);
                break;
        }
    }

    private async Task HandleStopRunnerAsync(StopRunnerCommand command)
    {
        var runnerGrain = _grainFactory.GetGrain<IRunnerInstanceGrain>(command.InstanceId);
        var state = await runnerGrain.GetState();

        try
        {
            await runnerGrain.MarkStopping();

            if (_lifecycleManager.RunningInstances.ContainsKey(command.InstanceId))
            {
                await _lifecycleManager.StopRunnerAsync(command.InstanceId);
            }
            else
            {
                var instanceHandle = command.InstanceHandle ?? ResolveInstanceHandle(state);
                if (string.IsNullOrWhiteSpace(instanceHandle))
                {
                    _logger.LogWarning("No local runner handle found for stop command {InstanceId}", command.InstanceId);
                }
                else
                {
                    await SelectBackend(state.Backend).StopRunnerAsync(instanceHandle);
                }
            }

            await runnerGrain.MarkStopped();
            _logger.LogInformation("Stopped runner instance {InstanceId} on HostSilo {HostId}", command.InstanceId, _identity.HostId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop runner instance {InstanceId} on HostSilo {HostId}", command.InstanceId, _identity.HostId);
            await runnerGrain.MarkFailed($"Stop failed: {ex.Message}");
        }
    }

    private async Task HandleCleanupOrphanAsync(CleanupOrphanCommand command)
    {
        try
        {
            _logger.LogInformation(
                "Cleaning orphaned {Backend} runner resource on HostSilo {HostId}",
                command.Backend,
                _identity.HostId);

            switch (command.Backend)
            {
                case ExecutionBackend.Docker when _dockerBackend is DockerBackend docker && !string.IsNullOrWhiteSpace(command.ContainerId):
                    await docker.CleanupOrphanContainerAsync(command.ContainerId);
                    break;
                case ExecutionBackend.Tart when _tartBackend is TartBackend tart && !string.IsNullOrWhiteSpace(command.VmName):
                    await tart.StopRunnerAsync(command.VmName);
                    break;
                case ExecutionBackend.Native when _nativeBackend is NativeBackend native && command.ProcessId.HasValue:
                    await native.CleanupOrphanProcessAsync(command.ProcessId.Value, command.InstanceDir);
                    break;
                default:
                    _logger.LogWarning("Cleanup command for {Backend} did not include a usable resource handle", command.Backend);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clean orphaned runner resource on HostSilo {HostId}", _identity.HostId);
        }
    }

    private async Task HandleListImagesAsync(ListImagesCommand command)
    {
        try
        {
            await PublishAsync(OrleansHostCommandDispatcher.ImageRefreshStatusStreamNamespace, new ImageRefreshStatusEvent
            {
                HostId = _identity.HostId,
                Stage = "refresh",
                Message = "Listing host images...",
                Success = true
            });

            var images = await ListImagesAsync(command.FilterType);
            await PublishAsync(OrleansHostCommandDispatcher.ImageListStreamNamespace, new ImageListEvent
            {
                HostId = _identity.HostId,
                Images = images
            });

            await PublishAsync(OrleansHostCommandDispatcher.ImageRefreshStatusStreamNamespace, new ImageRefreshStatusEvent
            {
                HostId = _identity.HostId,
                Stage = "complete",
                Message = $"Found {images.Count} images.",
                IsComplete = true,
                Success = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list images on HostSilo {HostId}", _identity.HostId);
            await PublishAsync(OrleansHostCommandDispatcher.ImageRefreshStatusStreamNamespace, new ImageRefreshStatusEvent
            {
                HostId = _identity.HostId,
                Stage = "failed",
                Message = ex.Message,
                IsComplete = true,
                Success = false
            });
        }
    }

    private async Task HandlePullImageAsync(PullImageCommand command)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(command.RegistryUrl))
                await _imageManager.LoginDockerRegistryAsync(command.RegistryUrl, command.Username, command.Password);

            if (command.ImageType == ImageType.Docker)
            {
                await _imageManager.PullDockerImageAsync(
                    command.ImageName,
                    command.Tag,
                    command.RegistryUrl,
                    evt => PublishAsync(OrleansHostCommandDispatcher.ImagePullProgressStreamNamespace, WithHost(evt, _identity.HostId)));
            }
            else
            {
                await _imageManager.PullTartImageAsync(
                    command.ImageName,
                    evt => PublishAsync(OrleansHostCommandDispatcher.ImagePullProgressStreamNamespace, WithHost(evt, _identity.HostId)));
            }

            await PublishAsync(OrleansHostCommandDispatcher.ImagePullCompleteStreamNamespace, new ImagePullCompleteEvent
            {
                HostId = _identity.HostId,
                ImageType = command.ImageType,
                ImageName = command.ImageName,
                Success = true
            });

            await HandleListImagesAsync(new ListImagesCommand { FilterType = command.ImageType });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pull {ImageType} image {ImageName} on HostSilo {HostId}", command.ImageType, command.ImageName, _identity.HostId);
            await PublishAsync(OrleansHostCommandDispatcher.ImagePullCompleteStreamNamespace, new ImagePullCompleteEvent
            {
                HostId = _identity.HostId,
                ImageType = command.ImageType,
                ImageName = command.ImageName,
                Success = false,
                Error = ex.Message
            });
        }

        static ImagePullProgressEvent WithHost(ImagePullProgressEvent evt, string hostId)
        {
            evt.HostId = hostId;
            return evt;
        }
    }

    private async Task HandleDeleteImageAsync(DeleteImageCommand command)
    {
        try
        {
            if (command.ImageType == ImageType.Docker)
                await _imageManager.DeleteDockerImageAsync(command.ImageId);
            else
                await _imageManager.DeleteTartImageAsync(command.ImageName);

            await PublishAsync(OrleansHostCommandDispatcher.ImageDeletedStreamNamespace, new ImageDeletedEvent
            {
                HostId = _identity.HostId,
                ImageType = command.ImageType,
                ImageId = command.ImageId,
                Success = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete {ImageType} image {ImageId} on HostSilo {HostId}", command.ImageType, command.ImageId, _identity.HostId);
            await PublishAsync(OrleansHostCommandDispatcher.ImageDeletedStreamNamespace, new ImageDeletedEvent
            {
                HostId = _identity.HostId,
                ImageType = command.ImageType,
                ImageId = command.ImageId,
                Success = false,
                Error = ex.Message
            });
        }
    }

    private async Task HandleGetHostLogsAsync(GetHostLogsCommand command)
    {
        try
        {
            await PublishAsync(OrleansHostCommandDispatcher.HostLogsStreamNamespace, new HostLogsEvent
            {
                HostId = _identity.HostId,
                Logs = await GetHostLogTailAsync(command.TailLines)
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch host logs on HostSilo {HostId}", _identity.HostId);
            await PublishAsync(OrleansHostCommandDispatcher.HostLogsStreamNamespace, new HostLogsEvent
            {
                HostId = _identity.HostId,
                Logs = $"Error fetching host logs: {ex.Message}"
            });
        }
    }

    private async Task HandleGetRunnerLogsAsync(GetRunnerLogsCommand command)
    {
        try
        {
            var logs = "";

            if (_dockerBackend is DockerBackend docker && await docker.IsAvailableAsync())
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
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Runner handle {Handle} did not resolve to Docker logs", command.InstanceHandle);
                }
            }

            if (string.IsNullOrEmpty(logs) && _nativeBackend is NativeBackend native)
                logs = await GetNativeRunnerLogsAsync(native, command.InstanceHandle, command.TailLines);

            await PublishAsync(OrleansHostCommandDispatcher.RunnerLogsStreamNamespace, new RunnerLogsEvent
            {
                HostId = _identity.HostId,
                InstanceHandle = command.InstanceHandle,
                Logs = string.IsNullOrEmpty(logs) ? "(No logs available for this runner instance)" : logs
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch runner logs on HostSilo {HostId}", _identity.HostId);
            await PublishAsync(OrleansHostCommandDispatcher.RunnerLogsStreamNamespace, new RunnerLogsEvent
            {
                HostId = _identity.HostId,
                InstanceHandle = command.InstanceHandle,
                Logs = $"Error fetching logs: {ex.Message}"
            });
        }
    }

    private async Task<string> GetNativeRunnerLogsAsync(NativeBackend native, string instanceHandle, int tailLines)
    {
        var tailCount = tailLines > 0 ? tailLines : 100;
        var logLines = new List<string>();

        var logFile = native.GetLogFilePath(instanceHandle);
        if (logFile != null && File.Exists(logFile))
            return await ReadTailLinesAsync(logFile, tailCount);

        var basePath = _configuration["Runner:BasePath"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".runnerrunner");
        var instancesDir = Path.Combine(basePath, "instances");
        if (!Directory.Exists(instancesDir))
            return "";

        var matchingDir = Directory.GetDirectories(instancesDir)
            .FirstOrDefault(d => Path.GetFileName(d).Equals(instanceHandle, StringComparison.OrdinalIgnoreCase)
                                 || Path.GetFileName(d).Contains(instanceHandle, StringComparison.OrdinalIgnoreCase));

        matchingDir ??= Directory.GetDirectories(instancesDir)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "runner.log"))
                                 || Directory.Exists(Path.Combine(d, "_diag")));

        if (matchingDir == null)
            return "";

        var runnerLog = Path.Combine(matchingDir, "runner.log");
        if (File.Exists(runnerLog))
            logLines.AddRange((await ReadTailLinesAsync(runnerLog, tailCount)).Split('\n'));

        var diagDir = Path.Combine(matchingDir, "_diag");
        if (Directory.Exists(diagDir))
        {
            foreach (var diagFile in Directory.GetFiles(diagDir, "*.log")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Take(2))
            {
                logLines.Add($"\n--- {Path.GetFileName(diagFile)} ---");
                logLines.Add(await ReadTailLinesAsync(diagFile, Math.Max(1, tailCount / 2)));
            }
        }

        return logLines.Count > 0 ? string.Join("\n", logLines) : "";
    }

    private async Task<string> GetHostLogTailAsync(int tailLines)
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
            return await ReadTailLinesAsync(logPath, tailCount);

        var details = string.Join("\n", candidatePaths.Select(p => $"  - {p}"));
        return
            $"(No readable HostSilo log file was found on this host.\nChecked:\n{details}\n\n" +
            "Runner status and runner logs can still be available even when the HostSilo process log is not written to a known file path.)";
    }

    private static async Task<string> ReadTailLinesAsync(string filePath, int lineCount)
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

    private async Task<List<AgentImageInfo>> ListImagesAsync(ImageType? filterType)
    {
        var images = new List<AgentImageInfo>();

        if (filterType is null or ImageType.Docker)
            images.AddRange(await _imageManager.ListDockerImagesAsync());

        if (filterType is null or ImageType.Tart)
            images.AddRange(await _imageManager.ListTartImagesAsync());

        return images;
    }

    private async Task RunHealthLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        var hostGrain = _grainFactory.GetGrain<IHostGrain>(_identity.HostId);

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await hostGrain.RecordHeartbeat("orleans-stream", _lifecycleManager.RunningInstances.Count);

                foreach (var snapshot in await _lifecycleManager.CollectRunnerHealthAsync(ct))
                {
                    var runnerGrain = _grainFactory.GetGrain<IRunnerInstanceGrain>(snapshot.Runner.InstanceId);
                    if (snapshot.Health.IsRunning)
                    {
                        await runnerGrain.UpdateHealth(
                            string.Equals(snapshot.Health.Status, "running", StringComparison.OrdinalIgnoreCase)
                                ? "Running"
                                : snapshot.Health.Status);
                    }
                    else
                    {
                        await MarkRunnerExitedAsync(snapshot.Runner.InstanceId, snapshot.Health.Status ?? "not running");
                    }
                }

                await PublishReconciliationReportAsync();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HostSilo health loop failed for host {HostId}", _identity.HostId);
            }
        }
    }

    private async Task PublishReconciliationReportAsync()
    {
        var streamProvider = _client.GetStreamProvider(OrleansHostCommandDispatcher.StreamProviderName);
        var stream = streamProvider.GetStream<ReconciliationReport>(
            StreamId.Create(OrleansHostCommandDispatcher.ReconciliationStreamNamespace, "all"));

        await stream.OnNextAsync(new ReconciliationReport
        {
            HostId = _identity.HostId,
            Runners = await DiscoverManagedRunnersAsync()
        });
    }

    private async Task<List<DiscoveredRunnerInfo>> DiscoverManagedRunnersAsync()
    {
        var runners = new List<DiscoveredRunnerInfo>();

        if (_dockerBackend is DockerBackend docker && await docker.IsAvailableAsync())
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
                _logger.LogWarning(ex, "Docker discovery failed during HostSilo reconciliation");
            }
        }

        if (_tartBackend is TartBackend tart && await tart.IsAvailableAsync())
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
                _logger.LogWarning(ex, "Tart discovery failed during HostSilo reconciliation");
            }
        }

        if (_nativeBackend is NativeBackend native)
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
                _logger.LogWarning(ex, "Native process discovery failed during HostSilo reconciliation");
            }
        }

        return runners;
    }

    private void OnRunnerExited(string instanceId, long exitCode, string reason)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await MarkRunnerExitedAsync(instanceId, exitCode == 0 ? "completed" : reason);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to report runner exit for {InstanceId}", instanceId);
            }
        });
    }

    private async Task MarkRunnerExitedAsync(string instanceId, string reason)
    {
        var runnerGrain = _grainFactory.GetGrain<IRunnerInstanceGrain>(instanceId);

        if (string.Equals(reason, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reason, "exited", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reason, "exited:0", StringComparison.OrdinalIgnoreCase))
        {
            await runnerGrain.MarkStopped();
            return;
        }

        await runnerGrain.MarkCrashed($"Runner no longer active on host ({reason})");
    }

    private IRunnerBackend SelectBackend(ExecutionBackend backend)
        => backend switch
        {
            ExecutionBackend.Docker => _dockerBackend,
            ExecutionBackend.Tart => _tartBackend,
            ExecutionBackend.Native => _nativeBackend,
            _ => throw new NotSupportedException($"Runner backend '{backend}' is not supported.")
        };

    private async Task PublishAsync<T>(string streamNamespace, T evt)
    {
        var streamProvider = _client.GetStreamProvider(OrleansHostCommandDispatcher.StreamProviderName);
        var stream = streamProvider.GetStream<T>(StreamId.Create(streamNamespace, "all"));
        await stream.OnNextAsync(evt);
    }

    private static string? ResolveInstanceHandle(RunnerInstanceGrainState state)
        => state.Backend switch
        {
            ExecutionBackend.Docker => state.ContainerId,
            ExecutionBackend.Tart => state.VmName,
            ExecutionBackend.Native => state.ProcessId?.ToString(),
            _ => state.ContainerId ?? state.VmName ?? state.ProcessId?.ToString()
        };
}
