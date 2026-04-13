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
        // Wait for Orleans silo to be fully started before subscribing to streams
        for (int attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                await Task.Delay(2000, ct);

                var streamProvider = _client.GetStreamProvider("RunnerEvents");

                var runnerStream = streamProvider.GetStream<RunnerStatusChangedEvent>(
                    StreamId.Create("RunnerStatus", "all"));
                _runnerSub = await runnerStream.SubscribeAsync((evt, token) =>
                {
                    OnRunnerStatusChanged?.Invoke(evt);
                    return Task.CompletedTask;
                });

                var hostStream = streamProvider.GetStream<HostStatusChangedEvent>(
                    StreamId.Create("HostStatus", "all"));
                _hostSub = await hostStream.SubscribeAsync((evt, token) =>
                {
                    OnHostStatusChanged?.Invoke(evt);
                    return Task.CompletedTask;
                });

                _logger.LogInformation("StreamSubscriptionService started (attempt {Attempt})", attempt + 1);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Stream subscription attempt {Attempt} failed: {Error}", attempt + 1, ex.Message);
            }
        }

        _logger.LogError("StreamSubscriptionService failed to start after 30 attempts");
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_runnerSub != null) await _runnerSub.UnsubscribeAsync();
        if (_hostSub != null) await _hostSub.UnsubscribeAsync();
    }
}
