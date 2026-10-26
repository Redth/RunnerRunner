using System.Text.Json;
using RunnerRunner.Agent.Backends;
using RunnerRunner.Agent.Services;
using RunnerRunner.Core.HostWorkers;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;
using System.Threading.Channels;

namespace RunnerRunner.HostWorker.Services;

internal sealed class HostCommandProcessor : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly HostWorkerIdentity _identity;
    private readonly RunnerLifecycleManager _lifecycleManager;
    private readonly ImageManager _imageManager;
    private readonly HostWorkerPaths _paths;
    private readonly DeploymentIntentStore _deploymentIntents;
    private readonly HostWorkerLocalLogStore _logStore;
    private readonly HostWorkerSelfUpdater _selfUpdater;
    private readonly HostResourceUsageCollector _resourceUsageCollector;
    private readonly ILogger<HostCommandProcessor> _logger;
    private readonly IRunnerBackend _dockerBackend;
    private readonly IRunnerBackend _tartBackend;
    private readonly IRunnerBackend _nativeBackend;
    private readonly Channel<HostWorkerMessage> _queue = Channel.CreateBounded<HostWorkerMessage>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private IHostWorkerEventSink? _eventSink;
    private long _sequence;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public HostCommandProcessor(
        IConfiguration configuration,
        HostWorkerIdentity identity,
        RunnerLifecycleManager lifecycleManager,
        ImageManager imageManager,
        HostWorkerPaths paths,
        HostWorkerLocalLogStore logStore,
        HostWorkerSelfUpdater selfUpdater,
        HostResourceUsageCollector resourceUsageCollector,
        ILogger<HostCommandProcessor> logger,
        ILoggerFactory loggerFactory,
        WindowsJobDockerService? jobDocker = null)
        : this(
            configuration,
            identity,
            lifecycleManager,
            imageManager,
            paths,
            logStore,
            selfUpdater,
            resourceUsageCollector,
            logger,
            loggerFactory,
            dockerBackend: new DockerBackend(loggerFactory.CreateLogger<DockerBackend>(), jobDocker?.Enabled == true ? jobDocker : null),
            tartBackend: null,
            nativeBackend: null)
    {
    }

    internal HostCommandProcessor(
        IConfiguration configuration,
        HostWorkerIdentity identity,
        RunnerLifecycleManager lifecycleManager,
        ImageManager imageManager,
        HostWorkerPaths paths,
        HostWorkerLocalLogStore logStore,
        HostWorkerSelfUpdater selfUpdater,
        HostResourceUsageCollector resourceUsageCollector,
        ILogger<HostCommandProcessor> logger,
        ILoggerFactory loggerFactory,
        IRunnerBackend? dockerBackend,
        IRunnerBackend? tartBackend,
        IRunnerBackend? nativeBackend)
    {
        _configuration = configuration;
        _identity = identity;
        _lifecycleManager = lifecycleManager;
        _imageManager = imageManager;
        _paths = paths;
        _deploymentIntents = new DeploymentIntentStore(paths, identity.HostId);
        _logStore = logStore;
        _selfUpdater = selfUpdater;
        _resourceUsageCollector = resourceUsageCollector;
        _logger = logger;
        _dockerBackend = dockerBackend ?? new DockerBackend(loggerFactory.CreateLogger<DockerBackend>());
        _tartBackend = tartBackend ?? new TartBackend(loggerFactory.CreateLogger<TartBackend>());
        _nativeBackend = nativeBackend ?? new NativeBackend(loggerFactory.CreateLogger<NativeBackend>());
    }

    public ValueTask EnqueueAsync(HostWorkerMessage message, CancellationToken cancellationToken)
        => _queue.Writer.WriteAsync(message, cancellationToken);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _lifecycleManager.OnRunnerExited += OnRunnerExited;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _lifecycleManager.OnRunnerExited -= OnRunnerExited;
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverOwnedRunnersAsync(stoppingToken);
        var healthLoop = RunHealthLoopAsync(stoppingToken);

        await foreach (var message in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessCommandAsync(message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HostWorker command processing failed outside a command handler");
            }
        }

        await healthLoop;
    }

    public override void Dispose()
    {
        _queue.Writer.TryComplete();
        base.Dispose();
    }

    public void AttachEventSink(IHostWorkerEventSink eventSink)
    {
        _eventSink = eventSink;
    }

    internal async Task ProcessCommandAsync(HostWorkerMessage message, CancellationToken ct)
    {
        await _operationGate.WaitAsync(ct);
        try
        {
            var envelope = HostWorkerProtocol.DeserializeCommand(message);
            try
            {
                await AppendJournalAsync(message.CommandId, envelope.Kind, "received", null, ct);
                await PublishStatusAsync(HostWorkerMessageKinds.CommandAccepted, message.CommandId, envelope.Kind, true, "Command accepted", null, ct);

                switch (envelope.Kind)
                {
                    case HostCommandKind.DeployRunner when envelope.DeployRunner != null:
                        await HandleDeployRunnerAsync(envelope.DeployRunner, ct);
                        break;
                    case HostCommandKind.ActivateRunner when envelope.ActivateRunner != null:
                        await HandleActivateRunnerAsync(envelope.ActivateRunner, ct);
                        break;
                    case HostCommandKind.StopRunner when envelope.StopRunner != null:
                        await HandleStopRunnerAsync(envelope.StopRunner, ct);
                        break;
                    case HostCommandKind.CleanupOrphan when envelope.CleanupOrphan != null:
                        await HandleCleanupOrphanAsync(envelope.CleanupOrphan, ct);
                        break;
                    case HostCommandKind.ListImages when envelope.ListImages != null:
                        await HandleListImagesAsync(envelope.ListImages, ct);
                        break;
                    case HostCommandKind.PullImage when envelope.PullImage != null:
                        await HandlePullImageAsync(envelope.PullImage, ct);
                        break;
                    case HostCommandKind.DeleteImage when envelope.DeleteImage != null:
                        await HandleDeleteImageAsync(envelope.DeleteImage, ct);
                        break;
                    case HostCommandKind.GetHostLogs when envelope.GetHostLogs != null:
                        await HandleGetHostLogsAsync(envelope.GetHostLogs, ct);
                        break;
                    case HostCommandKind.GetRunnerLogs when envelope.GetRunnerLogs != null:
                        await HandleGetRunnerLogsAsync(envelope.GetRunnerLogs, ct);
                        break;
                    case HostCommandKind.ApplyHostWorkerUpdate when envelope.ApplyHostWorkerUpdate != null:
                        await HandleApplyHostWorkerUpdateAsync(envelope.ApplyHostWorkerUpdate, ct);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported or malformed HostWorker command kind '{envelope.Kind}'.");
                }

                await AppendJournalAsync(message.CommandId, envelope.Kind, "completed", null, ct);
                await PublishStatusAsync(HostWorkerMessageKinds.CommandCompleted, message.CommandId, envelope.Kind, true, "Command completed", null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Command {CommandKind} ({CommandId}) failed on HostWorker {HostId}",
                    envelope.Kind, message.CommandId, _identity.HostId);
                await AppendJournalAsync(message.CommandId, envelope.Kind, "failed", ex.Message, ct);
                await PublishStatusAsync(HostWorkerMessageKinds.CommandRejected, message.CommandId, envelope.Kind, false, null, ex.Message, ct);
            }
        }
        finally { _operationGate.Release(); }
    }

    private async Task HandleDeployRunnerAsync(DeployRunnerCommand command, CancellationToken ct)
    {
        if (_deploymentIntents.Contains(command.InstanceId))
            throw new InvalidOperationException("Instance deployment was already claimed; reconcile it instead of replaying credentials.");

        _logger.LogInformation("Deploying runner {RunnerName} on HostWorker {HostId} with backend {Backend}",
            command.RunnerName, _identity.HostId, command.Backend);

        var intent = _deploymentIntents.Begin(command);
        var backend = SelectBackend(command.Backend);
        if (!await backend.IsAvailableAsync())
        {
            await PublishNotStartedAsync(command, $"Backend {command.Backend} is not available on host {_identity.HostName}", ct);
            return;
        }

        if (command.Backend == ExecutionBackend.Tart
            && await IsTartCapacityFullAsync(command, ct))
        {
            return;
        }

        await PublishAsync(HostWorkerMessageKinds.RunnerHealth, new RunnerHealthUpdateEvent
        {
            InstanceId = command.InstanceId,
            Status = RunnerInstanceStatus.Starting,
            CheckedAt = DateTime.UtcNow,
            StatusMessage = "Deploy command received by HostWorker"
        }, ct);

        try
        {
            var result = await _lifecycleManager.StartRunnerAsync(command, backend, ct, intent.Ownership,
                owner => _deploymentIntents.MarkCreating(command.InstanceId, owner),
                handle => _deploymentIntents.MarkCreated(command.InstanceId, handle));
            if (result == null)
                throw new InvalidOperationException("Runner backend did not return a started runner handle.");

            if (result.Prepared)
            {
                _deploymentIntents.MarkPrepared(command.InstanceId, result.InstanceHandle);
                await PublishAsync(HostWorkerMessageKinds.RunnerPrepared, new RunnerStartedEvent
                { InstanceId = command.InstanceId, RunnerName = result.RunnerName, InstanceHandle = result.InstanceHandle, Backend = command.Backend }, ct);
                return;
            }
            _deploymentIntents.MarkStarted(command.InstanceId, result.InstanceHandle);

            await PublishAsync(HostWorkerMessageKinds.RunnerStarted, new RunnerStartedEvent
            {
                InstanceId = command.InstanceId,
                RunnerName = result.RunnerName,
                InstanceHandle = result.InstanceHandle,
                Backend = command.Backend
            }, ct);
        }
        catch (Exception ex)
        {
            await PublishAsync(HostWorkerMessageKinds.RunnerHealth, new RunnerHealthUpdateEvent
            {
                InstanceId = command.InstanceId,
                Status = RunnerInstanceStatus.Stopping,
                CheckedAt = DateTime.UtcNow,
                StatusMessage = $"Deployment outcome uncertain; reconcile before releasing capacity. {ex.Message}"
            }, ct);
            throw;
        }
    }

    private async Task HandleActivateRunnerAsync(ActivateRunnerCommand command, CancellationToken ct)
    {
        var intent = _deploymentIntents.Read(command.InstanceId)
            ?? throw new InvalidOperationException("No prepared ownership exists.");
        if (intent.Ownership is null || SelectBackend(intent.Backend) is not IStagedRunnerBackend staged)
            throw new InvalidOperationException("Backend does not support prepared activation.");
        _deploymentIntents.ClaimActivation(command.InstanceId, command.InstanceHandle);
        await staged.ActivateRunnerAsync(intent.Ownership, command.InstanceHandle, command.JitConfig, ct);
        _deploymentIntents.MarkStarted(command.InstanceId, command.InstanceHandle);
        await PublishAsync(HostWorkerMessageKinds.RunnerStarted, new RunnerStartedEvent
        { InstanceId = command.InstanceId, RunnerName = intent.RunnerName, InstanceHandle = command.InstanceHandle, Backend = intent.Backend }, ct);
    }

    private async Task<bool> IsTartCapacityFullAsync(DeployRunnerCommand command, CancellationToken ct)
    {
        var usage = await _resourceUsageCollector.CollectAsync("tart deploy preflight", ct);
        if (usage == null)
            return false;

        await PublishAsync(HostWorkerMessageKinds.Heartbeat, new HeartbeatEvent
        {
            AgentId = _identity.HostId,
            RunningInstanceCount = _lifecycleManager.RunningInstances.Count,
            ResourceUsage = usage
        }, ct);

        if (command.BackendCapacityLimit is not int limit)
            return false;

        var runningTartVmCount = usage.RunningTartVmCount ?? 0;
        if (runningTartVmCount < Math.Max(0, limit))
            return false;

        var message = $"Tart capacity is full on host {_identity.HostName}: {runningTartVmCount}/{Math.Max(0, limit)} VM(s) are already running.";
        _logger.LogInformation("Rejecting Tart runner {RunnerName}: {Message}", command.RunnerName, message);
        await PublishNotStartedAsync(command, message, ct);

        return true;
    }

    private async Task PublishNotStartedAsync(DeployRunnerCommand command, string error, CancellationToken ct)
    {
        var receipt = _deploymentIntents.CompleteCleanup(command.InstanceId, null, neverStarted: true, "failed");
        await PublishAsync(HostWorkerMessageKinds.RunnerStopped, receipt, ct);
    }

    private async Task HandleStopRunnerAsync(StopRunnerCommand command, CancellationToken ct)
    {
        try
        {
            var intent = _deploymentIntents.Read(command.InstanceId)
                ?? throw new InvalidOperationException("No durable ownership record; refusing unbound native cleanup.");
            var handle = intent.InstanceHandle;
            if (_lifecycleManager.RunningInstances.TryGetValue(command.InstanceId, out var runner))
            {
                if (runner.Backend.BackendType != intent.Backend ||
                    (handle is not null && handle != runner.InstanceHandle))
                    throw new InvalidOperationException("Native runner differs from durable ownership.");
                handle = runner.InstanceHandle;
                if (command.InstanceHandle is not null && command.InstanceHandle != handle)
                    throw new InvalidOperationException("Stop handle differs from owned runner.");
                _deploymentIntents.MarkStarted(command.InstanceId, handle);
                await _lifecycleManager.StopRunnerAsync(command.InstanceId, ct);
            }
            else if (!string.IsNullOrWhiteSpace(handle))
            {
                if (command.InstanceHandle is not null && command.InstanceHandle != handle)
                    throw new InvalidOperationException("Stop handle differs from durable ownership.");
                // Never probe another backend with an opaque handle. A successful
                // Docker 'not found' says nothing about an owned Tart VM.
                var backend = SelectBackend(intent.Backend);
                if (intent.Ownership is null || backend is not IRecoverableRunnerBackend recoverable)
                    throw new InvalidOperationException("Restarted backend lacks immutable native ownership proof; manual reconciliation required.");
                var observed = await recoverable.InspectOwnedAsync(intent.Ownership, handle, ct);
                if (observed.Handle != handle || observed.State == NativeRunnerState.Unknown)
                    throw new InvalidOperationException("Native cleanup identity or outcome is uncertain.");
                if (observed.State != NativeRunnerState.Absent)
                    await backend.StopRunnerAsync(handle, ct);
            }
            else
            {
                throw new InvalidOperationException("Claimed deployment has no reconciled native handle; cleanup cannot be confirmed.");
            }

            var receipt = _deploymentIntents.CompleteCleanup(command.InstanceId, handle, neverStarted: false, "stopped");
            await PublishAsync(HostWorkerMessageKinds.RunnerStopped, receipt, ct);
        }
        catch (Exception ex)
        {
            await PublishAsync(HostWorkerMessageKinds.RunnerHealth, new RunnerHealthUpdateEvent
            {
                InstanceId = command.InstanceId,
                Status = RunnerInstanceStatus.Stopping,
                CheckedAt = DateTime.UtcNow,
                StatusMessage = $"Cleanup unconfirmed; capacity retained. Stop failed: {ex.Message}"
            }, ct);
            throw;
        }
    }

    private async Task HandleCleanupOrphanAsync(CleanupOrphanCommand command, CancellationToken ct)
    {
        switch (command.Backend)
        {
            case ExecutionBackend.Docker when _dockerBackend is DockerBackend docker && !string.IsNullOrWhiteSpace(command.ContainerId):
                await docker.CleanupOrphanContainerAsync(command.ContainerId, ct);
                break;
            case ExecutionBackend.Tart when _tartBackend is TartBackend tart && !string.IsNullOrWhiteSpace(command.VmName):
                await tart.StopRunnerAsync(command.VmName, ct);
                break;
            case ExecutionBackend.Native when _nativeBackend is NativeBackend native && command.ProcessId.HasValue:
                await native.CleanupOrphanProcessAsync(command.ProcessId.Value, command.InstanceDir, ct);
                break;
            default:
                _logger.LogWarning("Cleanup command for {Backend} did not include a usable resource handle", command.Backend);
                break;
        }
    }

    private async Task HandleListImagesAsync(ListImagesCommand command, CancellationToken ct)
    {
        try
        {
            await PublishAsync(HostWorkerMessageKinds.ImageRefreshStatus, new ImageRefreshStatusEvent
            {
                HostId = _identity.HostId,
                Stage = "refresh",
                Message = "Listing host images...",
                Success = true
            }, ct);

            var images = await ListImagesAsync(command.FilterType, ct);
            await PublishAsync(HostWorkerMessageKinds.ImageList, new ImageListEvent
            {
                HostId = _identity.HostId,
                Images = images
            }, ct);

            await PublishAsync(HostWorkerMessageKinds.ImageRefreshStatus, new ImageRefreshStatusEvent
            {
                HostId = _identity.HostId,
                Stage = "complete",
                Message = $"Found {images.Count} images.",
                IsComplete = true,
                Success = true
            }, ct);
        }
        catch (Exception ex)
        {
            await PublishAsync(HostWorkerMessageKinds.ImageRefreshStatus, new ImageRefreshStatusEvent
            {
                HostId = _identity.HostId,
                Stage = "failed",
                Message = ex.Message,
                IsComplete = true,
                Success = false
            }, ct);
            throw;
        }
    }

    private async Task HandlePullImageAsync(PullImageCommand command, CancellationToken ct)
    {
        var imageReference = ImageReference.Build(command.RegistryUrl, command.ImageName, command.Tag);

        try
        {
            if (command.ImageType == ImageType.Docker && !string.IsNullOrWhiteSpace(command.RegistryUrl))
                await _imageManager.LoginDockerRegistryAsync(command.RegistryUrl, command.Username, command.Password, ct);

            if (command.ImageType == ImageType.Docker)
            {
                await _imageManager.PullDockerImageAsync(
                    command.ImageName,
                    command.Tag,
                    command.RegistryUrl,
                    evt => PublishImagePullProgressAsync(command, evt, ct),
                    ct);
            }
            else
            {
                await _imageManager.PullTartImageAsync(
                    command.ImageName,
                    command.Tag,
                    command.RegistryUrl,
                    evt => PublishImagePullProgressAsync(command, evt, ct),
                    ct);
            }

            await PublishAsync(HostWorkerMessageKinds.ImagePullComplete, new ImagePullCompleteEvent
            {
                HostId = _identity.HostId,
                ImageType = command.ImageType,
                ImageName = imageReference,
                TaskId = command.TaskId,
                Success = true
            }, ct);

            await HandleListImagesAsync(new ListImagesCommand { FilterType = command.ImageType }, ct);
        }
        catch (Exception ex)
        {
            await PublishAsync(HostWorkerMessageKinds.ImagePullComplete, new ImagePullCompleteEvent
            {
                HostId = _identity.HostId,
                ImageType = command.ImageType,
                ImageName = imageReference,
                TaskId = command.TaskId,
                Success = false,
                Error = ex.Message
            }, ct);
            throw;
        }

        async Task PublishImagePullProgressAsync(PullImageCommand pullCommand, ImagePullProgressEvent evt, CancellationToken token)
        {
            evt.HostId = _identity.HostId;
            evt.TaskId = pullCommand.TaskId;
            var status = string.IsNullOrWhiteSpace(evt.Status)
                ? $"Pulling {evt.ImageName} {evt.ProgressPercent:0}%"
                : evt.Status;
            await PublishLocalLogFrameAsync(
                "task.progress",
                $"task.{pullCommand.TaskId ?? ImageReference.Build(pullCommand.RegistryUrl, pullCommand.ImageName, pullCommand.Tag)}",
                status,
                null,
                token,
                category: "ImagePull",
                level: "Information",
                taskId: pullCommand.TaskId);
            await PublishAsync(HostWorkerMessageKinds.ImagePullProgress, evt, token);
        }
    }

    private async Task HandleDeleteImageAsync(DeleteImageCommand command, CancellationToken ct)
    {
        try
        {
            if (command.ImageType == ImageType.Docker)
                await _imageManager.DeleteDockerImageAsync(command.ImageId, ct);
            else
                await _imageManager.DeleteTartImageAsync(command.ImageName, ct);

            await PublishAsync(HostWorkerMessageKinds.ImageDeleted, new ImageDeletedEvent
            {
                HostId = _identity.HostId,
                ImageType = command.ImageType,
                ImageId = command.ImageId,
                Success = true
            }, ct);
        }
        catch (Exception ex)
        {
            await PublishAsync(HostWorkerMessageKinds.ImageDeleted, new ImageDeletedEvent
            {
                HostId = _identity.HostId,
                ImageType = command.ImageType,
                ImageId = command.ImageId,
                Success = false,
                Error = ex.Message
            }, ct);
            throw;
        }
    }

    private async Task HandleGetHostLogsAsync(GetHostLogsCommand command, CancellationToken ct)
    {
        var logs = await GetHostLogTailAsync(command.TailLines, ct);

        await PublishAsync(HostWorkerMessageKinds.HostLogs, new HostLogsEvent
        {
            HostId = _identity.HostId,
            Logs = logs
        }, ct);
    }

    private async Task HandleGetRunnerLogsAsync(GetRunnerLogsCommand command, CancellationToken ct)
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
                    },
                    ct);
                var (stdout, stderr) = await stream.ReadOutputToEndAsync(ct);
                logs = stdout + stderr;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Runner handle {Handle} did not resolve to Docker logs", command.InstanceHandle);
            }
        }

        if (string.IsNullOrEmpty(logs) && _nativeBackend is NativeBackend native)
            logs = await GetNativeRunnerLogsAsync(native, command.InstanceHandle, command.RunnerInstanceId, command.TailLines, ct);

        logs = string.IsNullOrEmpty(logs) ? "(No logs available for this runner instance)" : logs;
        var runnerInstanceId = string.IsNullOrWhiteSpace(command.RunnerInstanceId)
            ? command.InstanceHandle
            : command.RunnerInstanceId;

        await PublishAsync(HostWorkerMessageKinds.RunnerLogs, new RunnerLogsEvent
        {
            HostId = _identity.HostId,
            InstanceHandle = command.InstanceHandle,
            RunnerInstanceId = runnerInstanceId,
            Logs = logs
        }, ct);
    }

    private async Task HandleApplyHostWorkerUpdateAsync(HostWorkerUpdateCommand command, CancellationToken ct)
    {
        try
        {
            await _selfUpdater.ApplyAsync(command, PublishUpdateStatusAsync, ct);
        }
        catch (Exception ex)
        {
            await PublishUpdateStatusAsync(new HostWorkerUpdateStatusEvent
            {
                HostId = _identity.HostId,
                CurrentVersion = HostWorkerVersion.Current,
                CurrentCommitSha = HostWorkerVersion.CommitSha,
                TargetVersion = command.TargetVersion,
                TargetCommitSha = command.TargetCommitSha,
                Stage = "failed",
                Message = ex.Message,
                IsComplete = true,
                Success = false,
                Error = ex.Message
            }, ct);
            throw;
        }
    }

    private async Task RunHealthLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await RecoverOwnedRunnersAsync(ct);
                await ReplayCleanupReceiptsAsync(ct);
                foreach (var snapshot in await _lifecycleManager.CollectRunnerHealthAsync(ct))
                {
                    await PublishRunnerHealthAsync(snapshot, ct);
                }

                await PublishAsync(HostWorkerMessageKinds.Reconciliation, new ReconciliationReport
                {
                    HostId = _identity.HostId,
                    Runners = await DiscoverManagedRunnersAsync(ct)
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HostWorker health loop failed for host {HostId}", _identity.HostId);
            }
        }
    }

    internal Task PublishRunnerHealthAsync(ManagedRunnerHealth snapshot, CancellationToken ct)
    {
        if (snapshot.CleanupConfirmed)
        {
            var reason = snapshot.Runner.IsEphemeral && snapshot.Health.Status == "exited:0"
                ? "completed"
                : snapshot.Health.Status ?? "not running";
            return PublishRunnerExitedAsync(snapshot.Runner, reason, ct);
        }

        var intent = _deploymentIntents.Read(snapshot.Runner.InstanceId);
        if (intent is { DeferredRegistration: true } && intent.State != "started")
            return Task.CompletedTask;

        return PublishAsync(HostWorkerMessageKinds.RunnerHealth, new RunnerHealthUpdateEvent
        {
            InstanceId = snapshot.Runner.InstanceId,
            // Stopping remains capacity-consuming in the server's current state model.
            // A timeout is not evidence of release, nor proof that the runner is healthy.
            Status = snapshot.Health.IsRunning ? RunnerInstanceStatus.Running : RunnerInstanceStatus.Stopping,
            CheckedAt = snapshot.Health.CheckedAt,
            StatusMessage = snapshot.Health.IsRunning
                ? snapshot.Health.Status ?? "Running"
                : $"Cleanup unconfirmed; capacity retained. {snapshot.Health.Status}"
        }, ct);
    }

    private async Task<List<AgentImageInfo>> ListImagesAsync(ImageType? filterType, CancellationToken ct)
    {
        var images = new List<AgentImageInfo>();

        if (filterType is null or ImageType.Docker)
            images.AddRange(await _imageManager.ListDockerImagesAsync(ct));

        if (filterType is null or ImageType.Tart)
            images.AddRange(await _imageManager.ListTartImagesAsync(ct));

        return images;
    }

    private async Task<List<DiscoveredRunnerInfo>> DiscoverManagedRunnersAsync(CancellationToken ct)
    {
        var runners = new List<DiscoveredRunnerInfo>();

        if (_dockerBackend is DockerBackend docker && await docker.IsAvailableAsync())
        {
            try
            {
                var discovered = await docker.DiscoverManagedContainersAsync(ct);
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
                _logger.LogWarning(ex, "Docker discovery failed during HostWorker reconciliation");
            }
        }

        if (_tartBackend is TartBackend tart && await tart.IsAvailableAsync())
        {
            try
            {
                var discovered = await tart.DiscoverManagedVmsAsync(ct);
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
                _logger.LogWarning(ex, "Tart discovery failed during HostWorker reconciliation");
            }
        }

        if (_nativeBackend is NativeBackend native)
        {
            try
            {
                var discovered = await native.DiscoverManagedProcessesAsync(ct);
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
                _logger.LogWarning(ex, "Native process discovery failed during HostWorker reconciliation");
            }
        }

        return runners;
    }

    private void OnRunnerExited(ManagedRunner runner, long exitCode, string reason)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await PublishRunnerExitedAsync(runner, exitCode == 0 ? "completed" : reason, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to report runner exit for {InstanceId}", runner.InstanceId);
            }
        });
    }

    private Task PublishRunnerExitedAsync(ManagedRunner runner, string reason, CancellationToken ct)
    {
        // The native exit monitor can finish before StartRunnerAsync returns.
        _deploymentIntents.MarkStarted(runner.InstanceId, runner.InstanceHandle);
        var receipt = _deploymentIntents.CompleteCleanup(runner.InstanceId, runner.InstanceHandle, neverStarted: false,
            string.Equals(reason, "completed", StringComparison.OrdinalIgnoreCase) ? "completed" : "crashed");
        return PublishAsync(HostWorkerMessageKinds.RunnerStopped, receipt, ct);
    }

    internal async Task RecoverOwnedRunnersAsync(CancellationToken ct)
    {
        // Recovery never races a live Create/Start command. The process lock also
        // prevents two workers from taking ownership of the same data root.
        if (!await _operationGate.WaitAsync(0, ct)) return;
        try
        {
            foreach (var intent in _deploymentIntents.RecoveryCandidates())
            {
                if (_lifecycleManager.RunningInstances.ContainsKey(intent.InstanceId)) continue;
                try
                {
                    var backend = SelectBackend(intent.Backend);
                    if (backend is not IRecoverableRunnerBackend recoverable) continue;
                    if (intent.State == "claimed" && intent.Ownership!.ImageId is null && intent.InstanceHandle is null)
                    {
                        // The durable pre-Create boundary was never crossed.
                        var neverStarted = _deploymentIntents.CompleteCleanup(intent.InstanceId, null, true, "failed");
                        await PublishAsync(HostWorkerMessageKinds.RunnerStopped, neverStarted, ct);
                        continue;
                    }
                    var observed = await recoverable.InspectOwnedAsync(intent.Ownership!, intent.InstanceHandle, ct);
                    if (observed.State == NativeRunnerState.Unknown) continue;
                    if (observed.Handle is null) throw new InvalidDataException("Native observation has no immutable handle.");
                    if (observed.State == NativeRunnerState.Running && intent.DeferredRegistration &&
                        intent.State is not ("activating" or "started"))
                    {
                        await backend.StopRunnerAsync(observed.Handle, ct);
                        var receipt = _deploymentIntents.CompleteCleanup(intent.InstanceId, observed.Handle, false, "stopped");
                        await PublishAsync(HostWorkerMessageKinds.RunnerStopped, receipt, ct);
                    }
                    else if (observed.State == NativeRunnerState.Running)
                    {
                        var activationUncertain = intent.DeferredRegistration && intent.State == "activating";
                        if (!activationUncertain)
                            _deploymentIntents.MarkStarted(intent.InstanceId, observed.Handle);
                        _lifecycleManager.AdoptRunner(new ManagedRunner
                        {
                            InstanceId = intent.InstanceId, ProfileId = intent.ProfileId, RunnerName = intent.RunnerName,
                            InstanceHandle = observed.Handle, Backend = backend, StartedAt = intent.CreatedAt, IsEphemeral = intent.IsEphemeral
                        });
                        if (activationUncertain)
                            await PublishAsync(HostWorkerMessageKinds.RunnerHealth, new RunnerHealthUpdateEvent
                            {
                                InstanceId = intent.InstanceId, Status = RunnerInstanceStatus.Starting,
                                CheckedAt = DateTime.UtcNow,
                                StatusMessage = "Activation outcome uncertain; observing the original environment without replay or termination."
                            }, ct);
                        else
                            await PublishAsync(HostWorkerMessageKinds.RunnerStarted, new RunnerStartedEvent
                        {
                            InstanceId = intent.InstanceId, RunnerName = intent.RunnerName,
                            Backend = intent.Backend, InstanceHandle = observed.Handle
                        }, ct);
                    }
                    else
                    {
                        if (observed.State == NativeRunnerState.Absent && intent.InstanceHandle != observed.Handle)
                            throw new InvalidDataException("Absence is not bound to a recorded immutable handle.");
                        _deploymentIntents.MarkCreated(intent.InstanceId, observed.Handle);
                        if (observed.State is NativeRunnerState.Created or NativeRunnerState.Exited)
                            await backend.StopRunnerAsync(observed.Handle, ct);
                        var receipt = _deploymentIntents.CompleteCleanup(intent.InstanceId, observed.Handle, false,
                            observed.State == NativeRunnerState.Exited && observed.ExitCode == 0 ? "completed" : "stopped");
                        await PublishAsync(HostWorkerMessageKinds.RunnerStopped, receipt, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Native recovery remains uncertain for {InstanceId}; ownership retained", intent.InstanceId);
                }
            }
        }
        finally { _operationGate.Release(); }
    }

    internal async Task ReplayCleanupReceiptsAsync(CancellationToken ct)
    {
        foreach (var receipt in _deploymentIntents.PendingCleanupReceipts())
            await PublishAsync(HostWorkerMessageKinds.RunnerStopped, receipt, ct);
    }

    internal void AcknowledgeCleanup(RunnerStoppedEvent receipt)
        => _deploymentIntents.AcknowledgeCleanup(receipt);

    private async Task<string> GetNativeRunnerLogsAsync(NativeBackend native, string instanceHandle, string? runnerInstanceId, int tailLines, CancellationToken ct)
    {
        var tailCount = tailLines > 0 ? tailLines : 100;
        var logLines = new List<string>();

        var logFile = native.GetLogFilePath(instanceHandle);
        if (logFile != null && File.Exists(logFile))
            return await ReadTailLinesAsync(logFile, tailCount, ct);

        var basePath = _configuration["Runner:BasePath"] ?? NativeBackend.GetDefaultRunnerBasePath();
        var instancesDir = Path.Combine(basePath, "instances");
        if (!Directory.Exists(instancesDir))
            return "";

        var instanceDirs = Directory.GetDirectories(instancesDir);
        var matchingDir = string.IsNullOrWhiteSpace(runnerInstanceId)
            ? null
            : await FindNativeInstanceDirectoryAsync(instanceDirs, runnerInstanceId, ct);

        matchingDir ??= instanceDirs
            .FirstOrDefault(d => Path.GetFileName(d).Equals(instanceHandle, StringComparison.OrdinalIgnoreCase)
                                 || Path.GetFileName(d).Contains(instanceHandle, StringComparison.OrdinalIgnoreCase));

        matchingDir ??= instanceDirs
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "runner.log"))
                                 || Directory.Exists(Path.Combine(d, "_diag")));

        if (matchingDir == null)
            return "";

        var runnerLog = Path.Combine(matchingDir, "runner.log");
        if (File.Exists(runnerLog))
            logLines.AddRange((await ReadTailLinesAsync(runnerLog, tailCount, ct)).Split('\n'));

        var diagDir = Path.Combine(matchingDir, "_diag");
        if (Directory.Exists(diagDir))
        {
            foreach (var diagFile in Directory.GetFiles(diagDir, "*.log")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Take(2))
            {
                logLines.Add($"\n--- {Path.GetFileName(diagFile)} ---");
                logLines.Add(await ReadTailLinesAsync(diagFile, Math.Max(1, tailCount / 2), ct));
            }
        }

        return logLines.Count > 0 ? string.Join("\n", logLines) : "";
    }

    private async Task<string?> FindNativeInstanceDirectoryAsync(string[] instanceDirs, string runnerInstanceId, CancellationToken ct)
    {
        foreach (var dir in instanceDirs)
        {
            var metadataPath = Path.Combine(dir, "rr-instance.json");
            if (!File.Exists(metadataPath))
                continue;

            try
            {
                await using var stream = File.OpenRead(metadataPath);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (doc.RootElement.TryGetProperty("InstanceId", out var instanceId)
                    && string.Equals(instanceId.GetString(), runnerInstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    return dir;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Skipping unreadable native runner metadata file {MetadataPath}", metadataPath);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Skipping inaccessible native runner metadata file {MetadataPath}", metadataPath);
            }
        }

        return null;
    }

    private async Task<string> GetHostLogTailAsync(int tailLines, CancellationToken ct)
    {
        var tailCount = tailLines > 0 ? tailLines : 100;
        var candidatePaths = new List<string>();

        void AddIfSet(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && !candidatePaths.Contains(path))
                candidatePaths.Add(path);
        }

        AddIfSet(_configuration["RunnerRunner:HostLogFile"]);
        AddIfSet(_configuration["HostWorker:LogFilePath"]);
        AddIfSet(Path.Combine(_paths.LogRoot, "hostworker.log"));
        AddIfSet(Path.Combine(Path.GetTempPath(), "runnerrunner-hostworker.log"));

        var logPath = candidatePaths.FirstOrDefault(File.Exists);
        if (logPath != null)
            return await ReadTailLinesAsync(logPath, tailCount, ct);

        var details = string.Join("\n", candidatePaths.Select(p => $"  - {p}"));
        return
            $"(No readable HostWorker log file was found on this host.\nChecked:\n{details}\n\n" +
            "Runner status and runner logs can still be available even when the HostWorker process log is not written to a known file path.)";
    }

    private async Task AppendJournalAsync(string commandId, HostCommandKind kind, string status, string? error, CancellationToken ct)
    {
        var entry = JsonSerializer.Serialize(new
        {
            Timestamp = DateTimeOffset.UtcNow,
            HostId = _identity.HostId,
            CommandId = commandId,
            Kind = kind.ToString(),
            Status = status,
            Error = error
        }, HostWorkerProtocol.JsonOptions);

        await File.AppendAllTextAsync(_paths.CommandJournalPath, entry + Environment.NewLine, ct);
        await PublishLocalLogFrameAsync("worker.command", $"command-{commandId}", entry, null, ct, commandId, $"HostCommand.{kind}", "Information");
    }

    private Task PublishStatusAsync(
        string messageKind,
        string commandId,
        HostCommandKind kind,
        bool success,
        string? message,
        string? error,
        CancellationToken ct)
        => PublishAsync(messageKind, new HostWorkerCommandStatus
        {
            CommandId = commandId,
            Kind = kind,
            Success = success,
            Message = message,
            Error = error
        }, ct);

    private Task PublishUpdateStatusAsync(HostWorkerUpdateStatusEvent evt, CancellationToken ct)
    {
        evt.HostId = _identity.HostId;
        return PublishAsync(HostWorkerMessageKinds.UpdateStatus, evt, ct);
    }

    private Task PublishAsync<T>(string kind, T payload, CancellationToken ct)
    {
        if (_eventSink == null)
            return Task.CompletedTask;

        return _eventSink.PublishAsync(HostWorkerProtocol.CreateMessage(
            _identity.HostId,
            kind,
            payload,
            sequence: Interlocked.Increment(ref _sequence)), ct).AsTask();
    }

    private async Task PublishLocalLogFrameAsync(
        string streamKind,
        string streamId,
        string text,
        string? runnerInstanceId,
        CancellationToken ct,
        string? commandId = null,
        string? category = null,
        string? level = null,
        string? taskId = null)
    {
        var frame = await _logStore.AppendAsync(
            streamKind,
            streamId,
            text,
            runnerInstanceId,
            ct,
            taskId: taskId,
            commandId: commandId,
            category: category,
            level: level,
            sourceType: runnerInstanceId == null ? "Host" : "Runner",
            sourceName: _identity.HostName);
        await PublishAsync(HostWorkerMessageKinds.LogFrame, frame, ct);
    }

    private IRunnerBackend SelectBackend(ExecutionBackend backend)
        => backend switch
        {
            ExecutionBackend.Docker => _dockerBackend,
            ExecutionBackend.Tart => _tartBackend,
            ExecutionBackend.Native => _nativeBackend,
            _ => throw new NotSupportedException($"Runner backend '{backend}' is not supported.")
        };

    private static async Task<string> ReadTailLinesAsync(string filePath, int lineCount, CancellationToken ct)
    {
        var maxLines = Math.Max(1, lineCount);
        var tail = new Queue<string>(maxLines);

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (tail.Count == maxLines)
                tail.Dequeue();

            tail.Enqueue(line);
        }

        return string.Join("\n", tail);
    }

}
