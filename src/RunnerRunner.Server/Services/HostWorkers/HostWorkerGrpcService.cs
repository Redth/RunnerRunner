using Grpc.Core;
using RunnerRunner.Core.HostWorkers;

namespace RunnerRunner.Server.Services.HostWorkers;

public sealed class HostWorkerGrpcService : HostWorkerControl.HostWorkerControlBase
{
    private readonly HostWorkerConnectionRegistry _registry;
    private readonly HostWorkerEventProcessor _events;
    private readonly ILogger<HostWorkerGrpcService> _logger;

    public HostWorkerGrpcService(
        HostWorkerConnectionRegistry registry,
        HostWorkerEventProcessor events,
        ILogger<HostWorkerGrpcService> logger)
    {
        _registry = registry;
        _events = events;
        _logger = logger;
    }

    public override async Task Connect(
        IAsyncStreamReader<HostWorkerMessage> requestStream,
        IServerStreamWriter<HostWorkerMessage> responseStream,
        ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken))
            return;

        var helloMessage = requestStream.Current;
        if (helloMessage.Kind != HostWorkerMessageKinds.Hello)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "First HostWorker message must be worker.hello."));

        var hello = HostWorkerProtocol.DeserializePayload<HostWorkerHello>(helloMessage);

        var canonicalHostId = await _events.WorkerConnectedAsync(
            hello.Agent,
            context.Peer,
            hello.Labels,
            hello.EnrollmentToken,
            context.CancellationToken);
        await using var connection = _registry.Register(
            canonicalHostId,
            hello.Agent.AgentId,
            hello.Agent.Name,
            helloMessage.HostId);

        _logger.LogInformation("HostWorker {HostId} connected from {Peer}", canonicalHostId, context.Peer);

        var sendLoop = SendLoopAsync(connection, responseStream, context.CancellationToken);
        try
        {
            while (await requestStream.MoveNext(context.CancellationToken))
                await _events.HandleMessageAsync(canonicalHostId, requestStream.Current, context.CancellationToken);
        }
        finally
        {
            var wasCurrentConnection = _registry.IsCurrent(canonicalHostId, connection);
            connection.Complete();
            if (wasCurrentConnection)
                await _events.WorkerDisconnectedAsync(canonicalHostId, CancellationToken.None);
            try
            {
                await sendLoop;
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
            }
            catch (RpcException) when (context.CancellationToken.IsCancellationRequested)
            {
            }
            _logger.LogInformation("HostWorker {HostId} disconnected", canonicalHostId);
        }
    }

    private static async Task SendLoopAsync(
        HostWorkerConnection connection,
        IServerStreamWriter<HostWorkerMessage> responseStream,
        CancellationToken cancellationToken)
    {
        await foreach (var message in connection.ReadAllAsync(cancellationToken))
            await responseStream.WriteAsync(message, cancellationToken);
    }

}
