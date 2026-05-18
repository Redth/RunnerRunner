using Shiny.DocumentDb;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Periodically removes stale offline GitHub runner registrations whose
/// dynamic instances are no longer active in RunnerRunner.
/// </summary>
public class GitHubRunnerSweepService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);

    private readonly IServiceProvider _services;
    private readonly ILogger<GitHubRunnerSweepService> _logger;

    public GitHubRunnerSweepService(IServiceProvider services, ILogger<GitHubRunnerSweepService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GitHub runner sweep service started");

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);

            using var timer = new PeriodicTimer(SweepInterval);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _services.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
                    var cleanupService = scope.ServiceProvider.GetRequiredService<RunnerRegistrationCleanupService>();
                    await cleanupService.SweepStaleRegistrationsAsync(store, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "GitHub stale-runner sweep failed");
                }

                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        _logger.LogInformation("GitHub runner sweep service stopped");
    }
}
