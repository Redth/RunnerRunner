using RunnerRunner.Core.HostWorkers;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Hubs;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;
using Grpc.Core;

namespace RunnerRunner.Server.Services.HostWorkers;

public sealed class HostWorkerEventProcessor
{
    private readonly IDocumentStore _store;
    private readonly IGrainFactory _grainFactory;
    private readonly HostWorkerLogCache _logCache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<HostWorkerEventProcessor> _logger;

    public HostWorkerEventProcessor(
        IDocumentStore store,
        IGrainFactory grainFactory,
        HostWorkerLogCache logCache,
        IConfiguration configuration,
        ILogger<HostWorkerEventProcessor> logger)
    {
        _store = store;
        _grainFactory = grainFactory;
        _logCache = logCache;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> WorkerConnectedAsync(
        AgentInfo agentInfo,
        string connectionId,
        IReadOnlyDictionary<string, string> labels,
        string? enrollmentToken,
        CancellationToken ct)
    {
        var host = await ResolveOrCreateHostAsync(agentInfo, labels, enrollmentToken, ct);
        var hostId = host.Id;

        var hostGrain = _grainFactory.GetGrain<IHostGrain>(hostId);
        await hostGrain.Register(agentInfo.Name, agentInfo.Platform, agentInfo.Architecture, agentInfo.AgentVersion ?? "");

        if (labels.Count > 0)
            await hostGrain.UpdateLabels(new Dictionary<string, string>(labels));

        await hostGrain.RecordHeartbeat(connectionId, agentInfo.CurrentRunners.Count);

        var scheduler = _grainFactory.GetGrain<ISchedulerGrain>(0);
        await scheduler.RegisterHost(hostId);

        AgentHub.NotifyQueueRelevantChange();
        _logger.LogInformation("HostWorker {HostName} registered as host {HostId}", agentInfo.Name, hostId);
        return hostId;
    }

    public async Task WorkerDisconnectedAsync(string hostId, CancellationToken ct)
    {
        var host = await _store.Get<Host>(hostId);
        if (host != null)
        {
            host.AgentStatus = AgentStatus.Offline;
            host.UpdatedAt = DateTime.UtcNow;
            await _store.Update(host);
        }

        try
        {
            var hostGrain = _grainFactory.GetGrain<IHostGrain>(hostId);
            await hostGrain.MarkOffline();

            var scheduler = _grainFactory.GetGrain<ISchedulerGrain>(0);
            await scheduler.UnregisterHost(hostId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync HostWorker disconnect for {HostId}", hostId);
        }

        AgentHub.NotifyQueueRelevantChange();
    }

    public async Task HandleMessageAsync(string canonicalHostId, HostWorkerMessage message, CancellationToken ct)
    {
        try
        {
            switch (message.Kind)
            {
                case HostWorkerMessageKinds.Heartbeat:
                    await HeartbeatAsync(canonicalHostId, HostWorkerProtocol.DeserializePayload<HeartbeatEvent>(message));
                    break;
                case HostWorkerMessageKinds.RunnerStarted:
                    await RunnerStartedAsync(HostWorkerProtocol.DeserializePayload<RunnerStartedEvent>(message));
                    break;
                case HostWorkerMessageKinds.RunnerStopped:
                    await RunnerStoppedAsync(HostWorkerProtocol.DeserializePayload<RunnerStoppedEvent>(message));
                    break;
                case HostWorkerMessageKinds.RunnerHealth:
                    await RunnerHealthUpdateAsync(HostWorkerProtocol.DeserializePayload<RunnerHealthUpdateEvent>(message));
                    break;
                case HostWorkerMessageKinds.Reconciliation:
                    await ReconciliationAsync(canonicalHostId, HostWorkerProtocol.DeserializePayload<ReconciliationReport>(message));
                    break;
                case HostWorkerMessageKinds.ImageList:
                    await ImageListAsync(canonicalHostId, HostWorkerProtocol.DeserializePayload<ImageListEvent>(message));
                    break;
                case HostWorkerMessageKinds.ImageRefreshStatus:
                    StreamSubscriptionService.PublishImageRefreshStatus(
                        WithHost(canonicalHostId, HostWorkerProtocol.DeserializePayload<ImageRefreshStatusEvent>(message)));
                    break;
                case HostWorkerMessageKinds.ImagePullProgress:
                    StreamSubscriptionService.PublishImagePullProgress(
                        WithHost(canonicalHostId, HostWorkerProtocol.DeserializePayload<ImagePullProgressEvent>(message)));
                    break;
                case HostWorkerMessageKinds.ImagePullComplete:
                    StreamSubscriptionService.PublishImagePullComplete(
                        WithHost(canonicalHostId, HostWorkerProtocol.DeserializePayload<ImagePullCompleteEvent>(message)));
                    break;
                case HostWorkerMessageKinds.ImageDeleted:
                    StreamSubscriptionService.PublishImageDeleted(
                        WithHost(canonicalHostId, HostWorkerProtocol.DeserializePayload<ImageDeletedEvent>(message)));
                    break;
                case HostWorkerMessageKinds.HostLogs:
                    StreamSubscriptionService.PublishHostLogs(
                        WithHost(canonicalHostId, HostWorkerProtocol.DeserializePayload<HostLogsEvent>(message)));
                    break;
                case HostWorkerMessageKinds.RunnerLogs:
                    StreamSubscriptionService.PublishRunnerLogs(
                        WithHost(canonicalHostId, HostWorkerProtocol.DeserializePayload<RunnerLogsEvent>(message)));
                    break;
                case HostWorkerMessageKinds.LogFrame:
                    IngestLogFrame(canonicalHostId, HostWorkerProtocol.DeserializePayload<HostWorkerLogFrame>(message));
                    break;
                case HostWorkerMessageKinds.UpdateStatus:
                    await UpdateStatusAsync(canonicalHostId, HostWorkerProtocol.DeserializePayload<HostWorkerUpdateStatusEvent>(message));
                    break;
                case HostWorkerMessageKinds.CommandAccepted:
                case HostWorkerMessageKinds.CommandCompleted:
                case HostWorkerMessageKinds.CommandRejected:
                    LogCommandStatus(message);
                    break;
                default:
                    _logger.LogDebug("Ignoring HostWorker message {Kind} from {HostId}", message.Kind, canonicalHostId);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process HostWorker message {Kind} from {HostId}", message.Kind, canonicalHostId);
        }
    }

    private async Task<Host> ResolveOrCreateHostAsync(
        AgentInfo agentInfo,
        IReadOnlyDictionary<string, string> labels,
        string? enrollmentToken,
        CancellationToken ct)
    {
        var hosts = await _store.Query<Host>().ToList();
        var identityHost = hosts.FirstOrDefault(h =>
            string.Equals(h.WorkerId, agentInfo.AgentId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(h.Id, agentInfo.AgentId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(h.Name, agentInfo.Name, StringComparison.OrdinalIgnoreCase));
        var tokenHost = hosts.FirstOrDefault(h => HostEnrollmentToken.Matches(h, enrollmentToken));

        var host = ResolveEnrolledHost(identityHost, tokenHost, agentInfo, enrollmentToken, hosts);
        if (host == null)
        {
            host = new Host
            {
                Id = agentInfo.AgentId,
                Name = agentInfo.Name,
                Platform = agentInfo.Platform,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow
            };
            await _store.Insert(host);
        }

        host.WorkerId = agentInfo.AgentId;
        host.Name = agentInfo.Name;
        host.AgentStatus = AgentStatus.Online;
        host.Platform = agentInfo.Platform;
        host.OsVersion = agentInfo.OsVersion;
        host.Architecture = agentInfo.Architecture;
        host.AgentVersion = agentInfo.AgentVersion;
        host.Capabilities = agentInfo.Capabilities;
        host.IsApproved = true;
        host.EnrolledAt ??= DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(host.EnrollmentToken) && string.IsNullOrWhiteSpace(host.EnrollmentTokenHash))
        {
            host.EnrollmentTokenHash = HostEnrollmentToken.Hash(host.EnrollmentToken);
            host.EnrollmentToken = null;
        }
        host.LastHeartbeat = DateTime.UtcNow;
        host.UpdatedAt = DateTime.UtcNow;
        foreach (var (key, value) in labels)
            host.Labels[key] = value;

        await _store.Update(host);
        return host;
    }

    private Host? ResolveEnrolledHost(
        Host? identityHost,
        Host? tokenHost,
        AgentInfo agentInfo,
        string? enrollmentToken,
        IReadOnlyCollection<Host> hosts)
    {
        if (identityHost != null && HostEnrollmentToken.HasToken(identityHost))
        {
            if (!HostEnrollmentToken.Matches(identityHost, enrollmentToken))
                ThrowUnauthenticated("HostWorker enrollment token is invalid for this host.");

            return identityHost;
        }

        if (tokenHost != null)
        {
            if (!string.IsNullOrWhiteSpace(tokenHost.WorkerId)
                && !string.Equals(tokenHost.WorkerId, agentInfo.AgentId, StringComparison.OrdinalIgnoreCase))
            {
                ThrowUnauthenticated("HostWorker enrollment token is already assigned to another host.");
            }

            return tokenHost;
        }

        var sharedToken = _configuration["HostWorker:EnrollmentToken"];
        if (HostEnrollmentToken.FixedTimeEquals(sharedToken, enrollmentToken))
            return identityHost;

        if (identityHost != null && !hosts.Any(HostEnrollmentToken.HasToken) && string.IsNullOrWhiteSpace(sharedToken))
            return identityHost;

        ThrowUnauthenticated("HostWorker enrollment token is invalid.");
        return null;
    }

    private static void ThrowUnauthenticated(string message)
        => throw new RpcException(new Status(StatusCode.Unauthenticated, message));

    private async Task HeartbeatAsync(string hostId, HeartbeatEvent evt)
    {
        var host = await _store.Get<Host>(hostId);
        if (host != null)
        {
            host.LastHeartbeat = DateTime.UtcNow;
            host.AgentStatus = AgentStatus.Online;
            host.UpdatedAt = DateTime.UtcNow;
            await _store.Update(host);
        }

        try
        {
            var hostGrain = _grainFactory.GetGrain<IHostGrain>(hostId);
            await hostGrain.RecordHeartbeat(hostId, evt.RunningInstanceCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync HostWorker heartbeat for {HostId}", hostId);
        }
    }

    private async Task RunnerStartedAsync(RunnerStartedEvent evt)
    {
        var instance = await _store.Get<RunnerInstance>(evt.InstanceId);
        if (instance != null)
        {
            instance.Status = RunnerInstanceStatus.Running;
            instance.StartedAt = DateTime.UtcNow;

            switch (evt.Backend)
            {
                case ExecutionBackend.Docker:
                    instance.ContainerId = evt.InstanceHandle;
                    break;
                case ExecutionBackend.Tart:
                    instance.VmName = evt.InstanceHandle;
                    break;
                case ExecutionBackend.Native:
                    if (int.TryParse(evt.InstanceHandle, out var pid))
                        instance.ProcessId = pid;
                    break;
            }

            await _store.Update(instance);
        }

        var runnerGrain = _grainFactory.GetGrain<IRunnerInstanceGrain>(evt.InstanceId);
        switch (evt.Backend)
        {
            case ExecutionBackend.Docker:
                await runnerGrain.MarkRunning(containerId: evt.InstanceHandle);
                break;
            case ExecutionBackend.Tart:
                await runnerGrain.MarkRunning(vmName: evt.InstanceHandle);
                break;
            case ExecutionBackend.Native:
                await runnerGrain.MarkRunning(processId: int.TryParse(evt.InstanceHandle, out var pid) ? pid : null);
                break;
            default:
                await runnerGrain.MarkRunning(containerId: evt.InstanceHandle);
                break;
        }

        AgentHub.NotifyQueueRelevantChange();
    }

    private async Task RunnerStoppedAsync(RunnerStoppedEvent evt)
    {
        var instance = await _store.Get<RunnerInstance>(evt.InstanceId);
        if (instance != null)
        {
            instance.Status = evt.Reason == "crashed" ? RunnerInstanceStatus.Crashed
                : evt.ErrorMessage != null ? RunnerInstanceStatus.Failed
                : RunnerInstanceStatus.Stopped;
            instance.ErrorMessage = evt.ErrorMessage;
            instance.StoppedAt = DateTime.UtcNow;
            await _store.Update(instance);

            if (instance.ProvisioningMode == "dynamic" && !string.IsNullOrWhiteSpace(instance.WebhookEventId))
            {
                var webhookEvent = await _store.Get<WebhookEvent>(instance.WebhookEventId);
                var now = DateTime.UtcNow;
                if (webhookEvent is not null && RunnerTimeoutService.PrepareLinkedEventRetry(
                        webhookEvent,
                        now,
                        $"Runner stopped before the queued job was confirmed in progress: {evt.ErrorMessage ?? evt.Reason}"))
                {
                    await _store.Update(webhookEvent);
                }
            }
        }

        var runnerGrain = _grainFactory.GetGrain<IRunnerInstanceGrain>(evt.InstanceId);
        if (evt.Reason == "crashed")
            await runnerGrain.MarkCrashed(evt.ErrorMessage ?? evt.Reason);
        else if (evt.ErrorMessage != null)
            await runnerGrain.MarkFailed(evt.ErrorMessage);
        else
            await runnerGrain.MarkStopped();

        AgentHub.NotifyQueueRelevantChange();
    }

    private async Task RunnerHealthUpdateAsync(RunnerHealthUpdateEvent evt)
    {
        var instance = await _store.Get<RunnerInstance>(evt.InstanceId);
        if (instance != null)
        {
            instance.Status = evt.Status;
            instance.LastHealthCheck = evt.CheckedAt;
            if (!string.IsNullOrEmpty(evt.StatusMessage))
                instance.StatusMessage = evt.StatusMessage;
            await _store.Update(instance);
        }

        var runnerGrain = _grainFactory.GetGrain<IRunnerInstanceGrain>(evt.InstanceId);
        await runnerGrain.UpdateHealth(evt.StatusMessage);
    }

    private Task ReconciliationAsync(string hostId, ReconciliationReport report)
    {
        report.HostId = hostId;
        StreamSubscriptionService.PublishReconciliation(report);
        return Task.CompletedTask;
    }

    private async Task ImageListAsync(string hostId, ImageListEvent evt)
    {
        evt.HostId = hostId;
        _logger.LogInformation("Received image list from HostWorker {HostId}: {Count} images", evt.HostId, evt.Images.Count);

        var oldImages = (await _store.Query<AgentImage>().ToList()).Where(i => i.HostId == evt.HostId).ToList();
        foreach (var old in oldImages)
            await _store.Remove<AgentImage>(old.Id);

        foreach (var img in evt.Images)
        {
            await _store.Insert(new AgentImage
            {
                HostId = evt.HostId,
                ImageType = img.ImageType,
                Repository = img.Repository,
                Tag = img.Tag,
                ImageId = img.ImageId,
                SizeBytes = img.SizeBytes,
                ImageCreatedAt = img.CreatedAt,
                LastReportedAt = DateTime.UtcNow
            });
        }

        StreamSubscriptionService.PublishImageRefreshStatus(new ImageRefreshStatusEvent
        {
            HostId = evt.HostId,
            Stage = "cache",
            Message = $"Loaded {evt.Images.Count} images.",
            IsComplete = true,
            Success = true
        });
    }

    private void LogCommandStatus(HostWorkerMessage message)
    {
        var status = HostWorkerProtocol.DeserializePayload<HostWorkerCommandStatus>(message);
        if (status.Success)
        {
            _logger.LogDebug("HostWorker command {CommandId} {Kind}: {Message}",
                status.CommandId, status.Kind, status.Message);
        }
        else
        {
            _logger.LogWarning("HostWorker command {CommandId} {Kind} failed: {Error}",
                status.CommandId, status.Kind, status.Error);
        }
    }

    private void IngestLogFrame(string hostId, HostWorkerLogFrame frame)
    {
        _logCache.Ingest(hostId, frame);
        _logger.LogTrace(
            "Ingested log frame {StreamKind}/{StreamId} from {HostId} at offset {Offset}",
            frame.StreamKind,
            frame.StreamId,
            hostId,
            frame.Offset);
    }

    private async Task UpdateStatusAsync(string hostId, HostWorkerUpdateStatusEvent evt)
    {
        var host = await _store.Get<Host>(hostId);
        if (host == null)
            return;

        host.UpdateStatus = evt.Success || !evt.IsComplete ? ToDisplayStage(evt.Stage) : "Failed";
        host.UpdateMessage = string.IsNullOrWhiteSpace(evt.Error) ? evt.Message : evt.Error;
        host.LatestAvailableVersion = evt.TargetVersion;
        host.LastUpdateStartedAt ??= DateTime.UtcNow;
        if (evt.IsComplete)
            host.LastUpdateCompletedAt = DateTime.UtcNow;

        await _store.Update(host);
        AgentHub.NotifyQueueRelevantChange();
    }

    private static string ToDisplayStage(string stage)
        => string.IsNullOrWhiteSpace(stage)
            ? "Updating"
            : char.ToUpperInvariant(stage[0]) + stage[1..];

    private static ImageRefreshStatusEvent WithHost(string hostId, ImageRefreshStatusEvent evt)
    {
        evt.HostId = hostId;
        return evt;
    }

    private static ImagePullProgressEvent WithHost(string hostId, ImagePullProgressEvent evt)
    {
        evt.HostId = hostId;
        return evt;
    }

    private static ImagePullCompleteEvent WithHost(string hostId, ImagePullCompleteEvent evt)
    {
        evt.HostId = hostId;
        return evt;
    }

    private static ImageDeletedEvent WithHost(string hostId, ImageDeletedEvent evt)
    {
        evt.HostId = hostId;
        return evt;
    }

    private static HostLogsEvent WithHost(string hostId, HostLogsEvent evt)
    {
        evt.HostId = hostId;
        return evt;
    }

    private static RunnerLogsEvent WithHost(string hostId, RunnerLogsEvent evt)
    {
        evt.HostId = hostId;
        return evt;
    }
}
