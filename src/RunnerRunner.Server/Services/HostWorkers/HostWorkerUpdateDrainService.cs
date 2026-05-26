namespace RunnerRunner.Server.Services.HostWorkers;

public sealed class HostWorkerUpdateDrainService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(10);

    private readonly HostWorkerUpdateService _updates;
    private readonly ILogger<HostWorkerUpdateDrainService> _logger;

    public HostWorkerUpdateDrainService(
        HostWorkerUpdateService updates,
        ILogger<HostWorkerUpdateDrainService> logger)
    {
        _updates = updates;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HostWorker update drain service started");

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            using var timer = new PeriodicTimer(ScanInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _updates.ProcessPendingDrainedUpdatesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while processing drained HostWorker updates");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        _logger.LogInformation("HostWorker update drain service stopped");
    }
}
