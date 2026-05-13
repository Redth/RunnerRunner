using Orleans.Streams;
using RunnerRunner.Core.Hub;

namespace RunnerRunner.Server.Services;

public sealed class OrleansHostCommandDispatcher : IHostCommandDispatcher
{
    public const string StreamProviderName = "RunnerEvents";
    public const string StreamNamespace = "HostCommands";
    public const string ReconciliationStreamNamespace = "HostReconciliation";
    public const string ImageListStreamNamespace = "HostImageLists";
    public const string ImageRefreshStatusStreamNamespace = "HostImageRefreshStatus";
    public const string ImagePullProgressStreamNamespace = "HostImagePullProgress";
    public const string ImagePullCompleteStreamNamespace = "HostImagePullComplete";
    public const string ImageDeletedStreamNamespace = "HostImageDeleted";
    public const string HostLogsStreamNamespace = "HostLogs";
    public const string RunnerLogsStreamNamespace = "RunnerLogs";

    private readonly IClusterClient _client;
    private readonly ILogger<OrleansHostCommandDispatcher> _logger;

    public OrleansHostCommandDispatcher(
        IClusterClient client,
        ILogger<OrleansHostCommandDispatcher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public Task DispatchDeployRunnerAsync(string hostId, DeployRunnerCommand command)
        => DispatchAsync(hostId, new HostCommandEnvelope
        {
            Kind = HostCommandKind.DeployRunner,
            DeployRunner = command
        });

    public Task DispatchStopRunnerAsync(string hostId, StopRunnerCommand command)
        => DispatchAsync(hostId, new HostCommandEnvelope
        {
            Kind = HostCommandKind.StopRunner,
            StopRunner = command
        });

    public Task DispatchCleanupOrphanAsync(string hostId, CleanupOrphanCommand command)
        => DispatchAsync(hostId, new HostCommandEnvelope
        {
            Kind = HostCommandKind.CleanupOrphan,
            CleanupOrphan = command
        });

    public Task DispatchListImagesAsync(string hostId, ListImagesCommand command)
        => DispatchAsync(hostId, new HostCommandEnvelope
        {
            Kind = HostCommandKind.ListImages,
            ListImages = command
        });

    public Task DispatchPullImageAsync(string hostId, PullImageCommand command)
        => DispatchAsync(hostId, new HostCommandEnvelope
        {
            Kind = HostCommandKind.PullImage,
            PullImage = command
        });

    public Task DispatchDeleteImageAsync(string hostId, DeleteImageCommand command)
        => DispatchAsync(hostId, new HostCommandEnvelope
        {
            Kind = HostCommandKind.DeleteImage,
            DeleteImage = command
        });

    public Task DispatchGetHostLogsAsync(string hostId, GetHostLogsCommand command)
        => DispatchAsync(hostId, new HostCommandEnvelope
        {
            Kind = HostCommandKind.GetHostLogs,
            GetHostLogs = command
        });

    public Task DispatchGetRunnerLogsAsync(string hostId, GetRunnerLogsCommand command)
        => DispatchAsync(hostId, new HostCommandEnvelope
        {
            Kind = HostCommandKind.GetRunnerLogs,
            GetRunnerLogs = command
        });

    private async Task DispatchAsync(string hostId, HostCommandEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Host id is required to dispatch a HostSilo command.", nameof(hostId));

        var streamProvider = _client.GetStreamProvider(StreamProviderName);
        var stream = streamProvider.GetStream<HostCommandEnvelope>(
            StreamId.Create(StreamNamespace, hostId));

        await stream.OnNextAsync(envelope);

        _logger.LogDebug("Dispatched {CommandKind} command to HostSilo {HostId}", envelope.Kind, hostId);
    }
}
