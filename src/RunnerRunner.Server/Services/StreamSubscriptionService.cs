using Orleans.Streams;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Events;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Services;

public class StreamSubscriptionService : IHostedService
{
    private readonly IClusterClient _client;
    private readonly IDocumentStore _store;
    private readonly ILogger<StreamSubscriptionService> _logger;
    private StreamSubscriptionHandle<RunnerStatusChangedEvent>? _runnerSub;
    private StreamSubscriptionHandle<HostStatusChangedEvent>? _hostSub;
    private StreamSubscriptionHandle<ReconciliationReport>? _reconciliationSub;
    private StreamSubscriptionHandle<ImageListEvent>? _imageListSub;
    private StreamSubscriptionHandle<ImageRefreshStatusEvent>? _imageRefreshStatusSub;
    private StreamSubscriptionHandle<ImagePullProgressEvent>? _imagePullProgressSub;
    private StreamSubscriptionHandle<ImagePullCompleteEvent>? _imagePullCompleteSub;
    private StreamSubscriptionHandle<ImageDeletedEvent>? _imageDeletedSub;
    private StreamSubscriptionHandle<HostLogsEvent>? _hostLogsSub;
    private StreamSubscriptionHandle<RunnerLogsEvent>? _runnerLogsSub;

    public static event Action<RunnerStatusChangedEvent>? OnRunnerStatusChanged;
    public static event Action<HostStatusChangedEvent>? OnHostStatusChanged;
    public static event Action<ReconciliationReport>? OnReconciliationReportReceived;
    public static event Action<ImageRefreshStatusEvent>? OnImageRefreshStatusReceived;
    public static event Action<ImagePullProgressEvent>? OnImagePullProgressReceived;
    public static event Action<ImagePullCompleteEvent>? OnImagePullCompleteReceived;
    public static event Action<ImageDeletedEvent>? OnImageDeletedReceived;
    public static event Action<HostLogsEvent>? OnHostLogsReceived;
    public static event Action<RunnerLogsEvent>? OnRunnerLogsReceived;

    public StreamSubscriptionService(IClusterClient client, IDocumentStore store, ILogger<StreamSubscriptionService> logger)
    {
        _client = client;
        _store = store;
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

                var reconciliationStream = streamProvider.GetStream<ReconciliationReport>(
                    StreamId.Create(HostWorkerStreamNames.ReconciliationStreamNamespace, "all"));
                _reconciliationSub = await reconciliationStream.SubscribeAsync((evt, token) =>
                {
                    OnReconciliationReportReceived?.Invoke(evt);
                    return Task.CompletedTask;
                });

                var imageListStream = streamProvider.GetStream<ImageListEvent>(
                    StreamId.Create(HostWorkerStreamNames.ImageListStreamNamespace, "all"));
                _imageListSub = await imageListStream.SubscribeAsync(async (evt, token) =>
                {
                    await UpdateImageCacheAsync(evt);
                });

                var imageRefreshStream = streamProvider.GetStream<ImageRefreshStatusEvent>(
                    StreamId.Create(HostWorkerStreamNames.ImageRefreshStatusStreamNamespace, "all"));
                _imageRefreshStatusSub = await imageRefreshStream.SubscribeAsync((evt, token) =>
                {
                    OnImageRefreshStatusReceived?.Invoke(evt);
                    return Task.CompletedTask;
                });

                var imagePullProgressStream = streamProvider.GetStream<ImagePullProgressEvent>(
                    StreamId.Create(HostWorkerStreamNames.ImagePullProgressStreamNamespace, "all"));
                _imagePullProgressSub = await imagePullProgressStream.SubscribeAsync((evt, token) =>
                {
                    OnImagePullProgressReceived?.Invoke(evt);
                    return Task.CompletedTask;
                });

                var imagePullCompleteStream = streamProvider.GetStream<ImagePullCompleteEvent>(
                    StreamId.Create(HostWorkerStreamNames.ImagePullCompleteStreamNamespace, "all"));
                _imagePullCompleteSub = await imagePullCompleteStream.SubscribeAsync((evt, token) =>
                {
                    OnImagePullCompleteReceived?.Invoke(evt);
                    return Task.CompletedTask;
                });

                var imageDeletedStream = streamProvider.GetStream<ImageDeletedEvent>(
                    StreamId.Create(HostWorkerStreamNames.ImageDeletedStreamNamespace, "all"));
                _imageDeletedSub = await imageDeletedStream.SubscribeAsync((evt, token) =>
                {
                    OnImageDeletedReceived?.Invoke(evt);
                    return Task.CompletedTask;
                });

                var hostLogsStream = streamProvider.GetStream<HostLogsEvent>(
                    StreamId.Create(HostWorkerStreamNames.HostLogsStreamNamespace, "all"));
                _hostLogsSub = await hostLogsStream.SubscribeAsync((evt, token) =>
                {
                    OnHostLogsReceived?.Invoke(evt);
                    return Task.CompletedTask;
                });

                var runnerLogsStream = streamProvider.GetStream<RunnerLogsEvent>(
                    StreamId.Create(HostWorkerStreamNames.RunnerLogsStreamNamespace, "all"));
                _runnerLogsSub = await runnerLogsStream.SubscribeAsync((evt, token) =>
                {
                    OnRunnerLogsReceived?.Invoke(evt);
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
        if (_reconciliationSub != null) await _reconciliationSub.UnsubscribeAsync();
        if (_imageListSub != null) await _imageListSub.UnsubscribeAsync();
        if (_imageRefreshStatusSub != null) await _imageRefreshStatusSub.UnsubscribeAsync();
        if (_imagePullProgressSub != null) await _imagePullProgressSub.UnsubscribeAsync();
        if (_imagePullCompleteSub != null) await _imagePullCompleteSub.UnsubscribeAsync();
        if (_imageDeletedSub != null) await _imageDeletedSub.UnsubscribeAsync();
        if (_hostLogsSub != null) await _hostLogsSub.UnsubscribeAsync();
        if (_runnerLogsSub != null) await _runnerLogsSub.UnsubscribeAsync();
    }

    private async Task UpdateImageCacheAsync(ImageListEvent evt)
    {
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

        OnImageRefreshStatusReceived?.Invoke(new ImageRefreshStatusEvent
        {
            HostId = evt.HostId,
            Stage = "cache",
            Message = $"Loaded {evt.Images.Count} images.",
            IsComplete = true,
            Success = true
        });
    }

    public static void PublishReconciliation(ReconciliationReport report)
        => OnReconciliationReportReceived?.Invoke(report);

    public static void PublishImageRefreshStatus(ImageRefreshStatusEvent evt)
        => OnImageRefreshStatusReceived?.Invoke(evt);

    public static void PublishImagePullProgress(ImagePullProgressEvent evt)
        => OnImagePullProgressReceived?.Invoke(evt);

    public static void PublishImagePullComplete(ImagePullCompleteEvent evt)
        => OnImagePullCompleteReceived?.Invoke(evt);

    public static void PublishImageDeleted(ImageDeletedEvent evt)
        => OnImageDeletedReceived?.Invoke(evt);

    public static void PublishHostLogs(HostLogsEvent evt)
        => OnHostLogsReceived?.Invoke(evt);

    public static void PublishRunnerLogs(RunnerLogsEvent evt)
        => OnRunnerLogsReceived?.Invoke(evt);
}
