using RunnerRunner.Core.HostWorkers;
using RunnerRunner.Core.Hub;

namespace RunnerRunner.Server.Services.HostWorkers;

public sealed class GrpcHostCommandDispatcher : IHostCommandDispatcher
{
    private readonly HostWorkerConnectionRegistry _registry;
    private readonly LongRunningTaskService _tasks;
    private readonly ILogger<GrpcHostCommandDispatcher> _logger;

    public GrpcHostCommandDispatcher(
        HostWorkerConnectionRegistry registry,
        LongRunningTaskService tasks,
        ILogger<GrpcHostCommandDispatcher> logger)
    {
        _registry = registry;
        _tasks = tasks;
        _logger = logger;
    }

    public bool CanDispatchToHost(string hostId)
        => _registry.IsConnected(hostId);

    public Task DispatchDeployRunnerAsync(string hostId, DeployRunnerCommand command)
        => DispatchAsync(hostId, HostCommandKind.DeployRunner, new HostCommandEnvelope
        {
            Kind = HostCommandKind.DeployRunner,
            DeployRunner = command
        }, command.InstanceId);

    public Task DispatchStopRunnerAsync(string hostId, StopRunnerCommand command)
        => DispatchAsync(hostId, HostCommandKind.StopRunner, new HostCommandEnvelope
        {
            Kind = HostCommandKind.StopRunner,
            StopRunner = command
        }, command.InstanceId);

    public Task DispatchCleanupOrphanAsync(string hostId, CleanupOrphanCommand command)
        => DispatchAsync(hostId, HostCommandKind.CleanupOrphan, new HostCommandEnvelope
        {
            Kind = HostCommandKind.CleanupOrphan,
            CleanupOrphan = command
        }, command.ContainerId ?? command.VmName ?? command.ProcessId?.ToString());

    public Task DispatchListImagesAsync(string hostId, ListImagesCommand command)
        => DispatchAsync(hostId, HostCommandKind.ListImages, new HostCommandEnvelope
        {
            Kind = HostCommandKind.ListImages,
            ListImages = command
        }, command.FilterType?.ToString());

    public async Task DispatchPullImageAsync(string hostId, PullImageCommand command)
    {
        var taskId = _tasks.TrackImagePull(hostId, command);
        try
        {
            await DispatchAsync(hostId, HostCommandKind.PullImage, new HostCommandEnvelope
            {
                Kind = HostCommandKind.PullImage,
                PullImage = command
            }, $"{command.ImageType}:{command.RegistryUrl}/{command.ImageName}:{command.Tag}");
        }
        catch (Exception ex)
        {
            _tasks.MarkFailed(taskId, ex.Message);
            throw;
        }
    }

    public Task DispatchDeleteImageAsync(string hostId, DeleteImageCommand command)
        => DispatchAsync(hostId, HostCommandKind.DeleteImage, new HostCommandEnvelope
        {
            Kind = HostCommandKind.DeleteImage,
            DeleteImage = command
        }, $"{command.ImageType}:{command.ImageId}");

    public Task DispatchGetHostLogsAsync(string hostId, GetHostLogsCommand command)
        => DispatchAsync(hostId, HostCommandKind.GetHostLogs, new HostCommandEnvelope
        {
            Kind = HostCommandKind.GetHostLogs,
            GetHostLogs = command
        }, $"tail:{command.TailLines}");

    public Task DispatchGetRunnerLogsAsync(string hostId, GetRunnerLogsCommand command)
        => DispatchAsync(hostId, HostCommandKind.GetRunnerLogs, new HostCommandEnvelope
        {
            Kind = HostCommandKind.GetRunnerLogs,
            GetRunnerLogs = command
        }, $"{command.InstanceHandle}:tail:{command.TailLines}");

    public Task DispatchApplyHostWorkerUpdateAsync(string hostId, HostWorkerUpdateCommand command)
        => DispatchAsync(hostId, HostCommandKind.ApplyHostWorkerUpdate, new HostCommandEnvelope
        {
            Kind = HostCommandKind.ApplyHostWorkerUpdate,
            ApplyHostWorkerUpdate = command
        }, command.TargetVersion);

    private async Task DispatchAsync(string hostId, HostCommandKind kind, HostCommandEnvelope envelope, string? idempotencyKeySuffix)
    {
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Host id is required to dispatch a HostWorker command.", nameof(hostId));

        var commandId = Guid.NewGuid().ToString("N");
        var idempotencyKey = string.IsNullOrWhiteSpace(idempotencyKeySuffix)
            ? $"{hostId}:{kind}:{commandId}"
            : $"{hostId}:{kind}:{idempotencyKeySuffix}";

        var message = HostWorkerProtocol.CreateMessage(
            hostId,
            HostWorkerMessageKinds.Command,
            envelope,
            commandId,
            idempotencyKey);

        await _registry.SendAsync(hostId, message);
        _logger.LogDebug("Dispatched {CommandKind} command {CommandId} to HostWorker {HostId}", kind, commandId, hostId);
    }
}
