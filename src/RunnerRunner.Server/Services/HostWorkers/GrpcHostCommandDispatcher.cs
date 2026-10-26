using RunnerRunner.Core.HostWorkers;
using RunnerRunner.Core.Hub;
using RunnerRunner.Server.Services.Admission;

namespace RunnerRunner.Server.Services.HostWorkers;

public sealed class GrpcHostCommandDispatcher : IHostCommandDispatcher
{
    private readonly HostWorkerConnectionRegistry _registry;
    private readonly LongRunningTaskService _tasks;
    private readonly IResourceAdmission _admission;
    private readonly ILogger<GrpcHostCommandDispatcher> _logger;

    public GrpcHostCommandDispatcher(
        HostWorkerConnectionRegistry registry,
        LongRunningTaskService tasks,
        IResourceAdmission admission,
        ILogger<GrpcHostCommandDispatcher> logger)
    {
        _registry = registry;
        _tasks = tasks;
        _admission = admission;
        _logger = logger;
    }

    public bool CanDispatchToHost(string hostId)
        => _registry.IsConnected(hostId);

    public async Task DispatchDeployRunnerAsync(string hostId, DeployRunnerCommand command)
    {
        var connection = _registry.GetConnection(hostId)
            ?? throw new InvalidOperationException($"HostWorker '{hostId}' is not connected.");
        // Resolve aliases once. The exact connection and canonical host own this
        // reservation even if the registry changes while PostgreSQL is committing.
        var budget = await _admission.GetBudgetAsync(connection.HostId);
        if (budget is not null)
        {
            if (!connection.SupportsDurableOwnership)
                throw new ResourceAdmissionException("This host budget requires a worker advertising durable-ownership-v1.");
            await _admission.ReserveAsync(connection.HostId, command);
        }
        else if (command.DeferRegistration)
            throw new ResourceAdmissionException("Prepared registration requires an opted-in host job budget.");
        var message = HostWorkerProtocol.CreateMessage(connection.HostId, HostWorkerMessageKinds.Command,
            new HostCommandEnvelope { Kind = HostCommandKind.DeployRunner, DeployRunner = command },
            Guid.NewGuid().ToString("N"), $"{connection.HostId}:DeployRunner:{command.InstanceId}");
        // Never release on send failure: delivery/outcome can be uncertain.
        await connection.SendAsync(message, CancellationToken.None);
    }

    public Task DispatchActivateRunnerAsync(string hostId, ActivateRunnerCommand command)
        => DispatchAsync(hostId, HostCommandKind.ActivateRunner, new HostCommandEnvelope
        { Kind = HostCommandKind.ActivateRunner, ActivateRunner = command }, command.InstanceId);

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
