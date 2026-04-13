using Orleans.Streams;
using RunnerRunner.Server.Grains.Events;

namespace RunnerRunner.Server.Services;

public class StreamSubscriptionService : IHostedService
{
    private readonly IClusterClient _client;
    private readonly ILogger<StreamSubscriptionService> _logger;
    private StreamSubscriptionHandle<RunnerStatusChangedEvent>? _runnerSub;
    private StreamSubscriptionHandle<HostStatusChangedEvent>? _hostSub;

    public static event Action<RunnerStatusChangedEvent>? OnRunnerStatusChanged;
    public static event Action<HostStatusChangedEvent>? OnHostStatusChanged;

    public StreamSubscriptionService(IClusterClient client, ILogger<StreamSubscriptionService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var streamProvider = _client.GetStreamProvider("RunnerEvents");

        var runnerStream = streamProvider.GetStream<RunnerStatusChangedEvent>(
            StreamId.Create("RunnerStatus", "all"));
        _runnerSub = await runnerStream.SubscribeAsync((evt, token) =>
        {
            _logger.LogDebug("Stream: Runner {Name} -> {Status}", evt.RunnerName, evt.Status);
            OnRunnerStatusChanged?.Invoke(evt);
            return Task.CompletedTask;
        });

        var hostStream = streamProvider.GetStream<HostStatusChangedEvent>(
            StreamId.Create("HostStatus", "all"));
        _hostSub = await hostStream.SubscribeAsync((evt, token) =>
        {
            _logger.LogDebug("Stream: Host {Name} -> {Status}", evt.HostName, evt.Status);
            OnHostStatusChanged?.Invoke(evt);
            return Task.CompletedTask;
        });

        _logger.LogInformation("StreamSubscriptionService started");
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_runnerSub != null) await _runnerSub.UnsubscribeAsync();
        if (_hostSub != null) await _hostSub.UnsubscribeAsync();
    }
}
